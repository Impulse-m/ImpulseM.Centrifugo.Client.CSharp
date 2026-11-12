using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The refresh retry of one subscription: the retry step of the connection's backoff, and the token the server
    /// refused with code 109. Runs on the loop of its client.
    /// </summary>
    internal sealed class SubscriptionRefreshRetry {
        private const int TokenExpiredCode = 109;
        private const int PermissionDeniedCode = 103;
        private readonly IRefreshRetryPolicy policy;
        private int step;
        private string? refusedToken;


        /// <summary>
        /// Creates the retry state of one subscription.
        /// </summary>
        /// <param name="policy">The retry policy shared with the connection refresh.</param>
        internal SubscriptionRefreshRetry(IRefreshRetryPolicy policy) {
            this.policy = policy;
        }


        /// <summary>
        /// True when the token is the one the server refused with 109 and the policy stops on a refused token.
        /// </summary>
        /// <param name="token">The token about to be sent.</param>
        internal bool IsRefused(string token) {
            return policy.StopOnRefusedToken && refusedToken != null && string.Equals(token, refusedToken, StringComparison.Ordinal);
        }


        /// <summary>
        /// Classifies a refused refresh. A retryable error (temporary, code below 100 or 109) retries, and a 109 remembers
        /// the token. Code 103 keeps the subscription. Any other code unsubscribes.
        /// </summary>
        /// <param name="error">The error of the refresh.</param>
        /// <param name="token">The token the refresh was sent with.</param>
        internal SubscriptionRefreshVerdict OnRefusal(CentrifugeException error, string token) {
            if (policy.IsRetryableRefresh(error)) {
                if (error.Code == TokenExpiredCode) {
                    refusedToken = token;
                }
                return SubscriptionRefreshVerdict.Retry;
            }
            if (error.Code == PermissionDeniedCode) {
                return SubscriptionRefreshVerdict.Keep;
            }
            return SubscriptionRefreshVerdict.Unsubscribe;
        }


        /// <summary>
        /// A subscribe succeeded: the retry step starts over.
        /// </summary>
        internal void ResetStep() {
            step = 0;
        }


        /// <summary>
        /// A refresh was accepted: the retry step starts over and the refused token is forgotten.
        /// </summary>
        internal void Accepted() {
            step = 0;
            refusedToken = null;
        }


        /// <summary>
        /// The delay of the next retry in milliseconds. The step moves on, up to the largest step of the policy.
        /// </summary>
        internal int NextDelayMillis() {
            int delay = policy.RetryDelayMillis(step);
            step = Math.Min(step + 1, policy.MaxRetryStep);
            return delay;
        }
    }
}
