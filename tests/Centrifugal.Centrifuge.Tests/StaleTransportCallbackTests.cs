// Ported from Java StaleTransportCallbackTest (1 test). The Java test sleeps 500 ms so that late callbacks of the replaced
// socket arrive. The port waits for the replaced socket to close at the TCP level (through a relay) and then for the steps
// queued on the loop, so no fixed delay is used (rule 4.4.2). The client's own callbacks cannot be observed, so a late
// callback that the guard drops is seen only as the absence of an effect.
using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// The callbacks of a socket that was replaced cannot report errors or move the client.
    /// </summary>
    [Collection("Integration")]
    public sealed class StaleTransportCallbackTests : FakeServerTestBase {
        /// <summary>After disconnect() and connect(), the callbacks of the replaced socket report nothing.</summary>
        [Fact]
        // Java: testOldTransportCallbacksIgnoredAfterDisconnectThenConnect
        public async Task OldTransportCallbacksIgnoredAfterDisconnectThenConnect() {
            Uri upstream = new Uri(Server.Url);
            using CountingRelay relay = new CountingRelay(upstream.Host, upstream.Port);
            CentrifugeClient client = new CentrifugeClient("ws://127.0.0.1:" + relay.Port + "/connection/websocket", new CentrifugeClientOptions {
                // A torn-down connection would only come back after this delay, far beyond the assertions below.
                MinReconnectDelay = TimeSpan.FromSeconds(60),
                MaxReconnectDelay = TimeSpan.FromSeconds(60),
            });
            try {
                Channel<CentrifugeErrorEventArgs> errors = Channel.CreateUnbounded<CentrifugeErrorEventArgs>();
                Channel<CentrifugeConnectingEventArgs> connecting = Channel.CreateUnbounded<CentrifugeConnectingEventArgs>();
                Channel<CentrifugeConnectedEventArgs> connected = Channel.CreateUnbounded<CentrifugeConnectedEventArgs>();
                client.Error += (object? sender, CentrifugeErrorEventArgs args) => errors.Writer.TryWrite(args);
                client.Connecting += (object? sender, CentrifugeConnectingEventArgs args) => connecting.Writer.TryWrite(args);
                client.Connected += (object? sender, CentrifugeConnectedEventArgs args) => connected.Writer.TryWrite(args);

                client.Connect();
                await connected.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

                client.Disconnect();
                // The replaced socket is closed at the TCP level: the relay has no open connection left.
                await relay.WaitForOpenAsync(0, TimeSpan.FromSeconds(5));
                client.Connect();
                await connected.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await client.LoopBarrierAsync();

                Assert.False(errors.Reader.TryRead(out _), "callbacks of the replaced socket must not report errors");
                Assert.Equal(2, CountOf(connecting));
                Assert.Equal(CentrifugeClientState.Connected, client.State);
            } finally {
                await client.DisposeAsync();
            }
        }


        // Counts the items a channel holds now, and takes them out.
        private static int CountOf(Channel<CentrifugeConnectingEventArgs> channel) {
            int count = 0;
            while (channel.Reader.TryRead(out _)) {
                count++;
            }
            return count;
        }
    }
}
