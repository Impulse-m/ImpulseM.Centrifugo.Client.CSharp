using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks that the fixed random source gives the draws the retry tests rely on.</summary>
    public sealed class FixedRandomTests {
        /// <summary>NextDouble returns the value the source was created with, on every call.</summary>
        [Fact]
        public void NextDouble_ReturnsTheValueOnEveryCall() {
            FixedRandom random = new FixedRandom(0.25);

            Assert.Equal(0.25, random.NextDouble());
            Assert.Equal(0.25, random.NextDouble());
        }

        /// <summary>The maximum source draws just below one.</summary>
        [Fact]
        public void MaximumDraws_IsJustBelowOne() {
            double draw = FixedRandom.MaximumDraws().NextDouble();

            Assert.InRange(draw, 0.999, 1.0);
            Assert.True(draw < 1.0);
        }

        /// <summary>The minimum source draws zero.</summary>
        [Fact]
        public void MinimumDraws_IsZero() {
            Assert.Equal(0.0, FixedRandom.MinimumDraws().NextDouble());
        }

        /// <summary>With the maximum draws a retry waits the largest delay of its first bounds (5000 to 10000 ms).</summary>
        [Fact]
        public void MaximumDraws_GivesTheLargestFirstRetryDelay() {
            RefreshRetryPolicy policy = new RefreshRetryPolicy(FixedRandom.MaximumDraws(), 5000, 10000, 300000, true);

            Assert.Equal(10000, policy.RetryDelayMillis(0));
        }

        /// <summary>With the minimum draws a retry waits the smallest delay of its first bounds (5000 to 10000 ms).</summary>
        [Fact]
        public void MinimumDraws_GivesTheSmallestFirstRetryDelay() {
            RefreshRetryPolicy policy = new RefreshRetryPolicy(FixedRandom.MinimumDraws(), 5000, 10000, 300000, true);

            Assert.Equal(5000, policy.RetryDelayMillis(0));
        }
    }
}
