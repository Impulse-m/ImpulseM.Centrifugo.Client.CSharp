using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The delay of the later resends of a refused rpc call. The main client and the rpc pool take the rule from here,
    /// so the two cannot disagree.
    /// </summary>
    internal static class RpcRetryDelays {
        /// <summary>The number of later resends of one call.</summary>
        internal const int LaterResends = 3;

        /// <summary>The base of the later delay in milliseconds: the n-th later resend waits at most this value shifted left by n.</summary>
        internal const int LaterBaseMillis = 250;


        /// <summary>
        /// The delay of a later resend (full jitter). It is a uniform draw over whole milliseconds, both ends included,
        /// in [0, bound], where bound is <see cref="LaterBaseMillis"/> shifted left by resends, capped at the remaining
        /// deadline. The remaining deadline is rounded down to whole milliseconds, so a resend never falls past the deadline.
        /// </summary>
        /// <param name="random">The source of the draw.</param>
        /// <param name="resends">The number of later resends already made (0, 1 or 2 for the first, second and third).</param>
        /// <param name="remaining">The time left until the deadline of the call.</param>
        /// <returns>The delay of the resend.</returns>
        internal static TimeSpan LaterDelay(Random random, int resends, TimeSpan remaining) {
            long remainingMs = remaining.Ticks / TimeSpan.TicksPerMillisecond;
            long boundMs = Math.Max(0L, Math.Min((long)LaterBaseMillis << resends, remainingMs));
            long drawnMs = random.Next((int)boundMs + 1);
            return TimeSpan.FromMilliseconds(drawnMs);
        }
    }
}
