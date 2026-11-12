// Port-only tests of the transport callbacks, pinning the upstream behaviour the fork keeps (decisions O-4 and the close
// mapping of the Java client). They have no Java counterpart of their own; see each test.
using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// The close codes of the socket, the empty close reason and an undecodable frame, as the transport callbacks map them.
    /// </summary>
    [Collection("Integration")]
    public sealed class TransportCallbackTests : FakeServerTestBase {
        /// <summary>
        /// A socket close maps its code: 1009 is a message size limit with no reconnect, codes below 3000 are a transport
        /// closed, and a server code keeps its reason. A permanent server code ends the connection.
        /// </summary>
        /// <param name="closeCode">The WebSocket close code the server sends.</param>
        /// <param name="closeReason">The close reason the server sends.</param>
        /// <param name="reconnect">True when the client reconnects.</param>
        /// <param name="expectedCode">The disconnect or connecting code the client reports.</param>
        /// <param name="expectedReason">The reason the client reports.</param>
        [Theory]
        [InlineData(1009, "too big", false, 3, "message size limit exceeded")]
        [InlineData(1000, "bye", true, 1, "transport closed")]
        [InlineData(3001, "custom", true, 3001, "custom")]
        [InlineData(3500, "gone", false, 3500, "gone")]
        [InlineData(3000, "", true, 3000, "connection closed")]
        public async Task CloseCodesMapAsInJava(int closeCode, string closeReason, bool reconnect, int expectedCode, string expectedReason) {
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Channel<(bool Disconnected, int Code, string Reason)> ends = Channel.CreateUnbounded<(bool Disconnected, int Code, string Reason)>();
            client.Connecting += (object? sender, CentrifugeConnectingEventArgs args) => ends.Writer.TryWrite((false, args.Code, args.Reason));
            client.Disconnected += (object? sender, CentrifugeDisconnectedEventArgs args) => ends.Writer.TryWrite((true, args.Code, args.Reason));

            await Server.CloseWithCodeAsync((ushort)closeCode, closeReason);
            (bool Disconnected, int Code, string Reason) end = await ends.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(!reconnect, end.Disconnected);
            Assert.Equal(expectedCode, end.Code);
            Assert.Equal(expectedReason, end.Reason);
        }


        /// <summary>
        /// A frame that is not Centrifugo data reports a parse error and then reconnects, with the transport closed code.
        /// </summary>
        [Fact]
        // The Java client disconnects with code 2 and no reconnect here. The fork keeps the upstream rule (decision O-4).
        public async Task UndecodableFrameReconnectsWithTransportClosed() {
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Channel<string> errors = Channel.CreateUnbounded<string>();
            Channel<CentrifugeConnectingEventArgs> connecting = Channel.CreateUnbounded<CentrifugeConnectingEventArgs>();
            client.Error += (object? sender, CentrifugeErrorEventArgs args) => errors.Writer.TryWrite(args.Type);
            client.Connecting += (object? sender, CentrifugeConnectingEventArgs args) => connecting.Writer.TryWrite(args);

            // A frame of length 3 whose message has an invalid tag (field 0, wire type 7).
            await Server.SendRawAsync(new byte[] { 0x03, 0x07, 0x00, 0x00 });

            string type = await errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("parse", type);
            CentrifugeConnectingEventArgs args = await connecting.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CentrifugeConnectingCodes.TransportClosed, args.Code);
            Assert.Equal("transport closed", args.Reason);
        }
    }
}
