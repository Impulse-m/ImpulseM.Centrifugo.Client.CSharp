// Cancel of the exactly-once rpc calls: a call that was not sent is never sent, a sent call is cancelled on the server, and a
// cancel the connection cannot carry is kept for the next connection.
// The refused cancel frames are in RpcWriteFailureTests. Not ported: RpcExactlyOnceTest.cancelWhoseSendFailsWithAnErrorIsKeptAndTheRestOfTheFlushGoesOut:
// .NET has no Error that bypasses the Exception handling; a cancel write fault takes the same path as the Exception twin, which is ported in RpcWriteFailureTests.
// Not ported: RpcExactlyOnceTest.failedCancelSendIsReportedAfterTheConnectReplyArmsItsExpiryTimer:
// it observes the timer that the connect reply arms, and no test reaches that timer.
// The upstream rule is kept: a cancel of a call that is done has no effect, and a cancel is never sent without the instance.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The cancel rules of the rpc calls, against the fake server.</summary>
    [Collection("Integration")]
    public sealed class RpcCancelTests : RpcTestBase {
        // Java: RpcExactlyOnceTest.cancelBeforeSendNeverSendsTheCall.
        /// <summary>A call cancelled before it is sent fails with code 17 and is never sent.</summary>
        [Fact]
        public async Task Cancel_BeforeSend_FailsWith17AndIsNeverSent() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });

            CentrifugeRpcCall call = client.RpcCall("method", ReadOnlyMemory<byte>.Empty);
            call.Cancel();
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, error.Code);

            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            Assert.DoesNotContain(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "method");
        }


        // Java: RpcExactlyOnceTest.cancelOfAQueuedCallWhileConnectingIsLocal.
        /// <summary>A call queued while the connect is in flight is cancelled locally: it fails with code 17 and is never sent.</summary>
        [Fact]
        public async Task Cancel_QueuedWhileConnecting_IsLocalAndNeverSent() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Connect != null);
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await Server.AwaitHeldAsync();

            CentrifugeRpcCall call = client.RpcCall("method", ReadOnlyMemory<byte>.Empty);
            call.Cancel();
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, error.Code);
            Server.ReleaseDeferredMatching((Protocol.Command cmd) => cmd.Connect != null);
            await connected;
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            Assert.DoesNotContain(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "method");
        }


        // Java: RpcExactlyOnceTest.cancelWhileDisconnectedIsSentWithTheNextConnect.
        /// <summary>
        /// A sent call that loses its connection is cancelled while disconnected: it fails with code 17 at once, and its cancel
        /// goes out with the next connection, for the msg_id of the call. Real time: the reconnect is an upstream LoopTimer, so its
        /// delays are the smallest the options allow.
        /// </summary>
        [Fact]
        public async Task Cancel_WhileDisconnected_IsSentWithTheNextConnect() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Connect != null || (cmd.Rpc != null && cmd.Rpc.Method == "long"));
            CommandArrivals reconnects = new CommandArrivals(RpcTestHelpers.Wait);
            RpcTestHelpers.CountArrivals(Server, reconnects, (Protocol.Command cmd) => cmd.Connect != null);
            CentrifugeRpcCall call = client.RpcCall("long", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            ulong msgId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "long").Rpc.MsgId;
            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnects.Reached(1);

            call.Cancel();
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, error.Code);
            Server.ReleaseDeferredMatching((Protocol.Command cmd) => cmd.Connect != null);
            await reconnected;
            await RpcTestHelpers.Bounded(client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty));

            Protocol.Command cancel = Assert.Single(RpcTestHelpers.Cancels(Server));
            Assert.Equal(msgId, cancel.Rpc.Cancel);
            Assert.Single(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "long");
        }


        // Java: RpcExactlyOnceTest.abortOfASentCallSendsACancelAndRejectsCancelled.
        /// <summary>Cancelling a sent call fails it with code 17 and sends a cancel with its msg_id and no method.</summary>
        [Fact]
        public async Task Cancel_SentCall_FailsWith17AndSendsACancelWithTheMsgId() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            call.Cancel();
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, error.Code);
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            Protocol.Command sent = Assert.Single(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "hold");
            Protocol.Command cancel = Assert.Single(RpcTestHelpers.Cancels(Server));
            Assert.Equal(sent.Rpc.MsgId, cancel.Rpc.Cancel);
            Assert.Equal(string.Empty, cancel.Rpc.Method);
        }


        // Java: RpcExactlyOnceTest.cancelAfterCompletionHasNoEffect.
        /// <summary>A cancel after the call completed has no effect and sends nothing.</summary>
        [Fact]
        public async Task Cancel_AfterCompletion_SendsNothing() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("method", ReadOnlyMemory<byte>.Empty);
            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(call.Result);
            call.Cancel();
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Empty(RpcTestHelpers.Cancels(Server));
        }


        // Java: RpcExactlyOnceTest.cancelAfterCloseDoesNotThrow.
        /// <summary>A cancel after the client closed does nothing and does not throw.</summary>
        [Fact]
        public async Task Cancel_AfterClose_DoesNotThrow() {
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            CentrifugeRpcCall call = client.RpcCall("method", ReadOnlyMemory<byte>.Empty);
            await client.CloseAsync(TimeSpan.FromSeconds(1));

            call.Cancel();
            await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
        }


        // Java: RpcExactlyOnceTest.cancelWithoutInstanceIsRejectedAndSendsNothing.
        /// <summary>Cancelling a sent call of a client without instance fails with code 15 and sends no cancel.</summary>
        [Fact]
        public async Task Cancel_WithoutInstance_FailsWith15AndSendsNothing() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            call.Cancel();
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));

            Assert.Equal(CentrifugeErrorCodes.Unsupported, error.Code);
            Assert.Equal("rpc cancel needs the instance option", error.Message);
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);
            Assert.Empty(RpcTestHelpers.Cancels(Server));
        }


        // Java: RpcExactlyOnceTest.legacyServerRejectsCancelOfASentCall.
        /// <summary>Cancelling a call sent to a server without RPC options fails with code 15 and sends no cancel.</summary>
        [Fact]
        public async Task Cancel_OnLegacyServer_FailsWith15AndSendsNothing() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            call.Cancel();
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));

            Assert.Equal(CentrifugeErrorCodes.Unsupported, error.Code);
            Assert.Equal("rpc cancel is not supported by the server", error.Message);
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);
            Assert.Empty(RpcTestHelpers.Cancels(Server));
        }


        // Java: RpcExactlyOnceTest.cancelsLostWithTheTransportAreCappedAtTheBound.
        /// <summary>
        /// Forty cancels sent before a transport loss are all on the wire; the kept cancels are capped at maxPendingRpc (32), so the
        /// newest 32 are sent with the next connection, after the 40 first ones.
        /// </summary>
        [Fact]
        public async Task Cancel_KeptCancels_AreCappedAtTheBound() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok", hold: (Protocol.Command cmd) => cmd.Rpc.Cancel != 0);
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            CommandArrivals holds = new CommandArrivals(RpcTestHelpers.Wait);
            CommandArrivals cancelArrivals = new CommandArrivals(RpcTestHelpers.Wait);
            RpcTestHelpers.CountArrivals(Server, holds, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            RpcTestHelpers.CountArrivals(Server, cancelArrivals, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Cancel != 0);
            CentrifugeClient client = NewClientWithTestClock(out _, new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                MaxPendingRpc = 32,
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            for (int i = 0; i < 40; i++) {
                CentrifugeRpcCall call = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
                await holds.Reached(i + 1);
                call.Cancel();
                await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            }
            await cancelArrivals.Reached(40);
            List<ulong> firstCancels = RpcTestHelpers.Cancels(Server).Select((Protocol.Command cmd) => cmd.Rpc.Cancel).ToList();
            Assert.Equal(40, firstCancels.Count);

            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnected;
            await RpcTestHelpers.Bounded(client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty));

            List<ulong> all = RpcTestHelpers.Cancels(Server).Select((Protocol.Command cmd) => cmd.Rpc.Cancel).ToList();
            Assert.Equal(72, all.Count);
            Assert.Equal(firstCancels.Skip(8).ToArray(), all.Skip(40).ToArray());
        }


        // Java: RpcExactlyOnceTest.unansweredCancelIsDroppedAfterTheTimeoutAndNotSentAgain.
        /// <summary>
        /// A cancel without a reply is dropped after one client timeout and is not sent again. Real time: the reconnect is an
        /// upstream LoopTimer, so its delays are the smallest the options allow.
        /// </summary>
        [Fact]
        public async Task Cancel_WithoutReplyAfterTheTimeout_IsNotSentAgain() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok", hold: (Protocol.Command cmd) => cmd.Rpc.Cancel != 0);
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                Timeout = TimeSpan.FromMilliseconds(200),
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty, new CentrifugeRpcOptions { Timeout = TimeSpan.FromSeconds(30) });
            await Server.AwaitHeldAsync();
            call.Cancel();
            await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);
            timers.Advance(200);
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnected;
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            Assert.Single(RpcTestHelpers.Cancels(Server));
        }


        // Java: RpcExactlyOnceTest.cancelWithoutReplyIsKeptAcrossTransportLoss.
        /// <summary>A cancel whose reply did not come is sent once more with the next connection, with the same msg_id.</summary>
        [Fact]
        public async Task Cancel_WithoutReply_IsKeptAcrossTheTransportLoss() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok", hold: (Protocol.Command cmd) => cmd.Rpc.Cancel != 0);
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            call.Cancel();
            await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnected;
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            List<ulong> msgIds = RpcTestHelpers.Cancels(Server).Select((Protocol.Command cmd) => cmd.Rpc.Cancel).ToList();
            Assert.Equal(2, msgIds.Count);
            Assert.Equal(msgIds[0], msgIds[1]);
        }


        // The reply to a cancel command sets the server time, like the reply to any pending command.
        /// <summary>The reply to a cancel command sets the server time: the next msg_id takes its time part from it.</summary>
        [Fact]
        public async Task Cancel_ReplySetsTheServerTime() {
            EnableRpcOptions();
            long localMillis = 1_700_000_000_000L;
            long serverMillis = localMillis + 60_000L;
            // Only the cancel reply carries the server time: the other replies would set it too.
            Server.OnCommand = (Protocol.Command cmd) => {
                if (cmd.Rpc == null) {
                    return null;
                }
                long time = cmd.Rpc.Cancel != 0 ? serverMillis : 0;
                return new Protocol.Reply { Id = cmd.Id, Time = time, Rpc = new Protocol.RPCResult() };
            };
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            client.WallClockMs = () => localMillis;
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            call.Cancel();
            await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            // The barrier is answered after the cancel reply, so the offset is set by then.
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);
            await client.RpcAsync("next", ReadOnlyMemory<byte>.Empty);

            ulong msgId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "next").Rpc.MsgId;
            Assert.Equal((ulong)(serverMillis / 1000), msgId >> MsgIdGenerator.SequenceBits);
        }
    }
}
