using System;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The handle of one rpc call made with the per-call options. <see cref="Result"/> completes with the answer, or faults
    /// with <see cref="CentrifugeException"/> or <see cref="CentrifugeTimeoutException"/>.
    /// </summary>
    public sealed class CentrifugeRpcCall {
        /// <summary>
        /// Gets the task of the call. It completes with the result, or faults with <see cref="CentrifugeException"/> (codes
        /// 11, 15, 16, 17 or a server code) or <see cref="CentrifugeTimeoutException"/>.
        /// </summary>
        public Task<CentrifugeRpcResult> Result { get; }


        private readonly Action cancelAction;


        /// <summary>
        /// Creates the handle of a call. The client creates it and gives the action that cancels the call.
        /// </summary>
        /// <param name="result">The task of the call.</param>
        /// <param name="cancelAction">Cancels the call. Called by <see cref="Cancel"/>.</param>
        internal CentrifugeRpcCall(Task<CentrifugeRpcResult> result, Action cancelAction) {
            Result = result;
            this.cancelAction = cancelAction;
        }


        /// <summary>
        /// Cancels the call. A call that was sent is cancelled on the server; a call that was not sent is never sent. Has no
        /// effect once the call is done. Safe to call from any thread.
        /// </summary>
        public void Cancel() {
            cancelAction();
        }
    }
}
