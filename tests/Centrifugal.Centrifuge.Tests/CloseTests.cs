// Not ported: testSlowCallbackIsCalledOnce and testThrowingCallbackIsCalledOnce: a publish completes one Task, so there is
// no callback to be slow or to throw.
// Not ported: testReplyQueuedBehindBusyExecutorIsNotTimedOut: it needs a handler that blocks the loop while the real call timer runs.
// Not ported: testSubscriptionCallTimeoutCallbackRunsOnClientThread: the subscription publish is not a close test.
// Not ported: CallFutureTest: the future type has no C# counterpart; a TaskCompletionSource settles once.
// The upstream rule is kept: a transport that never opened is cancelled at once by a disconnect, whatever its close does.
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Centrifugal.Centrifuge.Transports;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>Close with a wait, the grace of a transport that is closed, and the calls after close.</summary>
    public sealed class CloseTests : FakeServerTestBase {
        /// <summary>Called from an event handler (inside a step), CloseAsync does not wait and returns false, and the close happens.</summary>
        [Fact]
        public async Task CloseAsync_InsideStep_ReturnsFalseAndCloses() {
            CentrifugeClient client = NewClient();
            Task<bool>? result = null;

            client.Loop.Invoke(() => result = client.CloseAsync(TimeSpan.FromSeconds(5)));

            Assert.NotNull(result);
            Assert.True(result!.IsCompletedSuccessfully);
            Assert.False(await result);
            Assert.Throws<ObjectDisposedException>(() => client.Connect());
        }

        /// <summary>A zero wait returns false, and the close still happens.</summary>
        [Fact]
        public async Task CloseAsync_WithZeroWait_ReturnsFalseAndCloses() {
            CentrifugeClient client = NewClient();

            bool completed = await client.CloseAsync(TimeSpan.Zero);

            Assert.False(completed);
            Assert.Throws<ObjectDisposedException>(() => client.Connect());
        }

        /// <summary>A close with a wait completes within it, and returns true.</summary>
        [Fact]
        public async Task CloseAsync_WithWait_ReturnsTrue() {
            CentrifugeClient client = NewClient();

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
        }

        /// <summary>An infinite wait waits without a timeout, and returns true when the close completes.</summary>
        [Fact]
        public async Task CloseAsync_InfiniteWait_WaitsWithoutATimeout() {
            CentrifugeClient client = NewClient();

            bool completed = await client.CloseAsync(Timeout.InfiniteTimeSpan);

            Assert.True(completed);
        }

        /// <summary>A wait above int.MaxValue milliseconds waits without a timeout instead of throwing, and returns true when the close completes.</summary>
        [Fact]
        public async Task CloseAsync_WaitAboveIntMaxValueMilliseconds_WaitsWithoutATimeout() {
            CentrifugeClient client = NewClient();

            bool completed = await client.CloseAsync(TimeSpan.FromMilliseconds(int.MaxValue + 1000.0));

            Assert.True(completed);
        }

        /// <summary>A connected client closed with CloseAsync disconnects with code 0 and reason "client closed".</summary>
        [Fact]
        public async Task CloseFromConnected_RaisesDisconnectedZeroClientClosed() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));
            CentrifugeDisconnectedEventArgs args = await disconnected;

            Assert.True(completed);
            Assert.Equal(0, args.Code);
            Assert.Equal("client closed", args.Reason);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        /// <summary>A connected client with a server-side subscription raises ServerSubscribing before Disconnected code 0.</summary>
        [Fact]
        public async Task CloseFromConnected_RaisesSubscribingThenDisconnectedZero() {
            CentrifugeClient client = NewClient();
            ConnectResult reply = ConnectReplies.Connect();
            reply.Subs["srv"] = new SubscribeResult();
            Server.ConnectResult = reply;
            ConcurrentQueue<string> events = new ConcurrentQueue<string>();
            client.ServerSubscribing += (_, e) => events.Enqueue("subscribing:" + e.Channel);
            client.Disconnected += (_, e) => events.Enqueue("disconnected:" + e.Code);
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
            Assert.Equal(new[] { "subscribing:srv", "disconnected:0" }, events.ToArray());
        }

        /// <summary>After the close, the ping watchdog of the connection is cancelled: no reconnect and no further events.</summary>
        [Fact]
        public async Task Close_CancelsLiveTimers() {
            Server.ConnectResult = ConnectReplies.Connect(ping: 1);
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers,
                new CentrifugeClientOptions { MaxServerPingDelay = TimeSpan.FromMilliseconds(100) });
            ConcurrentQueue<string> events = new ConcurrentQueue<string>();
            client.Connecting += (_, _) => events.Enqueue("connecting");
            client.Disconnected += (_, _) => events.Enqueue("disconnected");
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));
            events.Clear();
            Assert.True(timers.PendingCount > 0, "the ping watchdog is armed before the close");

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, timers.PendingCount);
            timers.Advance(1600);
            await client.LoopBarrierAsync();

            Assert.True(completed);
            Assert.Equal(new[] { "disconnected" }, events.ToArray());
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        /// <summary>
        /// A transport that never opened is cancelled by the close step at once, although its close never completes. The close
        /// step cancels every grace timer at once, so this test does not show the disconnect path; see
        /// <see cref="Disconnect_NeverOpenedTransportWithHeldClose_IsCancelledAtOnce"/>.
        /// </summary>
        [Fact]
        public async Task Close_NeverOpenedTransportWithHeldClose_IsCancelledAtOnce() {
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers);
            TaskCompletionSource<ForwardingTransport> created =
                new TaskCompletionSource<ForwardingTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => {
                ForwardingTransport transport = new ForwardingTransport(new WebSocketTransport(endpoint), true, true);
                created.TrySetResult(transport);
                return transport;
            });
            client.Connect();
            ForwardingTransport transport = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
            Assert.Equal(1, transport.DisposeCount);
            Assert.Equal(0, timers.PendingWithDelay(1000));
        }

        /// <summary>An opened transport whose close is not answered is cancelled by the close step at once, not after the grace.</summary>
        [Fact]
        public async Task Close_OpenedTransportWithHeldClose_IsCancelledAtOnce() {
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers);
            TaskCompletionSource<ForwardingTransport> created =
                new TaskCompletionSource<ForwardingTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => {
                ForwardingTransport transport = new ForwardingTransport(new WebSocketTransport(endpoint), false, true);
                created.TrySetResult(transport);
                return transport;
            });
            client.Connect();
            ForwardingTransport transport = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await client.ReadyAsync(TimeSpan.FromSeconds(10));

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
            Assert.Equal(1, transport.DisposeCount);
            Assert.Equal(0, timers.PendingWithDelay(1000));
        }

        /// <summary>
        /// An opened transport whose close is not answered is cancelled when the clock passes the 1000 ms grace, and not
        /// before. The disconnect is the path that waits for the grace; the close step does not.
        /// </summary>
        [Fact]
        public async Task Disconnect_OpenedTransportWithHeldClose_IsCancelledWhenTheGraceEnds() {
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers);
            TaskCompletionSource<ForwardingTransport> created =
                new TaskCompletionSource<ForwardingTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => {
                ForwardingTransport transport = new ForwardingTransport(new WebSocketTransport(endpoint), false, true);
                created.TrySetResult(transport);
                return transport;
            });
            client.Connect();
            ForwardingTransport transport = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await client.ReadyAsync(TimeSpan.FromSeconds(10));

            client.Disconnect();
            await client.LoopBarrierAsync();
            Assert.Equal(1, timers.PendingWithDelay(1000));
            Assert.Equal(0, transport.DisposeCount);

            timers.Advance(999);
            await client.LoopBarrierAsync();
            Assert.Equal(0, transport.DisposeCount);

            timers.Advance(1);
            await client.LoopBarrierAsync();
            Assert.Equal(1, transport.DisposeCount);
        }

        /// <summary>
        /// A transport that never opened is cancelled by a disconnect at once, although its close never completes: the dispose
        /// does not wait for the 1000 ms grace, which the close step would not show (see the close test above).
        /// </summary>
        [Fact]
        public async Task Disconnect_NeverOpenedTransportWithHeldClose_IsCancelledAtOnce() {
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers);
            TaskCompletionSource<ForwardingTransport> created =
                new TaskCompletionSource<ForwardingTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => {
                ForwardingTransport transport = new ForwardingTransport(new WebSocketTransport(endpoint), true, true);
                created.TrySetResult(transport);
                return transport;
            });
            client.Connect();
            ForwardingTransport transport = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));

            client.Disconnect();
            await client.LoopBarrierAsync();

            Assert.Equal(1, transport.DisposeCount);
            Assert.Equal(0, timers.PendingWithDelay(1000));
        }

        /// <summary>
        /// An opened transport whose close completes within the grace: the grace timer is cancelled when the close completes, so
        /// no timer is pending once the transport is disposed, and the timer never fires on the closed transport.
        /// </summary>
        [Fact]
        public async Task Disconnect_CloseCompletesWithinTheGrace_CancelsTheGraceTimer() {
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers);
            TaskCompletionSource<ForwardingTransport> created =
                new TaskCompletionSource<ForwardingTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => {
                ForwardingTransport transport = new ForwardingTransport(new WebSocketTransport(endpoint), false, false);
                created.TrySetResult(transport);
                return transport;
            });
            client.Connect();
            ForwardingTransport transport = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await client.ReadyAsync(TimeSpan.FromSeconds(10));

            client.Disconnect();
            // Disposed completes after the close step of the transport, which cancels the grace timer on the loop.
            await transport.Disposed.WaitAsync(TimeSpan.FromSeconds(10));
            await client.LoopBarrierAsync();

            Assert.Equal(0, timers.PendingWithDelay(1000));
        }

        /// <summary>DisposeAsync starts the close, so a later CloseAsync with a wait returns true.</summary>
        [Fact]
        public async Task DisposeAsync_ThenCloseAsync_ReturnsTrue() {
            CentrifugeClient client = NewClient();
            await client.DisposeAsync();

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
        }

        /// <summary>Dispose starts the close, so a later CloseAsync with a wait returns true.</summary>
        [Fact]
        public async Task Dispose_ThenCloseAsync_ReturnsTrue() {
            CentrifugeClient client = NewClient();
            client.Dispose();

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
        }

        /// <summary>
        /// A Disconnected handler that disposes the client during the close joins the close that runs: the Disconnected event is
        /// raised once, and CloseAsync returns true.
        /// </summary>
        [Fact]
        public async Task Dispose_FromDisconnectedHandlerDuringClose_JoinsTheClose() {
            CentrifugeClient client = NewClient();
            int disconnects = 0;
            client.Disconnected += (_, _) => {
                disconnects++;
                client.Dispose();
            };
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));
            await client.LoopBarrierAsync();

            Assert.True(completed);
            Assert.Equal(1, disconnects);
        }

        // Java: testCallWaitingForConnectionFailsOnClose
        /// <summary>A call that waits for the connection gets its failure when the client is closed, and does not wait for ever.</summary>
        [Fact]
        public async Task Publish_WaitingForTheConnection_FailsOnClose() {
            CentrifugeClient client = NewClient();
            Task publish = client.PublishAsync("ch", new byte[] { 1 });

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
            CentrifugeException failure = await Assert.ThrowsAnyAsync<CentrifugeException>(() => publish.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(CentrifugeErrorCodes.ClientDisconnected, failure.Code);
            Assert.IsNotType<CentrifugeTimeoutException>(failure);
        }

        /// <summary>A second close call with a wait waits for the close that already started, and returns true.</summary>
        [Fact]
        public async Task CloseAsync_SecondCall_WaitsForTheFirstClose() {
            CentrifugeClient client = NewClient();
            await client.CloseAsync(TimeSpan.Zero);

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
        }
    }
}
