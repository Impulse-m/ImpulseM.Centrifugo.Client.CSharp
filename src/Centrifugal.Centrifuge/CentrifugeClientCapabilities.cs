namespace Centrifugal.Centrifuge {


    /// <summary>A snapshot of what the server confirmed for one connection. Immutable.</summary>
    public sealed class CentrifugeClientCapabilities {
        /// <summary>
        /// Creates the snapshot.
        /// </summary>
        /// <param name="rpcOptionsSupported">The connect reply confirmed RPC options.</param>
        internal CentrifugeClientCapabilities(bool rpcOptionsSupported) {
            RpcOptionsSupported = rpcOptionsSupported;
        }

        /// <summary>True when the server confirmed RPC options (per-call headers and fields) for this connection.</summary>
        public bool RpcOptionsSupported { get; }
    }
}
