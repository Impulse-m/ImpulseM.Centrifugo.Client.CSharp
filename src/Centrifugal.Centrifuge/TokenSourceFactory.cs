namespace Centrifugal.Centrifuge {


    /// <summary>
    /// Creates the rpc pool token source of one client and hands it the identity sink.
    /// Throws NotImplementedException until the token source is written. Reached only through AuthRefresh.AcquireTokenSource.
    /// </summary>
    internal static class TokenSourceFactory {
        /// <summary>
        /// Creates the token source and sets it as the identity sink, once.
        /// </summary>
        /// <param name="auth">The connection auth state.</param>
        /// <param name="host">The client seam.</param>
        /// <param name="identity">The identity of the client.</param>
        public static IRpcPoolTokenSource Create(IConnectionAuth auth, IClientHost host, ConnectionIdentity identity) {
            throw new System.NotImplementedException();
        }
    }
}
