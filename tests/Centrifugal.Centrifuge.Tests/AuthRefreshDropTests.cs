// The connection token refresh across a disconnect, a transport loss and a SetToken: a refresh that dies with its
// transport keeps the token of the accepted connect, a timer fetch that ends while the client is not connected gives the
// token of the next connect, and a timer fetch that ends after a disconnect or a SetToken is dropped. The fetches are
// held by a task the test completes. The transport-loss tests hold the reconnect at the fake server (BeforeAccept) and
// use a one-millisecond reconnect delay, so the order of the steps is fixed by the test.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the connection token across disconnects, transport losses and SetToken.</summary>
    public sealed class AuthRefreshDropTests : FakeServerTestBase {
        // The renewal delay of a token with a ttl of 100 seconds: 95 percent of it, in milliseconds.
        private const int RenewalDelayMs = 95000;

        // The timeout of a token fetch with the default options, in milliseconds: the fetch timer has this delay.
        private const int FetchTimeoutMs = 5000;

        // Java: refusedRefreshKeepsTheIdentityAndTheToken. A refusal with code 103 keeps the connection, its identity and
        // its token: the reconnect carries the token of the connect.
        [Fact]
        public async Task RefreshAsync_RefusedWithCode103_KeepsTheIdentityAndTheToken() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1", ping: 25);
            Server.OnCommand = (Command command) => command.Refresh != null
                ? new Reply { Id = command.Id, Error = new Error { Code = 103, Message = "permission denied" } }
                : null;
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Token = "token-1" });
            List<string> authEvents = new List<string>();
            client.Auth += (object? sender, CentrifugeAuthEventArgs args) => authEvents.Add(args.User + "/" + args.AuthClass);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => client.RefreshAsync("token-2"));
            Assert.Equal(103, error.Code);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal(new[] { "u1/" }, authEvents.ToArray());

            Task reconnected = WhenConnected(client);
            client.Disconnect();
            await client.LoopBarrierAsync();
            client.Connect();
            await reconnected;
            Assert.Equal("token-1", RefreshTestEvents.ConnectTokens(Server).Last());
        }


        // Java: refusedRefreshAfterAnAcceptedConnectKeepsTheConnectedToken. Two refusals in a row, while connected, leave
        // the token of the accepted connect in place for the next connect.
        [Fact]
        public async Task RefreshAsync_RefusedTwiceAfterAnAcceptedConnect_KeepsTheConnectedToken() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1", ping: 25);
            Server.OnCommand = (Command command) => command.Refresh != null
                ? new Reply { Id = command.Id, Error = new Error { Code = 103, Message = "permission denied" } }
                : null;
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Token = "token-1" });
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            await Assert.ThrowsAsync<CentrifugeException>(() => client.RefreshAsync("token-2"));
            await Assert.ThrowsAsync<CentrifugeException>(() => client.RefreshAsync("token-3"));

            Task reconnected = WhenConnected(client);
            client.Disconnect();
            await client.LoopBarrierAsync();
            client.Connect();
            await reconnected;
            Assert.Equal("token-1", RefreshTestEvents.ConnectTokens(Server).Last());
        }


        // Java: refreshWhileDisconnectedSetsTheTokenOfTheNextConnect. A refresh made while the connect with the old token is
        // still in flight is the token of the next connect: the connect with the old token is refused with code 109 (token
        // expired), the next connect carries the refresh token, and the refresh is sent once that connect is up.
        [Fact]
        public async Task RefreshAsync_WhileDisconnected_SetsTheTokenOfTheNextConnect() {
            RefreshArrivals arrivals = new RefreshArrivals();
            TaskCompletionSource connectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1");
            Server.BeforeCommand = (Command command) => command.Connect != null && command.Connect.Token == "token-1"
                ? connectGate.Task
                : Task.CompletedTask;
            Server.OnCommand = (Command command) => {
                if (command.Connect != null && command.Connect.Token == "token-1") {
                    return new Reply { Id = command.Id, Error = new Error { Code = 109, Message = "token expired" } };
                }
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") };
            };
            CentrifugeClient client = NewClient(ReconnectingOptions("token-1"));
            Task connected = WhenConnected(client);
            client.Connect();
            await RefreshTestEvents.UntilAsync(client, () => Server.Received.Any((Command received) => received.Connect != null));

            Task<CentrifugeAuthResult> refresh = client.RefreshAsync("token-2");
            await client.LoopBarrierAsync();
            connectGate.SetResult();

            await connected;
            Assert.Equal("u2", (await refresh.WaitAsync(TimeSpan.FromSeconds(5))).User);
            Assert.Equal(new[] { "token-1", "token-2" }, RefreshTestEvents.ConnectTokens(Server).ToArray());
            Assert.Equal(new[] { "token-2" }, arrivals.Tokens());
        }


        // Java: refreshQueuedBehindAFetchThatEndsWhileDisconnectedIsSentWithTheNextConnect. A refresh queued behind a timer
        // fetch is sent with the next connect when the fetch ends while the client is not connected: the fetched token is
        // dropped, and the refresh goes out once the connect is up.
        [Fact]
        public async Task RefreshAsync_QueuedBehindATimerFetchEndingWhileDisconnected_IsSentWithTheNextConnect() {
            RefreshArrivals arrivals = new RefreshArrivals();
            TaskCompletionSource<string> fetch = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource acceptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return command.Refresh.Token == "token-2"
                    ? new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") }
                    : FakeCentrifugoServer.NoReply;
            };
            CentrifugeClientOptions options = ReconnectingOptions("token-initial");
            options.GetToken = () => {
                Interlocked.Increment(ref calls);
                return fetch.Task;
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            await RefreshTestEvents.UntilAsync(client, () => Volatile.Read(ref calls) == 1);
            Server.BeforeAccept = () => acceptGate.Task;
            Task reconnected = WhenConnected(client);
            Server.CloseConnection();
            await RefreshTestEvents.UntilAsync(client, () => client.State == CentrifugeClientState.Connecting);

            Task<CentrifugeAuthResult> refresh = client.RefreshAsync("token-2");
            await client.LoopBarrierAsync();
            int requestedBefore = timers.RequestedDelays.Count;
            fetch.SetResult("token-timer");
            // The fetch ends and its queued refresh starts: its deadline is the only timer that step requests.
            await RefreshTestEvents.UntilAsync(client, () => timers.RequestedDelays.Count > requestedBefore);
            Assert.Empty(arrivals.Tokens());
            Assert.False(refresh.IsCompleted);

            acceptGate.SetResult();
            await reconnected;
            CentrifugeAuthResult result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("u2", result.User);
            Assert.Equal("token-2", RefreshTestEvents.ConnectTokens(Server).Last());
            Assert.Equal(new[] { "token-2" }, arrivals.Tokens());
        }


        // A refresh made while disconnected waits for the next connect. An explicit Disconnect does not fail it: the refresh
        // is still pending after the disconnect, and the next connect sends it with its token.
        [Fact]
        public async Task RefreshAsync_WaitingForAConnectThenDisconnect_IsNotFailed() {
            RefreshArrivals arrivals = new RefreshArrivals();
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") };
            };
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Token = "token-1" });
            Task<CentrifugeAuthResult> refresh = client.RefreshAsync("token-2");

            client.Disconnect();
            await client.LoopBarrierAsync();
            Assert.False(refresh.IsCompleted);
            Assert.Empty(arrivals.Tokens());

            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Assert.Equal("u2", (await refresh.WaitAsync(TimeSpan.FromSeconds(5))).User);
            Assert.Equal(new[] { "token-2" }, arrivals.Tokens());
        }


        // Java: tokenOfAnAcceptedConnectSurvivesADroppedRefresh. The refresh is in flight when the transport is lost: it fails,
        // and the reconnect carries the token of the accepted connect.
        [Fact]
        public async Task RefreshAsync_DroppedWithItsTransport_KeepsTheTokenOfAnAcceptedConnect() {
            RefreshArrivals arrivals = new RefreshArrivals();
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return FakeCentrifugoServer.NoReply;
            };
            CentrifugeClient client = NewClient(ReconnectingOptions("token-a"));
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeAuthResult> refresh = client.RefreshAsync("token-b");
            await arrivals.Nth(0);
            Task reconnected = WhenConnected(client);
            Server.CloseConnection();
            await Assert.ThrowsAnyAsync<CentrifugeException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(5)));
            await reconnected;

            Assert.Equal("token-a", RefreshTestEvents.ConnectTokens(Server).Last());
        }


        // Java: droppedRefreshKeepsTheQueuedTokenOfAnotherRefresh. The refresh in flight dies with its transport; the queued
        // refresh is sent after the reconnect, with its own token.
        [Fact]
        public async Task RefreshAsync_DroppedInFlight_KeepsTheQueuedTokenOfAnotherRefresh() {
            RefreshArrivals arrivals = new RefreshArrivals();
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return command.Refresh.Token == "token-a"
                    ? FakeCentrifugoServer.NoReply
                    : new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") };
            };
            CentrifugeClient client = NewClient(ReconnectingOptions("token-initial"));
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeAuthResult> first = client.RefreshAsync("token-a");
            Task<CentrifugeAuthResult> second = client.RefreshAsync("token-b");
            await arrivals.Nth(0);
            Task reconnected = WhenConnected(client);
            Server.CloseConnection();
            await Assert.ThrowsAnyAsync<CentrifugeException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            await reconnected;

            Assert.Equal("u2", (await second.WaitAsync(TimeSpan.FromSeconds(5))).User);
            Assert.Equal(new[] { "token-a", "token-b" }, arrivals.Tokens());
        }


        // Java: refusedRefreshKeepsATokenSetWhileItWasInFlight. A refresh made while disconnected stores its token for the
        // next connect; a token the application sets meanwhile is kept when the server refuses the refresh.
        [Fact]
        public async Task RefreshAsync_RefusedAfterASetToken_KeepsTheSetToken() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1");
            Server.OnCommand = (Command command) => command.Refresh != null
                ? new Reply { Id = command.Id, Error = new Error { Code = 103, Message = "permission denied" } }
                : null;
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Token = "token-1" });
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            client.Disconnect();
            await client.LoopBarrierAsync();

            Task<CentrifugeAuthResult> refresh = client.RefreshAsync("token-2");
            client.SetToken("token-set");
            await client.LoopBarrierAsync();
            Task reconnected = WhenConnected(client);
            client.Connect();
            await reconnected;

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(103, error.Code);
            Task again = WhenConnected(client);
            client.Disconnect();
            await client.LoopBarrierAsync();
            client.Connect();
            await again;
            Assert.Equal("token-set", RefreshTestEvents.ConnectTokens(Server).Last());
        }


        // Java: timerTokenFetchedAcrossAnExplicitDisconnectIsDropped. The fetch ends after the disconnect: its token is neither
        // sent nor kept, and the next connect carries the token of the client.
        [Fact]
        public async Task TimerFetch_EndingAfterAnExplicitDisconnect_IsDropped() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            TaskCompletionSource<string> fetch = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => {
                    Interlocked.Increment(ref calls);
                    return fetch.Task;
                },
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            await RefreshTestEvents.UntilAsync(client, () => Volatile.Read(ref calls) == 1);
            client.Disconnect();
            await client.LoopBarrierAsync();
            fetch.SetResult("token-2");
            await RefreshTestEvents.UntilAsync(client, () => timers.PendingWithDelay(FetchTimeoutMs) == 0);

            Task reconnected = WhenConnected(client);
            client.Connect();
            await reconnected;
            Assert.Equal("token-initial", RefreshTestEvents.ConnectTokens(Server).Last());
            Assert.Empty(arrivals.Tokens());
        }


        // Java: timerTokenFetchedAcrossSetTokenIsDropped. The client is disconnected when the fetch ends; a SetToken meanwhile
        // makes the answer stale, so it is dropped and the next connect carries the set token.
        [Fact]
        public async Task TimerFetch_EndingAfterASetToken_IsDropped() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            TaskCompletionSource<string> fetch = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => {
                    Interlocked.Increment(ref calls);
                    return fetch.Task;
                },
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            await RefreshTestEvents.UntilAsync(client, () => Volatile.Read(ref calls) == 1);
            client.Disconnect();
            client.SetToken("token-set");
            await client.LoopBarrierAsync();
            fetch.SetResult("token-2");
            await RefreshTestEvents.UntilAsync(client, () => timers.PendingWithDelay(FetchTimeoutMs) == 0);

            Task reconnected = WhenConnected(client);
            client.Connect();
            await reconnected;
            Assert.Equal("token-set", RefreshTestEvents.ConnectTokens(Server).Last());
            Assert.Empty(arrivals.Tokens());
        }


        // Java: timerTokenFetchedAcrossSetTokenWhileConnectedIsDropped. A SetToken while connected drops the fetch in flight;
        // the timer is armed again at once, and the next fetch is the one that is sent. The stale token is never sent.
        [Fact]
        public async Task TimerFetch_EndingAfterASetTokenWhileConnected_IsDroppedAndFetchedAgain() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            TaskCompletionSource<string> stale = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Interlocked.Increment(ref calls) == 1
                    ? stale.Task
                    : Task.FromResult("token-fresh"),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            await RefreshTestEvents.UntilAsync(client, () => Volatile.Read(ref calls) == 1);
            client.SetToken("token-set");
            await client.LoopBarrierAsync();
            stale.SetResult("token-stale");
            await RefreshTestEvents.UntilAsync(client, () => timers.PendingWithDelay(0) == 1);
            Assert.Equal(1, timers.PendingWithDelay(0));

            timers.Advance(0);
            Command command = await arrivals.Nth(0);
            Assert.Equal("token-fresh", command.Refresh.Token);
            Assert.Equal(new[] { "token-fresh" }, arrivals.Tokens());
        }


        // Java: timerTokenFetchedWhileDisconnectedIsTheTokenOfTheNextConnect. The transport is lost while the fetch is in
        // flight, and the reconnect is held at the server. The fetch then ends while the client is not connected: its token
        // is the token of the next connect, and nothing is sent.
        [Fact]
        public async Task TimerFetch_EndingWhileDisconnected_IsTheTokenOfTheNextConnect() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            TaskCompletionSource<string> fetch = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource acceptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            CentrifugeClientOptions options = ReconnectingOptions("token-initial");
            options.GetToken = () => {
                Interlocked.Increment(ref calls);
                return fetch.Task;
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            await RefreshTestEvents.UntilAsync(client, () => Volatile.Read(ref calls) == 1);
            Server.BeforeAccept = () => acceptGate.Task;
            Task reconnected = WhenConnected(client);
            Server.CloseConnection();
            await RefreshTestEvents.UntilAsync(client, () => client.State == CentrifugeClientState.Connecting);
            Assert.Equal(CentrifugeClientState.Connecting, client.State);

            fetch.SetResult("token-2");
            await RefreshTestEvents.UntilAsync(client, () => timers.PendingWithDelay(FetchTimeoutMs) == 0);
            acceptGate.SetResult();
            await reconnected;

            Assert.Equal("token-2", RefreshTestEvents.ConnectTokens(Server).Last());
            Assert.Empty(arrivals.Tokens());
        }


        // The fetch of a disconnected client fails with an unauthorized error (no token exchange): it is reported as a token
        // error, the session goes on, and the token is unchanged. The unauthorized end of a connected client is in
        // AuthRefreshFetchTests.
        [Fact]
        public async Task TimerFetch_UnauthorizedWhileDisconnected_IsAnErrorOnly() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            TaskCompletionSource<string> fetch = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource acceptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            CentrifugeClientOptions options = ReconnectingOptions("token-initial");
            options.GetToken = () => {
                Interlocked.Increment(ref calls);
                return fetch.Task;
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            await RefreshTestEvents.UntilAsync(client, () => Volatile.Read(ref calls) == 1);
            Server.BeforeAccept = () => acceptGate.Task;
            Task reconnected = WhenConnected(client);
            Task<CentrifugeErrorEventArgs> fetchError = RefreshTestEvents.WhenErrorOfType(client, "refreshToken");
            Server.CloseConnection();
            await RefreshTestEvents.UntilAsync(client, () => client.State == CentrifugeClientState.Connecting);

            fetch.SetException(new CentrifugeUnauthorizedException());
            CentrifugeErrorEventArgs error = await fetchError;
            Assert.Equal("refreshToken", error.Type);
            acceptGate.SetResult();
            await reconnected;

            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal("token-initial", RefreshTestEvents.ConnectTokens(Server).Last());
            Assert.Empty(arrivals.Tokens());
        }


        // A fetch of a disconnected client that answers an empty token ends the session with the unauthorized code: the
        // token is not kept, and no reconnect follows.
        [Fact]
        public async Task TimerFetch_EmptyTokenWhileDisconnected_EndsTheSession() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            TaskCompletionSource<string> fetch = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource acceptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            CentrifugeClientOptions options = ReconnectingOptions("token-initial");
            options.GetToken = () => {
                Interlocked.Increment(ref calls);
                return fetch.Task;
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            await RefreshTestEvents.UntilAsync(client, () => Volatile.Read(ref calls) == 1);
            Server.BeforeAccept = () => acceptGate.Task;
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);
            Server.CloseConnection();
            await RefreshTestEvents.UntilAsync(client, () => client.State == CentrifugeClientState.Connecting);

            fetch.SetResult(string.Empty);
            CentrifugeDisconnectedEventArgs args = await disconnected.WaitAsync(TimeSpan.FromSeconds(5));
            acceptGate.TrySetResult();
            await client.LoopBarrierAsync();

            Assert.Equal(CentrifugeDisconnectedCodes.Unauthorized, args.Code);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            Assert.Empty(arrivals.Tokens());
        }


        // Options of a client that reconnects at once after a transport loss.
        private static CentrifugeClientOptions ReconnectingOptions(string token) {
            return new CentrifugeClientOptions {
                Token = token,
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            };
        }


        // Records the refresh commands of the fake server and never answers them, unless the test answers. The connect
        // reply expires with a ttl of 100 seconds, so the expiry timer of the connection is armed.
        private void WatchRefreshes(RefreshArrivals arrivals) {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return FakeCentrifugoServer.NoReply;
            };
        }
    }
}
