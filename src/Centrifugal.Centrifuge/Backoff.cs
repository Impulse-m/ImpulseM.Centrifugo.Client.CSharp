using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The rule of the fork's retry delays: a capped exponential bound, and a full-jitter draw under that bound.
    /// The upstream reconnect delay keeps its own rule in <c>Utilities.CalculateBackoff</c>.
    /// </summary>
    internal static class Backoff {
        /// <summary>
        /// The bound of a retry at a step: the base shifted left by the step, capped at the cap.
        /// </summary>
        /// <param name="baseMillis">The bound of the first retry in milliseconds.</param>
        /// <param name="step">The retry step, zero for the first retry.</param>
        /// <param name="capMillis">The largest bound in milliseconds.</param>
        /// <returns>The bound in milliseconds.</returns>
        internal static long CappedExponential(long baseMillis, int step, long capMillis) {
            return Math.Min(capMillis, baseMillis << step);
        }


        /// <summary>
        /// The delay of a retry at a step with full jitter: a uniform draw in [0, bound], where the bound is
        /// <see cref="CappedExponential"/> for the same arguments. The fork's retry rules all draw their delay this way.
        /// </summary>
        /// <param name="random">The source of the draw.</param>
        /// <param name="baseMillis">The bound of the first retry in milliseconds.</param>
        /// <param name="step">The retry step, zero for the first retry.</param>
        /// <param name="capMillis">The largest bound in milliseconds.</param>
        /// <returns>The drawn milliseconds.</returns>
        internal static long CappedJitter(Random random, long baseMillis, int step, long capMillis) {
            return FullJitter(random, CappedExponential(baseMillis, step, capMillis));
        }


        /// <summary>
        /// A uniform draw over whole milliseconds, both ends included, in [0, bound]. A bound below zero draws zero.
        /// The bound is kept below <see cref="int.MaxValue"/> so that the draw fits the random source.
        /// </summary>
        /// <param name="random">The source of the draw.</param>
        /// <param name="boundMillis">The largest value of the draw in milliseconds.</param>
        /// <returns>The drawn milliseconds.</returns>
        private static long FullJitter(Random random, long boundMillis) {
            long upper = Math.Clamp(boundMillis, 0L, int.MaxValue - 1L);
            return random.Next((int)upper + 1);
        }
    }
}
