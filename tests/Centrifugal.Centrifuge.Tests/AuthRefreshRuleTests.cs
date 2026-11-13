// The connection token refresh rules that the other refresh test files do not reach: a timer refresh stores its token for the
// next connect, a timer fetch that ends on a replaced transport or after a refresh was accepted while disconnected is
// dropped, a queued refresh runs after a refused timer refresh, a refresh shared while disconnected stores the newest
// token, the connect does not ask the getter for a refresh made while disconnected, an error of a refresh carries no
// token, and a timer entry queued before a reconnect is dropped. The clock is the fake one; the reconnects use one
// millisecond delays.
// Not ported: no AuthTest method names the executor rejection of a refresh call: the executor of the C# client cannot
// reject a step, so a refresh after close is answered by the closed case (RefreshAsyncTests).
// Not ported: refusesCallAfterLogoutUntilConnectAttempt (RpcPoolTokenTest): the pool token source is not in the client
// yet, so the connect-attempt call of the connection token is tested with the pool token source.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the connection token refresh rules that the other refresh test files do not reach.</summary>
    public sealed class AuthRefreshRuleTests : FakeServerTestBase {
        // The renewal delay of a token with a ttl of 100 seconds, in milliseconds.
        private const int RenewalDelayMs = 95000;

        // The timeout of a token fetch with the default options, in milliseconds.
        private const int FetchTimeoutMs = 5000;


        [Fact]
        public async Task RefreshTimer_AcceptedRefresh_StoresTheTokenForTheNextConnect() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshesAccepted(arrivals, "u1");
            int calls = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult("token-timer");
                },
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            await arrivals.Nth(0);
            await RefreshTestEvents.UntilAsync(client, () => timers.PendingCount == 0);

            Task reconnected = WhenConnected(client);
            client.Disconnect();
            await client.LoopBarrierAsync();
            client.Connect();
            await reconnected;

            Assert.Equal("token-timer", RefreshTestEvents.ConnectTokens(Server).Last());
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(new[] { "token-timer" }, arrivals.Tokens());
        }


        [Fact]
        public async Task TimerFetch_EndingOnAReplacedTransport_IsDropped() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            TaskCompletionSource<string> fetch = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            Task reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnected;

            fetch.SetResult("token-stale");
            await RefreshTestEvents.UntilAsync(client, () => timers.PendingWithDelay(FetchTimeoutMs) == 0);
            Assert.Empty(arrivals.Tokens());
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        [Fact]
        public async Task TimerFetch_OverruledByARefreshAcceptedWhileDisconnected_IsDropped() {
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
                return new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") };
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

            // The refresh waits behind the fetch. When the fetch ends, the refresh starts and requests its deadline, the only
            // timer requested by that step.
            Task<CentrifugeAuthResult> refresh = client.RefreshAsync("token-2");
            await client.LoopBarrierAsync();
            int requestedBefore = timers.RequestedDelays.Count;
            fetch.SetResult("token-timer");
            await RefreshTestEvents.UntilAsync(client, () => timers.RequestedDelays.Count > requestedBefore);
            acceptGate.SetResult();
            await reconnected;

            Assert.Equal("token-2", RefreshTestEvents.ConnectTokens(Server).Last());
            CentrifugeAuthResult result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("u2", result.User);
            Assert.Equal(new[] { "token-2" }, arrivals.Tokens());
        }


        [Fact]
        public async Task RefreshTimer_RefusedForPermission_RunsTheQueuedRefresh() {
            RefreshArrivals arrivals = new RefreshArrivals();
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return command.Refresh.Token == "token-timer"
                    ? FakeCentrifugoServer.NoReply
                    : new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") };
            };
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Task.FromResult("token-timer"),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            Command timerCommand = await arrivals.Nth(0);
            Task<CentrifugeAuthResult> requested = client.RefreshAsync("token-2");
            await client.LoopBarrierAsync();
            Assert.Equal(new[] { "token-timer" }, arrivals.Tokens());

            await Server.SendReplyAsync(new Reply {
                Id = timerCommand.Id,
                Error = new Error { Code = 103, Message = "permission denied" },
            });
            Assert.Equal("u2", (await requested.WaitAsync(TimeSpan.FromSeconds(5))).User);
            Assert.Equal(new[] { "token-timer", "token-2" }, arrivals.Tokens());
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        [Fact]
        public async Task RefreshAsync_SharedWhileDisconnected_StoresTheNewestToken() {
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
            Task<CentrifugeAuthResult> first = client.RefreshAsync("token-2");
            Task<CentrifugeAuthResult> second = client.RefreshAsync("token-3");
            Task<CentrifugeAuthResult> third = client.RefreshAsync("token-2");
            await client.LoopBarrierAsync();

            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("token-2", RefreshTestEvents.ConnectTokens(Server).Last());
            Assert.Equal(new[] { "token-2", "token-3" }, arrivals.Tokens());
        }


        [Fact]
        public async Task RefreshAsync_WhileDisconnected_DoesNotAskTheGetterAtConnect() {
            int calls = 0;
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1");
            Server.OnCommand = (Command command) => command.Refresh != null
                ? new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") }
                : null;
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Token = "token-1",
                GetToken = () => {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult("token-getter");
                },
            });
            Task<CentrifugeAuthResult> refresh = client.RefreshAsync("token-2");
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Assert.Equal("u2", (await refresh.WaitAsync(TimeSpan.FromSeconds(5))).User);
            Assert.Equal(0, Volatile.Read(ref calls));
            Assert.Equal("token-2", RefreshTestEvents.ConnectTokens(Server).Last());
        }


        [Fact]
        public async Task RefreshAsync_TimeoutMessageCarriesNoToken() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                Timeout = TimeSpan.FromMilliseconds(500),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeAuthResult> call = client.RefreshAsync("token-secret-2");
            await arrivals.Nth(0);
            timers.Advance(500);

            CentrifugeTimeoutException error = await Assert.ThrowsAsync<CentrifugeTimeoutException>(
                () => call.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.DoesNotContain("token-secret", error.Message);
            Assert.DoesNotContain("token-secret", error.ToString());
        }


        [Fact]
        public async Task RefreshTimer_QueuedAcrossAReconnect_IsDropped() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            int calls = 0;
            CentrifugeClientOptions options = ReconnectingOptions("token-initial");
            // The requested refresh in flight must outlive the advance of the clock: its deadline is beyond the advance.
            options.Timeout = TimeSpan.FromMinutes(5);
            options.GetToken = () => {
                Interlocked.Increment(ref calls);
                return Task.FromResult("token-timer");
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeAuthResult> inFlight = client.RefreshAsync("token-a");
            await arrivals.Nth(0);
            timers.Advance(RenewalDelayMs);
            await client.LoopBarrierAsync();
            Assert.Equal(0, Volatile.Read(ref calls));

            Task reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnected;
            await Assert.ThrowsAnyAsync<CentrifugeException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(5)));
            await client.LoopBarrierAsync();

            Assert.Equal(0, Volatile.Read(ref calls));
            Assert.Equal(new[] { "token-a" }, arrivals.Tokens());
            Assert.Equal(1, timers.PendingWithDelay(RenewalDelayMs));
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        [Fact]
        public async Task TimerFetch_ClosedWhileItRuns_FailsTheRefreshQueuedBehindIt() {
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
            Task<CentrifugeAuthResult> queued = client.RefreshAsync("token-2");
            await client.LoopBarrierAsync();

            await client.DisposeAsync();

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(CentrifugeErrorCodes.ClientClosed, error.Code);
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


        // Records the refresh commands of the fake server and never answers them. The connect reply expires with a ttl of
        // 100 seconds, so the expiry timer of the connection is armed.
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


        // Records the refresh commands of the fake server and accepts each of them for the user, with no expiry.
        private void WatchRefreshesAccepted(RefreshArrivals arrivals, string user) {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: user);
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, user, "") };
            };
        }
    }
}
