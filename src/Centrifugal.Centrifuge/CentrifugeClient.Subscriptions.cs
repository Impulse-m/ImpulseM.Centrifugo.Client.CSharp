namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// The subscriptions reach the client through this internal member: the timers and the refresh retry policy come
    /// from the host adapter, and the publish, unsubscribe and disconnect calls are the client methods of Client.cs.
    /// </summary>
    public partial class CentrifugeClient
    {
        /// <summary>The host of the client's helpers: the timers, the refresh retry policy and the loop.</summary>
        internal IClientHost Host
        {
            get
            {
                return _host;
            }
        }
    }
}
