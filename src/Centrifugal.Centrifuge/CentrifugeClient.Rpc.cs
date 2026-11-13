using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The exactly-once rpc calls of the client: every rpc call goes through the rpc session, with or without per-call options.
    /// </summary>
    public partial class CentrifugeClient {
        /// <summary>
        /// Sends an rpc call with per-call options. The call waits for the connection and for a refresh in flight, and its
        /// deadline holds across the whole wait and all resends. A cancelled token cancels the call: a call that was sent is
        /// cancelled on the server too, as Cancel does. A token that is cancelled before the call settles the call with
        /// <see cref="CentrifugeException"/> code 17 (rpc call cancelled): nothing is queued and nothing is sent.
        /// A client-side loss faults the call with a <see cref="CentrifugeException"/> of the same code, message and cause.
        /// </summary>
        /// <param name="method">The method name.</param>
        /// <param name="data">The request payload.</param>
        /// <param name="options">The options of the call: headers and timeout. Null for a plain call.</param>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <returns>The result of the call. Faults with <see cref="CentrifugeException"/> or <see cref="CentrifugeTimeoutException"/>.</returns>
        /// <exception cref="ArgumentNullException">The method is null.</exception>
        public Task<CentrifugeRpcResult> RpcAsync(string method, ReadOnlyMemory<byte> data, CentrifugeRpcOptions? options, CancellationToken cancellationToken) {
            return StartRpcCall(method, data, options, cancellationToken, true).Result;
        }


        /// <summary>
        /// Sends an rpc call with per-call options and no cancellation token. The call is cancelled only by its handle (see
        /// <see cref="RpcCall"/>) or by its deadline. Its failures are the same as those of the overload with a token.
        /// </summary>
        /// <param name="method">The method name.</param>
        /// <param name="data">The request payload.</param>
        /// <param name="options">The options of the call: headers and timeout.</param>
        /// <returns>The result of the call. Faults with <see cref="CentrifugeException"/> or <see cref="CentrifugeTimeoutException"/>.</returns>
        /// <exception cref="ArgumentNullException">The method or the options are null.</exception>
        public Task<CentrifugeRpcResult> RpcAsync(string method, ReadOnlyMemory<byte> data, CentrifugeRpcOptions options) {
            ArgumentNullException.ThrowIfNull(options);
            return RpcAsync(method, data, options, CancellationToken.None);
        }


        /// <summary>
        /// Sends an rpc call with per-call options and returns its handle. The handle gives the result and cancels the call.
        /// A client-side loss faults the result with a <see cref="CentrifugeException"/> of the same code, message and cause.
        /// </summary>
        /// <param name="method">The method name.</param>
        /// <param name="data">The request payload.</param>
        /// <param name="options">The options of the call: headers and timeout. Null for a plain call.</param>
        /// <returns>The handle of the call.</returns>
        /// <exception cref="ArgumentNullException">The method is null.</exception>
        public CentrifugeRpcCall RpcCall(string method, ReadOnlyMemory<byte> data, CentrifugeRpcOptions? options = null) {
            return StartRpcCall(method, data, options, CancellationToken.None, true);
        }


        /// <summary>
        /// Gets the server time in Unix milliseconds: the wall clock plus the offset of the last reply that carried a time. Safe
        /// from any thread.
        /// </summary>
        /// <returns>The server time.</returns>
        public long ServerTimeMillis() {
            return _host.ServerTimeMillis();
        }


        // The upstream RpcAsync overload. Its failures keep the upstream types: a client-side loss is a plain CentrifugeException,
        // and a token cancelled before the call is an OperationCanceledException.
        private Task<CentrifugeRpcResult> StartUpstreamRpc(string method, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) {
            return StartRpcCall(method, data, null, cancellationToken, false).Result;
        }


        // Creates the call on this thread and submits it to the loop. The deadline starts in the submit step. A fork call whose
        // token is cancelled already goes through the cancel step and is never submitted: it holds no slot and sends nothing.
        private CentrifugeRpcCall StartRpcCall(string method, ReadOnlyMemory<byte> data, CentrifugeRpcOptions? options, CancellationToken cancellationToken, bool forkEntry) {
            ArgumentNullException.ThrowIfNull(method);
            if (IsDisposed) {
                return new CentrifugeRpcCall(Task.FromException<CentrifugeRpcResult>(new ObjectDisposedException(nameof(CentrifugeClient))), () => { });
            }
            if (!forkEntry && cancellationToken.IsCancellationRequested) {
                return new CentrifugeRpcCall(Task.FromCanceled<CentrifugeRpcResult>(cancellationToken), () => { });
            }
            TaskCompletionSource<CentrifugeRpcResult> completion = new TaskCompletionSource<CentrifugeRpcResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            IReadOnlyDictionary<string, string> headers = options != null ? options.Headers : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
            // The payload is copied here, on the caller's thread: the caller may reuse its buffer once the call is made.
            ReadOnlyMemory<byte> payload = data.Span.ToArray();
            RpcCallState state = new RpcCallState(method, payload, headers, RpcTimeoutMs(options), completion, forkEntry);
            CentrifugeRpcCall handle = new CentrifugeRpcCall(completion.Task, () => Submission.ExecuteOrIgnore(_host, () => _rpcSession.Cancel(state)));
            if (forkEntry && cancellationToken.IsCancellationRequested) {
                Loop.Invoke(() => _rpcSession.Cancel(state));
                return handle;
            }
            if (cancellationToken.CanBeCanceled) {
                CancellationTokenRegistration registration = cancellationToken.Register(() => handle.Cancel());
                completion.Task.ContinueWith((Task task) => registration.Dispose(), TaskScheduler.Default);
            }
            Loop.Invoke(() => _rpcSession.Submit(state));
            return handle;
        }


        // The deadline of a call: its own timeout when set, else the client timeout. At least one millisecond.
        private int RpcTimeoutMs(CentrifugeRpcOptions? options) {
            TimeSpan? timeout = options?.Timeout;
            if (!timeout.HasValue) {
                return TimeoutMs;
            }
            return (int)Math.Clamp(timeout.Value.TotalMilliseconds, 1d, (double)int.MaxValue);
        }
    }
}
