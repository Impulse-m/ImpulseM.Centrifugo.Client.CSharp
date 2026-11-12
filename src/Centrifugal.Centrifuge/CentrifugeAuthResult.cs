namespace Centrifugal.Centrifuge {


    /// <summary>The identity and expiry the server confirmed on a token refresh. Immutable.</summary>
    public sealed class CentrifugeAuthResult {
        /// <summary>
        /// Creates the result.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="authClass">The auth class.</param>
        /// <param name="expires">The token expires.</param>
        /// <param name="ttl">The token ttl in seconds.</param>
        internal CentrifugeAuthResult(string user, string authClass, bool expires, uint ttl) {
            User = user;
            AuthClass = authClass;
            Expires = expires;
            Ttl = ttl;
        }

        /// <summary>The user the server confirmed.</summary>
        public string User { get; }


        /// <summary>The auth class the server confirmed.</summary>
        public string AuthClass { get; }


        /// <summary>True when the token expires.</summary>
        public bool Expires { get; }


        /// <summary>The token ttl in seconds. Zero when the token does not expire.</summary>
        public uint Ttl { get; }
    }
}
