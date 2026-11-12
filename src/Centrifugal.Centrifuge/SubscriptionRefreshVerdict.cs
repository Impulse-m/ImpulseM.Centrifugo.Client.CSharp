namespace Centrifugal.Centrifuge {


    /// <summary>What a refused subscription token refresh does next.</summary>
    internal enum SubscriptionRefreshVerdict {
        /// <summary>Retry the refresh after the retry delay.</summary>
        Retry,
        /// <summary>Keep the subscription and arm no timer.</summary>
        Keep,
        /// <summary>Unsubscribe with the error code of the server.</summary>
        Unsubscribe,
    }
}
