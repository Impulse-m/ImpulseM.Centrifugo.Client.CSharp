using System;

namespace Centrifugal.Centrifuge {


    /// <summary>The identity the server last confirmed. Loop only.</summary>
    internal sealed class ConnectionIdentity {
        private readonly IClientHost host;
        private string currentUser = string.Empty;
        private string currentAuthClass = string.Empty;
        private string confirmedToken = string.Empty;
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
                return currentUser;
            }
        }


        /// <summary>The auth class last confirmed. Not cleared on disconnect.</summary>
        public string CurrentAuthClass {
            get {
                return currentAuthClass;
            }
        }


        /// <summary>The connection token at the last confirmation.</summary>
        public string ConfirmedToken {
            get {
                return confirmedToken;
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
        /// notice list, never directly.
        /// </summary>
        /// <param name="result">The connect reply.</param>
        public Exception? OnConnectIdentity(Protocol.ConnectResult result) {
            CentrifugeAuthEventArgs? args = ApplyAuth(result.User, result.AuthClass, true);
            Exception? failure = null;
            if (args != null) {
                failure = host.RaiseAuth(args);
            }
            if (failure == null) {
                tokenSink?.OnAuth(currentUser, currentAuthClass, true);
            }
            return failure;
        }


        /// <summary>
        /// The refresh identity step. Called by AuthRefresh on a successful timer or requested refresh while Connected.
        /// The auth event is raised only when the identity changed. A handler failure is reported by the raise (through
        /// the error event, or logged when nothing listens to it), it does not disconnect, and the sink is then not notified.
        /// </summary>
        /// <param name="user">The user of the refresh reply.</param>
        /// <param name="authClass">The auth class of the refresh reply.</param>
        public void OnRefreshIdentity(string user, string authClass) {
            CentrifugeAuthEventArgs? args = ApplyAuth(user, authClass, false);
            Exception? failure = null;
            if (args != null) {
                failure = host.RaiseAuth(args);
            }
            if (failure == null) {
                tokenSink?.OnAuth(currentUser, currentAuthClass, false);
            }
        }


        /// <summary>
        /// Records the identity and returns the event args when one is due (always, or the identity changed). Null otherwise.
        /// The connection token is the one in effect now, so a refresh that stored its token first is confirmed with it.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="authClass">The auth class.</param>
        /// <param name="always">True on connect: the event is due even when the identity did not change.</param>
        public CentrifugeAuthEventArgs? ApplyAuth(string user, string authClass, bool always) {
            string newUser = user ?? string.Empty;
            string newAuthClass = authClass ?? string.Empty;
            bool changed = newUser != currentUser || newAuthClass != currentAuthClass;
            currentUser = newUser;
            currentAuthClass = newAuthClass;
            confirmedToken = host.ConnectionToken;
            if (always || changed) {
                return new CentrifugeAuthEventArgs(newUser, newAuthClass);
            }
            return null;
        }
    }
}
