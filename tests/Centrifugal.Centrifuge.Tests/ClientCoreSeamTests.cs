// The connect path around the event handlers. A failure of a handler is reported as an error of type "unclassified", and the
// client disconnects once with code 2; the rest of the connect reply still runs.
// Not ported: testThrowingConnectingHandlerDoesNotStopConnect: a failing handler disconnects the client with code 2 and no
// reconnect, so the connect does not go on past it.
// The upstream rule is kept: a null token from GetToken fails the client as unauthorized, with no bad-protocol disconnect.
// The upstream rule is kept: a bad endpoint is refused by the constructor with an ArgumentException, and no error event is raised.
// The upstream rule is kept: a recovered publication of a server-side subscription is delivered only when the connect reply says it recovered.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Centrifugal.Centrifuge.Transports;
using Google.Protobuf;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>The connect path around the Connected event: a failure disconnects once, visibly, with code 2.</summary>
    public sealed class ClientCoreSeamTests : FakeServerTestBase {
        private const string Channel = "news";

        // Half the ping window of a connect reply with ping 1 s and the default MaxServerPingDelay of 10 s: (1000 + 10000 + 1) / 2.
        private const int PingHalfWindowMs = 5500;

        // The renewal delay of a connection with a ttl of 100 s: 95 percent of the ttl, in milliseconds.
        private const int ExpiryDelayMs = 95000;


        // Java: testThrowingConnectedHandlerStopsVisibly
        /// <summary>A Connected handler that throws disconnects the client once with code 2 and reason "exception in event handler".</summary>
        [Fact]
        public async Task ConnectedHandler_Throws_DisconnectsCode2NoReconnect() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);
            client.Connected += (_, _) => throw new InvalidOperationException("handler failed");

            client.Connect();
            CentrifugeDisconnectedEventArgs args = await disconnected;

            Assert.Equal(2, args.Code);
            Assert.StartsWith("exception in event handler", args.Reason);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }


        /// <summary>The failure of a Connected handler is reported as an error of type "unclassified" that wraps the handler's exception.</summary>
        [Fact]
        public async Task ConnectedHandler_Throws_RaisesUnclassifiedWrappingTheFailure() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);
            List<CentrifugeErrorEventArgs> errors = new List<CentrifugeErrorEventArgs>();
            client.Error += (_, e) => errors.Add(e);
            client.Connected += (_, _) => throw new InvalidOperationException("handler failed");

            client.Connect();
            await disconnected;

            CentrifugeErrorEventArgs error = Assert.Single(errors);
            Assert.Equal("unclassified", error.Type);
            CentrifugeUnclassifiedException unclassified = Assert.IsType<CentrifugeUnclassifiedException>(error.Exception);
            Assert.IsType<InvalidOperationException>(unclassified.Error);
        }


        /// <summary>A Connected handler that returns normally leaves the client connected.</summary>
        [Fact]
        public async Task ConnectedHandler_ReturnsNormally_StaysConnected() {
            CentrifugeClient client = NewClient();
            client.Connect();

            await client.ReadyAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        // Java: listenerFailureInOnConnectedLeavesTheHandshakeWhole
        /// <summary>
        /// A Connected handler that throws leaves the handshake whole: the call queued while disconnected reaches the server,
        /// the ping watchdog is armed when the failure is reported, and the failure is reported as "unclassified". The expiry
        /// timer is an upstream timer with no test seam, so its arming is not checked.
        /// </summary>
        [Fact]
        public async Task OnConnected_ListenerThrows_LeavesTheHandshakeWhole() {
            Server.ConnectResult = ConnectReplies.Connect(ping: 1, expires: true, ttl: 100);
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers);
            bool failOnConnect = false;
            client.Connected += (_, _) => {
                if (failOnConnect) {
                    throw new InvalidOperationException("listener failed on connect");
                }
            };
            TaskCompletionSource<CentrifugeErrorEventArgs> reported =
                new TaskCompletionSource<CentrifugeErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool watchdogArmedAtReport = false;
            bool expiryArmedAtReport = false;
            client.Error += (_, e) => {
                if (e.Type != "unclassified") {
                    return;
                }
                // The ping watchdog arms its first check at half the window: (ping 1 s + MaxServerPingDelay 10 s + 1) / 2.
                watchdogArmedAtReport = timers.PendingWithDelay(PingHalfWindowMs) == 1;
                // The expiry timer of the connection is armed before the listeners run: 95 percent of the ttl of 100 s.
                expiryArmedAtReport = timers.PendingWithDelay(ExpiryDelayMs) == 1;
                reported.TrySetResult(e);
            };
            TaskCompletionSource<bool> queuedWritten = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.BeforeCommand = (Command command) => {
                if (command.Subscribe != null && command.Subscribe.Channel == "queued") {
                    queuedWritten.TrySetResult(true);
                }
                return Task.CompletedTask;
            };

            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));
            client.Disconnect();
            CentrifugeSubscription queued = client.NewSubscription("queued");
            queued.Subscribe();
            failOnConnect = true;
            client.Connect();

            CentrifugeErrorEventArgs error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await queuedWritten.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(watchdogArmedAtReport);
            Assert.True(expiryArmedAtReport);
            Assert.IsType<InvalidOperationException>(((CentrifugeUnclassifiedException)error.Exception!).Error);
        }


        // Java: queuedCallIsWrittenBeforeTheSubscribedAndPublicationCallbacks
        /// <summary>
        /// A call queued while disconnected is written before the subscribed and the publication callbacks of the connect reply run.
        /// The queued call is a client-side subscribe, so the result of an rpc call is not checked here.
        /// The subscribed callback waits on the loop until the fake server has received the queued subscribe; the server reads it
        /// on its own thread, so the wait does not deadlock.
        /// </summary>
        [Fact]
        public async Task QueuedCall_IsWrittenBeforeSubscribedAndPublication() {
            Server.ConnectResult = RecoveredPublicationReply();
            CentrifugeClient client = NewClient();
            TaskCompletionSource<bool> queuedWritten = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.BeforeCommand = (Command command) => {
                if (command.Subscribe != null && command.Subscribe.Channel == "queued") {
                    queuedWritten.TrySetResult(true);
                }
                return Task.CompletedTask;
            };
            ConcurrentQueue<string> order = new ConcurrentQueue<string>();
            bool writtenBeforeSubscribed = false;
            TaskCompletionSource<bool> published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ServerSubscribed += (_, _) => {
                writtenBeforeSubscribed = queuedWritten.Task.Wait(TimeSpan.FromSeconds(10));
                order.Enqueue("subscribed");
            };
            client.Publication += (_, _) => {
                order.Enqueue("publication");
                published.TrySetResult(true);
            };
            CentrifugeSubscription queued = client.NewSubscription("queued");
            queued.Subscribe();

            client.Connect();
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(writtenBeforeSubscribed);
            Assert.Equal(new[] { "subscribed", "publication" }, order.ToArray());
        }


        // Java: errorFromOnConnectedStillDeliversTheRecoveredPublication
        /// <summary>
        /// A failure of Connected that is not an application exception still delivers the publication the connect reply recovers,
        /// and is reported as "unclassified".
        /// </summary>
        [Fact]
        public async Task OnConnected_ErrorThrows_StillDeliversRecoveredPublication() {
            Server.ConnectResult = RecoveredPublicationReply();
            CentrifugeClient client = NewClient();
            TaskCompletionSource<CentrifugePublicationEventArgs> publication =
                new TaskCompletionSource<CentrifugePublicationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<CentrifugeErrorEventArgs> reported =
                new TaskCompletionSource<CentrifugeErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Connected += (_, _) => throw new LinkageFailure("listener linkage failed on connect");
            client.Publication += (_, e) => publication.TrySetResult(e);
            client.Error += (_, e) => {
                if (e.Type == "unclassified") {
                    reported.TrySetResult(e);
                }
            };

            client.Connect();

            CentrifugePublicationEventArgs delivered = await publication.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CentrifugeErrorEventArgs error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Channel, delivered.Channel);
            Assert.IsType<LinkageFailure>(((CentrifugeUnclassifiedException)error.Exception!).Error);
        }


        /// <summary>
        /// A failure while the connect reply is processed outside the notice list (here, reading the transport's emulation flag
        /// in the connect reply) is reported as an error of type "unclassified" that wraps the exception. The client disconnects
        /// once with code 2 and reason "bad protocol (message)", and does not reconnect.
        /// </summary>
        [Fact]
        public async Task ConnectReply_FailsOutsideTheNotices_DisconnectsOnceWithCode2NoReconnect() {
            CentrifugeClient client = NewClient();
            TaskCompletionSource<ForwardingTransport> created =
                new TaskCompletionSource<ForwardingTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => {
                ForwardingTransport transport = new ForwardingTransport(new WebSocketTransport(endpoint), false, false);
                created.TrySetResult(transport);
                return transport;
            });
            // Armed once the connect command is written, so the flag is read when the reply is handled.
            Server.BeforeCommand = (Command command) => {
                if (command.Connect != null) {
                    created.Task.Result.ThrowOnUsesEmulation = true;
                }
                return Task.CompletedTask;
            };
            List<CentrifugeErrorEventArgs> errors = new List<CentrifugeErrorEventArgs>();
            client.Error += (_, e) => errors.Add(e);
            List<CentrifugeDisconnectedEventArgs> disconnects = new List<CentrifugeDisconnectedEventArgs>();
            client.Disconnected += (_, e) => disconnects.Add(e);
            List<CentrifugeConnectingEventArgs> connecting = new List<CentrifugeConnectingEventArgs>();
            client.Connecting += (_, e) => connecting.Add(e);
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);

            client.Connect();
            CentrifugeDisconnectedEventArgs args = await disconnected.WaitAsync(TimeSpan.FromSeconds(10));
            await client.LoopBarrierAsync();

            Assert.Equal(2, args.Code);
            Assert.Equal("bad protocol (message)", args.Reason);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            Assert.Single(disconnects);
            CentrifugeErrorEventArgs error = Assert.Single(errors);
            Assert.Equal("unclassified", error.Type);
            Assert.IsType<InvalidOperationException>(((CentrifugeUnclassifiedException)error.Exception!).Error);
            // The only Connecting event is the one of the connect call: no reconnect follows the disconnect.
            Assert.Single(connecting);
        }


        // Java: testConnectionStaysWithoutServerPings
        /// <summary>A server with pings disabled sends no ping interval: the client arms no watchdog, stays connected and does not reconnect.</summary>
        [Fact]
        public async Task ServerPingDisabled_ConnectionStaysWithoutServerPings() {
            Server.ConnectResult = ConnectReplies.Connect();
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, new CentrifugeClientOptions {
                MaxServerPingDelay = TimeSpan.FromMilliseconds(300),
                MinReconnectDelay = TimeSpan.FromMilliseconds(50),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
            });
            int connecting = 0;
            client.Connecting += (_, _) => connecting++;

            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, timers.PendingCount);
            timers.Advance(1000);
            await client.LoopBarrierAsync();

            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal(1, connecting);
        }


        // The connect reply that subscribes Channel and recovers one publication on it.
        private static ConnectResult RecoveredPublicationReply() {
            ConnectResult reply = ConnectReplies.Connect();
            reply.Subs[Channel] = new SubscribeResult {
                Recoverable = true,
                Recovered = true,
                Positioned = true,
                Epoch = "epoch",
                Offset = 1,
                Publications = {
                    new Protocol.Publication {
                        Offset = 1,
                        Data = ByteString.CopyFromUtf8("{}"),
                    },
                },
            };
            return reply;
        }


        // An exception type standing in for a fatal error (C# has no Error class).
        private sealed class LinkageFailure : Exception {
            public LinkageFailure(string message) : base(message) {
            }
        }
    }
}
