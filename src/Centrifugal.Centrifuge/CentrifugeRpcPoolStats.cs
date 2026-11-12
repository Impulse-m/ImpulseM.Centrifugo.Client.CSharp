namespace Centrifugal.Centrifuge {


    /// <summary>Counts of an rpc pool (Java RpcPool.Stats).</summary>
    /// <param name="Connections">The connections of the pool.</param>
    /// <param name="Inflight">The calls in flight.</param>
    /// <param name="Queued">The calls waiting for a connection.</param>
    /// <param name="Redirects">The redirects in progress.</param>
    public readonly record struct CentrifugeRpcPoolStats(int Connections, int Inflight, int Queued, int Redirects);
}
