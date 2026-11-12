using System;

namespace Centrifugal.Centrifuge {


    /// <summary>Event arguments of the Auth event: the identity the server confirmed. Immutable.</summary>
    public sealed class CentrifugeAuthEventArgs : EventArgs {
        /// <summary>
        /// Creates the arguments. A null value becomes an empty string.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="authClass">The auth class.</param>
        public CentrifugeAuthEventArgs(string? user, string? authClass) {
            User = user ?? string.Empty;
            AuthClass = authClass ?? string.Empty;
        }

        /// <summary>The user the server confirmed.</summary>
        public string User { get; }


        /// <summary>The auth class the server confirmed.</summary>
        public string AuthClass { get; }
    }
}
