using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Centrifugal.Centrifuge.Transports;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// A test double of the client host: the helper classes of the client run against it without a client or a server.
    /// Its settable members are the state the test sets; its lists record what the helpers asked for. The fake runs
    /// no step of its own: the test sets the state, calls the helper, and reads the records. The records are read
    /// after the step that wrote them, as the tests do.
    /// </summary>
    internal sealed class FakeClientHost : IClientHost {
        /// <summary>Set by the tests: the answer of the token source request, see RequestConnectionTokenHandler.</summary>
        public Action<Action<string?>, Action<Exception>>? RequestConnectionTokenHandler { get; set; }


        /// <summary>Set by the tests: the answer of PublishToChannelAsync. Null completes at once.</summary>
        public Func<string, byte[], Task>? PublishHandler { get; set; }


        /// <summary>Set by the tests: the answer of SendOnTransportAsync. Null fails with a lost connection.</summary>
        public Func<ITransport, Protocol.Command, Task<Protocol.Reply>>? SendOnTransportHandler { get; set; }


        /// <summary>Set by the tests: the answer of TransitionSuperseded.</summary>
        public bool TransitionSupersededResult { get; set; }


        /// <summary>Set by the tests: the answer of RaiseAuth and RaiseNotice, the exception a handler threw, or null.</summary>
        public Exception? RaiseResult { get; set; }


        /// <summary>Set by the tests: the answer of HandlerFailed.</summary>
        public bool HandlerFailedResult { get; set; }


        /// <summary>The reported errors, as (type, error), in order.</summary>
        public List<(string Type, Exception Error)> Reported { get; } = new List<(string Type, Exception Error)>();


        /// <summary>The unclassified errors, in order.</summary>
        public List<Exception> Unclassified { get; } = new List<Exception>();


        /// <summary>The disconnects requested, as (code, reason, reconnect), in order.</summary>
        public List<(int Code, string Reason, bool Reconnect)> Disconnects { get; } = new List<(int Code, string Reason, bool Reconnect)>();


        /// <summary>The commands queued for the next frame, in order.</summary>
        public List<Protocol.Command> QueuedCommands { get; } = new List<Protocol.Command>();


        /// <summary>The connect replies handed to ResumeConnect, in order.</summary>
        public List<Protocol.ConnectResult> ResumedConnects { get; } = new List<Protocol.ConnectResult>();


        /// <summary>The ping settings handed to ApplyPing, in order.</summary>
        public List<(uint Seconds, bool SendPong)> AppliedPings { get; } = new List<(uint Seconds, bool SendPong)>();


        /// <summary>The transports handed to CancelTransport, in order.</summary>
        public List<ITransport> CancelledTransports { get; } = new List<ITransport>();


        /// <summary>The auth events raised, in order.</summary>
        public List<CentrifugeAuthEventArgs> AuthRaised { get; } = new List<CentrifugeAuthEventArgs>();


        /// <summary>The types of the notices raised, in order.</summary>
        public List<string> NoticeTypes { get; } = new List<string>();


        /// <summary>The pushes handed to DispatchHeldPush, in order.</summary>
        public List<Protocol.Push> HeldPushes { get; } = new List<Protocol.Push>();


        /// <summary>The exceptions given to HandlerFailed that were not null, in order.</summary>
        public List<Exception> HandlerFailures { get; } = new List<Exception>();


        /// <summary>The channels given to SendUnsubscribe, in order.</summary>
        public List<string> Unsubscribes { get; } = new List<string>();


        /// <summary>The subscriptions moved to another id, as (subscription, old id, new id), in order.</summary>
        public List<(CentrifugeSubscription Subscription, long OldId, long NewId)> SubscriptionIds { get; } = new List<(CentrifugeSubscription Subscription, long OldId, long NewId)>();


        /// <summary>The messages of the publications that could not be decoded, in order.</summary>
        public List<string> UndecodablePublications { get; } = new List<string>();


        /// <summary>The channel publishes of PublishToChannelAsync, as (channel, data), in order.</summary>
        public List<(string Channel, byte[] Data)> PublishedChannels { get; } = new List<(string Channel, byte[] Data)>();


        /// <summary>The token requests not answered yet, as (onToken, onError), in order. Filled when RequestConnectionTokenHandler is null.</summary>
        public List<(Action<string?> OnToken, Action<Exception> OnError)> ConnectionTokenRequests { get; } = new List<(Action<string?> OnToken, Action<Exception> OnError)>();


        /// <summary>The number of FlushRpc calls.</summary>
        public int FlushCount { get; private set; }


        /// <summary>The number of FailUnauthorized calls.</summary>
        public int FailedUnauthorized { get; private set; }


        /// <summary>The fake timers. Advance them to move the clock of the host.</summary>
        public FakeStepTimerFactory FakeTimers { get; }


        /// <summary>The loop of the host: a fake host runs its steps on this loop.</summary>
        public EventLoop Loop { get; }


        /// <summary>The state of the client.</summary>
        public CentrifugeClientState State { get; set; } = CentrifugeClientState.Disconnected;


        /// <summary>True once the client was closed.</summary>
        public bool IsClosed { get; set; }


        /// <summary>True when the state is Connected.</summary>
        public bool IsConnected {
            get {
                return State == CentrifugeClientState.Connected;
            }
        }


        /// <summary>True when the transport of the current attempt is open.</summary>
        public bool IsTransportOpen { get; set; }


        /// <summary>The transport of the current attempt, or null.</summary>
        public ITransport? Transport { get; set; }


        /// <summary>Options.Timeout in milliseconds.</summary>
        public int TimeoutMs { get; set; } = 5000;


        /// <summary>Options.MaxServerPingDelay.</summary>
        public TimeSpan MaxServerPingDelay { get; set; } = TimeSpan.FromSeconds(10);


        /// <summary>True when the connect reply confirmed RPC options.</summary>
        public bool RpcOptionsSupported { get; set; }


        /// <summary>True when the instance is set.</summary>
        public bool HasInstance { get; set; }


        /// <summary>The instance of the client.</summary>
        public string Instance { get; set; } = string.Empty;


        /// <summary>True when a refresh of the connection token is required.</summary>
        public bool RefreshRequired { get; set; }


        /// <summary>The connection token.</summary>
        public string ConnectionToken { get; set; } = string.Empty;


        /// <summary>The retry policy of the host.</summary>
        public IRefreshRetryPolicy RefreshRetryPolicy { get; set; } = new RefreshRetryPolicy(new FixedRandom(0.0), 5000, 10000, 300000, true);


        /// <summary>The monotonic clock of the host, in milliseconds: the elapsed time of the fake timers.</summary>
        public long NowMs {
            get {
                return FakeTimers.ElapsedMs;
            }
        }


        /// <summary>The unix time in milliseconds that ServerTimeMillis returns.</summary>
        public long ServerTimeMillisValue { get; set; }


        /// <summary>The next command id NextCommandId returns. Starts at 1: an id is never 0.</summary>
        public uint NextCommandIdValue { get; set; } = 1;


        /// <summary>
        /// Creates the host. Its loop runs the steps the helpers queue; its timers are the fake ones.
        /// </summary>
        public FakeClientHost() {
            Loop = new EventLoop(null, null);
            FakeTimers = new FakeStepTimerFactory(Loop);
        }

        /// <summary>The timer factory the helpers use: the fake one.</summary>
        public IStepTimerFactory Timers {
            get {
                return FakeTimers;
            }
        }

        /// <summary>Returns the value of ServerTimeMillisValue.</summary>
        public long ServerTimeMillis() {
            return ServerTimeMillisValue;
        }

        /// <summary>Returns the next command id and advances the counter.</summary>
        public uint NextCommandId() {
            uint id = NextCommandIdValue;
            NextCommandIdValue++;
            return id;
        }

        /// <summary>Records the command. The write result is reported as written at once.</summary>
        /// <param name="command">The command.</param>
        /// <param name="sent">The write result, completed with true.</param>
        public void QueueCommand(Protocol.Command command, TaskCompletionSource<bool>? sent) {
            QueuedCommands.Add(command);
            sent?.TrySetResult(true);
        }

        /// <summary>Answers from SendOnTransportHandler. Without a handler the command fails as a lost connection.</summary>
        /// <param name="transport">The transport.</param>
        /// <param name="command">The command.</param>
        /// <param name="cancellationToken">Not used by the fake.</param>
        public Task<Protocol.Reply> SendOnTransportAsync(ITransport transport, Protocol.Command command, CancellationToken cancellationToken) {
            if (SendOnTransportHandler != null) {
                return SendOnTransportHandler(transport, command);
            }
            return Task.FromException<Protocol.Reply>(new CentrifugeConnectionLostException(11, "connection closed", false, null));
        }

        /// <summary>Counts one flush.</summary>
        public void FlushRpc() {
            FlushCount++;
        }

        /// <summary>Records the connect reply.</summary>
        /// <param name="result">The connect reply.</param>
        public void ResumeConnect(Protocol.ConnectResult result) {
            ResumedConnects.Add(result);
        }

        /// <summary>Records the ping settings.</summary>
        /// <param name="pingSeconds">The ping interval in seconds.</param>
        /// <param name="sendPong">The pong flag.</param>
        public void ApplyPing(uint pingSeconds, bool sendPong) {
            AppliedPings.Add((pingSeconds, sendPong));
        }

        /// <summary>Records the transport.</summary>
        /// <param name="transport">The transport.</param>
        public void CancelTransport(ITransport transport) {
            CancelledTransports.Add(transport);
        }

        /// <summary>Records the error.</summary>
        /// <param name="type">The error type.</param>
        /// <param name="error">The error.</param>
        public void ReportError(string type, Exception error) {
            Reported.Add((type, error));
        }

        /// <summary>Records the error.</summary>
        /// <param name="error">The error.</param>
        public void ReportUnclassified(Exception error) {
            Unclassified.Add(error);
        }

        /// <summary>Records the event args and returns RaiseResult.</summary>
        /// <param name="args">The auth event args.</param>
        public Exception? RaiseAuth(CentrifugeAuthEventArgs args) {
            AuthRaised.Add(args);
            return RaiseResult;
        }

        /// <summary>Records the notice type and returns RaiseResult. The handler is not run.</summary>
        /// <param name="handler">The handler, not run.</param>
        /// <param name="args">The event args, not recorded.</param>
        /// <param name="type">The notice type.</param>
        public Exception? RaiseNotice<TArgs>(EventHandler<TArgs>? handler, TArgs args, string type) {
            NoticeTypes.Add(type);
            return RaiseResult;
        }

        /// <summary>Returns TransitionSupersededResult.</summary>
        /// <param name="transition">The transition number, not used by the fake.</param>
        public bool TransitionSuperseded(int transition) {
            return TransitionSupersededResult;
        }

        /// <summary>Records a non-null exception and returns HandlerFailedResult.</summary>
        /// <param name="exception">The exception the handler threw.</param>
        public bool HandlerFailed(Exception? exception) {
            if (exception != null) {
                HandlerFailures.Add(exception);
            }
            return HandlerFailedResult;
        }

        /// <summary>Records the push.</summary>
        /// <param name="push">The push.</param>
        public void DispatchHeldPush(Protocol.Push push) {
            HeldPushes.Add(push);
        }

        /// <summary>Records the disconnect.</summary>
        /// <param name="code">The code.</param>
        /// <param name="reason">The reason.</param>
        /// <param name="reconnect">The reconnect flag.</param>
        public void DisconnectWith(int code, string reason, bool reconnect) {
            Disconnects.Add((code, reason, reconnect));
        }

        /// <summary>Counts the call and records a disconnect with code 1 and no reconnect.</summary>
        public void FailUnauthorized() {
            FailedUnauthorized++;
            Disconnects.Add((CentrifugeDisconnectedCodes.Unauthorized, "unauthorized", false));
        }

        /// <summary>Answers through RequestConnectionTokenHandler, or records the request when the handler is null.</summary>
        /// <param name="onToken">Receives the token.</param>
        /// <param name="onError">Receives the error.</param>
        public void RequestConnectionToken(Action<string?> onToken, Action<Exception> onError) {
            if (RequestConnectionTokenHandler != null) {
                RequestConnectionTokenHandler(onToken, onError);
                return;
            }
            ConnectionTokenRequests.Add((onToken, onError));
        }

        /// <summary>Records the publish and answers from PublishHandler. Completes at once without a handler.</summary>
        /// <param name="channel">The channel.</param>
        /// <param name="data">The data, copied.</param>
        /// <param name="cancellationToken">Not used by the fake.</param>
        public Task PublishToChannelAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) {
            byte[] bytes = data.ToArray();
            PublishedChannels.Add((channel, bytes));
            if (PublishHandler != null) {
                return PublishHandler(channel, bytes);
            }
            return Task.CompletedTask;
        }

        /// <summary>Records the channel.</summary>
        /// <param name="channel">The channel.</param>
        public void SendUnsubscribe(string channel) {
            Unsubscribes.Add(channel);
        }

        /// <summary>Records the subscription move.</summary>
        /// <param name="subscription">The subscription.</param>
        /// <param name="oldId">The old id.</param>
        /// <param name="newId">The new id.</param>
        public void SetSubscriptionId(CentrifugeSubscription subscription, long oldId, long newId) {
            SubscriptionIds.Add((subscription, oldId, newId));
        }

        /// <summary>Records the message.</summary>
        /// <param name="message">The message.</param>
        public void HandleUndecodablePublication(string message) {
            UndecodablePublications.Add(message);
        }
    }
}
