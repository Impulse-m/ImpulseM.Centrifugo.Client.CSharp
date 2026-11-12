using System.Collections.Generic;
using System.Linq;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The calls that wait to be written. Drain returns the calls in the resend order: the calls with a msg_id first, in
    /// msg_id order, then the calls that never got one, in the order they were added. Used on the client loop only.
    /// </summary>
    internal sealed class RpcQueue {
        private readonly List<RpcCallState> entries = new List<RpcCallState>();


        /// <summary>The number of queued calls.</summary>
        internal int Count {
            get {
                return entries.Count;
            }
        }


        /// <summary>
        /// Adds a call at the end of the queue.
        /// </summary>
        /// <param name="call">The call.</param>
        internal void Add(RpcCallState call) {
            entries.Add(call);
        }


        /// <summary>
        /// Removes a call, when it is queued. Compares by reference.
        /// </summary>
        /// <param name="call">The call.</param>
        internal void Remove(RpcCallState call) {
            entries.Remove(call);
        }


        /// <summary>
        /// Empties the queue and returns its calls in the resend order.
        /// </summary>
        /// <returns>The calls that were queued.</returns>
        internal List<RpcCallState> Drain() {
            List<RpcCallState> ordered = entries
                .OrderBy((RpcCallState call) => call.MsgId == 0 ? 1 : 0)
                .ThenBy((RpcCallState call) => call.MsgId)
                .ToList();
            entries.Clear();
            return ordered;
        }
    }
}
