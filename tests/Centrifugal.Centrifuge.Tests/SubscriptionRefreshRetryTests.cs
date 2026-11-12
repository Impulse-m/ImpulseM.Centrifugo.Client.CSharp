using System.Collections.Generic;
using System.Linq;
using Xunit;

// Unit tests of the subscription refresh retry (SubscriptionRefreshRetry) with a stub policy. Retry_UsesPolicyDelay uses the
// real RefreshRetryPolicy with its bounds of 10000 and 20000 ms. The timed parts of the refusals are in SubscriptionRefreshTests.
namespace Centrifugal.Centrifuge.Tests {


    public class SubscriptionRefreshRetryTests {
        private sealed class StubPolicy : IRefreshRetryPolicy {
            public bool StopOnRefusedToken { get; set; } = true;

            public int MaxRetryStep {
                get {
                    return 30;
                }
            }

            public List<int> Steps { get; } = new List<int>();


            public int RetryDelayMillis(int step) {
                Steps.Add(step);
                return 1000 + step;
            }


            public bool IsRetryableRefresh(CentrifugeException error) {
                return error.Temporary || error.Code < 100 || error.Code == 109;
            }
        }


        [Fact]
        // Java: subscriptionRefreshRetryUsesTheFormula
        // The real policy with bounds of 10000 and 20000 ms and cap 300000, and the top draw: the first retry waits the
        // ceiling of step 0, and the second waits 10 s plus 20 s: the minimum plus the top of its spread, under the
        // step-1 ceiling of 40 s.
        public void Retry_UsesPolicyDelay() {
            RefreshRetryPolicy policy = new RefreshRetryPolicy(FixedRandom.MaximumDraws(), 10000, 20000, 300000, true);
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(policy);

            Assert.Equal(20000, retry.NextDelayMillis());
            Assert.Equal(30000, retry.NextDelayMillis());
        }


        [Fact]
        public void Retry_StepStopsAtTheLargestStep() {
            StubPolicy policy = new StubPolicy();
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(policy);

            for (int i = 0; i < 40; i++) {
                retry.NextDelayMillis();
            }

            Assert.Equal(30, policy.Steps.Last());
            Assert.Equal(30, policy.Steps.Max());
        }


        [Fact]
        // Java: subscriptionRefreshAcceptedResetsTheRetryStep
        public void Accepted_ResetsStep() {
            StubPolicy policy = new StubPolicy();
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(policy);
            retry.NextDelayMillis();
            retry.NextDelayMillis();

            retry.Accepted();

            Assert.Equal(1000, retry.NextDelayMillis());
            Assert.Equal(0, policy.Steps.Last());
        }


        [Fact]
        public void ResetStep_AfterSubscribe_RestartsTheRetryDelay() {
            StubPolicy policy = new StubPolicy();
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(policy);
            retry.NextDelayMillis();

            retry.ResetStep();

            Assert.Equal(1000, retry.NextDelayMillis());
        }


        [Fact]
        // Java: subscriptionRefreshRefusedWith109IsRetried
        public void OnRefusal_109_RetriesAndRemembersToken() {
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(new StubPolicy());

            SubscriptionRefreshVerdict verdict = retry.OnRefusal(new CentrifugeException(109, "token expired"), "t1");

            Assert.Equal(SubscriptionRefreshVerdict.Retry, verdict);
            Assert.True(retry.IsRefused("t1"));
            Assert.False(retry.IsRefused("t2"));
        }


        [Fact]
        // Java: subscriptionRefreshWith103KeepsTheConnection
        public void OnRefusal_103_Keeps() {
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(new StubPolicy());

            SubscriptionRefreshVerdict verdict = retry.OnRefusal(new CentrifugeException(103, "permission denied"), "t1");

            Assert.Equal(SubscriptionRefreshVerdict.Keep, verdict);
            Assert.False(retry.IsRefused("t1"));
        }


        [Fact]
        public void OnRefusal_TemporaryPermissionDenied_Retries() {
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(new StubPolicy());

            SubscriptionRefreshVerdict verdict = retry.OnRefusal(new CentrifugeException(103, "permission denied", true), "t1");

            Assert.Equal(SubscriptionRefreshVerdict.Retry, verdict);
        }


        [Fact]
        // Java: subscriptionRefreshCodeBelow100IsRetried
        public void OnRefusal_CodeBelow100_Retries() {
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(new StubPolicy());

            SubscriptionRefreshVerdict verdict = retry.OnRefusal(new CentrifugeException(5, "client error"), "t1");

            Assert.Equal(SubscriptionRefreshVerdict.Retry, verdict);
        }


        [Fact]
        public void OnRefusal_OtherCode_Unsubscribes() {
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(new StubPolicy());

            SubscriptionRefreshVerdict verdict = retry.OnRefusal(new CentrifugeException(3000, "forbidden"), "t1");

            Assert.Equal(SubscriptionRefreshVerdict.Unsubscribe, verdict);
        }


        [Fact]
        // Java: subscriptionRefreshDoesNotResendARefusedToken
        // The policy check: a refused token is not sent again.
        public void IsRefused_RefusedTokenIsNotSentAgain() {
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(new StubPolicy());
            retry.OnRefusal(new CentrifugeException(109, "token expired"), "t1");

            Assert.True(retry.IsRefused("t1"));
        }


        [Fact]
        public void IsRefused_StopOnRefusedTokenOff_IsFalse() {
            StubPolicy policy = new StubPolicy { StopOnRefusedToken = false };
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(policy);
            retry.OnRefusal(new CentrifugeException(109, "token expired"), "t1");

            Assert.False(retry.IsRefused("t1"));
        }


        [Fact]
        public void Accepted_ForgetsRefusedToken() {
            SubscriptionRefreshRetry retry = new SubscriptionRefreshRetry(new StubPolicy());
            retry.OnRefusal(new CentrifugeException(109, "token expired"), "t1");

            retry.Accepted();

            Assert.False(retry.IsRefused("t1"));
        }
    }
}
