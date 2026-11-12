using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Closing: the close step that Dispose, DisposeAsync and CloseAsync start, the wait for it with a timeout, and
    /// the grace that ends a transport whose peer does not answer its close frame.
    /// </summary>
    public partial class CentrifugeClient
    {
        /// <summary>How long a transport gets to answer its close frame before it is cancelled.</summary>
        private const int CloseGraceMs = 1000;

        /// <summary>Guards the start of the close step, so that the close starts once.</summary>
        private readonly object closeLock = new object();

        /// <summary>Transports closed with a close frame that wait for their grace timer. Used on the loop only.</summary>
        private readonly Dictionary<ITransport, IStepTimer> closeGraceTimers = new Dictionary<ITransport, IStepTimer>();

        /// <summary>The close step of the client. Null until a close is started. Set once, under closeLock.</summary>
        private Task? closeOperation;

        /// <summary>
        /// Closes the client: the subscriptions are moved to subscribing, the client disconnects, the transport is cancelled
        /// and the client's work ends. Later calls to the client fail. Returns true when the close step completed within
        /// <paramref name="awaitTermination"/>. Returns false, and the close still happens, when
        /// <paramref name="awaitTermination"/> is zero or negative, when the wait times out, or when called from
        /// an event handler (the handler holds the loop, so the close cannot be waited for there).
        /// </summary>
        /// <param name="awaitTermination">The longest time to wait for the close step to complete.</param>
        /// <param name="cancellationToken">Stops the wait, not the close.</param>
        /// <returns>True when the close completed in time, else false.</returns>
        public Task<bool> CloseAsync(TimeSpan awaitTermination, CancellationToken cancellationToken = default)
        {
            Task operation = StartClose();
            if (awaitTermination <= TimeSpan.Zero || Loop.InStep)
            {
                return Task.FromResult(false);
            }
            return WaitForCloseAsync(operation, awaitTermination, cancellationToken);
        }

        /// <summary>
        /// Asynchronously disposes the client: every subscription is moved to subscribing, the client disconnects, and
        /// the transport is cancelled. Called from an event handler it doesn't wait for the close (the handler holds the loop).
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            Task operation = StartClose();
            if (Loop.InStep)
            {
                return;
            }
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch
            {
                // Closing errors are ignored.
            }
        }

        /// <summary>
        /// Disposes the client: every subscription is moved to subscribing, the client disconnects, and the transport is
        /// cancelled. The close step is waited for, except from an event handler (the handler holds the loop) and in the
        /// browser (a blocking wait would deadlock the single thread). Prefer <see cref="DisposeAsync"/>.
        /// </summary>
        public void Dispose()
        {
            Task operation = StartClose();
            if (Loop.InStep || IsBrowser)
            {
                return;
            }
            try
            {
                operation.GetAwaiter().GetResult();
            }
            catch
            {
                // Closing errors are ignored.
            }
        }

        /// <summary>
        /// Waits for the close step with a timeout.
        /// </summary>
        /// <param name="operation">The close step.</param>
        /// <param name="awaitTermination">The longest time to wait.</param>
        /// <param name="cancellationToken">Stops the wait.</param>
        /// <returns>True when the close step completed in time.</returns>
        private static async Task<bool> WaitForCloseAsync(Task operation, TimeSpan awaitTermination, CancellationToken cancellationToken)
        {
            Task timeout = Task.Delay(awaitTermination, cancellationToken);
            Task finished = await Task.WhenAny(operation, timeout).ConfigureAwait(false);
            if (finished != operation)
            {
                if (timeout.IsCanceled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// Starts the close step at the first call and returns its task. Later calls get the same task, so each close
        /// call, also one after Dispose or DisposeAsync, can wait for the close that started first.
        /// </summary>
        /// <returns>The task of the close step.</returns>
        private Task StartClose()
        {
            lock (closeLock)
            {
                if (closeOperation == null)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    closeOperation = Close();
                }
                return closeOperation;
            }
        }

        /// <summary>
        /// The close step. Its task completes when the step has ended. The close does not wait for the peer: the last
        /// transport is cancelled at once.
        /// </summary>
        /// <returns>The task of the close step.</returns>
        private Task Close()
        {
            TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Invoke(() =>
            {
                _auth.InvalidateTimerFetch();
                // Removed before its Unsubscribed event: a handler may create a new subscription of the channel.
                foreach (CentrifugeSubscription sub in _subscriptions.Values.ToList())
                {
                    Unregister(sub);
                    sub.CloseForClient();
                }
                DisconnectInternal(CentrifugeDisconnectedCodes.DisconnectCalled, "client closed", false);
                // The live timers of the client end with it (Java scheduler.shutdownNow): none may fire after the close.
                ClearConnectTimer();
                ClearReconnectTimer();
                ClearServerPingTimer();
                ClearRefreshTimer();
                _flushTimer?.Cancel();
                _flushTimer = null;
                _rpcSession.OnClose();
                _auth.FailWaiting();
                CancelCloseGraceTimers();
                Loop.Complete(() => closed.TrySetResult(true));
            });
            return closed.Task;
        }

        /// <summary>
        /// Closes a transport with a close frame, and cancels it when the grace has passed (Java closeTransport). A peer that
        /// never answers the close frame would otherwise keep the socket. Runs on the loop.
        /// </summary>
        /// <param name="transport">The transport to close.</param>
        private void CloseWithGrace(ITransport transport)
        {
            _ = CloseTransportAsync(transport);
            IStepTimer timer = _timers.Start(CloseGraceMs, () =>
            {
                closeGraceTimers.Remove(transport);
                CancelTransportNow(transport);
            });
            closeGraceTimers[transport] = timer;
        }

        /// <summary>
        /// Cancels at once the transports that wait for their grace, and stops their grace timers. Runs in the close step.
        /// </summary>
        private void CancelCloseGraceTimers()
        {
            List<KeyValuePair<ITransport, IStepTimer>> pending = new List<KeyValuePair<ITransport, IStepTimer>>(closeGraceTimers);
            closeGraceTimers.Clear();
            foreach (KeyValuePair<ITransport, IStepTimer> entry in pending)
            {
                entry.Value.Cancel();
                CancelTransportNow(entry.Key);
            }
        }
    }
}
