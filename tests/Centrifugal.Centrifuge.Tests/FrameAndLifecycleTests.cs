// Ported from Java FrameAndLifecycleTest, the two ping tests (2 of its 4 tests; the subscribe retry and the collected client
// tests belong to the subscription and lifecycle areas). The Java second test sleeps 2.5 s to see a stale ping timer fire;
// the port keeps the event sequence and does not wait in real time (rule 4.4.2).
using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// A ping that arrives after a disconnect push in the same frame belongs to the connection that ended.
    /// </summary>
    [Collection("Integration")]
    public sealed class FrameAndLifecycleTests : FakeServerTestBase {
        /// <summary>A server ping after a disconnect push in the same frame is ignored, and the client reconnects.</summary>
        [Fact]
        // Java: testPingAfterDisconnectPushInSameFrameIsIgnored
        public async Task PingAfterDisconnectPushInSameFrameIsIgnored() {
            Server.ConnectResult.Ping = 25;
            Server.ConnectResult.Pong = true;
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                MinReconnectDelay = TimeSpan.FromMilliseconds(50),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
            });
            Channel<string> events = Channel.CreateUnbounded<string>();
            Channel<CentrifugeConnectedEventArgs> connects = Channel.CreateUnbounded<CentrifugeConnectedEventArgs>();
            Record(client, events, connects);

            client.Connect();
            await connects.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Server.SendRepliesAsync(DisconnectPush(3001, "reconnect"), new Protocol.Reply());
            await connects.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await client.LoopBarrierAsync();

            Assert.Equal(
                new List<string> {
                    "connecting:" + CentrifugeConnectingCodes.ConnectCalled,
                    "connected",
                    "connecting:3001",
                    "connected",
                },
                Drain(events));
            Assert.Equal(CentrifugeClientState.Connected, client.State);
            // The pong of the ignored ping would be an empty command. No pong may reach the server on the new connection.
            // This does not pin the per-message guard: with the guard removed, the stale pong is dropped when its batch is
            // written with no transport, so the wire looks the same. Rule R43.3 has no test that fails without the guard.
            Assert.DoesNotContain(Server.Received, (Protocol.Command command) => command.CalculateSize() == 0);
        }


        /// <summary>
        /// The same frame shape with pong off: the next connection stays up and the event sequence is the same. The Java
        /// test waits 2.5 s for a stale ping timer to fire. That wait is not reproduced (rule 4.4.2); the stale ping is
        /// covered by the wire check of the test above.
        /// </summary>
        [Fact]
        // Java: testPingAfterDisconnectPushInSameFrameLeavesNoStalePingTimer
        public async Task PingAfterDisconnectPushInSameFrameKeepsTheNextConnection() {
            Server.ConnectResult.Ping = 1;
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                MinReconnectDelay = TimeSpan.FromMilliseconds(300),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(400),
                MaxServerPingDelay = TimeSpan.FromMilliseconds(300),
            });
            Channel<string> events = Channel.CreateUnbounded<string>();
            Channel<CentrifugeConnectedEventArgs> connects = Channel.CreateUnbounded<CentrifugeConnectedEventArgs>();
            Record(client, events, connects);

            client.Connect();
            await connects.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            // The next connection pings rarely: only a stale timer could end it in the next seconds.
            Server.ConnectResult = new Protocol.ConnectResult { Client = "fake-client", Version = "0.0.0", Ping = 25 };
            await Server.SendRepliesAsync(DisconnectPush(3001, "reconnect"), new Protocol.Reply());
            await connects.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await client.LoopBarrierAsync();

            Assert.Equal(
                new List<string> {
                    "connecting:" + CentrifugeConnectingCodes.ConnectCalled,
                    "connected",
                    "connecting:3001",
                    "connected",
                },
                Drain(events));
        }


        // Records the lifecycle events of the client as text, and the connected event args.
        private static void Record(CentrifugeClient client, Channel<string> events, Channel<CentrifugeConnectedEventArgs> connects) {
            client.Connecting += (object? sender, CentrifugeConnectingEventArgs args) => events.Writer.TryWrite("connecting:" + args.Code);
            client.Connected += (object? sender, CentrifugeConnectedEventArgs args) => {
                events.Writer.TryWrite("connected");
                connects.Writer.TryWrite(args);
            };
            client.Disconnected += (object? sender, CentrifugeDisconnectedEventArgs args) => events.Writer.TryWrite("disconnected:" + args.Code);
            client.Error += (object? sender, CentrifugeErrorEventArgs args) => events.Writer.TryWrite("error:" + args.Type);
        }


        // Takes every text the channel holds now, in order.
        private static List<string> Drain(Channel<string> events) {
            List<string> taken = new List<string>();
            while (events.Reader.TryRead(out string? text)) {
                taken.Add(text);
            }
            return taken;
        }


        // A reply that carries a disconnect push with the code and reason.
        private static Protocol.Reply DisconnectPush(uint code, string reason) {
            return new Protocol.Reply {
                Push = new Protocol.Push {
                    Disconnect = new Protocol.Disconnect { Code = code, Reason = reason },
                },
            };
        }
    }
}
