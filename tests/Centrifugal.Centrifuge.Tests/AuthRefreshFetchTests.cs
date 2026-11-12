// The timer fetch of the connection token and its failures, on the fake clock: a retry after a fetch failure grows with
// each failure up to the cap, a silent getter is reported at the client timeout and arms the retry, a getter that throws
// does not stop a requested refresh, an unauthorized or empty token ends the session, and a token refused as expired is
// retried with a fresh token. The retry delays are the ones of RefreshRetryPolicy with the draws of FixedRandom.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the timer fetch of the connection token and the retries of its failures.</summary>
    public sealed class AuthRefreshFetchTests : FakeServerTestBase {
        // The renewal delay of a token with a ttl of 100 seconds: 95 percent of it, in milliseconds.
        private const int RenewalDelayMs = 95000;

        // The disconnect code of an unauthorized session end.
        private const int UnauthorizedCode = 1;

        // The error type of a failed token fetch of the timer.
        private const string FetchErrorType = "refreshToken";

        // The error type of a refused timer refresh.
        private const string RefreshErrorType = "refresh";


        // Java: timerRefreshWithAnExpiredTokenRetriesWithAFreshToken
        [Fact]
        public async Task RefreshTimer_TokenRefusedAsExpired_RetriesWithAFreshToken() {
            RefreshArrivals arrivals = new RefreshArrivals();
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                if (command.Refresh.Token == "token-1") {
                    return new Reply { Id = command.Id, Error = new Error { Code = 109, Message = "expired" } };
                }
                return new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(true, 100, "u1", "") };
            };
            int fetches = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                Timeout = TimeSpan.FromSeconds(60),
                GetToken = () => Task.FromResult("token-" + Interlocked.Increment(ref fetches)),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            ErrorCount refusals = new ErrorCount(client, RefreshErrorType);

            timers.Advance(RenewalDelayMs);
            await refusals.Reached(1);
            await client.LoopBarrierAsync();
            timers.Advance(LastDelay(timers));

            Command second = await arrivals.Nth(1);
            Assert.Equal("token-2", second.Refresh.Token);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        // Java: timerRetryCeilingGrowsWithEachFailureUpToTheCap. The client retries with bounds of 5 s and 10 s, and the
        // ceiling doubles with each failure. With the largest draws, the retry after the k-th failure waits the top of the
        // range of its step: 10 s, 15 s, 25 s, then 45 s.
        [Fact]
        public async Task RefreshTimer_RepeatedFetchFailures_RetryCeilingGrows() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                Timeout = TimeSpan.FromSeconds(60),
                GetToken = () => Task.FromException<string>(new InvalidOperationException("token service down")),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options, FixedRandom.MaximumDraws());
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            ErrorCount failures = new ErrorCount(client, FetchErrorType);

            timers.Advance(RenewalDelayMs);
            List<int> delays = new List<int>();
            for (int failure = 0; failure < 4; failure++) {
                await failures.Reached(failure + 1);
                await client.LoopBarrierAsync();
                int delay = LastDelay(timers);
                delays.Add(delay);
                if (failure < 3) {
                    timers.Advance(delay);
                }
            }

            Assert.Equal(new[] { 10000, 15000, 25000, 45000 }, delays.ToArray());
        }


        // Java: silentTokenGetterIsReportedAtTheClientTimeout, and the retry that the timeout arms (the rule of the silent
        // getter). The getter never answers; the fetch is bounded by the client timeout.
        [Fact]
        public async Task RefreshTimer_SilentGetter_IsReportedAtTheTimeoutAndArmsTheRetry() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                Timeout = TimeSpan.FromMilliseconds(300),
                GetToken = () => new TaskCompletionSource<string>().Task,
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            ErrorCount failures = new ErrorCount(client, FetchErrorType);

            timers.Advance(RenewalDelayMs);
            timers.Advance(300);
            await failures.Reached(1);
            await client.LoopBarrierAsync();

            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal(1, timers.PendingWithDelay(5000));
        }


        // Java: explicitRefreshIsAnsweredAfterATokenGetterThrowsOnTheTimer
        [Fact]
        public async Task RefreshAsync_AfterTheTimerGetterThrew_IsAnsweredNormally() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => command.Refresh != null
                ? new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u1", "") }
                : null;
            int calls = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => {
                    if (Interlocked.Increment(ref calls) == 1) {
                        throw new InvalidOperationException("token getter failed");
                    }
                    return Task.FromResult("token-timer");
                },
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            ErrorCount failures = new ErrorCount(client, FetchErrorType);

            timers.Advance(RenewalDelayMs);
            await failures.Reached(1);
            await client.LoopBarrierAsync();
            CentrifugeAuthResult result = await client.RefreshAsync("token-2");

            Assert.Equal("u1", result.User);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        // Java: timerFetchFailingWithUnauthorizedEndsTheSessionWithoutARetry
        [Fact]
        public async Task RefreshTimer_FetchUnauthorized_EndsTheSessionWithoutAnErrorOrARetry() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Task.FromException<string>(new CentrifugeUnauthorizedException()),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            ErrorCount failures = new ErrorCount(client, FetchErrorType);
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);

            timers.Advance(RenewalDelayMs);
            CentrifugeDisconnectedEventArgs args = await disconnected.WaitAsync(TimeSpan.FromSeconds(5));
            await client.LoopBarrierAsync();

            Assert.Equal(UnauthorizedCode, args.Code);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            Assert.Equal(0, failures.Count);
            Assert.DoesNotContain(Server.Received, received => received.Refresh != null);
        }


        // The token of a timer fetch is empty: the session ends with the unauthorized code, and no refresh is sent.
        [Fact]
        public async Task RefreshTimer_FetchReturnsNoToken_EndsTheSession() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Task.FromResult<string>(null!),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);

            timers.Advance(RenewalDelayMs);
            CentrifugeDisconnectedEventArgs args = await disconnected.WaitAsync(TimeSpan.FromSeconds(5));
            await client.LoopBarrierAsync();

            Assert.Equal(UnauthorizedCode, args.Code);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            Assert.DoesNotContain(Server.Received, received => received.Refresh != null);
        }


        // The fake clock delay of the last timer that was started: the retry that a failure armed.
        private static int LastDelay(FakeStepTimerFactory timers) {
            IReadOnlyList<int> delays = timers.RequestedDelays;
            return delays[delays.Count - 1];
        }


        /// <summary>Counts the error events of one type and completes a wait that the count reaches.</summary>
        private sealed class ErrorCount {
            private readonly object gate = new object();
            private readonly string type;
            private readonly List<(int Target, TaskCompletionSource<bool> Source)> waiters = new List<(int, TaskCompletionSource<bool>)>();
            private int count;


            /// <summary>
            /// Starts counting the error events of the type.
            /// </summary>
            /// <param name="client">The client whose error events are counted.</param>
            /// <param name="type">The error type to count.</param>
            public ErrorCount(CentrifugeClient client, string type) {
                this.type = type;
                client.Error += (object? sender, CentrifugeErrorEventArgs args) => OnError(args);
            }


            /// <summary>The number of error events counted so far.</summary>
            public int Count {
                get {
                    lock (gate) {
                        return count;
                    }
                }
            }


            /// <summary>
            /// Completes when the count is at least n, or fails after five seconds.
            /// </summary>
            /// <param name="n">The count to reach.</param>
            public Task Reached(int n) {
                lock (gate) {
                    if (count >= n) {
                        return Task.CompletedTask;
                    }
                    TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    waiters.Add((n, source));
                    return source.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }


            private void OnError(CentrifugeErrorEventArgs args) {
                if (args.Type != type) {
                    return;
                }
                lock (gate) {
                    count++;
                    foreach ((int target, TaskCompletionSource<bool> source) in waiters.ToArray()) {
                        if (count >= target) {
                            source.TrySetResult(true);
                            waiters.Remove((target, source));
                        }
                    }
                }
            }
        }
    }
}
