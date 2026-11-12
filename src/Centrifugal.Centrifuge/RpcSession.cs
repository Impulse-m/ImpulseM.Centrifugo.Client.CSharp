using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The rpc calls of the client with per-call options (Java RpcSession). Every command id comes from
    /// IClientHost.NextCommandId.
    /// </summary>
    internal sealed class RpcSession {
        private readonly IClientHost host;
        private readonly IStepTimerFactory timers;
        private readonly int maxPending;
        private readonly Random random;


        /// <summary>
        /// Creates the rpc session of one client.
        /// </summary>
        /// <param name="host">The client seam.</param>
        /// <param name="timers">The step timer factory.</param>
        /// <param name="maxPending">Options.MaxPendingRpc.</param>
        /// <param name="random">The per-client random source (the retry delays).</param>
        internal RpcSession(IClientHost host, IStepTimerFactory timers, int maxPending, Random random) {
            this.host = host;
            this.timers = timers;
            this.maxPending = maxPending;
            this.random = random;
        }


        /// <summary>
        /// Handles a reply whose id belongs to an rpc call, and observes the server time of the reply.
        /// </summary>
        /// <param name="reply">The reply.</param>
        /// <returns>True when the reply belonged to an rpc call.</returns>
        public bool OnReply(Protocol.Reply reply) {
            return false;
        }


        /// <summary>
        /// Writes the queued calls to the transport. There is no in-flight or connected check here: CentrifugeClient.FlushRpc
        /// makes that check before it calls this method.
        /// </summary>
        public void Flush() {
        }


        /// <summary>
        /// Settles the calls that were on the lost transport.
        /// </summary>
        /// <param name="keep">True when the session is kept for a reconnect.</param>
        public void OnTransportLost(bool keep) {
        }


        /// <summary>Settles the pending calls when the client closes.</summary>
        public void OnClose() {
        }
    }
}
