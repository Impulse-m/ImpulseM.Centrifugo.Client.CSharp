// Java ConnectListenerTest:
// - listenerFailureInOnConnectedLeavesTheHandshakeWhole: ported in part (OnConnected_ListenerThrows_LeavesTheHandshakeWhole).
//   The queued call is a client-side subscribe, not an rpc (the rpc answer belongs to slice S07). The expiry timer is an
//   upstream LoopTimer with no test seam, so its arming is not checked; the watchdog (fork timer) is checked.
// - errorFromOnConnectedStillDeliversTheRecoveredPublication: ported (OnConnected_ErrorThrows_StillDeliversRecoveredPublication).
//   An Exception subclass stands in for NoClassDefFoundError (rule R17).
// - queuedCallIsWrittenBeforeTheSubscribedAndPublicationCallbacks: ordering part ported (QueuedCall_IsWrittenBeforeSubscribedAndPublication,
//   the queued call is a client-side subscribe). Open: the "{}" result of the queued rpc call needs the rpc answer of slice S07.
// Java ServerPingDisabledTest.testConnectionStaysWithoutServerPings: ported (ServerPingDisabled_ConnectionStaysWithoutServerPings),
// with the fake clock in place of the real one-second wait.
// Java AppExceptionTest.testThrowingConnectedHandlerStopsVisibly is ported below.
// Lead decision L2: a failure of a connect handler is reported as "unclassified" (the tests above pin it). No test pins the
// earlier error type "connected".
// Lead decision L3, kept upstream, no code change (notes only):
// - a throwing Connecting handler (O-3, rule R42.9, R43f): Java testThrowingConnectingHandlerDoesNotStopConnect is not ported.
//   The handler's failure disconnects the client with code 2 and no reconnect (upstream HandlerFailed).
// - connect error 109 (R53): upstream code and reconnect, with the refresh on the next attempt (Client.ConnectError).
// - a null token from GetToken (R12c): upstream FailUnauthorized, not the Java "bad protocol (token)" path.
// - a bad endpoint (R46): the upstream constructor ArgumentException stays; no ConfigurationError event.
// - a throwing Error handler (plan 1.4.9, Java T15): upstream disconnects the client, the Java drop is not ported.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>The connect path around the Connected event: a failure disconnects once, visibly, with code 2.</summary>
    public sealed class ClientCoreSeamTests : FakeServerTestBase {
        private const string Channel = "news";

        // Half the ping window of a connect reply with ping 1 s and the default MaxServerPingDelay of 10 s: (1000 + 10000 + 1) / 2.
        private const int PingHalfWindowMs = 5500;


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


        // Java: listenerFailureInOnConnectedLeavesTheHandshakeWhole (the expiry timer part is not checked, see the header)
        /// <summary>
        /// A Connected handler that throws leaves the handshake whole: the call queued while disconnected reaches the server,
        /// the ping watchdog is armed when the failure is reported, and the failure is reported as "unclassified".
        /// </summary>
        [Fact]
        public async Task OnConnected_ListenerThrows_LeavesTheHandshakeWhole() {
            Server.ConnectResult = ConnectReplies.Connect(ping: 1);
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
            client.Error += (_, e) => {
                if (e.Type != "unclassified") {
                    return;
                }
                // The ping watchdog arms its first check at half the window: (ping 1 s + MaxServerPingDelay 10 s + 1) / 2.
                watchdogArmedAtReport = timers.PendingWithDelay(PingHalfWindowMs) == 1;
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
            Assert.IsType<InvalidOperationException>(((CentrifugeUnclassifiedException)error.Exception!).Error);
        }


        // Java: queuedCallIsWrittenBeforeTheSubscribedAndPublicationCallbacks (the ordering part; the rpc result waits for S07)
        /// <summary>
        /// A call queued while disconnected is written before the subscribed and the publication callbacks of the connect reply run.
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


        // An Exception standing in for the Error of the Java test (rule R17: C# has no Error class).
        private sealed class LinkageFailure : Exception {
            public LinkageFailure(string message) : base(message) {
            }
        }
    }
}
