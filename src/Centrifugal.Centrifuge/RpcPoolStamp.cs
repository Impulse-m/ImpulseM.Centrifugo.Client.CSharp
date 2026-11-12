namespace Centrifugal.Centrifuge {


    /// <summary>The identity stamp of the confirmed token. Immutable.</summary>
    internal sealed class RpcPoolStamp {
        /// <summary>
        /// Creates a stamp.
        /// </summary>
        /// <param name="generation">The identity generation the stamp was taken at.</param>
        /// <param name="recorded">An identity was recorded.</param>
        /// <param name="user">The confirmed user.</param>
        /// <param name="authClass">The confirmed auth class.</param>
        /// <param name="token">The confirmed token.</param>
        public RpcPoolStamp(long generation, bool recorded, string user, string authClass, string token) {
            Generation = generation;
            Recorded = recorded;
            User = user;
            AuthClass = authClass;
            Token = token;
        }

        /// <summary>The identity generation the stamp was taken at.</summary>
        public long Generation { get; }


        /// <summary>An identity was recorded (a connect or a refresh confirmed one).</summary>
        public bool Recorded { get; }


        /// <summary>The confirmed user.</summary>
        public string User { get; }


        /// <summary>The confirmed auth class.</summary>
        public string AuthClass { get; }


        /// <summary>The confirmed token.</summary>
        public string Token { get; }
    }
}
