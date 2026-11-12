using System;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The client seam (IClientHost) of the client. The interface is internal, so a public class cannot list it as a
    /// base. The client hands its helpers this private adapter, which forwards every member to the client.
    /// </summary>
    public partial class CentrifugeClient {
        /// <summary>The private adapter that implements IClientHost for the helpers of the client.</summary>
        private sealed class HostAdapter : IClientHost {
            private readonly CentrifugeClient owner;


            /// <summary>
            /// Creates the adapter of one client.
            /// </summary>
            /// <param name="owner">The client.</param>
            public HostAdapter(CentrifugeClient owner) {
                this.owner = owner;
            }


            /// <summary>The loop that owns all client state.</summary>
            public EventLoop Loop {
                get {
                    return owner.Loop;
                }
            }


            /// <summary>Upstream state.</summary>
            public CentrifugeClientState State {
                get {
                    return owner._state;
                }
            }


            /// <summary>True once Dispose or DisposeAsync was called.</summary>
            public bool IsClosed {
                get {
                    return owner.IsDisposed;
                }
            }


            /// <summary>State is Connected.</summary>
            public bool IsConnected {
                get {
                    return owner._state == CentrifugeClientState.Connected;
                }
            }


            /// <summary>The transport of the current attempt is open.</summary>
            public bool IsTransportOpen {
                get {
                    return owner.TransportIsOpen;
                }
            }


            /// <summary>The transport of the current attempt, or null.</summary>
            public ITransport? Transport {
                get {
                    return owner._transport;
                }
            }


            /// <summary>Options.Timeout in milliseconds.</summary>
            public int TimeoutMs {
                get {
                    return owner.TimeoutMs;
                }
            }


            /// <summary>Options.MaxServerPingDelay.</summary>
            public TimeSpan MaxServerPingDelay {
                get {
                    return owner._options.MaxServerPingDelay;
                }
            }


            /// <summary>The connect reply confirmed RPC options.</summary>
            public bool RpcOptionsSupported {
                get {
                    return owner._rpcOptionsSupported;
                }
            }


            /// <summary>Options.Instance is not empty.</summary>
            public bool HasInstance {
                get {
                    return owner._hasInstance;
                }
            }


            /// <summary>Options.Instance. Never log it or put it in an exception message.</summary>
            public string Instance {
                get {
                    return owner._options.Instance;
                }
            }


            /// <summary>True when a connection token refresh is required.</summary>
            public bool RefreshRequired {
                get {
                    return owner._refreshRequired;
                }
                set {
                    owner._refreshRequired = value;
                }
            }


            /// <summary>The connection token.</summary>
            public string ConnectionToken {
                get {
                    return owner._token;
                }
                set {
                    owner._token = value;
                }
            }


            /// <summary>Options.GetToken is set.</summary>
            public bool HasTokenGetter {
                get {
                    return owner._options.GetToken != null;
                }
            }


            /// <summary>Stores the client id a refresh reply carries.</summary>
            public void SetClientId(string clientId) {
                owner._clientId = clientId;
            }


            /// <summary>The retry policy shared by the connection refresh and the subscription refresh.</summary>
            public IRefreshRetryPolicy RefreshRetryPolicy {
                get {
                    return owner._refreshRetryPolicy;
                }
            }


            /// <summary>Creates one-shot step timers.</summary>
            public IStepTimerFactory Timers {
                get {
                    return owner._timers;
                }
            }


            /// <summary>Monotonic milliseconds.</summary>
            public long NowMs {
                get {
                    return owner._nowMs();
                }
            }


            /// <summary>Unix milliseconds: wall clock plus the server offset.</summary>
            public long ServerTimeMillis() {
                return owner.WallNowMs() + Interlocked.Read(ref owner._serverTimeOffsetMillis);
            }


            /// <summary>Next command id.</summary>
            public uint NextCommandId() {
                return owner.NextCommandId();
            }


            /// <summary>Queues one command for the next frame.</summary>
            public void QueueCommand(Protocol.Command command, TaskCompletionSource<bool>? sent) {
                owner.AddCommand(command, sent);
            }


            /// <summary>Sends one plain command on the given transport, with the client timeout.</summary>
            public Task<Protocol.Reply> SendOnTransportAsync(ITransport transport, Protocol.Command command, CancellationToken cancellationToken) {
                return owner.SendOnTransportCall(transport, command, cancellationToken);
            }


            /// <summary>Writes the queued rpc calls when no refresh is in flight and the client is Connected.</summary>
            public void FlushRpc() {
                if (owner._auth.IsInFlight || owner._state != CentrifugeClientState.Connected) {
                    return;
                }
                owner._rpcSession.Flush();
            }


            /// <summary>Runs the connect reply path for a reply held by the token exchange.</summary>
            public void ResumeConnect(Protocol.ConnectResult result) {
                owner.ResumeHeldConnect(result);
            }


            /// <summary>Applies the ping interval and the pong flag of a held connect reply.</summary>
            public void ApplyPing(uint pingSeconds, bool sendPong) {
                owner._serverPing = pingSeconds;
                owner._sendPong = sendPong;
            }


            /// <summary>Cancels the transport without a close handshake.</summary>
            public void CancelTransport(ITransport transport) {
                owner.CancelTransportNow(transport);
            }


            /// <summary>Raises the error event with a type string.</summary>
            public void ReportError(string type, Exception error) {
                owner.EmitErrorEvent(type, error);
            }


            /// <summary>Raises the error event with type "unclassified".</summary>
            public void ReportUnclassified(Exception error) {
                owner.EmitErrorEvent("unclassified", new CentrifugeUnclassifiedException(error));
            }


            /// <summary>Raises the auth event through the notice path.</summary>
            public Exception? RaiseAuth(CentrifugeAuthEventArgs args) {
                // On the connect path the caller reports the failure (as unclassified), so no report here.
                return EventDispatch.Raise(owner, owner.Auth, args, "auth", owner._connectAuthRaise ? null : owner.Error, owner._logger);
            }


            /// <summary>Raises an event handler through the notice path.</summary>
            public Exception? RaiseNotice<TArgs>(EventHandler<TArgs>? handler, TArgs args, string type) {
                return EventDispatch.Raise(owner, handler, args, type, owner.Error, owner._logger);
            }


            /// <summary>True when an event handler made another state transition after the given one.</summary>
            public bool TransitionSuperseded(int transition) {
                return owner.TransitionSuperseded(transition);
            }


            /// <summary>An exception escaped an event handler.</summary>
            public bool HandlerFailed(Exception? exception) {
                return owner.HandlerFailed(exception);
            }


            /// <summary>Dispatches a push that the token exchange held.</summary>
            public void DispatchHeldPush(Protocol.Push push) {
                owner.HandlePush(push);
            }


            /// <summary>Upstream DisconnectInternal.</summary>
            public void DisconnectWith(int code, string reason, bool reconnect) {
                owner.DisconnectInternal(code, reason, reconnect);
            }


            /// <summary>Disconnects with code 1 and no reconnect.</summary>
            public void FailUnauthorized() {
                owner.FailUnauthorized();
            }


            /// <summary>Asks for the connection token through the token getter.</summary>
            public void RequestConnectionToken(Action<string?> onToken, Action<Exception> onError) {
                owner.GetConnectionToken(onToken, onError);
            }


            /// <summary>Plain publish of one channel, for a subscription publish debounce.</summary>
            public Task PublishToChannelAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) {
                return owner.PublishAsync(channel, data, cancellationToken);
            }


            /// <summary>Queues an unsubscribe for a channel.</summary>
            public void SendUnsubscribe(string channel) {
                owner.SendUnsubscribe(channel);
            }


            /// <summary>Moves a subscription from one compaction id to another.</summary>
            public void SetSubscriptionId(CentrifugeSubscription subscription, long oldId, long newId) {
                owner.SetSubscriptionId(subscription, oldId, newId);
            }


            /// <summary>Reports a publication that could not be decoded.</summary>
            public void HandleUndecodablePublication(string message) {
                owner.HandleUndecodablePublication(message);
            }
        }
    }
}
