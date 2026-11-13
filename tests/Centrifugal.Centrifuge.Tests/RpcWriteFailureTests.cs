// The failed writes of the rpc calls: a frame the transport refuses, a send that throws, a refused first write of a tracked
// call (it takes a new msg_id, and its acks come back), and a refused cancel (it is reported and kept for the next connection).
// The refusal happens in a test-local transport wrapper, so the fake server never sees a refused frame.
// The client writes the calls queued in one step as one frame, so a refused frame fails the calls in it: each test here
// refuses a frame that holds one call.
// The upstream rule is kept: a write that throws fails its call with the write error (code 10) and keeps the cause.
// Not ported: RpcExactlyOnceTest.callWhoseSendFailsWithAnErrorFailsAloneAndTheRestOfTheFlushGoesOut, for its second and third calls: the
// wire transport refuses a whole frame, and the calls queued in one step share one frame, so the calls behind the refused one cannot be
// kept by the wire transport. The Error variant is not ported either: .NET has no Error that bypasses the Exception handling.

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
        /// <summary>
        /// A plain call whose frame is refused fails alone with code 10 and its cause. The call queued after it, started once the
        /// refusal has failed, is sent. The calls queued in the same flush as the refused one are not covered here (see the header).
        /// </summary>
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


        /// <summary>
        /// A call started through RpcCall whose frame is refused fails with CentrifugeConnectionLostException: the same code and cause
        /// as the plain call, so that the rpc pool recognises the loss by its type.
        /// </summary>
        [Fact]
        public async Task RpcCall_RefusedFrame_FailsWithTheConnectionLostType() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            List<Protocol.Command> refused = new List<Protocol.Command>();
            IOException error = new IOException("broken pipe");
            CentrifugeClient client = NewRefusingClient(null, RefuseFirst((Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "first", error, refused), false);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("first", ReadOnlyMemory<byte>.Empty);
            CentrifugeConnectionLostException failure = await Assert.ThrowsAsync<CentrifugeConnectionLostException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.TransportWriteError, failure.Code);
            Assert.Same(error, failure.InnerException);
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
            // The msg_id of the first call is set once the calls are on the wire: only the cancel of that call is refused.
            ulong[] firstMsgId = { 0 };
            CentrifugeClient client = NewRefusingClient(
                new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance },
                RefuseFirst((Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Cancel != 0 && cmd.Rpc.Cancel == firstMsgId[0], new InvalidOperationException("cancel send failed"), refused),
                true);
            TaskCompletionSource<CentrifugeErrorEventArgs> reported = new TaskCompletionSource<CentrifugeErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Error += (object? sender, CentrifugeErrorEventArgs args) => reported.TrySetResult(args);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Connect != null || (cmd.Rpc != null && cmd.Rpc.Method == "hold"));
            CommandArrivals holds = new CommandArrivals(RpcTestHelpers.Wait);
            CommandArrivals reconnects = new CommandArrivals(RpcTestHelpers.Wait);
            RpcTestHelpers.CountArrivals(Server, holds, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            RpcTestHelpers.CountArrivals(Server, reconnects, (Protocol.Command cmd) => cmd.Connect != null);
            CentrifugeRpcCall first = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            CentrifugeRpcCall second = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await holds.Reached(2);
            List<ulong> msgIds = RpcTestHelpers.Requests(Server).Where((Protocol.Command cmd) => cmd.Rpc.Method == "hold").Select((Protocol.Command cmd) => cmd.Rpc.MsgId).ToList();
            firstMsgId[0] = msgIds[0];
            // The second cancel is written and answered while connected. The barrier is answered after that cancel's reply on the same
            // socket, so the second cancel is settled before the refused one drops the connection.
            second.Cancel();
            CentrifugeException cancelledSecond = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(second.Result));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, cancelledSecond.Code);
            await RpcTestHelpers.Bounded(client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty));
            first.Cancel();
            CentrifugeException cancelled = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(first.Result));
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
            Assert.Equal(msgIds[0], refused[0].Rpc.Cancel);
            // The second cancel went out on the first connection; the refused one is kept and sent with the next connection.
            List<ulong> sent = RpcTestHelpers.Cancels(Server).Select((Protocol.Command cmd) => cmd.Rpc.Cancel).ToList();
            Assert.Equal(2, sent.Count);
            Assert.Equal(msgIds.OrderBy((ulong msgId) => msgId).ToArray(), sent.OrderBy((ulong msgId) => msgId).ToArray());
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
