using System;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Centrifugal.Centrifuge.Transports;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The expiry timer of the connection token: the timer that renews the token before it expires, its fetch of a
    /// token from the getter (bounded by the client timeout), the refresh command of a fetched token, and the retries
    /// after a failure. Part of AuthRefresh.
    /// </summary>
    internal sealed partial class AuthRefresh {
        /// <summary>
        /// Starts the timer that runs one fetch of a token when it falls due. A timer that was cancelled is ignored.
        /// </summary>
        /// <param name="delayMs">The delay in milliseconds.</param>
        private void ScheduleTimer(int delayMs) {
            CancelTimer();
            long generation = timerGeneration;
            timer = timers.Start(delayMs, () => OnTimer(generation));
        }


        private void OnTimer(long generation) {
            if (generation != timerGeneration) {
                return;
            }
            timer = null;
            if (inFlight) {
                waiting.Add(new WaitingRefresh(null, timerGeneration));
                return;
            }
            inFlight = true;
            FetchAndRefresh();
        }


        // Fetches a token for the timer refresh. The fetch is bounded by the client timeout; a late answer is ignored.
        private void FetchAndRefresh() {
            if (!host.IsConnected) {
                Complete(false);
                return;
            }
            ITransport? transport = host.Transport;
            long refreshGen = refreshGeneration;
            long sessionGen = sessionGeneration;
            bool answered = false;
            IStepTimer fetchTimer = timers.Start(host.TimeoutMs, () => {
                if (answered) {
                    return;
                }
                answered = true;
                OnTokenFetched(transport, refreshGen, sessionGen, null, new CentrifugeTimeoutException("token timeout"));
            });
            FetchTimerToken(
                token => {
                    if (answered) {
                        return;
                    }
                    answered = true;
                    fetchTimer.Cancel();
                    OnTokenFetched(transport, refreshGen, sessionGen, token, null);
                },
                error => {
                    if (answered) {
                        return;
                    }
                    answered = true;
                    fetchTimer.Cancel();
                    OnTokenFetched(transport, refreshGen, sessionGen, null, error);
                });
        }


        // The token of a timer refresh comes from the token exchange when one is set, else from the token getter.
        private void FetchTimerToken(Action<string?> onToken, Action<Exception> onError) {
            if (tokenExchange.IsActive) {
                Task<string> renewal = tokenExchange.RenewAsync(CancellationToken.None);
                renewal.ContinueWith(done => host.Loop.Execute(() => {
                    if (done.IsFaulted) {
                        onError(done.Exception!.InnerException ?? done.Exception!);
                        return;
                    }
                    onToken(done.Result);
                }));
                return;
            }
            host.RequestConnectionToken(onToken, onError);
        }


        private void OnTokenFetched(ITransport? transport, long refreshGen, long sessionGen, string? token, Exception? error) {
            bool commandSent = false;
            try {
                if (host.IsClosed) {
                    return;
                }
                if (host.IsConnected && !ReferenceEquals(transport, host.Transport)) {
                    return;
                }
                if (sessionGen != sessionGeneration) {
                    if (host.IsConnected) {
                        ScheduleTimer(0);
                    }
                    return;
                }
                if (tokenExchange.IsActive && error is CentrifugeUnauthorizedException) {
                    host.FailUnauthorized();
                    return;
                }
                if (!host.IsConnected) {
                    KeepTimerTokenForConnect(error, token, refreshGen);
                    return;
                }
                if (HasRequestWaiting()) {
                    timerSuperseded = true;
                    return;
                }
                if (error != null) {
                    if (error is CentrifugeUnauthorizedException) {
                        host.FailUnauthorized();
                        return;
                    }
                    ScheduleRetry();
                    host.ReportError("refreshToken", new CentrifugeException(CentrifugeErrorCodes.ClientRefreshToken, error.Message, true, error));
                    return;
                }
                if (string.IsNullOrEmpty(token)) {
                    host.FailUnauthorized();
                    return;
                }
                if (retryPolicy.StopOnRefusedToken && token == refusedTimerToken) {
                    host.RefreshRequired = true;
                    return;
                }
                commandInFlight = true;
                commandSent = true;
                StartRefreshCommand(token!, (sentToken, result, failure) => FinishTimer(transport, sentToken, result, failure));
            }
            finally {
                if (!commandSent) {
                    Complete(false);
                }
            }
        }


        // A fetch while disconnected gives the token of the next connect; the answer is then not sent.
        private void KeepTimerTokenForConnect(Exception? error, string? token, long refreshGen) {
            if (refreshGen != refreshGeneration) {
                return;
            }
            if (error != null) {
                host.ReportError("refreshToken", new CentrifugeException(CentrifugeErrorCodes.ClientRefreshToken, error.Message, true, error));
                return;
            }
            if (string.IsNullOrEmpty(token)) {
                host.FailUnauthorized();
                return;
            }
            host.ConnectionToken = token!;
        }


        // The outcome of a timer refresh. complete runs on every path; the sink learns of a failure after it.
        private void FinishTimer(ITransport? transport, string token, RefreshResult? result, CentrifugeException? error) {
            bool failed = false;
            try {
                if (!ReferenceEquals(transport, host.Transport) || !host.IsConnected) {
                    return;
                }
                if (error != null) {
                    failed = true;
                    if (IsReplyError(error) && error.Code == RefreshRequiredCode) {
                        refusedTimerToken = token;
                    }
                    host.ReportError("refresh", error);
                    SettleTimerError(error);
                    return;
                }
                host.ConnectionToken = token;
                timerSuperseded = false;
                refusedTimerToken = null;
                refreshGeneration++;
                ScheduleExpiry(result!.Expires, result.Ttl);
                identity.OnRefreshIdentity(result.User, result.AuthClass);
            }
            finally {
                Complete(false);
                if (failed) {
                    identity.TokenSink?.OnRefreshFailed(error!, false);
                }
            }
        }


        // A failed timer refresh: a temporary error, a transport loss or a timeout is retried; a refusal for permission
        // keeps the connection; any other server code ends the connection.
        private void SettleTimerError(CentrifugeException error) {
            if (!IsReplyError(error) || retryPolicy.IsRetryableRefresh(error)) {
                ScheduleRetry();
                return;
            }
            if (error.Code == PermissionDeniedCode) {
                return;
            }
            host.DisconnectWith(error.Code, error.Message, false);
        }


        private void ScheduleRetry() {
            ScheduleTimer(retryPolicy.RetryDelayMillis(retryStep));
            retryStep = Math.Min(retryStep + 1, retryPolicy.MaxRetryStep);
        }
    }
}
