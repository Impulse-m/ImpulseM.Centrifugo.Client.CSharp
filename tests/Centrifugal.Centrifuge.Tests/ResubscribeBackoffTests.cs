using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

// The resubscribe backoff of a subscription, against the fake server. Ported from ResubscribeBackoffTest.java (1 test):
// testBackoffResetsAfterSuccessfulSubscribe -> Resubscribe_BackoffResetsAfterSuccessfulSubscribe. The numbers are the Java ones:
// 11 failed subscribes, one success, then 7 failed subscribes that must all follow within 3 seconds.
namespace Centrifugal.Centrifuge.Tests {


    [Collection("Integration")]
    public class ResubscribeBackoffTests : IAsyncLifetime {
        private static readonly TimeSpan wait = TimeSpan.FromSeconds(30);
        private const int PrimingFailures = 11;
        private const int MeasuredFailures = 7;
        private const int InsufficientState = 2500;
        private readonly FakeCentrifugoServer server = new FakeCentrifugoServer();
        private CentrifugeClient? client;


        public async Task InitializeAsync() {
            await server.StartAsync();
        }


        public async Task DisposeAsync() {
            if (client != null) {
                await client.DisposeAsync();
            }
            await server.DisposeAsync();
        }


        private Reply TemporaryError(Command cmd) {
            Reply error = server.ErrorReply(108, "not available", true, null);
            error.Id = cmd.Id;
            return error;
        }


        [Fact]
        // Java: testBackoffResetsAfterSuccessfulSubscribe. With the step reset by the successful subscribe, the retries are spaced
        // by at most 1 + 2^k ms for k = 0..5, so all seven land within the window. Without the reset the step carries on from 11.
        public async Task Resubscribe_BackoffResetsAfterSuccessfulSubscribe() {
            int subscribes = 0;
            int measured = 0;
            TaskCompletionSource<bool> measuredAll = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OnCommand = cmd => {
                if (cmd.Subscribe == null) {
                    return null;
                }
                int n = Interlocked.Increment(ref subscribes);
                if (n == PrimingFailures + 1) {
                    return null;
                }
                if (n > PrimingFailures + 1) {
                    if (Interlocked.Increment(ref measured) == MeasuredFailures) {
                        measuredAll.TrySetResult(true);
                    }
                }
                return TemporaryError(cmd);
            };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            List<string> unexpected = new List<string>();
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions {
                // A 1 ms floor keeps the priming failures cheap; the 20 s ceiling leaves room for the step to grow.
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

            // The success: the subscribe that follows the priming failures is the 12th, and its reply is the default one.
            await subscribed.Task.WaitAsync(wait);

            // Send the subscription back to subscribing: the server then fails every attempt.
            await server.SendPushAsync(new Push {
                Channel = "ch",
                Unsubscribe = new Unsubscribe { Code = InsufficientState, Reason = "insufficient state" }
            });

            await measuredAll.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(unexpected);
        }
    }
}
