using System;

namespace Centrifugal.Centrifuge {


    /// <summary>The identity the server last confirmed. Loop only.</summary>
    internal sealed class ConnectionIdentity {
        private readonly IClientHost host;
        private IConnectionTokenSink? tokenSink;


        /// <summary>
        /// Creates the identity of one client.
        /// </summary>
        /// <param name="host">The client seam.</param>
        public ConnectionIdentity(IClientHost host) {
            this.host = host;
        }


        /// <summary>The user last confirmed. Not cleared on disconnect.</summary>
        public string CurrentUser {
            get {
                return string.Empty;
            }
        }


        /// <summary>The auth class last confirmed. Not cleared on disconnect.</summary>
        public string CurrentAuthClass {
            get {
                return string.Empty;
            }
        }


        /// <summary>The connection token at the last confirmation.</summary>
        public string ConfirmedToken {
            get {
                return string.Empty;
            }
        }


        /// <summary>The identity sink, or null while no pool uses the client.</summary>
        public IConnectionTokenSink? TokenSink {
            get {
                return tokenSink;
            }
        }


        /// <summary>Sets the identity sink. Called once, when the token source is created.</summary>
        /// <param name="sink">The identity sink.</param>
        public void SetTokenSink(IConnectionTokenSink sink) {
            tokenSink = sink;
        }


        /// <summary>
        /// The connect identity step. Records the identity, raises the auth event through IClientHost.RaiseAuth, and
        /// notifies the sink when the raise did not fail. Returns the handler failure, or null. Called from the connect
        /// notice list, never directly. Returns null and raises no event until the identity is recorded.
        /// </summary>
        /// <param name="result">The connect reply.</param>
        public Exception? OnConnectIdentity(Protocol.ConnectResult result) {
            return null;
        }


        /// <summary>
        /// The refresh identity step. Called by AuthRefresh on a successful timer or requested refresh while Connected.
        /// The auth event is report-only. The sink is notified always. Does nothing until the identity is recorded.
        /// </summary>
        /// <param name="user">The user of the refresh reply.</param>
        /// <param name="authClass">The auth class of the refresh reply.</param>
        public void OnRefreshIdentity(string user, string authClass) {
        }


        /// <summary>
        /// Records the identity and returns the event args when one is due (always, or the identity changed). Null otherwise.
        /// Throws NotImplementedException until the identity is recorded.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="authClass">The auth class.</param>
        /// <param name="always">True on connect: the event is due even when the identity did not change.</param>
        public CentrifugeAuthEventArgs? ApplyAuth(string user, string authClass, bool always) {
            throw new NotImplementedException();
        }
    }
}
