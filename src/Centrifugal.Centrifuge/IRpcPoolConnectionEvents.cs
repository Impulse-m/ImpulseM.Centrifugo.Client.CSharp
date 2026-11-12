namespace Centrifugal.Centrifuge {


    /// <summary>
    /// Connection events of the pool. The pool connections post them to the engine. The engine implements them on the
    /// pool loop. connection is the RpcPoolConnection object, which the engine casts.
    /// </summary>
    internal interface IRpcPoolConnectionEvents {
        /// <summary>A connection starts its client connect.</summary>
        void OnConnecting(object connection);

        /// <summary>A connection reached Connected.</summary>
        void OnConnected(object connection);

        /// <summary>A connection left Connected or Connecting, with the main client's code and reason.</summary>
        void OnDisconnected(object connection, int code, string reason);
    }
}
