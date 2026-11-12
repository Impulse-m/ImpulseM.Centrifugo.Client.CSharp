// The retry delay of a failed token refresh: the capped exponential bound and its full jitter. The formula is checked with
// the Java bounds of 10 s and 20 s and the default cap of five minutes. The client passes its own bounds of 5 s and 10 s.
// The upstream rule is kept: a cap at or below the first ceiling gives the flat retry.

using System;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the retry delay of a failed token refresh.</summary>
    public sealed class RefreshRetryPolicyTests {
        private const int MinMillis = 10000;
        private const int MaxMillis = 20000;
        private const int CapMillis = 300000;

        // The largest delay per step with the default cap (the top of the jitter range of each step).
        private static readonly int[] maximumByStep = { 20000, 30000, 50000, 90000, 170000, 300000 };

        // The number of seeded draws checked for each step.
        private const int DrawsPerStep = 50;


        // Java: retryDelayEscalatesAndStopsAtTheCap
        [Fact]
        public void RetryDelayMillis_EscalatesAndStopsAtTheCap() {
            RefreshRetryPolicy low = new RefreshRetryPolicy(FixedRandom.MinimumDraws(), MinMillis, MaxMillis, CapMillis, true);
            RefreshRetryPolicy high = new RefreshRetryPolicy(FixedRandom.MaximumDraws(), MinMillis, MaxMillis, CapMillis, true);

            for (int step = 0; step <= 30; step++) {
                int expected = step < maximumByStep.Length ? maximumByStep[step] : CapMillis;
                Assert.Equal(MinMillis, low.RetryDelayMillis(step));
                Assert.Equal(expected, high.RetryDelayMillis(step));
            }
        }


        // Java: retryDelayDrawsStayInsideTheRange
        [Fact]
        public void RetryDelayMillis_SeededDraws_StayInsideTheRangeOfTheStep() {
            Random random = new Random(42);
            RefreshRetryPolicy policy = new RefreshRetryPolicy(random, MinMillis, MaxMillis, CapMillis, true);

            for (int step = 0; step < maximumByStep.Length; step++) {
                for (int draw = 0; draw < DrawsPerStep; draw++) {
                    int delay = policy.RetryDelayMillis(step);
                    Assert.InRange(delay, MinMillis, maximumByStep[step]);
                }
            }
        }


        // Java: theCapHoldsForAStepBeyondTheDoublings
        [Fact]
        public void RetryDelayMillis_StepsBeyondTheDoublings_NeverExceedTheCap() {
            RefreshRetryPolicy policy = new RefreshRetryPolicy(FixedRandom.MaximumDraws(), MinMillis, MaxMillis, CapMillis, true);

            Assert.Equal(CapMillis, policy.RetryDelayMillis(12));
            Assert.Equal(CapMillis, policy.RetryDelayMillis(30));
            Assert.Equal(CapMillis, policy.RetryDelayMillis(999));
        }


        // A step above the largest step counts as the largest step.
        [Fact]
        public void RetryDelayMillis_StepAboveTheMaximum_IsClampedToTheMaximum() {
            RefreshRetryPolicy policy = new RefreshRetryPolicy(FixedRandom.MaximumDraws(), MinMillis, MaxMillis, CapMillis, true);

            Assert.Equal(policy.RetryDelayMillis(30), policy.RetryDelayMillis(500));
            Assert.Equal(30, policy.MaxRetryStep);
        }


        // A cap at or below the first ceiling keeps the flat retry: every step draws from the first range.
        [Fact]
        public void RetryDelayMillis_CapAtOrBelowTheMaximum_KeepsTheFlatRetry() {
            RefreshRetryPolicy policy = new RefreshRetryPolicy(FixedRandom.MaximumDraws(), MinMillis, MaxMillis, MaxMillis, true);

            for (int step = 0; step <= 30; step++) {
                Assert.Equal(MaxMillis, policy.RetryDelayMillis(step));
            }
        }


        // Only the temporary errors, the client codes below 100 and the code 109 are retried.
        [Fact]
        public void IsRetryableRefresh_ClassifiesTheErrors() {
            RefreshRetryPolicy policy = new RefreshRetryPolicy(FixedRandom.MinimumDraws(), MinMillis, MaxMillis, CapMillis, true);

            Assert.True(policy.IsRetryableRefresh(new CentrifugeException(109, "expired")));
            Assert.True(policy.IsRetryableRefresh(new CentrifugeException(5, "client code")));
            Assert.True(policy.IsRetryableRefresh(new CentrifugeException(101, "temporary", true)));
            Assert.False(policy.IsRetryableRefresh(new CentrifugeException(101, "refused")));
            Assert.False(policy.IsRetryableRefresh(new CentrifugeException(103, "permission")));
        }
    }
}
