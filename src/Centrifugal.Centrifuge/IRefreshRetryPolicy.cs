namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The retry rules of a failed token refresh. One implementation per client, shared by the connection refresh and
    /// the subscription refresh. The step counter of each retry chain is kept by its owner, not here.
    /// </summary>
    internal interface IRefreshRetryPolicy {
        /// <summary>Options.StopOnRefusedToken (default true).</summary>
        bool StopOnRefusedToken { get; }

        /// <summary>The largest step: 30 (Java RETRY_MAX_STEP).</summary>
        int MaxRetryStep { get; }

        /// <summary>Delay in whole milliseconds for a retry at this step. The formula is in RefreshRetryPolicy. The step is clamped to 0..MaxRetryStep.</summary>
        int RetryDelayMillis(int step);

        /// <summary>True when the error is temporary, or its code is below 100, or its code is 109 (Java isRetryableRefresh).</summary>
        bool IsRetryableRefresh(CentrifugeException error);
    }
}
