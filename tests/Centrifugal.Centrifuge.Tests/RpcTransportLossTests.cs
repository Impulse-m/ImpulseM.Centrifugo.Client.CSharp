// Lost transport and terminal loss of the exactly-once rpc calls: a tracked call is sent again with its msg_id after a
// reconnect, a plain call fails with the transport, and a terminal loss (disconnect or close) fails the calls for good.
// Not ported: RpcExactlyOnceTest.lateReplyToASettledCallIsAckedWithTheNextCall and the late-reply tests of ServerTimeTest:
// they need a reply that arrives after the deadline, which the fake server cannot time against the real loop.
// The upstream rule is kept: a plain call is never sent again after the transport was lost.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;
using Google.Protobuf;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The loss of the transport and the terminal loss of the rpc calls, against the fake server.</summary>
    [Collection("Integration")]
    public sealed class RpcTransportLossTests : RpcTestBase {
        // Java: RpcExactlyOnceTest.droppedCallRunsOnceAndTheResendGetsTheSameReply.
        /// <summary>
        /// A call whose reply was lost is sent again with the same msg_id as a retransmit. The server runs the call once and
        /// answers the resend from its record: the payload is the same, and the answer is cached.
        /// </summary>
        [Fact]
        public async Task RpcAsync_LostReply_RunsOnceAndTheResendGetsTheSameReply() {
            EnableRpcOptions();
            int[] executions = { 0 };
            Server.OnCommand = RpcTestHelpers.Serve("{}", cachedOnRetransmit: true);
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && !cmd.Rpc.Retransmit && cmd.Rpc.Method == "method");
            Func<Protocol.Command, Protocol.Reply?> answer = Server.OnCommand!;
            Server.OnCommand = (Protocol.Command cmd) => {
                if (cmd.Rpc != null && !cmd.Rpc.Retransmit) {
                    Interlocked.Increment(ref executions[0]);
                }
                return answer(cmd);
            };
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeRpcResult> call = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnected;
            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(call);

            Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.True(result.Cached);
            Assert.Equal(1, Volatile.Read(ref executions[0]));
            List<Protocol.Command> requests = RpcTestHelpers.Requests(Server).Where((Protocol.Command cmd) => cmd.Rpc.Method == "method").ToList();
            Assert.Equal(2, requests.Count);
            Assert.Equal(requests[0].Rpc.MsgId, requests[1].Rpc.MsgId);
            Assert.False(requests[0].Rpc.Retransmit);
            Assert.True(requests[1].Rpc.Retransmit);
        }


        // Java: CallSettleTest.testReplyQueuedBehindBusyExecutorIsNotTimedOut (the rpc deadline on the fake clock stands for the
        // publish timeout: the deadline is a step of the loop, as the reply is, so the wait for the handler is a latch, not a sleep).
        /// <summary>
        /// A reply that arrives while a handler holds the loop is delivered: the deadline does not fail a call whose reply is
        /// already queued behind the handler.
        /// </summary>
        [Fact]
        public async Task RpcAsync_ReplyQueuedBehindABlockedHandler_IsNotTimedOut() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "method");
            ManualResetEventSlim gate = new ManualResetEventSlim(false);
            TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> replyReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance, Timeout = TimeSpan.FromMilliseconds(300) });
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => new RpcWireTransport(
                new WebSocketTransport(endpoint),
                null,
                false,
                (IReadOnlyList<byte[]> messages) => {
                    if (RpcWireTransport.CarriesRpcResult(messages)) {
                        replyReceived.TrySetResult(true);
                    }
                }));
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeRpcResult> call = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            uint commandId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "method").Id;

            // A step that holds the loop runs on a pool thread, so the reader thread keeps receiving frames.
            Task busy = Task.Run(() => client.Loop.Execute(() => {
                entered.TrySetResult(true);
                gate.Wait(RpcTestHelpers.Wait);
            }));
            await RpcTestHelpers.Bounded(entered.Task);

            // The reply is handed to the loop behind the busy step, and then the deadline is due.
            await RpcTestHelpers.AnswerAsync(Server, commandId, "ok");
            await RpcTestHelpers.Bounded(replyReceived.Task);
            timers.Advance(300);
            gate.Set();
            await busy;

            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(call);
            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Empty(RpcTestHelpers.Cancels(Server));
        }


        // Java: RpcExactlyOnceTest.lostCallsAreSentAgainInMsgIdOrder.
        /// <summary>Three calls lost with their transport are sent again with their msg_ids, in msg_id order.</summary>
        [Fact]
        public async Task RpcAsync_LostCalls_AreSentAgainInMsgIdOrder() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && !cmd.Rpc.Retransmit && cmd.Rpc.Method == "held");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            List<Task<CentrifugeRpcResult>> calls = new List<Task<CentrifugeRpcResult>>();
            for (int i = 0; i < 3; i++) {
                calls.Add(client.RpcAsync("held", ReadOnlyMemory<byte>.Empty));
            }
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);
            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnected;
            await RpcTestHelpers.Bounded(Task.WhenAll(calls));

            List<Protocol.Command> held = RpcTestHelpers.Requests(Server).Where((Protocol.Command cmd) => cmd.Rpc.Method == "held").ToList();
            Assert.Equal(6, held.Count);
            List<ulong> firstSends = held.Take(3).Select((Protocol.Command cmd) => cmd.Rpc.MsgId).ToList();
            List<ulong> resends = held.Skip(3).Select((Protocol.Command cmd) => cmd.Rpc.MsgId).ToList();
            Assert.Equal(firstSends, resends);
            Assert.Equal(firstSends.OrderBy((ulong msgId) => msgId).ToList(), resends);
            Assert.All(held.Skip(3), (Protocol.Command cmd) => Assert.True(cmd.Rpc.Retransmit));
        }


        // Java: RpcExactlyOnceTest.legacyCallLostWithItsTransportFails and lostCallWithoutInstanceFailsWithItsTransport.
        /// <summary>A plain call lost with its transport fails with code 11 and is not sent again.</summary>
        [Fact]
        public async Task RpcAsync_PlainCallLostWithItsTransport_FailsWith11AndIsNotSentAgain() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "held");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeRpcResult> call = client.RpcAsync("held", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            Server.CloseConnection();

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call));
            Assert.Equal(CentrifugeErrorCodes.ConnectionClosed, error.Code);
            Assert.Single(RpcTestHelpers.Requests(Server));
        }


        // Java: RpcExactlyOnceTest.callQueuedWhileDisconnectedIsCompletedByClose.
        /// <summary>Closing the client fails a call that was never written with code 17 and sends nothing.</summary>
        [Fact]
        public async Task Close_CallQueuedWhileDisconnected_FailsWith17AndSendsNothing() {
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            CentrifugeRpcCall call = client.RpcCall("method", ReadOnlyMemory<byte>.Empty);

            await client.CloseAsync(TimeSpan.FromSeconds(1));

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, error.Code);
        }


        // Java: RpcExactlyOnceTest.callQueuedBeforeConnectIsCancelledByDisconnectForGood.
        /// <summary>Disconnect for good fails a call that was never written with code 17; a later connect does not send it.</summary>
        [Fact]
        public async Task Disconnect_CallQueuedBeforeConnect_FailsWith17AndIsNotSentByALaterConnect() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            CentrifugeRpcCall call = client.RpcCall("method", ReadOnlyMemory<byte>.Empty);

            client.Disconnect();
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, error.Code);

            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            Assert.DoesNotContain(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "method");
        }


        // Java: RpcExactlyOnceTest.writtenCallRejectedByDisconnectForGoodKeepsItsCancelForTheNextConnect.
        /// <summary>A written call fails with the transport on a disconnect for good; its cancel is sent with the next connection and the call is not sent again.</summary>
        [Fact]
        public async Task Disconnect_WrittenCall_KeepsItsCancelForTheNextConnect() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "held");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("held", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            ulong msgId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "held").Rpc.MsgId;
            client.Disconnect();
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.ConnectionClosed, error.Code);

            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            client.Connect();
            await reconnected;
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            Protocol.Command cancel = Assert.Single(RpcTestHelpers.Cancels(Server));
            Assert.Equal(msgId, cancel.Rpc.Cancel);
            Assert.Single(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "held");
        }


        // A call that was lost with its transport, then ended by a disconnect for good, fails without a cancel.
        /// <summary>A call lost with its transport and then ended by a disconnect for good fails with code 11 and sends no cancel.</summary>
        [Fact]
        public async Task Disconnect_QueuedCallWithWrittenHistory_FailsWithoutACancel() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "held");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("held", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            TaskCompletionSource<bool> connecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Connecting += (object? sender, CentrifugeConnectingEventArgs args) => connecting.TrySetResult(true);
            Server.CloseConnection();
            await RpcTestHelpers.Bounded(connecting.Task);

            client.Disconnect();
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.ConnectionClosed, error.Code);

            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            client.Connect();
            await reconnected;
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);

            Assert.Empty(RpcTestHelpers.Cancels(Server));
        }


        // Java: RpcExactlyOnceTest.rpcAfterCloseThrowsAndItsCallbackNeverFires.
        /// <summary>After close, a call is refused: its task faults with ObjectDisposedException and no call is queued.</summary>
        [Fact]
        public async Task RpcAsync_AfterClose_FaultsWithObjectDisposed() {
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            await client.CloseAsync(TimeSpan.FromSeconds(1));

            await Assert.ThrowsAsync<ObjectDisposedException>(() => RpcTestHelpers.Bounded(client.RpcAsync("method", ReadOnlyMemory<byte>.Empty)));
        }


        // Java: RpcExactlyOnceTest.rpcStartedAfterCloseFailsAtOnce.
        /// <summary>
        /// A call made before close, whose submit step runs after close, fails at once with code 17 and sends nothing. A step
        /// holds the loop on a pool thread, so the call and the close both queue behind it.
        /// </summary>
        [Fact]
        public async Task Close_CallStartedAfterClose_FailsWith17AndSendsNothing() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            ManualResetEventSlim gate = new ManualResetEventSlim(false);
            TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task busy = Task.Run(() => client.Loop.Execute(() => {
                entered.TrySetResult(true);
                gate.Wait(RpcTestHelpers.Wait);
            }));
            await RpcTestHelpers.Bounded(entered.Task);

            CentrifugeRpcCall call = client.RpcCall("method", ReadOnlyMemory<byte>.Empty);
            Task<bool> closing = client.CloseAsync(TimeSpan.FromSeconds(1));
            gate.Set();
            await busy;

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, error.Code);
            await closing.WaitAsync(RpcTestHelpers.Wait);
            Assert.Empty(RpcTestHelpers.Requests(Server));
        }


        // Java: RpcExactlyOnceTest.acksOfACommandLostWithItsTransportAreSentAgain.
        /// <summary>The acks of a lost request are sent again with its retransmit.</summary>
        [Fact]
        public async Task RpcAsync_AcksOfALostRequest_AreSentAgainWithTheRetransmit() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "second" && !cmd.Rpc.Retransmit);
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await client.RpcAsync("first", ReadOnlyMemory<byte>.Empty);
            Task<CentrifugeRpcResult> second = client.RpcAsync("second", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            ulong firstMsgId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "first").Rpc.MsgId;
            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnected;
            await RpcTestHelpers.Bounded(second);

            Protocol.Command resend = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "second" && cmd.Rpc.Retransmit);
            Assert.Equal(new[] { firstMsgId }, resend.Rpc.Ack.ToArray());
        }


        // Java: RpcExactlyOnceTest.deadlineRejectsACallLostWhileDisconnected.
        /// <summary>
        /// A sent call that loses its connection, and whose reconnect is held, fails with a timeout at its deadline on the
        /// client clock. It is not sent again: the only request is the first one, and the cancel waits for the next connection.
        /// Real time: the reconnect is an upstream LoopTimer, so its delays are the smallest the options allow.
        /// </summary>
        [Fact]
        public async Task RpcAsync_LostWhileDisconnected_TimesOutAtItsDeadlineAndIsNotSentAgain() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Connect != null || (cmd.Rpc != null && cmd.Rpc.Method == "slow"));
            CommandArrivals reconnects = new CommandArrivals(RpcTestHelpers.Wait);
            RpcTestHelpers.CountArrivals(Server, reconnects, (Protocol.Command cmd) => cmd.Connect != null);
            CentrifugeRpcCall call = client.RpcCall("slow", ReadOnlyMemory<byte>.Empty, new CentrifugeRpcOptions { Timeout = TimeSpan.FromMilliseconds(300) });
            await Server.AwaitHeldAsync();
            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnects.Reached(1);

            timers.Advance(300);

            await Assert.ThrowsAsync<CentrifugeTimeoutException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Single(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "slow");
            Server.ReleaseDeferredMatching((Protocol.Command cmd) => cmd.Connect != null);
            await reconnected;
            await RpcTestHelpers.Bounded(client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty));

            Assert.Single(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "slow");
            Protocol.Command cancel = Assert.Single(RpcTestHelpers.Cancels(Server));
            Assert.Equal(RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "slow").Rpc.MsgId, cancel.Rpc.Cancel);
        }


        /// <summary>A call that waits for a connection past its deadline fails with a timeout on the client clock.</summary>
        [Fact]
        public async Task RpcAsync_WaitingForTheConnectionPastItsDeadline_FailsWithTimeout() {
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance, Timeout = TimeSpan.FromMilliseconds(300) });
            CentrifugeRpcCall call = client.RpcCall("method", ReadOnlyMemory<byte>.Empty);

            timers.Advance(300);

            await Assert.ThrowsAsync<CentrifugeTimeoutException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Contains(300, timers.RequestedDelays);
        }


        // Java: RpcExactlyOnceTest.deadlineOfASentCallFiresOnTheClientClockAndCancelsIt.
        /// <summary>The deadline of a sent call is a timer of the client clock: at its expiry the call times out and a cancel is sent.</summary>
        [Fact]
        public async Task Deadline_OfASentCall_FiresOnTheClientClockAndCancelsIt() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "held");
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance, Timeout = TimeSpan.FromMilliseconds(300) });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("held", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            timers.Advance(299);
            Assert.False(call.Result.IsCompleted);
            timers.Advance(1);

            await Assert.ThrowsAsync<CentrifugeTimeoutException>(() => RpcTestHelpers.Bounded(call.Result));
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);
            Assert.Single(RpcTestHelpers.Cancels(Server));
            Assert.Contains(300, timers.RequestedDelays);
        }
    }
}
