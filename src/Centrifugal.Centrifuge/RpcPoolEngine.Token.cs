using System;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The token path of the rpc pool engine (partial class). The token rules (refresh, lending, stamps) are in this
    /// file. The queue, placement, attempt, resend, retire and close methods are in RpcPoolEngine.cs.
    /// </summary>
    internal sealed partial class RpcPoolEngine {
        /// <summary>
        /// Returns the token of a pool connection. The connection calls it from its token getter, off the engine loop,
        /// and the call waits for the engine's answer to the token request. Implements IRpcPoolEngine.TokenOfAsync.
        /// Throws NotImplementedException until the token path is written.
        /// </summary>
        /// <param name="connection">The RpcPoolConnection object (cast inside the method).</param>
        public Task<string> TokenOfAsync(object connection) {
            throw new NotImplementedException();
        }


        /// <summary>
        /// Adds or removes the engine as a listener of the token source, by whether a call waits for a token.
        /// Throws NotImplementedException until the token path is written.
        /// </summary>
        private void UpdateListening() {
            throw new NotImplementedException();
        }
    }
}
