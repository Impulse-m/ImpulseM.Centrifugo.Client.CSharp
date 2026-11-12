using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The pool engine as the public pool class sees it. Implemented by RpcPoolEngine.
    /// Every member posts to the engine loop. RpcAsync takes the call options unpacked, so no slice type is named here.
    /// </summary>
    internal interface IRpcPoolEngine : IRpcPoolListener, IRpcPoolConnectionEvents {
        /// <summary>One call. endpointKey is "" by default. timeout null means the pool timeout.</summary>
        Task<CentrifugeRpcResult> RpcAsync(
            string endpointKey,
            string method,
            ReadOnlyMemory<byte> data,
            string? affinity,
            IReadOnlyDictionary<string, string>? headers,
            TimeSpan? timeout,
            CancellationToken cancellationToken
        );


        /// <summary>
        /// Returns the token of a pool connection, for the connection's token getter. Called from the connection's
        /// callback thread, off the engine loop, and it may wait for the engine.
        /// </summary>
        /// <param name="connection">The pool connection object (RpcPoolConnection, cast by the engine).</param>
        Task<string> TokenOfAsync(object connection);


        /// <summary>Counts of the pool (Java stats()).</summary>
        Task<CentrifugeRpcPoolStats> GetStatsAsync();


        /// <summary>Starts the close. Returns at once (Java close()).</summary>
        void Close();
    }
}
