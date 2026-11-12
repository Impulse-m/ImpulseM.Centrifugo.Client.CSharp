using System;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The only way the helper classes (connection auth, rpc session, token exchange, ping watchdog,
    /// rpc pool token source) reach the client. The client exposes it through a private adapter in
    /// CentrifugeClient.Host.cs. Every member runs on the loop, except where a comment says otherwise.
    /// </summary>
    internal interface IClientHost {
        /// <summary>The loop that owns all client state.</summary>
        EventLoop Loop { get; }

        /// <summary>Upstream state: Disconnected, Connecting or Connected.</summary>
        CentrifugeClientState State { get; }

        /// <summary>True once Dispose or DisposeAsync was called. Set before the close step runs. Readable from any thread.</summary>
        bool IsClosed { get; }

        /// <summary>State is Connected.</summary>
        bool IsConnected { get; }

        /// <summary>The transport of the current attempt is open.</summary>
        bool IsTransportOpen { get; }

        /// <summary>The transport of the current attempt, or null. Compare by reference.</summary>
        ITransport? Transport { get; }

        /// <summary>Options.Timeout in milliseconds.</summary>
        int TimeoutMs { get; }

        /// <summary>Options.MaxServerPingDelay.</summary>
        TimeSpan MaxServerPingDelay { get; }

        /// <summary>The connect reply confirmed RPC options (bit ConnectCapabilities.RpcOptionsFlag). Cleared on each new transport and on each disconnect. Readable from any thread.</summary>
        bool RpcOptionsSupported { get; }

        /// <summary>Options.Instance is not empty. Read once, at construction.</summary>
        bool HasInstance { get; }

        /// <summary>Options.Instance. Never log it or put it in an exception message.</summary>
        string Instance { get; }

        /// <summary>Refresh required flag. Set by a connect error 109 and by a connect with an empty token and a getter.</summary>
        bool RefreshRequired { get; set; }

        /// <summary>The connection token. The setter stores the token.</summary>
        string ConnectionToken { get; set; }

        /// <summary>The retry policy shared by the connection refresh and the subscription refresh. One per client.</summary>
        IRefreshRetryPolicy RefreshRetryPolicy { get; }

        /// <summary>Creates one-shot step timers. Tests inject FakeStepTimerFactory.</summary>
        IStepTimerFactory Timers { get; }

        /// <summary>Monotonic milliseconds. For deadlines and delays only, never for server time.</summary>
        long NowMs { get; }

        /// <summary>Unix milliseconds: wall clock plus the server offset.</summary>
        long ServerTimeMillis();

        /// <summary>Next command id. Unsigned, never 0 (upstream NextCommandId).</summary>
        uint NextCommandId();

        /// <summary>Queues one command for the next frame. sent completes in a later step with the write result (true = written).</summary>
        void QueueCommand(Protocol.Command command, TaskCompletionSource<bool>? sent);

        /// <summary>
        /// One plain command on the given transport, for the token exchange. Fails with CentrifugeConnectionLostException
        /// when the transport is not the current one or is lost before the reply. Not held and not retransmitted.
        /// </summary>
        Task<Protocol.Reply> SendOnTransportAsync(ITransport transport, Protocol.Command command, CancellationToken cancellationToken);

        /// <summary>
        /// Writes the queued rpc calls when no refresh is in flight and the client is Connected. CentrifugeClient.FlushRpc
        /// makes that check. RpcSession.Flush only writes.
        /// </summary>
        void FlushRpc();

        /// <summary>Runs the connect reply path once for a reply held by the token exchange bootstrap.</summary>
        void ResumeConnect(Protocol.ConnectResult result);

        /// <summary>Applies the ping interval and the pong flag of a connect reply held by the token exchange bootstrap.</summary>
        void ApplyPing(uint pingSeconds, bool sendPong);

        /// <summary>Cancels the transport without a close handshake. Used for a silent peer or a held attempt.</summary>
        void CancelTransport(ITransport transport);

        /// <summary>Raises the error event with a type string. Does not stop the client.</summary>
        void ReportError(string type, Exception error);

        /// <summary>Raises the error event with type "unclassified", wrapped in CentrifugeUnclassifiedException. Does not stop the client.</summary>
        void ReportUnclassified(Exception error);

        /// <summary>
        /// Raises the auth event through the notice path. Returns the exception a handler threw, or null.
        /// The caller decides what a failure means (connect path or refresh path). Does not disconnect.
        /// </summary>
        Exception? RaiseAuth(CentrifugeAuthEventArgs args);

        /// <summary>
        /// Raises an event handler of this client through the notice path: returns the exception the handler threw,
        /// or null. The error event is raised by the dispatcher. Does not disconnect (see ConnectNotices).
        /// </summary>
        Exception? RaiseNotice<TArgs>(EventHandler<TArgs>? handler, TArgs args, string type);

        /// <summary>True when an event handler made another state transition after <paramref name="transition"/> (upstream TransitionSuperseded).</summary>
        bool TransitionSuperseded(int transition);

        /// <summary>An exception escaped an event handler: counts it and disconnects with BadProtocol unless already Disconnected (upstream HandlerFailed).</summary>
        bool HandlerFailed(Exception? exception);

        /// <summary>Dispatches a push that the token exchange held (see ITokenExchangeHooks.HoldPush). Skips the hold check.</summary>
        void DispatchHeldPush(Protocol.Push push);

        /// <summary>Upstream DisconnectInternal. reconnect false ends the session.</summary>
        void DisconnectWith(int code, string reason, bool reconnect);

        /// <summary>Disconnects with code 1 ("unauthorized") and no reconnect.</summary>
        void FailUnauthorized();

        /// <summary>Asks for the connection token through the token getter. Answers on the loop: onToken with the token, or onError.</summary>
        void RequestConnectionToken(Action<string?> onToken, Action<Exception> onError);

        /// <summary>Plain publish of one channel, for a subscription publish debounce. Completes when written, or fails.</summary>
        Task PublishToChannelAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

        /// <summary>Queues an unsubscribe for a channel.</summary>
        void SendUnsubscribe(string channel);

        /// <summary>Moves a subscription from one compaction id to another in the routing table.</summary>
        void SetSubscriptionId(CentrifugeSubscription subscription, long oldId, long newId);

        /// <summary>Reports a publication that could not be decoded.</summary>
        void HandleUndecodablePublication(string message);
    }
}
