using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The state of one rpc call. The caller creates it on its own thread. The client loop reads and changes the rest of
    /// it, in steps only.
    /// </summary>
    internal sealed class RpcCallState {
        /// <summary>
        /// Creates the state of a new call.
        /// </summary>
        /// <param name="method">The method name.</param>
        /// <param name="data">The request payload.</param>
        /// <param name="headers">The headers sent with the call only.</param>
        /// <param name="timeoutMs">The deadline of the call in milliseconds, counted across all attempts.</param>
        /// <param name="completion">Completed with the result, or faulted, once the call settles.</param>
        internal RpcCallState(string method, ReadOnlyMemory<byte> data, IReadOnlyDictionary<string, string> headers, int timeoutMs, TaskCompletionSource<CentrifugeRpcResult> completion) {
            Method = method;
            Data = data;
            Headers = headers;
            TimeoutMs = timeoutMs;
            Completion = completion;
        }


        /// <summary>The method name.</summary>
        internal string Method { get; }


        /// <summary>The request payload. Null once the call settled (the payload is dropped).</summary>
        internal ReadOnlyMemory<byte>? Data { get; set; }


        /// <summary>The headers sent with the call only.</summary>
        internal IReadOnlyDictionary<string, string> Headers { get; }


        /// <summary>The deadline of the call in milliseconds, counted across all attempts.</summary>
        internal int TimeoutMs { get; }


        /// <summary>Completed with the result, or faulted, once the call settles.</summary>
        internal TaskCompletionSource<CentrifugeRpcResult> Completion { get; }


        /// <summary>The msg_id of the call. Zero until the first tracked write.</summary>
        internal ulong MsgId { get; set; }


        /// <summary>The command id of the attempt on the wire.</summary>
        internal uint CommandId { get; set; }


        /// <summary>The call holds a slot of the maxPendingRpc bound until it settles.</summary>
        internal bool HoldsSlot { get; set; }


        /// <summary>The call was written at least once.</summary>
        internal bool EverSent { get; set; }


        /// <summary>The attempt on the wire, or the last attempt, carries a msg_id.</summary>
        internal bool WireTracked { get; set; }


        /// <summary>An attempt of the call is on the wire.</summary>
        internal bool OnWire { get; set; }


        /// <summary>The call settled: its completion was decided.</summary>
        internal bool Done { get; set; }


        /// <summary>The deadline timer of the call. Cancelled when the call settles.</summary>
        internal IStepTimer? DeadlineTimer { get; set; }


        /// <summary>The acks that the attempt on the wire carries. They go back to the queue of acks when the attempt is not answered.</summary>
        internal List<ulong>? CarriedAcks { get; set; }
    }
}
