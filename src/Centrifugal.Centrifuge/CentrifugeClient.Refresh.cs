using System;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The connection token refresh of the client: the public call that replaces the connection token now and resolves the
    /// identity the server confirms for it.
    /// </summary>
    public partial class CentrifugeClient {
        /// <summary>
        /// Replaces the connection token now and resolves the identity (user and auth class) the server confirms for it.
        /// A refresh made while the client is not connected is sent with the next connect. Concurrent calls with the same
        /// token share one command. The cancellation detaches the caller only: the refresh itself carries on.
        /// </summary>
        /// <param name="token">The new connection token.</param>
        /// <param name="cancellationToken">Detaches the caller: the returned task is cancelled, the refresh continues.</param>
        /// <returns>The identity and the expiry the server confirmed for the token.</returns>
        /// <exception cref="ArgumentNullException">The token is null.</exception>
        public Task<CentrifugeAuthResult> RefreshAsync(string token, CancellationToken cancellationToken = default) {
            if (token == null) {
                throw new ArgumentNullException(nameof(token));
            }
            if (IsDisposed) {
                return Task.FromException<CentrifugeAuthResult>(new CentrifugeException(CentrifugeErrorCodes.ClientClosed, "client closed", false));
            }
            // The loop completes the waiter. The caller gets the outcome through a second task, so the cancellation
            // registration is released in the completion step, before the caller can observe the outcome.
            TaskCompletionSource<CentrifugeAuthResult> waiter = new TaskCompletionSource<CentrifugeAuthResult>();
            TaskCompletionSource<CentrifugeAuthResult> caller = new TaskCompletionSource<CentrifugeAuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = default;
            if (cancellationToken.CanBeCanceled) {
                registration = cancellationToken.Register(
                    () => Loop.Execute(() => Loop.Complete(() => caller.TrySetCanceled(cancellationToken))));
            }
            waiter.Task.ContinueWith(
                done => {
                    registration.Dispose();
                    if (done.IsCanceled) {
                        caller.TrySetCanceled();
                    }
                    else if (done.IsFaulted) {
                        caller.TrySetException(done.Exception!.InnerException!);
                    }
                    else {
                        caller.TrySetResult(done.Result);
                    }
                },
                TaskContinuationOptions.ExecuteSynchronously);
            Loop.Invoke(() => _auth.RefreshWith(token, waiter));
            return caller.Task;
        }
    }
}
