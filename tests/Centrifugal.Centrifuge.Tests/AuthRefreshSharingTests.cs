// The sharing and ordering of the connection token refreshes: callers that share one command count toward the bound and
// each get the result, queued timer entries take no place in the bound, a timer refresh and a requested refresh run one
// after the other, a requested refresh restarts the expiry chain, and a refusal of a requested refresh does not clear the
// token that the server refused as expired. The clock is the fake one.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks how the connection token refreshes share commands, queue and restart the expiry chain.</summary>
    public sealed class AuthRefreshSharingTests : FakeServerTestBase {
        // The renewal delay of a token with a ttl of 100 seconds: 95 percent of it, in milliseconds.
        private const int RenewalDelayMs = 95000;

        // The code of a refresh that is over the bound of refreshes in progress.
        private const int LimitExceededCode = 16;

        // Java: sharedRefreshCallsCountTowardTheBoundAndEachGetTheResult. Callers that share one command count toward the
        // bound of 32, and the one that would be the 33rd is refused at once; all of them get the result of the command.
        [Fact]
        public async Task RefreshAsync_SharedCallsCountTowardTheBound_AndEachGetsTheResult() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            List<Task<CentrifugeAuthResult>> callers = new List<Task<CentrifugeAuthResult>>();
            for (int caller = 0; caller < 32; caller++) {
                callers.Add(client.RefreshAsync("token-a"));
            }
            CentrifugeException refused = await Assert.ThrowsAsync<CentrifugeException>(
                () => client.RefreshAsync("token-a").WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(LimitExceededCode, refused.Code);

            Command first = await arrivals.Nth(0);
            await client.LoopBarrierAsync();
            Assert.Equal(new[] { "token-a" }, arrivals.Tokens());
            await Server.SendReplyAsync(new Reply { Id = first.Id, Refresh = ConnectReplies.Refresh(false, 0, "u1", "") });

            CentrifugeAuthResult[] results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(results, (CentrifugeAuthResult result) => Assert.Equal("u1", result.User));
        }


        // Java: timerEntriesDoNotCountAgainstTheRefreshBound. A timer entry queued behind a refresh in flight takes no place in
        // the bound: 31 requested refreshes queued behind the one in flight are all accepted, and the next one is refused.
        [Fact]
        public async Task RefreshAsync_QueuedTimerEntry_DoesNotCountAgainstTheBound() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            int calls = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
                // The in-flight refresh must outlive the advance of the clock: its deadline is beyond the advance.
                Timeout = TimeSpan.FromMinutes(5),
                GetToken = () => {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult("token-timer");
                },
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeAuthResult> inFlight = client.RefreshAsync("token-a");
            await arrivals.Nth(0);
            timers.Advance(RenewalDelayMs);
            await client.LoopBarrierAsync();

            List<Task<CentrifugeAuthResult>> queued = new List<Task<CentrifugeAuthResult>>();
            for (int index = 1; index <= 31; index++) {
                queued.Add(client.RefreshAsync("token-" + index));
            }
            CentrifugeException refused = await Assert.ThrowsAsync<CentrifugeException>(
                () => client.RefreshAsync("token-32").WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(LimitExceededCode, refused.Code);
            Assert.Equal(new[] { "token-a" }, arrivals.Tokens());
            Assert.Equal(0, Volatile.Read(ref calls));
            Assert.All(queued, (Task<CentrifugeAuthResult> caller) => Assert.False(caller.IsCompleted));

            // The refresh in flight is accepted: its expiry replaces the timer entry, which is then dropped unfetched. The 31
            // requested refreshes go out in order, and each accepted caller gets its result.
            Command first = await arrivals.Nth(0);
            await Server.SendReplyAsync(new Reply { Id = first.Id, Refresh = ConnectReplies.Refresh(false, 0, "u1", "") });
            for (int index = 1; index <= 31; index++) {
                Command command = await arrivals.Nth(index);
                await Server.SendReplyAsync(new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u1", "") });
            }
            Assert.Equal("u1", (await inFlight.WaitAsync(TimeSpan.FromSeconds(5))).User);
            await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref calls));
            Assert.Equal(32, arrivals.Tokens().Length);
            Assert.Equal("token-1", arrivals.Tokens()[1]);
            Assert.Equal("token-31", arrivals.Tokens()[31]);
        }


        // Java: timerRefreshAndManualRefreshRunOneAfterTheOther. The requested refresh waits for the timer refresh in flight;
        // on the wire the timer token goes first, then the requested token.
        [Fact]
        public async Task RefreshAsync_WaitsForATimerRefreshInFlight_AndRunsAfterIt() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
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
            Task<CentrifugeAuthResult> manual = client.RefreshAsync("token-manual");
            await client.LoopBarrierAsync();
            Assert.Equal(new[] { "token-timer" }, arrivals.Tokens());

            await Server.SendReplyAsync(new Reply { Id = timerCommand.Id, Refresh = ConnectReplies.Refresh(true, 100, "u1", "") });
            Command manualCommand = await arrivals.Nth(1);
            Assert.Equal("token-manual", manualCommand.Refresh.Token);
            await Server.SendReplyAsync(new Reply { Id = manualCommand.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") });

            Assert.Equal("u2", (await manual.WaitAsync(TimeSpan.FromSeconds(5))).User);
            Assert.Equal(new[] { "token-timer", "token-manual" }, arrivals.Tokens());
        }


        // Java: manualRefreshNearExpiryKeepsOneTimerChain. A requested refresh restarts the expiry from its own time: the timer
        // of the old expiry is gone, and the next timer refresh comes one renewal delay after the requested one.
        [Fact]
        public async Task RefreshAsync_NearExpiry_KeepsOneTimerChain() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
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

            timers.Advance(90000);
            Task<CentrifugeAuthResult> manual = client.RefreshAsync("token-manual");
            Command manualCommand = await arrivals.Nth(0);
            await Server.SendReplyAsync(new Reply { Id = manualCommand.Id, Refresh = ConnectReplies.Refresh(true, 100, "u1", "") });
            await manual.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, timers.PendingCount);

            timers.Advance(5000);
            await client.LoopBarrierAsync();
            Assert.Equal(0, Volatile.Read(ref calls));
            Assert.Equal(1, timers.PendingWithDelay(RenewalDelayMs));

            timers.Advance(90000);
            Command timerCommand = await arrivals.Nth(1);
            Assert.Equal("token-timer", timerCommand.Refresh.Token);
            Assert.Equal(1, Volatile.Read(ref calls));
        }


        // A requested success keeps the token that the server refused as expired, so a fetch that returns it again sends nothing.
        [Fact]
        public async Task RefreshTimer_RequestedSuccessDoesNotClearTheRefusedTimerToken() {
            RefreshArrivals arrivals = new RefreshArrivals();
            int refusals = 0;
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                if (command.Refresh.Token == "token-1" && Interlocked.Increment(ref refusals) == 1) {
                    return new Reply { Id = command.Id, Error = new Error { Code = 109, Message = "expired" } };
                }
                return new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(true, 100, "u1", "") };
            };
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Token = "token-initial",
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
            await client.RefreshAsync("token-2").WaitAsync(TimeSpan.FromSeconds(5));

            timers.Advance(RenewalDelayMs);
            await RefreshTestEvents.UntilAsync(client, () => timers.PendingCount == 0);
            Assert.Equal(0, timers.PendingCount);
            Assert.Equal(new[] { "token-1", "token-2" }, arrivals.Tokens());
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        // The caller of one shared command has a continuation that fails: the other callers of the command still get the result.
        // Java: aThrowingCallbackDoesNotStopTheOthersSharingItsRefresh. The callers are tasks, so a failing callback is a failing
        // continuation of one caller.
        [Fact]
        public async Task RefreshAsync_ThrowingCallerContinuation_DoesNotStopTheOtherCallers() {
            RefreshArrivals arrivals = new RefreshArrivals();
            WatchRefreshes(arrivals);
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeAuthResult> first = client.RefreshAsync("token-a");
            Task<CentrifugeAuthResult> second = client.RefreshAsync("token-a");
            Task failing = first.ContinueWith((Task<CentrifugeAuthResult> done) => {
                throw new InvalidOperationException("caller failed");
            }, TaskContinuationOptions.ExecuteSynchronously);
            Command command = await arrivals.Nth(0);
            await Server.SendReplyAsync(new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u1", "") });

            Assert.Equal("u1", (await second.WaitAsync(TimeSpan.FromSeconds(5))).User);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => failing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("u1", (await first.WaitAsync(TimeSpan.FromSeconds(5))).User);
        }


        // A requested refresh queued behind a timer fetch that ends while connected: the fetched token is superseded, so it is
        // not sent, and the requested token goes out alone.
        [Fact]
        public async Task RefreshAsync_QueuedBehindATimerFetchThatEndsConnected_SendsOnlyTheRequestedToken() {
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
            Task<CentrifugeAuthResult> requested = client.RefreshAsync("token-2");
            await client.LoopBarrierAsync();
            fetch.SetResult("token-timer");

            Command command = await arrivals.Nth(0);
            Assert.Equal("token-2", command.Refresh.Token);
            await Server.SendReplyAsync(new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") });
            Assert.Equal("u2", (await requested.WaitAsync(TimeSpan.FromSeconds(5))).User);
            await client.LoopBarrierAsync();
            Assert.Equal(new[] { "token-2" }, arrivals.Tokens());
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
