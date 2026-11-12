using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The factory of the token exchange hooks of one client. The only static entry point.
    /// Returns the null object when no exchange is set.
    /// </summary>
    internal static class TokenExchangeFlow {
        /// <summary>
        /// Creates the hooks of one client.
        /// </summary>
        /// <param name="host">The client seam.</param>
        /// <param name="exchange">Options.TokenExchange, or null.</param>
        /// <param name="random">The per-client random source (the renewal share draw).</param>
        public static ITokenExchangeHooks Create(IClientHost host, CentrifugeTokenExchange? exchange, Random random) {
            if (exchange == null) {
                return new NullTokenExchangeHooks();
            }
            throw new NotImplementedException();
        }
    }
}
