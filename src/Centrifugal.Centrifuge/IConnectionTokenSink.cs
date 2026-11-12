namespace Centrifugal.Centrifuge {


    /// <summary>Receives the identity and refresh events the rpc pool token source needs. Implemented by the rpc pool token source.</summary>
    internal interface IConnectionTokenSink {
        /// <summary>After the identity is recorded and the auth event is raised. always is true on connect, false on refresh.</summary>
        void OnAuth(string user, string authClass, bool always);

        /// <summary>From the disconnect path, before the state is written.</summary>
        void OnDisconnect(int code, bool terminal);

        /// <summary>A refresh failed. explicitCall is true for a requested refresh, false for a timer refresh.</summary>
        void OnRefreshFailed(CentrifugeException error, bool explicitCall);
    }
}
