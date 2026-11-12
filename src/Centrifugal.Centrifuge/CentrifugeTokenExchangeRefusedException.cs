namespace Centrifugal.Centrifuge {


    /// <summary>The issuer refused the refresh token of a token exchange. Carries the server error of the rpc.</summary>
    public sealed class CentrifugeTokenExchangeRefusedException : CentrifugeUnauthorizedException {
        /// <summary>
        /// Creates the exception from the server error of the rpc.
        /// </summary>
        /// <param name="serverError">The server error: code, message, temporary, app code, app type and metadata.</param>
        internal CentrifugeTokenExchangeRefusedException(CentrifugeException serverError) : base(serverError.Message) {
            ServerError = serverError;
        }

        /// <summary>The server error the rpc was rejected with: code, message, temporary, app code, app type and metadata.</summary>
        public CentrifugeException ServerError { get; }
    }
}
