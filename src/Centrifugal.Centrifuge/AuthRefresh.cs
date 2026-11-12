using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The connection token refresh (Java AuthRefresh). Implements the state the token source and the token exchange
    /// read (IConnectionAuth). The refresh timer and the refresh call are not started until the refresh path is written.
    /// </summary>
    internal sealed class AuthRefresh : IConnectionAuth {
        private readonly IClientHost host;
        private readonly ConnectionIdentity identity;
        private readonly ITokenExchangeHooks tokenExchange;
        private readonly IStepTimerFactory timers;
        private readonly IRefreshRetryPolicy retryPolicy;


        /// <summary>
        /// Creates the refresh of one client.
        /// </summary>
        /// <param name="host">The client seam.</param>
        /// <param name="identity">The identity of the client.</param>
        /// <param name="tokenExchange">The token exchange hooks (the null object when none is set).</param>
        /// <param name="timers">The step timer factory.</param>
        /// <param name="retryPolicy">The retry policy of the refresh.</param>
        internal AuthRefresh(
            IClientHost host,
            ConnectionIdentity identity,
            ITokenExchangeHooks tokenExchange,
            IStepTimerFactory timers,
            IRefreshRetryPolicy retryPolicy
        ) {
            this.host = host;
            this.identity = identity;
            this.tokenExchange = tokenExchange;
            this.timers = timers;
            this.retryPolicy = retryPolicy;
        }


        /// <summary>The token at the last identity confirmation.</summary>
        public string ConfirmedToken {
            get {
                return identity.ConfirmedToken;
            }
        }


        /// <summary>The last confirmed user.</summary>
        public string CurrentUser {
            get {
                return identity.CurrentUser;
            }
        }


        /// <summary>The last confirmed auth class.</summary>
        public string CurrentAuthClass {
            get {
                return identity.CurrentAuthClass;
            }
        }


        /// <summary>True when a connection token renewal can be requested. False until the refresh path is written.</summary>
        public bool CanRenew {
            get {
                return false;
            }
        }


        /// <summary>True while a connection token refresh is in flight. False until the refresh path is written.</summary>
        public bool IsInFlight {
            get {
                return false;
            }
        }


        /// <summary>The number of accepted connection token refreshes. Zero until the refresh path is written.</summary>
        public long RefreshGeneration {
            get {
                return 0;
            }
        }


        /// <summary>Called once per connect attempt, before the connect command is built. Runs the token exchange hook.</summary>
        public void BeforeConnect() {
            tokenExchange.BeforeConnect();
        }


        /// <summary>Invalidates a timer refresh fetch. No-op until the refresh path is written.</summary>
        public void InvalidateTimerFetch() {
        }


        /// <summary>
        /// Called from the disconnect path, before the state is written. Resets the token exchange.
        /// </summary>
        /// <param name="code">The disconnect code.</param>
        /// <param name="terminal">True when the session ends.</param>
        public void OnDisconnect(int code, bool terminal) {
            tokenExchange.Reset(code, terminal);
        }


        /// <summary>Fails the waiting refresh calls at close. No-op until the refresh path is written.</summary>
        public void FailWaiting() {
        }


        /// <summary>
        /// Schedules the expiry refresh from the expires and ttl of a connect reply. Throws NotImplementedException until the
        /// refresh path is written.
        /// </summary>
        /// <param name="expires">The token expires.</param>
        /// <param name="ttl">The ttl in seconds.</param>
        public void ScheduleExpiry(bool expires, uint ttl) {
            throw new NotImplementedException();
        }


        /// <summary>True when the token has an expiry record that is past.</summary>
        /// <param name="token">The token.</param>
        public bool TokenExpired(string token) {
            return tokenExchange.IsTokenExpired(token);
        }


        /// <summary>Starts a timer refresh at 0 ms. Returns false until the refresh path is written.</summary>
        public bool RenewNow() {
            return false;
        }


        /// <summary>
        /// Creates the rpc pool token source on first use. Throws NotImplementedException until the refresh path is written.
        /// </summary>
        public IRpcPoolTokenSource AcquireTokenSource() {
            throw new NotImplementedException();
        }
    }
}
