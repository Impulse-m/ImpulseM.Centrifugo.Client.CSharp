using System;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// A random source for the tests, so a test chooses the draws. Created from a value, NextDouble returns the value and
    /// every Next(bound) draw is the value's share of the bound. Created from a function, every Next(bound) draw is the
    /// result of the function, so a test scripts the draws in order or by bound.
    /// </summary>
    public sealed class FixedRandom : Random {
        /// <summary>The exclusive upper bound of the last Next draw; zero before the first draw.</summary>
        public int LastBound { get; private set; }


        private readonly double? value;
        private readonly Func<int, int>? draw;


        /// <summary>
        /// Creates the source whose NextDouble and Next draws follow one value.
        /// </summary>
        /// <param name="value">The value every NextDouble call returns, in [0, 1).</param>
        public FixedRandom(double value) {
            this.value = value;
        }


        /// <summary>
        /// Creates the source whose Next draws are given by a function.
        /// </summary>
        /// <param name="draw">Returns the draw for an exclusive upper bound.</param>
        public FixedRandom(Func<int, int> draw) {
            this.draw = draw;
        }


        /// <summary>A source whose draws are the top of their range: a retry waits the largest delay of its bounds.</summary>
        public static Random MaximumDraws() {
            return new FixedRandom(1.0 - 1e-12);
        }


        /// <summary>A source whose draws are the bottom of their range: a retry waits the smallest delay of its bounds.</summary>
        public static Random MinimumDraws() {
            return new FixedRandom(0.0);
        }


        /// <summary>A source whose Next draws return the given values in order, one per draw.</summary>
        /// <param name="values">The values returned by the draws, in order.</param>
        public static FixedRandom Scripted(params int[] values) {
            int index = 0;
            return new FixedRandom(bound => values[index++]);
        }


        /// <summary>Returns the value the source was created with. A source created from a function has no value.</summary>
        /// <exception cref="InvalidOperationException">The source was created from a function.</exception>
        public override double NextDouble() {
            if (value.HasValue) {
                return value.Value;
            }
            throw new InvalidOperationException("The source draws by function and has no NextDouble value.");
        }


        /// <summary>Returns the result of the function, or the value's share of the bound, and records the bound.</summary>
        /// <param name="maxValue">The exclusive upper bound of the draw.</param>
        public override int Next(int maxValue) {
            LastBound = maxValue;
            if (draw != null) {
                return draw(maxValue);
            }
            int share = (int)(value.GetValueOrDefault() * maxValue);
            return Math.Min(share, Math.Max(0, maxValue - 1));
        }
    }
}
