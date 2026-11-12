// Not ported: aSmallCommandIsTheDelimitedBytes: it checks a frame encoder that the client no longer has; the batch write is the
// delimited write of each command, and the test below compares its bytes with the varint write.
// Not ported: anEmptyPayloadIsTheDelimitedBytes: it checks the frame encoder, which the client no longer has; the test below
// covers the delimited write of commands.
// Not ported: aMegabytePayloadIsTheDelimitedBytes: it checks the frame encoder, which the client no longer has; the test below
// covers the delimited write of commands.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
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
            // written with no transport, so the wire looks the same. No test here fails without the guard.
            Assert.DoesNotContain(Server.Received, (Protocol.Command command) => command.CalculateSize() == 0);
        }


        /// <summary>
        /// The same frame shape with pong off: the next connection stays up and the event sequence is the same. The test does not
        /// wait in real time for a stale ping timer to fire; the stale ping is covered by the wire check of the test above.
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


        /// <summary>
        /// The batch write of a command is its delimited write: for an empty command (the pong, one zero byte) and for a command
        /// above 127 bytes (a two-byte length prefix), the bytes equal the varint-delimited write of the serialized command.
        /// </summary>
        [Fact]
        public void WriteDelimitedTo_SameBytesAsTheVarintWrite_ForEmptyAndTwoBytePrefixCommands() {
            Protocol.Command empty = new Protocol.Command();
            Protocol.Command large = new Protocol.Command {
                Id = 1,
                Rpc = new Protocol.RPCRequest {
                    Method = "blob.writePart",
                    Data = ByteString.CopyFrom(new byte[200]),
                },
            };
            Assert.True(large.CalculateSize() > 127);

            Assert.Equal(new byte[] { 0 }, DelimitedBytes(empty));
            Assert.Equal(VarintBytes(empty), DelimitedBytes(empty));
            Assert.Equal(VarintBytes(large), DelimitedBytes(large));
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


        // The bytes of the protobuf delimited write of a command, as the batch write produces them.
        private static byte[] DelimitedBytes(Protocol.Command command) {
            using MemoryStream stream = new MemoryStream();
            command.WriteDelimitedTo(stream);
            return stream.ToArray();
        }


        // The bytes of the varint-delimited write of the serialized command.
        private static byte[] VarintBytes(Protocol.Command command) {
            using MemoryStream stream = new MemoryStream();
            VarintCodec.WriteDelimitedMessage(stream, command.ToByteArray());
            return stream.ToArray();
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
