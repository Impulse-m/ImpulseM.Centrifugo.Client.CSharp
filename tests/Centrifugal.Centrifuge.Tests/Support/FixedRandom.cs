using System;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// A random source whose draws are always the same value, so a test chooses the delay of each retry.
    /// </summary>
    public sealed class FixedRandom : Random {
        private readonly double value;


        /// <summary>
        /// Creates the source.
        /// </summary>
        /// <param name="value">The value every NextDouble call returns, in [0, 1).</param>
        public FixedRandom(double value) {
            this.value = value;
        }

        /// <summary>A source whose draws are the top of their range: a retry waits the largest delay of its bounds.</summary>
        public static Random MaximumDraws() {
            return new FixedRandom(1.0 - 1e-12);
        }

        /// <summary>A source whose draws are the bottom of their range: a retry waits the smallest delay of its bounds.</summary>
        public static Random MinimumDraws() {
            return new FixedRandom(0.0);
        }

        /// <summary>Returns the value the source was created with.</summary>
        public override double NextDouble() {
            return value;
        }
    }
}
