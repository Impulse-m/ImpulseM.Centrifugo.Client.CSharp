// The retries of a failed timer refresh, on the fake clock: a token getter that fails with an I/O error and a client-side
// error code are retried with a fresh token, a token refused as expired is not sent again while the client stops on a
// refused token (and is sent again when it does not), and the retry ceiling grows with each failure, up to the cap, and
// starts again after an accepted expiry. The retry delays are the ones of RefreshRetryPolicy with the draws of FixedRandom.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the retries of a failed timer refresh and the refusal of a token the server refused as expired.</summary>
    public sealed class AuthRefreshRetryRulesTests : FakeServerTestBase {
        // The renewal delay of a token with a ttl of 100 seconds: 95 percent of it, in milliseconds.
        private const int RenewalDelayMs = 95000;

        // The first retry delay with the minimum draws of the policy: the bottom of its range.
        private const int FirstRetryMinimumMs = 5000;

        // Java: timerRefreshWithATransportErrorIsRetriedWithTheSameBackoff
        [Fact]
        public async Task RefreshTimer_TokenGetterFailsWithAnIoError_IsRetriedWithAFreshToken() {
            RefreshArrivals arrivals = new RefreshArrivals();
            ArmExpiringConnection(arrivals, (Command command) => AcceptedReply(command));
            int fetches = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Interlocked.Increment(ref fetches) == 1
                    ? Task.FromException<string>(new IOException("token service unreachable"))
                    : Task.FromResult("token-2"),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeErrorEventArgs> fetchError = RefreshTestEvents.WhenErrorOfType(client, "refreshToken");

            timers.Advance(RenewalDelayMs);
            await fetchError;
            await client.LoopBarrierAsync();
            int delay = RefreshTestEvents.LastDelay(timers);
            Assert.InRange(delay, FirstRetryMinimumMs, 2 * FirstRetryMinimumMs);

            timers.Advance(delay);
            Command command = await arrivals.Nth(0);
            Assert.Equal("token-2", command.Refresh.Token);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        // Java: timerRefreshWithAClientErrorCodeIsRetriedWithAFreshToken
        [Fact]
        public async Task RefreshTimer_ClientErrorCode_IsRetriedWithAFreshToken() {
            RefreshArrivals arrivals = new RefreshArrivals();
            ArmExpiringConnection(arrivals, (Command command) => command.Refresh.Token == "token-1"
                ? new Reply { Id = command.Id, Error = new Error { Code = 3, Message = "client error" } }
                : AcceptedReply(command));
            int fetches = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Task.FromResult("token-" + Interlocked.Increment(ref fetches)),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeErrorEventArgs> refusal = RefreshTestEvents.WhenErrorOfType(client, "refresh");

            timers.Advance(RenewalDelayMs);
            CentrifugeErrorEventArgs error = await refusal;
            await client.LoopBarrierAsync();
            Assert.Equal(3, error.Code);
            timers.Advance(RefreshTestEvents.LastDelay(timers));

            Command second = await arrivals.Nth(1);
            Assert.Equal("token-2", second.Refresh.Token);
            Assert.Equal(new[] { "token-1", "token-2" }, arrivals.Tokens());
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        // Java: refusedTokenIsNotSentAgainWithStopOnRefusedToken. The getter returns the refused token again: the retry
        // fetches it, sends nothing and arms nothing. The next connect fetches a fresh token.
        [Fact]
        public async Task RefreshTimer_RefusedTokenIsNotSentAgainWithStopOnRefusedToken() {
            RefreshArrivals arrivals = new RefreshArrivals();
            ArmExpiringConnection(arrivals, (Command command) => command.Refresh.Token == "token-1"
                ? RefusedAsExpired(command)
                : AcceptedReply(command));
            int fetches = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Task.FromResult(Interlocked.Increment(ref fetches) <= 2 ? "token-1" : "token-2"),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeErrorEventArgs> refusal = RefreshTestEvents.WhenErrorOfType(client, "refresh");

            timers.Advance(RenewalDelayMs);
            await refusal;
            await client.LoopBarrierAsync();
            timers.Advance(RefreshTestEvents.LastDelay(timers));
            await RefreshTestEvents.UntilAsync(client, () => Volatile.Read(ref fetches) == 2 && timers.PendingCount == 0);

            Assert.Equal(2, Volatile.Read(ref fetches));
            Assert.Equal(0, timers.PendingCount);
            Assert.Equal(new[] { "token-1" }, arrivals.Tokens());
            Assert.Equal(CentrifugeClientState.Connected, client.State);

            Task reconnected = WhenConnected(client);
            client.Disconnect();
            await client.LoopBarrierAsync();
            client.Connect();
            await reconnected;
            Assert.Equal("token-2", RefreshTestEvents.ConnectTokens(Server).Last());
        }


        // The same refusal with Options.StopOnRefusedToken off: the retry sends the refused token again, after the retry delay.
        [Fact]
        public async Task RefreshTimer_RefusedTokenIsSentAgainWithStopOnRefusedTokenOff() {
            RefreshArrivals arrivals = new RefreshArrivals();
            int refusals = 0;
            ArmExpiringConnection(arrivals, (Command command) => command.Refresh.Token == "token-1"
                && Interlocked.Increment(ref refusals) == 1
                ? RefusedAsExpired(command)
                : AcceptedReply(command));
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                StopOnRefusedToken = false,
                GetToken = () => Task.FromResult("token-1"),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeErrorEventArgs> refusal = RefreshTestEvents.WhenErrorOfType(client, "refresh");

            timers.Advance(RenewalDelayMs);
            await refusal;
            await client.LoopBarrierAsync();
            Assert.Equal(FirstRetryMinimumMs, RefreshTestEvents.LastDelay(timers));
            timers.Advance(FirstRetryMinimumMs);

            Command second = await arrivals.Nth(1);
            Assert.Equal("token-1", second.Refresh.Token);
            Assert.Equal(new[] { "token-1", "token-1" }, arrivals.Tokens());
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        // Java: timerRetryCeilingStartsAgainAfterASuccessfulExpiry. Four failing retries after the expiry fetch grow the retry
        // delay, and the fifth retry succeeds. Then eight cycles, each a failure after an accepted expiry: the accepted refresh
        // resets the retry step, so every failure after it draws from the first step again.
        [Fact]
        public async Task RefreshTimer_RetryCeilingStartsAgainAfterASuccessfulExpiry() {
            RefreshArrivals arrivals = new RefreshArrivals();
            ArmExpiringConnection(arrivals, (Command command) => AcceptedReply(command));
            int failing = 1;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Volatile.Read(ref failing) == 1
                    ? Task.FromException<string>(new InvalidOperationException("token service down"))
                    : Task.FromResult("token-ok"),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options, FixedRandom.MaximumDraws());
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            RefreshTestEvents.ErrorCounter fetchErrors = new RefreshTestEvents.ErrorCounter(client, "refreshToken");

            List<int> delays = new List<int>();
            timers.Advance(RenewalDelayMs);
            for (int failure = 0; failure < 5; failure++) {
                await fetchErrors.Reached(failure + 1);
                await client.LoopBarrierAsync();
                delays.Add(RefreshTestEvents.LastDelay(timers));
                if (failure == 4) {
                    Volatile.Write(ref failing, 0);
                }
                timers.Advance(delays[failure]);
            }
            await RefreshTestEvents.UntilAsync(client, () => timers.PendingWithDelay(RenewalDelayMs) == 1);

            int failures = 5;
            for (int cycle = 0; cycle < 8; cycle++) {
                Volatile.Write(ref failing, 1);
                timers.Advance(RenewalDelayMs);
                failures++;
                await fetchErrors.Reached(failures);
                await client.LoopBarrierAsync();
                delays.Add(RefreshTestEvents.LastDelay(timers));

                Volatile.Write(ref failing, 0);
                timers.Advance(delays[delays.Count - 1]);
                await RefreshTestEvents.UntilAsync(client, () => timers.PendingWithDelay(RenewalDelayMs) == 1);
            }

            Assert.Equal(
                new[] { 10000, 15000, 25000, 45000, 85000, 10000, 10000, 10000, 10000, 10000, 10000, 10000, 10000 },
                delays.ToArray());
        }


        // Java: refusedRefreshBehindATimerFetchArmsTheRetryInsteadOfFetchingAtOnce. A requested refresh queued behind a timer
        // fetch is refused: the fetched token is superseded, so it is neither sent nor fetched again at once. The retry
        // fetches once, after its delay.
        [Fact]
        public async Task RefreshTimer_RefusedRefreshBehindATimerFetch_ArmsTheRetry() {
            RefreshArrivals arrivals = new RefreshArrivals();
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return command.Refresh.Token == "token-2"
                    ? new Reply { Id = command.Id, Error = new Error { Code = 103, Message = "permission denied" } }
                    : AcceptedReply(command);
            };
            TaskCompletionSource<string> fetch = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int fetches = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Interlocked.Increment(ref fetches) == 1
                    ? fetch.Task
                    : Task.FromResult("token-retry"),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            await RefreshTestEvents.UntilAsync(client, () => Volatile.Read(ref fetches) == 1);
            Task<CentrifugeAuthResult> refused = client.RefreshAsync("token-2");
            await client.LoopBarrierAsync();
            fetch.SetResult("token-timer");
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => refused.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(103, error.Code);

            await client.LoopBarrierAsync();
            Assert.Equal(1, Volatile.Read(ref fetches));
            Assert.Equal(new[] { "token-2" }, arrivals.Tokens());
            Assert.Equal(FirstRetryMinimumMs, RefreshTestEvents.LastDelay(timers));

            timers.Advance(FirstRetryMinimumMs);
            await RefreshTestEvents.UntilAsync(client, () => Volatile.Read(ref fetches) == 2);
            Command retry = await arrivals.Nth(1);
            Assert.Equal("token-retry", retry.Refresh.Token);
            Assert.Equal(2, Volatile.Read(ref fetches));
        }


        // Java: timerRetryCeilingGrowsByDefault. With the default cap of five minutes, nine failures in a row give the
        // delays of the policy table, and the ninth is at the cap.
        [Fact]
        public async Task RefreshTimer_RetryCeilingGrowsByDefault() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Task.FromException<string>(new InvalidOperationException("token service down")),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options, FixedRandom.MaximumDraws());
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            RefreshTestEvents.ErrorCounter fetchErrors = new RefreshTestEvents.ErrorCounter(client, "refreshToken");

            List<int> delays = new List<int>();
            timers.Advance(RenewalDelayMs);
            for (int failure = 0; failure < 9; failure++) {
                await fetchErrors.Reached(failure + 1);
                await client.LoopBarrierAsync();
                int delay = RefreshTestEvents.LastDelay(timers);
                delays.Add(delay);
                timers.Advance(delay);
            }

            Assert.Equal(
                new[] { 10000, 15000, 25000, 45000, 85000, 165000, 300000, 300000, 300000 },
                delays.ToArray());
        }


        // The connect reply of the connection: it expires, with a ttl of 100 seconds, and the auth refresh is watched.
        private void ArmExpiringConnection(RefreshArrivals arrivals, Func<Command, Reply> answer) {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return answer(command);
            };
        }


        // An accepted refresh of a token with a ttl of 100 seconds for the user (u1 by default).
        private static Reply AcceptedReply(Command command, string user = "u1") {
            return new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(true, 100, user, "") };
        }


        // A refusal of a token as expired: code 109, which the timer refresh retries with a fresh token.
        private static Reply RefusedAsExpired(Command command) {
            return new Reply { Id = command.Id, Error = new Error { Code = 109, Message = "expired" } };
        }
    }
}
