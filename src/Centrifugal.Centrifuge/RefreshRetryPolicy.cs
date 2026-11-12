using System;

namespace Centrifugal.Centrifuge {


    /// <summary>The retry delay of a failed token refresh: capped exponential with full jitter over [min, ceiling].</summary>
    internal sealed class RefreshRetryPolicy : IRefreshRetryPolicy {
        private const int MaxStep = 30;

        // Error codes below this one are client-side codes, which a refresh retries.
        private const int ClientCodeLimit = 100;

        // The connect error code that asks for a token refresh, which a refresh retries.
        private const int RefreshRequiredCode = 109;


        private readonly Random random;
        private readonly int minMillis;
        private readonly int maxMillis;
        private readonly int capMillis;
        private readonly bool stopOnRefusedToken;


        /// <summary>
        /// Creates the policy of one client.
        /// </summary>
        /// <param name="random">The per-client random source.</param>
        /// <param name="minMillis">The lower bound of the delay.</param>
        /// <param name="maxMillis">The upper bound of the first ceiling.</param>
        /// <param name="capMillis">The cap of the ceiling (Options.TokenRetryCap).</param>
        /// <param name="stopOnRefusedToken">Options.StopOnRefusedToken.</param>
        public RefreshRetryPolicy(
            Random random,
            int minMillis,
            int maxMillis,
            int capMillis,
            bool stopOnRefusedToken
        ) {
            this.random = random;
            this.minMillis = minMillis;
            this.maxMillis = maxMillis;
            this.capMillis = capMillis;
            this.stopOnRefusedToken = stopOnRefusedToken;
        }

        /// <summary>Options.StopOnRefusedToken.</summary>
        public bool StopOnRefusedToken {
            get {
                return stopOnRefusedToken;
            }
        }

        /// <summary>The largest step: 30.</summary>
        public int MaxRetryStep {
            get {
                return MaxStep;
            }
        }

        /// <summary>
        /// Delay in whole milliseconds for a retry at this step. The step is clamped to 0..MaxRetryStep.
        /// Without a cap above the maximum the step does not count: the delay is flat.
        /// </summary>
        /// <param name="step">The retry step.</param>
        public int RetryDelayMillis(int step) {
            int clamped = Math.Clamp(step, 0, MaxStep);
            int effectiveStep = capMillis > maxMillis ? clamped : 0;
            int ceiling = CeilingMillis(effectiveStep);
            long spread = Math.Min((long)ceiling, (long)minMillis << effectiveStep);
            long jitter = (long)(random.NextDouble() * (spread + 1));
            return (int)Math.Min((long)ceiling, minMillis + jitter);
        }

        /// <summary>True when the error is temporary, or its code is below 100, or its code is 109.</summary>
        /// <param name="error">The refresh error.</param>
        public bool IsRetryableRefresh(CentrifugeException error) {
            return error.Temporary || error.Code < ClientCodeLimit || error.Code == RefreshRequiredCode;
        }

        private int CeilingMillis(int effectiveStep) {
            long grown = (long)maxMillis << effectiveStep;
            return (int)Math.Max((long)maxMillis, Math.Min((long)capMillis, grown));
        }
    }
}
