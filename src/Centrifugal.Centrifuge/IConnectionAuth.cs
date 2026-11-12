using System;

namespace Centrifugal.Centrifuge {


    /// <summary>What the rpc pool token source and the token exchange read from the connection auth state. Implemented by AuthRefresh.</summary>
    internal interface IConnectionAuth {
        /// <summary>The token at the last identity confirmation (Java confirmedToken). Empty when none.</summary>
        string ConfirmedToken { get; }

        /// <summary>The last confirmed user. Not cleared on disconnect.</summary>
        string CurrentUser { get; }

        /// <summary>The last confirmed auth class. Not cleared on disconnect.</summary>
        string CurrentAuthClass { get; }

        /// <summary>A token exchange or a token getter is set.</summary>
        bool CanRenew { get; }

        /// <summary>A requested refresh is in flight or queued (Java isInFlight).</summary>
        bool IsInFlight { get; }

        /// <summary>Counts every accepted refresh call and every accepted token.</summary>
        long RefreshGeneration { get; }

        /// <summary>True when the token has an expiry record that is past. False without a token exchange.</summary>
        bool TokenExpired(string token);

        /// <summary>Starts a timer refresh at 0 ms when Connected, not in flight and renewable. Returns true when started.</summary>
        bool RenewNow();

        /// <summary>
        /// Creates the rpc pool token source on first use (Java tokenSource()) and hands it the identity sink.
        /// A client that no pool uses never calls this.
        /// </summary>
        IRpcPoolTokenSource AcquireTokenSource();
    }
}
