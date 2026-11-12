using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

// The resubscribe backoff of a subscription, against the fake server, on the fake clock. Ported from ResubscribeBackoffTest.java
// (1 test): testBackoffResetsAfterSuccessfulSubscribe -> Resubscribe_BackoffResetsAfterSuccessfulSubscribe. The numbers are the
// Java ones: 11 failed subscribes, one success, then 7 failed subscribes whose retries must add up to at most 3 seconds.
// Each retry is a fake timer; the test fires it with Advance, so no test waits for real time.
namespace Centrifugal.Centrifuge.Tests {


    [Collection("Integration")]
    public class ResubscribeBackoffTests : FakeServerTestBase {
        private static readonly TimeSpan wait = TimeSpan.FromSeconds(30);
        private const int PrimingFailures = 11;
        private const int MeasuredFailures = 7;
        private const int InsufficientState = 2500;
        private const int MeasuredWindowMs = 3000;
        private readonly CommandArrivals subscribes = new CommandArrivals(wait);


        public override async Task InitializeAsync() {
            await base.InitializeAsync();
            Server.BeforeCommand = cmd => {
                if (cmd.Subscribe != null) {
                    subscribes.Add();
                }
                return Task.CompletedTask;
            };
        }


        private Reply TemporaryError(Command cmd) {
            Reply error = Server.ErrorReply(108, "not available", true, null);
            error.Id = cmd.Id;
            return error;
        }


        private static int LastRequestedDelay(FakeStepTimerFactory timers) {
            IReadOnlyList<int> delays = timers.RequestedDelays;
            return delays[delays.Count - 1];
        }


        // The barrier is answered after the failed subscribe that armed the retry, so the retry is in place when it runs.
        private static async Task<int> FireRetryAsync(CentrifugeClient testClient, FakeStepTimerFactory timers) {
            await testClient.PublishAsync("barrier", ReadOnlyMemory<byte>.Empty).WaitAsync(wait);
            int delay = LastRequestedDelay(timers);
            timers.Advance(delay);
            return delay;
        }


        [Fact]
        // Java: testBackoffResetsAfterSuccessfulSubscribe. With the step reset by the successful subscribe, the retries are spaced
        // by at most 1 + 2^k ms for k = 0..6, so all seven land within the window. Without the reset the step carries on from 11.
        public async Task Resubscribe_BackoffResetsAfterSuccessfulSubscribe() {
            Server.OnCommand = cmd => {
                if (cmd.Subscribe == null) {
                    return null;
                }
                if (subscribes.Count == PrimingFailures + 1) {
                    return null;
                }
                return TemporaryError(cmd);
            };
            CentrifugeClient testClient = NewClientWithTestClock(out FakeStepTimerFactory timers);
            testClient.Connect();
            await testClient.ReadyAsync().WaitAsync(wait);
            List<string> unexpected = new List<string>();
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = testClient.NewSubscription("ch", new CentrifugeSubscriptionOptions {
                // A 1 ms floor keeps the priming retries short; the 20 s ceiling leaves room for the step to grow.
                MinResubscribeDelay = TimeSpan.FromMilliseconds(1),
                MaxResubscribeDelay = TimeSpan.FromMilliseconds(20000)
            });
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Error += (_, e) => {
                // Temporary subscribe errors are expected here, nothing else is.
                if (e.Type != "subscribe") {
                    unexpected.Add(e.Type);
                }
            };
            sub.Subscribe();

            // Each failed subscribe arms one retry. Firing it sends the next subscribe; the last one succeeds.
            for (int attempt = 1; attempt <= PrimingFailures; attempt++) {
                await subscribes.Reached(attempt);
                await FireRetryAsync(testClient, timers);
            }
            await subscribed.Task.WaitAsync(wait);

            // Send the subscription back to subscribing: the server then fails every attempt.
            await Server.SendPushAsync(new Push {
                Channel = "ch",
                Unsubscribe = new Unsubscribe { Code = InsufficientState, Reason = "insufficient state" }
            });

            List<int> retryDelays = new List<int>();
            int waitedMs = 0;
            for (int failure = 1; failure <= MeasuredFailures; failure++) {
                // Subscribes 13 to 19: the subscribe after the success is the 13th.
                await subscribes.Reached(PrimingFailures + 1 + failure);
                await testClient.PublishAsync("barrier", ReadOnlyMemory<byte>.Empty).WaitAsync(wait);
                int delay = LastRequestedDelay(timers);
                retryDelays.Add(delay);
                if (failure < MeasuredFailures) {
                    timers.Advance(delay);
                    waitedMs += delay;
                }
            }

            Assert.Equal(MeasuredFailures, retryDelays.Count);
            for (int step = 0; step < MeasuredFailures; step++) {
                Assert.True(retryDelays[step] <= 1 + (1 << step), $"retry {step} waited {retryDelays[step]} ms");
            }
            Assert.True(waitedMs <= MeasuredWindowMs, $"the measured retries waited {waitedMs} ms");
            Assert.Empty(unexpected);
        }
    }
}
