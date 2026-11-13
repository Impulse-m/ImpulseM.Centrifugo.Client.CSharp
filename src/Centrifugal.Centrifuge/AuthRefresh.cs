using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Centrifugal.Centrifuge.Transports;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The connection token refresh of one client: the requested refreshes (single flight, shared commands, the bound on
    /// the calls in progress), the identity they confirm, and the expiry timer with its retries. The timer part is in
    /// AuthRefreshTimer.cs. Runs on the loop only. Implements the state the token source and the token exchange read.
    /// </summary>
    internal sealed partial class AuthRefresh : IConnectionAuth {
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


        /// <summary>True when the connection token can be renewed by a timer refresh: a token exchange or a token getter is set.</summary>
        public bool CanRenew {
            get {
                return tokenExchange.IsActive || host.HasTokenGetter;
            }
        }


        /// <summary>True while a requested refresh is in progress or a refresh command is in flight.</summary>
        public bool IsInFlight {
            get {
                return commandInFlight || requestedInProgress > 0;
            }
        }


        /// <summary>Counts every accepted refresh call and every accepted token.</summary>
        public long RefreshGeneration {
            get {
                return refreshGeneration;
            }
        }


        // The most requested refreshes that may be in progress at once.
        private const int MaxInProgress = 32;

        // The server code that refuses a connection token as expired (the refresh is retried with a fresh token).
        private const int RefreshRequiredCode = 109;

        // The server code that refuses the refresh for permission: the connection and its identity are kept.
        private const int PermissionDeniedCode = 103;

        private readonly IClientHost host;
        private readonly ConnectionIdentity identity;
        private readonly ITokenExchangeHooks tokenExchange;
        private readonly IStepTimerFactory timers;
        private readonly IRefreshRetryPolicy retryPolicy;

        // Queued refreshes, in order: requested entries (with a token) and timer entries (token null).
        private readonly List<WaitingRefresh> waiting = new List<WaitingRefresh>();

        // Requested refresh commands made while the client is not Connected. Sent by the next connect.
        private readonly List<RefreshCommand> connectQueue = new List<RefreshCommand>();

        private bool inFlight;
        private bool commandInFlight;
        private string? inFlightToken;
        private List<TaskCompletionSource<CentrifugeAuthResult>> inFlightCallbacks = new List<TaskCompletionSource<CentrifugeAuthResult>>();
        private int requestedInProgress;
        private bool connectTokenStored;
        private string tokenBeforeConnect = string.Empty;
        private string? connectTokenSet;
        private IStepTimer? timer;
        private long timerGeneration;
        private bool timerSuperseded;
        private long refreshGeneration;
        private long sessionGeneration;
        private int retryStep;
        private string? refusedTimerToken;
        private IRpcPoolTokenSource? tokenSource;


        /// <summary>
        /// Creates the rpc pool token source on first use and hands it the identity sink.
        /// </summary>
        public IRpcPoolTokenSource AcquireTokenSource() {
            if (tokenSource == null) {
                tokenSource = TokenSourceFactory.Create(this, host, identity);
            }
            return tokenSource;
        }


        /// <summary>Called once per connect attempt, before the connect command is built.</summary>
        public void BeforeConnect() {
            tokenExchange.BeforeConnect();
        }


        /// <summary>Invalidates a timer fetch that is in progress: its token is dropped when it arrives.</summary>
        public void InvalidateTimerFetch() {
            sessionGeneration++;
        }


        /// <summary>
        /// Called from the disconnect path, before the state is written. Cancels the expiry timer, resets the token
        /// exchange and notifies the identity sink.
        /// </summary>
        /// <param name="code">The disconnect code.</param>
        /// <param name="terminal">True when the session ends.</param>
        public void OnDisconnect(int code, bool terminal) {
            CancelTimer();
            commandInFlight = false;
            timerSuperseded = false;
            tokenExchange.Reset(code, terminal);
            identity.TokenSink?.OnDisconnect(code, terminal);
        }


        /// <summary>
        /// Fails every queued requested refresh at close, with a closed error. Timer entries are dropped. Does not
        /// complete the refresh in flight: its own command outcome does.
        /// </summary>
        public void FailWaiting() {
            List<WaitingRefresh> entries = new List<WaitingRefresh>(waiting);
            waiting.Clear();
            foreach (WaitingRefresh entry in entries) {
                if (entry.Token == null) {
                    continue;
                }
                requestedInProgress -= entry.Waiters.Count;
                Deliver(entry.Waiters, null, ClosedError());
            }
            List<RefreshCommand> queued = new List<RefreshCommand>(connectQueue);
            connectQueue.Clear();
            foreach (RefreshCommand call in queued) {
                Answer(call, null, ClosedError());
            }
        }


        /// <summary>
        /// Starts a timer refresh at 0 ms when the client is Connected and no refresh is in flight. Returns true when the
        /// refresh was started.
        /// </summary>
        public bool RenewNow() {
            // The private flag also covers a timer fetch in progress, which IsInFlight does not.
            if (!host.IsConnected || inFlight || !CanRenew) {
                return false;
            }
            ScheduleTimer(0);
            return true;
        }


        /// <summary>True when the token has an expiry record that is past. False without a token exchange.</summary>
        /// <param name="token">The token.</param>
        public bool TokenExpired(string token) {
            return tokenExchange.IsTokenExpired(token);
        }


        /// <summary>
        /// Runs one requested refresh. The waiter is completed in a later step with the identity, or with an error. The
        /// refresh is shared with a queued or in-flight refresh of the same token, and queued behind another one.
        /// </summary>
        /// <param name="token">The connection token to send.</param>
        /// <param name="waiter">Completed with the outcome of the refresh.</param>
        internal void RefreshWith(string token, TaskCompletionSource<CentrifugeAuthResult> waiter) {
            if (host.IsClosed) {
                host.Loop.Complete(() => waiter.TrySetException(ClosedError()));
                return;
            }
            if (requestedInProgress >= MaxInProgress) {
                host.Loop.Complete(() => waiter.TrySetException(new CentrifugeException(
                    CentrifugeErrorCodes.LimitExceeded, "too many refreshes are in progress", false)));
                return;
            }
            requestedInProgress++;
            refreshGeneration++;
            if (!host.IsConnected) {
                KeepTokenForConnect(token);
            }
            WaitingRefresh? queued = FindQueuedRequested(token);
            if (queued != null) {
                queued.Waiters.Add(waiter);
                return;
            }
            if (inFlightToken != null && inFlightToken == token) {
                inFlightCallbacks.Add(waiter);
                return;
            }
            if (inFlight) {
                WaitingRefresh entry = new WaitingRefresh(token, refreshGeneration);
                entry.Waiters.Add(waiter);
                waiting.Add(entry);
                return;
            }
            inFlight = true;
            StartRequested(token, new List<TaskCompletionSource<CentrifugeAuthResult>> { waiter });
        }


        /// <summary>
        /// Arms the expiry timer from the expires and ttl of an accepted connect or refresh, and resets the retry step.
        /// Also sends the refresh commands that waited for the connection.
        /// </summary>
        /// <param name="expires">The token expires.</param>
        /// <param name="ttl">The ttl in seconds.</param>
        internal void ScheduleExpiry(bool expires, uint ttl) {
            retryStep = 0;
            tokenExchange.Observe(expires, ttl);
            if (expires) {
                ScheduleTimer(tokenExchange.RenewalDelayMilliseconds(ttl));
            }
            else {
                CancelTimer();
            }
            FlushConnectQueue();
        }


        /// <summary>
        /// Cancels the expiry timer. The next timer step of a cancelled timer is ignored.
        /// </summary>
        internal void CancelTimer() {
            timerGeneration++;
            timer?.Cancel();
            timer = null;
        }


        private WaitingRefresh? FindQueuedRequested(string token) {
            for (int index = waiting.Count - 1; index >= 0; index--) {
                if (waiting[index].Token == token) {
                    return waiting[index];
                }
            }
            return null;
        }


        private bool HasRequestWaiting() {
            return waiting.Any(entry => entry.Token != null);
        }


        private void KeepTokenForConnect(string token) {
            if (!connectTokenStored) {
                tokenBeforeConnect = host.ConnectionToken;
                connectTokenStored = true;
            }
            connectTokenSet = token;
            host.ConnectionToken = token;
        }


        private void RestoreConnectToken() {
            if (connectTokenStored && host.ConnectionToken == connectTokenSet) {
                host.ConnectionToken = tokenBeforeConnect;
            }
            connectTokenStored = false;
        }


        private void SettleConnectToken(CentrifugeException error) {
            if (requestedInProgress > 0) {
                return;
            }
            if (IsReplyError(error)) {
                RestoreConnectToken();
            }
            else {
                connectTokenStored = false;
            }
        }


        private void StartRequested(string token, List<TaskCompletionSource<CentrifugeAuthResult>> waiters) {
            inFlightToken = token;
            inFlightCallbacks = waiters;
            StartRefreshCommand(token, FinishRequested);
        }


        private void FinishRequested(string token, RefreshResult? result, CentrifugeException? error) {
            List<TaskCompletionSource<CentrifugeAuthResult>> callbacks = inFlightCallbacks;
            inFlightCallbacks = new List<TaskCompletionSource<CentrifugeAuthResult>>();
            inFlightToken = null;
            requestedInProgress -= callbacks.Count;
            bool failed = error != null;
            try {
                if (failed) {
                    SettleConnectToken(error!);
                    Deliver(callbacks, null, error);
                }
                else {
                    if (!string.IsNullOrEmpty(result!.Client)) {
                        host.SetClientId(result.Client);
                    }
                    host.ConnectionToken = token;
                    connectTokenStored = false;
                    timerSuperseded = false;
                    refreshGeneration++;
                    tokenExchange.TokenAccepted();
                    if (host.IsConnected) {
                        ScheduleExpiry(result!.Expires, result.Ttl);
                    }
                    else {
                        tokenExchange.Observe(result!.Expires, result.Ttl);
                    }
                    CentrifugeAuthResult outcome = new CentrifugeAuthResult(result!.User, result.AuthClass, result.Expires, result.Ttl);
                    if (host.IsConnected) {
                        identity.OnRefreshIdentity(result.User, result.AuthClass);
                    }
                    Deliver(callbacks, outcome, null);
                }
            }
            finally {
                Complete(failed);
            }
            if (failed && host.IsConnected) {
                identity.TokenSink?.OnRefreshFailed(error!, true);
            }
        }


        // Runs after every in-flight refresh: starts the next queued entry, or ends the timer retry that a refusal superseded.
        private void Complete(bool refused) {
            inFlight = false;
            commandInFlight = false;
            if (host.IsClosed) {
                FailWaiting();
                host.FlushRpc();
                return;
            }
            while (waiting.Count > 0) {
                WaitingRefresh entry = waiting[0];
                waiting.RemoveAt(0);
                if (entry.Token != null) {
                    inFlight = true;
                    StartRequested(entry.Token, entry.Waiters);
                    return;
                }
                if (!host.IsConnected || entry.Generation != timerGeneration) {
                    continue;
                }
                if (refused) {
                    timerSuperseded = true;
                    continue;
                }
                inFlight = true;
                host.FlushRpc();
                FetchAndRefresh();
                return;
            }
            if (refused && timerSuperseded) {
                timerSuperseded = false;
                ScheduleRetry();
            }
            host.FlushRpc();
        }


        private void StartRefreshCommand(string token, Action<string, RefreshResult?, CentrifugeException?> onDone) {
            Command request = new Command {
                Refresh = new RefreshRequest { Token = token },
            };
            RefreshCommand call = new RefreshCommand(token, request, onDone);
            // The deadline starts when the command is made, also while the client is not Connected.
            call.Deadline = timers.Start(host.TimeoutMs, () => Answer(call, null, new CentrifugeTimeoutException("refresh timeout")));
            if (host.IsConnected) {
                SendRefreshCommand(call);
            }
            else {
                connectQueue.Add(call);
            }
        }


        private void FlushConnectQueue() {
            if (!host.IsConnected || connectQueue.Count == 0) {
                return;
            }
            List<RefreshCommand> pending = new List<RefreshCommand>(connectQueue);
            connectQueue.Clear();
            foreach (RefreshCommand call in pending) {
                if (!call.Answered) {
                    SendRefreshCommand(call);
                }
            }
        }


        private void SendRefreshCommand(RefreshCommand call) {
            ITransport? transport = host.Transport;
            if (transport == null) {
                Answer(call, null, new CentrifugeConnectionLostException(CentrifugeErrorCodes.ConnectionClosed, "connection closed", false, null));
                return;
            }
            Task<Reply> sent = host.SendOnTransportAsync(transport, call.Request, CancellationToken.None);
            sent.ContinueWith(done => host.Loop.Execute(() => OnRefreshReply(call, done)));
        }


        private void OnRefreshReply(RefreshCommand call, Task<Reply> done) {
            if (done.IsFaulted) {
                Exception inner = done.Exception!.InnerException ?? done.Exception!;
                Answer(call, null, inner as CentrifugeException
                    ?? new CentrifugeException(CentrifugeErrorCodes.TransportClosed, inner.Message, true, inner));
                return;
            }
            if (done.IsCanceled) {
                Answer(call, null, new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "refresh cancelled", true));
                return;
            }
            RefreshResult? result = done.Result.Refresh;
            if (result == null) {
                // A reply breaking the protocol: retried as a temporary error.
                Answer(call, null, new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "refresh reply without result", true));
                return;
            }
            Answer(call, result, null);
        }


        // Answers one refresh command once: its deadline is cancelled, and its owner gets the outcome.
        private void Answer(RefreshCommand call, RefreshResult? result, CentrifugeException? error) {
            if (call.Answered) {
                return;
            }
            call.Answered = true;
            call.Deadline?.Cancel();
            call.OnDone(call.Token, result, error);
        }


        private void Deliver(List<TaskCompletionSource<CentrifugeAuthResult>> waiters, CentrifugeAuthResult? result, CentrifugeException? error) {
            foreach (TaskCompletionSource<CentrifugeAuthResult> waiter in waiters) {
                TaskCompletionSource<CentrifugeAuthResult> target = waiter;
                if (error != null) {
                    CentrifugeException failure = error;
                    host.Loop.Complete(() => target.TrySetException(failure));
                }
                else {
                    CentrifugeAuthResult outcome = result!;
                    host.Loop.Complete(() => target.TrySetResult(outcome));
                }
            }
        }


        private static CentrifugeException ClosedError() {
            return new CentrifugeException(CentrifugeErrorCodes.ClientClosed, "client closed", false);
        }


        // A server error reply is an exact CentrifugeException; a timeout and a lost connection are subclasses.
        private static bool IsReplyError(CentrifugeException error) {
            return error.GetType() == typeof(CentrifugeException);
        }


        /// <summary>One refresh command: the request, its outcome callback and its deadline. Answered once.</summary>
        private sealed class RefreshCommand {
            /// <summary>
            /// Creates the command.
            /// </summary>
            /// <param name="token">The token sent.</param>
            /// <param name="request">The command.</param>
            /// <param name="onDone">Receives the outcome: the token, the result or the error.</param>
            public RefreshCommand(string token, Command request, Action<string, RefreshResult?, CentrifugeException?> onDone) {
                Token = token;
                Request = request;
                OnDone = onDone;
            }

            /// <summary>The token sent.</summary>
            public string Token { get; }

            /// <summary>The command.</summary>
            public Command Request { get; }

            /// <summary>Receives the outcome.</summary>
            public Action<string, RefreshResult?, CentrifugeException?> OnDone { get; }

            /// <summary>True once the outcome was delivered.</summary>
            public bool Answered { get; set; }

            /// <summary>The deadline timer of the command, or null.</summary>
            public IStepTimer? Deadline { get; set; }
        }
    }
}
