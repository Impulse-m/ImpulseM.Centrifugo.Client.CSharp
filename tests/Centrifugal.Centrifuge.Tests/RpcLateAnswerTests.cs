// The answers that arrive after a call settled on its own: an answer for an attempt that the transport loss already ended is
// not waited for, and it is not acknowledged. The deadline runs on the fake clock, so the test waits for no real time.
// The upstream rule is kept: a late answer after a transport loss is not waited for.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The answers that arrive for calls which settled locally, against the fake server with a fake clock.</summary>
    [Collection("Integration")]
    public sealed class RpcLateAnswerTests : RpcTestBase {
        /// <summary>
        /// A call that timed out while its answer was held loses its connection. The late answer arrives on the next connection
        /// and is not acknowledged: the next request carries no ack for that msg_id. Real time: the reconnect is an upstream
        /// LoopTimer, so its delays are the smallest the options allow.
        /// </summary>
        [Fact]
        public async Task RpcAsync_LateAnswerAfterATransportLoss_IsNotAcknowledged() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            uint[] heldCommandId = { 0 };
            TaskCompletionSource<bool> lateReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                Timeout = TimeSpan.FromMilliseconds(300),
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => new RpcWireTransport(
                new WebSocketTransport(endpoint),
                null,
                false,
                (IReadOnlyList<byte[]> messages) => {
                    foreach (byte[] message in messages) {
                        if (heldCommandId[0] != 0 && Protocol.Reply.Parser.ParseFrom(message).Id == heldCommandId[0]) {
                            lateReceived.TrySetResult(true);
                        }
                    }
                }));
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "held");
            CommandArrivals cancels = new CommandArrivals(RpcTestHelpers.Wait);
            RpcTestHelpers.CountArrivals(Server, cancels, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Cancel != 0);
            CentrifugeRpcCall call = client.RpcCall("held", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            uint commandId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "held").Id;
            heldCommandId[0] = commandId;

            timers.Advance(300);
            await Assert.ThrowsAsync<CentrifugeTimeoutException>(() => RpcTestHelpers.Bounded(call.Result));
            await cancels.Reached(1);

            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnected;
            await RpcTestHelpers.AnswerAsync(Server, commandId, "late");
            // The late answer is handed to the client before the next call is made, so the call is made after it is handled.
            await lateReceived.Task.WaitAsync(RpcTestHelpers.Wait);
            await RpcTestHelpers.Bounded(client.RpcAsync("probe", ReadOnlyMemory<byte>.Empty));

            Protocol.Command probe = Assert.Single(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "probe");
            Assert.Empty(probe.Rpc.Ack);
        }


        // Java: RpcExactlyOnceTest.lateReplyToASettledCallIsAckedWithTheNextCall.
        /// <summary>
        /// A call that timed out keeps its attempt for one call timeout. A reply that arrives in that window is acknowledged: the
        /// next request carries the msg_id of the late reply. The deadline and the window run on the fake clock.
        /// </summary>
        [Fact]
        public async Task RpcCall_LateReplyInItsTimeoutWindow_IsAckedWithTheNextCall() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "held");
            CommandArrivals cancels = new CommandArrivals(RpcTestHelpers.Wait);
            RpcTestHelpers.CountArrivals(Server, cancels, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Cancel != 0);
            uint[] heldCommandId = { 0 };
            TaskCompletionSource<bool> lateReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                Timeout = TimeSpan.FromMilliseconds(300),
            });
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => new RpcWireTransport(
                new WebSocketTransport(endpoint),
                null,
                false,
                (IReadOnlyList<byte[]> messages) => {
                    foreach (byte[] message in messages) {
                        if (heldCommandId[0] != 0 && Protocol.Reply.Parser.ParseFrom(message).Id == heldCommandId[0]) {
                            lateReceived.TrySetResult(true);
                        }
                    }
                }));
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("held", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            Protocol.Command held = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "held");
            heldCommandId[0] = held.Id;

            timers.Advance(300);
            await Assert.ThrowsAsync<CentrifugeTimeoutException>(() => RpcTestHelpers.Bounded(call.Result));
            await cancels.Reached(1);
            Protocol.Command cancel = Assert.Single(RpcTestHelpers.Cancels(Server));
            Assert.Equal(held.Rpc.MsgId, cancel.Rpc.Cancel);
            await RpcTestHelpers.AnswerAsync(Server, held.Id, "late");
            await lateReceived.Task.WaitAsync(RpcTestHelpers.Wait);
            await RpcTestHelpers.Bounded(client.RpcAsync("probe", ReadOnlyMemory<byte>.Empty));

            Protocol.Command probe = Assert.Single(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "probe");
            Assert.Equal(new[] { held.Rpc.MsgId }, probe.Rpc.Ack.ToArray());
        }
    }
}
