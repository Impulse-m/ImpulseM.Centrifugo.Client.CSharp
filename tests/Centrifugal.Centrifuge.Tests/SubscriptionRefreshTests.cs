using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

// The subscription token refresh timer and its refusals, against the fake server. The timer is the full TTL of the
// subscribe reply (one second here). Ported in part from SubscriptionRefreshRetryTest.java: the integration part of
// subscriptionRefreshWith103KeepsTheConnection (Refresh_103_KeepsSubscription) uses the same ttl 1.
// Gap tests (no Java test pins them): Refresh_OtherCode_Unsubscribes and Refresh_NoTokenGetter_KeepsUpstreamBehaviour.
// The refresh timers run on the fake clock: the test moves the clock with Advance, and a barrier (a publish that the
// fake server answers) follows each step whose effect the test checks. No test waits for real time.
namespace Centrifugal.Centrifuge.Tests {


    [Collection("Integration")]
    public class SubscriptionRefreshTests : FakeServerTestBase {
        private static readonly TimeSpan wait = TimeSpan.FromSeconds(5);
        private readonly CommandArrivals refreshes = new CommandArrivals(wait);


        public override async Task InitializeAsync() {
            await base.InitializeAsync();
            Server.BeforeCommand = cmd => {
                if (cmd.SubRefresh != null) {
                    refreshes.Add();
                }
                return Task.CompletedTask;
            };
        }


        private int CountRefreshes() {
            return Server.Received.Count(c => c.SubRefresh != null);
        }


        private static async Task BarrierAsync(CentrifugeClient testClient) {
            await testClient.PublishAsync("barrier", ReadOnlyMemory<byte>.Empty).WaitAsync(wait);
        }


        // The barrier is answered after the steps that came before it, so the timers they armed are in place.
        private static async Task AdvanceAsync(CentrifugeClient testClient, FakeStepTimerFactory timers, int ms) {
            await BarrierAsync(testClient);
            timers.Advance(ms);
        }


        // Answers the oldest held refresh as accepted with a one-second TTL, then waits until the answer is processed.
        // The fake server dispatches its commands in order, so the barrier comes after the refresh was held.
        private async Task AcceptNextRefreshAsync(CentrifugeClient testClient, ConcurrentQueue<Command> held) {
            Assert.True(held.TryDequeue(out Command? refresh));
            await Server.SendReplyAsync(new Reply { Id = refresh!.Id, SubRefresh = new SubRefreshResult { Expires = true, Ttl = 1 } });
            await BarrierAsync(testClient);
        }


        [Fact]
        // The integration part of subscriptionRefreshWith103KeepsTheConnection (ttl 1). The unit part is OnRefusal_103_Keeps.
        public async Task Refresh_103_KeepsSubscription() {
            Server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            Server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                Reply error = Server.ErrorReply(103, "permission denied", false, null);
                error.Id = cmd.Id;
                return error;
            };
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<int> refreshError = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Error += (_, e) => {
                if (e.Type == "refresh") {
                    refreshError.TrySetResult(e.Code);
                }
            };
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            await AdvanceAsync(testClient, timers, 1000);

            Assert.Equal(103, await refreshError.Task.WaitAsync(wait));
            await BarrierAsync(testClient);
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
            Assert.Equal(1, CountRefreshes());
        }


        [Fact]
        // A refresh refused with another code unsubscribes with that code and message.
        public async Task Refresh_OtherCode_Unsubscribes() {
            Server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            Server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                Reply error = Server.ErrorReply(3000, "forbidden", false, null);
                error.Id = cmd.Id;
                return error;
            };
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<int> unsubscribed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Unsubscribed += (_, e) => unsubscribed.TrySetResult(e.Code);
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            await AdvanceAsync(testClient, timers, 1000);

            Assert.Equal(3000, await unsubscribed.Task.WaitAsync(wait));
        }


        [Fact]
        // A refresh without a token getter is a configuration error, then unauthorized.
        public async Task Refresh_NoTokenGetter_KeepsUpstreamBehaviour() {
            Server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<int> unsubscribed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions { Token = "static" });
            sub.Unsubscribed += (_, e) => unsubscribed.TrySetResult(e.Code);
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            await AdvanceAsync(testClient, timers, 1000);

            Assert.Equal(CentrifugeUnsubscribedCodes.Unauthorized, await unsubscribed.Task.WaitAsync(wait));
        }


        [Fact]
        // A token getter that throws during a refresh reports a refreshToken error and keeps the subscription subscribed.
        public async Task Refresh_TokenGetterFails_ReportsAndKeepsSubscription() {
            Server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            int calls = 0;
            TaskCompletionSource<string> errorType = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions {
                GetToken = _ => {
                    if (Interlocked.Increment(ref calls) == 2) {
                        throw new InvalidOperationException("token failed");
                    }
                    return Task.FromResult("t1");
                }
            });
            sub.Error += (_, e) => {
                if (e.Type == "refreshToken") {
                    errorType.TrySetResult(e.Type);
                }
            };
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            await AdvanceAsync(testClient, timers, 1000);

            Assert.Equal("refreshToken", await errorType.Task.WaitAsync(wait));
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
            Assert.Equal(0, CountRefreshes());
        }


        [Fact]
        // A refresh error is reported before the subscription classifies it: the error event comes before the unsubscribe.
        public async Task Refresh_ErrorIsReportedBeforeClassification() {
            Server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            Server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                Reply error = Server.ErrorReply(3000, "forbidden", false, null);
                error.Id = cmd.Id;
                return error;
            };
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            List<string> order = new List<string>();
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Error += (_, e) => {
                if (e.Type == "refresh") {
                    order.Add("error");
                }
            };
            sub.Unsubscribed += (_, _) => {
                order.Add("unsubscribed");
                done.TrySetResult(true);
            };
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            await AdvanceAsync(testClient, timers, 1000);

            await done.Task.WaitAsync(wait);
            Assert.Equal(new List<string> { "error", "unsubscribed" }, order);
        }


        [Fact]
        // The integration part of subscriptionRefreshDoesNotResendARefusedToken; the unit part is IsRefused_RefusedTokenIsNotSentAgain.
        // A token refused with 109 is retried after the connection's retry delay (5 to 10 seconds; the minimum draw gives 5
        // seconds). The getter returns the same token, so it is not sent again: the retry calls the getter and sends nothing.
        public async Task Refresh_Refused109_SameTokenNotSentAgain() {
            Server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            Server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                Reply error = Server.ErrorReply(109, "token expired", false, null);
                error.Id = cmd.Id;
                return error;
            };
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            CommandArrivals tokenCalls = new CommandArrivals(wait);
            TaskCompletionSource<int> refreshError = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions {
                GetToken = _ => {
                    tokenCalls.Add();
                    return Task.FromResult("same-token");
                }
            });
            sub.Error += (_, e) => {
                if (e.Type == "refresh") {
                    refreshError.TrySetResult(e.Code);
                }
            };
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            await AdvanceAsync(testClient, timers, 1000);

            Assert.Equal(109, await refreshError.Task.WaitAsync(wait));
            await BarrierAsync(testClient);
            timers.Advance(timers.RequestedDelays[timers.RequestedDelays.Count - 1]);
            // Token calls: the subscribe, the refused refresh and the retry.
            await tokenCalls.Reached(3);
            await BarrierAsync(testClient);

            // Best effort: the getter's continuation that would send a refresh is posted to the loop after the getter
            // returns, and the barrier is posted from this thread, so the two posts race. A regression that sends the
            // refused token again is caught only in the runs where the barrier comes after the continuation. A strict
            // check needs a test-only hook in Subscription.cs or Client.cs, which this test does not use.
            Assert.Equal(1, CountRefreshes());
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
            // No retry is armed after the refused token (the timed part of subscriptionRefreshDoesNotResendARefusedToken).
            // The ping watchdog of the connect reply (ping 25 s) is the only timer left.
            Assert.Equal(0, timers.PendingWithDelay(1000));
            Assert.Equal(1, timers.PendingCount);
        }


        [Fact]
        // A refresh reply that arrives after the subscription unsubscribed is dropped: no refresh is sent after it.
        public async Task Refresh_ReplyAfterUnsubscribe_IsDropped() {
            Server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            TaskCompletionSource<Command> held = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                held.TrySetResult(cmd);
                return FakeCentrifugoServer.NoReply;
            };
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            await AdvanceAsync(testClient, timers, 1000);
            Command refresh = await held.Task.WaitAsync(wait);

            sub.Unsubscribe();
            await Server.SendReplyAsync(new Reply { Id = refresh.Id, SubRefresh = new SubRefreshResult { Expires = true, Ttl = 1 } });
            await BarrierAsync(testClient);
            // A reply that had re-armed the timer would fire now, and send a second refresh.
            timers.Advance(1000);
            await BarrierAsync(testClient);

            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);
            Assert.Equal(1, CountRefreshes());
            Assert.Equal(0, timers.PendingWithDelay(1000));
        }


        [Fact]
        // A refresh accepted without expires arms no further refresh.
        public async Task Refresh_AcceptedWithoutExpires_ArmsNothing() {
            Server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            Server.OnCommand = cmd => cmd.SubRefresh == null ? null : new Reply { Id = cmd.Id, SubRefresh = new SubRefreshResult() };
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            await AdvanceAsync(testClient, timers, 1000);
            await refreshes.Reached(1);
            await BarrierAsync(testClient);
            // A timer armed by the accepted reply would fire now, and send a second refresh.
            timers.Advance(1000);
            await BarrierAsync(testClient);

            Assert.Equal(1, CountRefreshes());
            Assert.Equal(0, timers.PendingWithDelay(1000));
        }


        [Fact]
        // One refresh timer at a time. A refresh started while the TTL timer is pending drops that timer, and its accepted
        // reply arms exactly one timer. The refreshes are held until the test answers them, so each in-flight state is checked.
        public async Task Refresh_ReplacesTimer_NoDuplicate() {
            Server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            ConcurrentQueue<Command> held = new ConcurrentQueue<Command>();
            Server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                held.Enqueue(cmd);
                return FakeCentrifugoServer.NoReply;
            };
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            // The TTL timer fires and its refresh is held: no refresh timer is pending while the refresh is in flight.
            // The ping watchdog of the connect reply is a separate timer and is not counted here.
            await AdvanceAsync(testClient, timers, 1000);
            await refreshes.Reached(1);
            await BarrierAsync(testClient);
            Assert.Equal(0, timers.PendingWithDelay(1000));

            // The accepted reply arms one timer.
            await AcceptNextRefreshAsync(testClient, held);
            Assert.Equal(1, timers.PendingWithDelay(1000));

            // A refresh started while that timer is pending drops it, so the in-flight state has no refresh timer.
            sub.RefreshToken();
            await refreshes.Reached(2);
            await BarrierAsync(testClient);
            Assert.Equal(0, timers.PendingWithDelay(1000));

            // Its accepted reply arms exactly one timer: no duplicate.
            await AcceptNextRefreshAsync(testClient, held);
            Assert.Equal(2, CountRefreshes());
            Assert.Equal(1, timers.PendingWithDelay(1000));
        }


        [Fact]
        // Leaving SUBSCRIBED drops the refresh timer: the resubscribe reply has no expires, so no refresh is sent.
        public async Task Leaving_Subscribed_DropsRefreshTimer() {
            int subscribes = 0;
            Server.OnSubscribe = (_, _) => Interlocked.Increment(ref subscribes) == 1
                ? new SubscribeResult { Expires = true, Ttl = 1 }
                : new SubscribeResult();
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<bool> firstSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> secondSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Subscribed += (_, _) => {
                if (!firstSubscribed.Task.IsCompleted) {
                    firstSubscribed.TrySetResult(true);
                } else {
                    secondSubscribed.TrySetResult(true);
                }
            };
            sub.Subscribe();
            await firstSubscribed.Task.WaitAsync(wait);

            await Server.SendPushAsync(new Push {
                Channel = "ch",
                Unsubscribe = new Unsubscribe { Code = 2500, Reason = "resubscribe" }
            });
            await secondSubscribed.Task.WaitAsync(wait);
            await BarrierAsync(testClient);
            // The TTL of the first subscribe would fire now, if its timer had survived the leave.
            timers.Advance(1000);
            await BarrierAsync(testClient);

            Assert.Equal(0, timers.PendingWithDelay(1000));
            Assert.Equal(0, CountRefreshes());
        }


        [Fact]
        // The refresh is due at the full TTL in milliseconds, capped at int.MaxValue.
        public void TtlToFullMilliseconds_IsFullTtlCapped() {
            Assert.Equal(1000, CentrifugeSubscription.TtlToFullMilliseconds(1));
            Assert.Equal(int.MaxValue, CentrifugeSubscription.TtlToFullMilliseconds(uint.MaxValue));
        }
    }
}
