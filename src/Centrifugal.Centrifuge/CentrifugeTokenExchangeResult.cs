namespace Centrifugal.Centrifuge {


    /// <summary>The result of a token exchange: the token, and the refresh token when the issuer rotated it. Immutable.</summary>
    public sealed class CentrifugeTokenExchangeResult {
        /// <summary>
        /// Creates the result.
        /// </summary>
        /// <param name="token">The access token, which becomes the connection token.</param>
        /// <param name="refreshToken">The rotated refresh token, or null when the issuer did not rotate it.</param>
        /// <param name="graceSeconds">The grace in seconds, or -1 for none published.</param>
        public CentrifugeTokenExchangeResult(string token, string? refreshToken, int graceSeconds = -1) {
            Token = token;
            RefreshToken = refreshToken;
            GraceSeconds = graceSeconds;
        }

        /// <summary>The access token, which becomes the connection token.</summary>
        public string Token { get; }


        /// <summary>The rotated refresh token, or null when the issuer did not rotate it.</summary>
        public string? RefreshToken { get; }


        /// <summary>The grace in seconds, or -1 when the issuer published none.</summary>
        public int GraceSeconds { get; }
    }
}
