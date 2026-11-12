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
        /// <paramref name="awaitTermination"/> is zero or negative (other than <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>),
        /// when the wait times out, or when called from an event handler (the handler holds the loop, so the close cannot be waited
        /// for there).
        /// </summary>
        /// <param name="awaitTermination">The longest time to wait for the close step to complete.
        /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> waits without a timeout.</param>
        /// <param name="cancellationToken">Stops the wait, not the close.</param>
        /// <returns>True when the close completed in time, else false.</returns>
        public Task<bool> CloseAsync(TimeSpan awaitTermination, CancellationToken cancellationToken = default)
        {
            Task operation = StartClose();
            if (Loop.InStep || (awaitTermination <= TimeSpan.Zero && awaitTermination != System.Threading.Timeout.InfiniteTimeSpan))
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
        /// Waits for the close step with a timeout. The timeout delay is cancelled when the close step completes, so no
        /// timer is left behind. A limit that is <see cref="Timeout.InfiniteTimeSpan"/> or above int.MaxValue milliseconds
        /// waits without a timeout.
        /// </summary>
        /// <param name="operation">The close step.</param>
        /// <param name="awaitTermination">The longest time to wait.</param>
        /// <param name="cancellationToken">Stops the wait.</param>
        /// <returns>True when the close step completed in time.</returns>
        private static async Task<bool> WaitForCloseAsync(Task operation, TimeSpan awaitTermination, CancellationToken cancellationToken)
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (IsBoundedWait(awaitTermination))
            {
                limit.CancelAfter(awaitTermination);
            }
            Task timeout = Task.Delay(System.Threading.Timeout.Infinite, limit.Token);
            Task finished = await Task.WhenAny(operation, timeout).ConfigureAwait(false);
            if (finished == operation)
            {
                // The close completed: its timeout delay is cancelled with it.
                limit.Cancel();
                return true;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            return false;
        }

        /// <summary>
        /// Whether a wait with this limit can end at a timeout: the limit is not <see cref="Timeout.InfiniteTimeSpan"/>
        /// and fits the timer of the framework (at most int.MaxValue milliseconds).
        /// </summary>
        /// <param name="awaitTermination">The limit of the wait.</param>
        private static bool IsBoundedWait(TimeSpan awaitTermination) =>
            awaitTermination != System.Threading.Timeout.InfiniteTimeSpan && awaitTermination.TotalMilliseconds <= int.MaxValue;

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
                    TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    // Stored before the close runs: a handler that disposes the client during the close joins this close.
                    closeOperation = closed.Task;
                    RunClose(closed);
                }
                return closeOperation;
            }
        }

        /// <summary>
        /// The close step. It completes <paramref name="closed"/> when the step has ended. The close does not wait for
        /// the peer: the last transport is cancelled at once.
        /// </summary>
        /// <param name="closed">Completed when the close step has ended.</param>
        private void RunClose(TaskCompletionSource<bool> closed)
        {
            Loop.Invoke(() =>
            {
                // Completed even when the step throws, so that a later close call joins this one instead of waiting forever.
                try
                {
                    _auth.InvalidateTimerFetch();
                    // Removed before its Unsubscribed event: a handler may create a new subscription of the channel.
                    foreach (CentrifugeSubscription sub in _subscriptions.Values.ToList())
                    {
                        Unregister(sub);
                        sub.CloseForClient();
                    }
                    DisconnectInternal(CentrifugeDisconnectedCodes.DisconnectCalled, "client closed", false);
                    // The live timers of the client end with it: none may fire after the close.
                    ClearConnectTimer();
                    ClearReconnectTimer();
                    ClearServerPingTimer();
                    _flushTimer?.Cancel();
                    _flushTimer = null;
                    _rpcSession.OnClose();
                    _auth.FailWaiting();
                    CancelCloseGraceTimers();
                }
                finally
                {
                    Loop.Complete(() => closed.TrySetResult(true));
                }
            });
        }

        /// <summary>
        /// Closes a transport with a close frame, and cancels it when the grace has passed. A peer that never answers the
        /// close frame would otherwise keep the socket. A close that completes within the grace cancels the grace timer.
        /// Runs on the loop.
        /// </summary>
        /// <param name="transport">The transport to close.</param>
        private void CloseWithGrace(ITransport transport)
        {
            _ = CloseGraceAsync(transport);
            IStepTimer timer = _timers.Start(CloseGraceMs, () =>
            {
                closeGraceTimers.Remove(transport);
                CancelTransportNow(transport);
            });
            closeGraceTimers[transport] = timer;
        }

        /// <summary>
        /// Sends the close frame of a transport and waits for the close to complete. Then the grace timer of the transport is
        /// cancelled on the loop, so it never fires on a transport that closed, and the transport is disposed.
        /// </summary>
        /// <param name="transport">The transport to close.</param>
        private async Task CloseGraceAsync(ITransport transport)
        {
            try
            {
                await Task.Run(transport.CloseAsync).ConfigureAwait(false);
            }
            catch
            {
                // The connection is torn down anyway.
            }
            Loop.Execute(() => CancelCloseGrace(transport));
            transport.Dispose();
        }

        /// <summary>
        /// Cancels the grace timer of a transport whose close completed, and removes it. Runs on the loop.
        /// </summary>
        /// <param name="transport">The transport whose close completed.</param>
        private void CancelCloseGrace(ITransport transport)
        {
            if (!closeGraceTimers.TryGetValue(transport, out IStepTimer? timer))
            {
                return;
            }
            closeGraceTimers.Remove(transport);
            timer!.Cancel();
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
