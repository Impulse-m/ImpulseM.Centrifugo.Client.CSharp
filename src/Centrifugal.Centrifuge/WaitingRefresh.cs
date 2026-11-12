using System.Collections.Generic;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// One queued entry of the connection refresh: a requested refresh with its token and the callers that share it, or a
    /// timer entry (token null) that fetches a token when it starts. The generation is the refresh generation of a
    /// requested entry when it was queued, and the timer generation of a timer entry.
    /// </summary>
    internal sealed class WaitingRefresh {
        /// <summary>
        /// Creates a queued entry.
        /// </summary>
        /// <param name="token">The token of a requested refresh, or null for a timer entry.</param>
        /// <param name="generation">The generation when the entry was queued.</param>
        public WaitingRefresh(string? token, long generation) {
            Token = token;
            Generation = generation;
        }


        /// <summary>The token of a requested refresh, or null for a timer entry.</summary>
        public string? Token { get; }


        /// <summary>The generation when the entry was queued.</summary>
        public long Generation { get; }


        /// <summary>The callers waiting for the outcome of this entry. Empty for a timer entry.</summary>
        public List<TaskCompletionSource<CentrifugeAuthResult>> Waiters {
            get {
                return waiters;
            }
        }


        private readonly List<TaskCompletionSource<CentrifugeAuthResult>> waiters = new List<TaskCompletionSource<CentrifugeAuthResult>>();
    }
}
