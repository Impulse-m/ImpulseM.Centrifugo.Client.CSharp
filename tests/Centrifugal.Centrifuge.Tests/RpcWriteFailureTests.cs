// The failed writes of the rpc calls: a frame the transport refuses, a send that throws, a refused first write of a tracked
// call (it takes a new msg_id, and its acks come back), and a refused cancel (it is reported and kept for the next connection).
// The refusal happens in a test-local transport wrapper, so the fake server never sees a refused frame.
// The client writes the calls queued in one step as one frame, so a refused frame fails the calls in it: each test here
// refuses a frame that holds one call.
// The upstream rule is kept: a write that throws fails its call with the write error (code 10) and keeps the cause.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The failed writes of the rpc calls, against the fake server with a transport that refuses frames.</summary>
    [Collection("Integration")]
    public sealed class RpcWriteFailureTests : RpcTestBase {
        // Java: RpcExactlyOnceTest.callWhoseSendFailsWithAnErrorFailsAloneAndTheRestOfTheFlushGoesOut.
        /// <summary>A plain call whose frame is refused fails alone with code 10 and its cause; the calls after it are sent.</summary>
        [Fact]
        public async Task RpcAsync_RefusedFrameOfAPlainCall_FailsItAloneWith10AndTheLaterCallIsSent() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            List<Protocol.Command> refused = new List<Protocol.Command>();
            IOException error = new IOException("broken pipe");
            CentrifugeClient client = NewRefusingClient(null, RefuseFirst((Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "first", error, refused), false);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeException failure = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(client.RpcAsync("first", ReadOnlyMemory<byte>.Empty)));
            Assert.Equal(CentrifugeErrorCodes.TransportWriteError, failure.Code);
            Assert.Same(error, failure.InnerException);
            CentrifugeRpcResult later = await RpcTestHelpers.Bounded(client.RpcAsync("later", ReadOnlyMemory<byte>.Empty));

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(later.Data.Span));
            Assert.Single(refused);
            Assert.DoesNotContain(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "first");
        }


        /// <summary>A send that throws fails its call with code 10 and keeps the thrown exception as its cause.</summary>
        [Fact]
        public async Task RpcAsync_SendThatThrows_FailsTheCallWith10AndKeepsTheCause() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            List<Protocol.Command> refused = new List<Protocol.Command>();
            InvalidOperationException error = new InvalidOperationException("transport failed inside the send");
            CentrifugeClient client = NewRefusingClient(null, RefuseFirst((Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "first", error, refused), true);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeException failure = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(client.RpcAsync("first", ReadOnlyMemory<byte>.Empty)));
            Assert.Equal(CentrifugeErrorCodes.TransportWriteError, failure.Code);
            Assert.Same(error, failure.InnerException);
            await RpcTestHelpers.Bounded(client.RpcAsync("later", ReadOnlyMemory<byte>.Empty));
        }


        // A refused first write of a tracked call: the call is queued again as a call never sent, so its next write takes a new
        // msg_id and is not a retransmit. The acks of the refused frame come back to the front of the queue of acks.
        /// <summary>A tracked call whose first frame is refused is sent again with a new msg_id, not as a retransmit, and carries its acks.</summary>
        [Fact]
        public async Task RpcAsync_RefusedFirstWriteOfATrackedCall_IsSentAgainWithANewMsgIdAndKeepsItsAcks() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            List<Protocol.Command> refused = new List<Protocol.Command>();
            TaskCompletionSource<bool> refusedSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeClient client = NewRefusingClient(
                new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance },
                RefuseFirst((Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "second", new IOException("broken pipe"), refused, refusedSeen),
                false);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            await RpcTestHelpers.Bounded(client.RpcAsync("first", ReadOnlyMemory<byte>.Empty));

            Task<CentrifugeRpcResult> second = client.RpcAsync("second", ReadOnlyMemory<byte>.Empty);
            // The frame of the refused call is formed before the refusal. A call submitted after the refusal goes into a later
            // frame, so the refusal does not take it too.
            await RpcTestHelpers.Bounded(refusedSeen.Task);
            // The refused call waits in the queue until a flush. Each barrier call flushes the queue, so the call is written at the
            // first barrier after its failure has been handled; the bound stops a test that never gets there.
            for (int i = 0; i < 100 && !RpcTestHelpers.Requests(Server).Any((Protocol.Command cmd) => cmd.Rpc.Method == "second"); i++) {
                await RpcTestHelpers.Bounded(client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty));
            }
            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(second);

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Protocol.Command request = Assert.Single(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "second");
            Assert.False(request.Rpc.Retransmit);
            Assert.NotEqual(refused[0].Rpc.MsgId, request.Rpc.MsgId);
            ulong firstMsgId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "first").Rpc.MsgId;
            Assert.Equal(firstMsgId, request.Rpc.Ack[0]);
            Assert.Single(request.Rpc.Ack, (ulong msgId) => msgId == firstMsgId);
        }


        // A refused cancel: the failure is reported (an error that is not an IO failure is reported as unclassified), the cancel is
        // kept, and the next connection writes it once. The cancelled call fails with code 17 at once.
        // Java: RpcExactlyOnceTest.cancelWhoseSendThrowsIsKeptAndTheRestOfTheFlushGoesOut.
        /// <summary>A cancel whose frame is refused is reported, kept, and sent once with the next connection.</summary>
        [Fact]
        public async Task Cancel_RefusedCancelFrame_IsReportedAndKeptForTheNextConnection() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            List<Protocol.Command> refused = new List<Protocol.Command>();
            CentrifugeClient client = NewRefusingClient(
                new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance },
                RefuseFirst((Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Cancel != 0, new InvalidOperationException("cancel send failed"), refused),
                true);
            TaskCompletionSource<CentrifugeErrorEventArgs> reported = new TaskCompletionSource<CentrifugeErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Error += (object? sender, CentrifugeErrorEventArgs args) => reported.TrySetResult(args);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Connect != null || (cmd.Rpc != null && cmd.Rpc.Method == "hold"));
            CommandArrivals reconnects = new CommandArrivals(RpcTestHelpers.Wait);
            RpcTestHelpers.CountArrivals(Server, reconnects, (Protocol.Command cmd) => cmd.Connect != null);
            CentrifugeRpcCall call = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            ulong msgId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "hold").Rpc.MsgId;
            call.Cancel();
            CentrifugeException cancelled = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, cancelled.Code);
            CentrifugeErrorEventArgs report = await RpcTestHelpers.Bounded(reported.Task);
            Assert.Equal("unclassified", report.Type);
            Assert.IsType<CentrifugeUnclassifiedException>(report.Exception);
            Assert.IsType<InvalidOperationException>(report.Exception!.InnerException);
            Assert.Equal("cancel send failed", report.Exception.InnerException!.Message);

            // The reconnect is held, so the call below waits in the queue with the kept cancel, and both go out with the connect.
            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnects.Reached(1);
            CentrifugeRpcCall queued = client.RpcCall("queued", ReadOnlyMemory<byte>.Empty);
            Server.ReleaseDeferredMatching((Protocol.Command cmd) => cmd.Connect != null);
            await reconnected;
            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(queued.Result);

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Single(refused);
            Assert.Equal(msgId, refused[0].Rpc.Cancel);
            Protocol.Command sent = Assert.Single(RpcTestHelpers.Cancels(Server));
            Assert.Equal(msgId, sent.Rpc.Cancel);
        }


        // Refuses the first frame that carries a command for which matches is true. The refused command is recorded, and seen (when
        // given) completes at the refusal.
        private static Func<IReadOnlyList<Protocol.Command>, Exception?> RefuseFirst(
            Func<Protocol.Command, bool> matches,
            Exception error,
            List<Protocol.Command> refused,
            TaskCompletionSource<bool>? seen = null) {
            bool[] done = { false };
            return (IReadOnlyList<Protocol.Command> commands) => {
                lock (refused) {
                    foreach (Protocol.Command command in commands) {
                        if (!done[0] && matches(command)) {
                            done[0] = true;
                            refused.Add(command);
                            seen?.TrySetResult(true);
                            return error;
                        }
                    }
                }
                return null;
            };
        }


        // A client whose transports are the real ones, wrapped so that the frames the refusal names fail their send.
        private CentrifugeClient NewRefusingClient(CentrifugeClientOptions? options, Func<IReadOnlyList<Protocol.Command>, Exception?> refuse, bool throwOnRefuse) {
            CentrifugeClient client = NewClient(options);
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => new RpcWireTransport(new WebSocketTransport(endpoint), refuse, throwOnRefuse, null));
            return client;
        }
    }
}
