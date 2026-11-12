// Client-level ping tests on the fake server: the pong to a server ping, the no-ping hook that ignores an old transport,
// and the option check. The watchdog timing is tested on the fake step timers in PingWatchdogTests, because the client's
// own timers are real.
using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// Pings and pongs on a client connected to the fake server, and the no-ping hook of the client.
    /// </summary>
    [Collection("Integration")]
    public sealed class PingWatchdogClientTests : FakeServerTestBase {
        /// <summary>A server ping gets exactly one pong, an empty command, when the connect reply confirms pong.</summary>
        [Fact]
        // Java: serverPingStillGetsItsPong
        public async Task ServerPingStillGetsItsPong() {
            Server.ConnectResult.Pong = true;
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            TaskCompletionSource<bool> pong = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.BeforeCommand = (Protocol.Command command) => {
                if (command.CalculateSize() == 0) {
                    pong.TrySetResult(true);
                }
                return Task.CompletedTask;
            };
            int before = Server.Received.Count;
            await Server.SendPingAsync();
            await pong.Task.WaitAsync(TimeSpan.FromSeconds(5));

            IReadOnlyList<Protocol.Command> received = Server.Received;
            Assert.Equal(before + 1, received.Count);
            Assert.Equal(new Protocol.Command(), received[before]);
        }


        /// <summary>The no-ping hook ignores a transport that is no longer the current one: the new connection stays connected.</summary>
        [Fact]
        public async Task PingHookIgnoresOldTransport() {
            CentrifugeClient client = NewClient();
            Channel<CentrifugeConnectedEventArgs> connects = Channel.CreateUnbounded<CentrifugeConnectedEventArgs>();
            Channel<CentrifugeDisconnectedEventArgs> disconnects = Channel.CreateUnbounded<CentrifugeDisconnectedEventArgs>();
            client.Connected += (object? sender, CentrifugeConnectedEventArgs args) => connects.Writer.TryWrite(args);
            client.Disconnected += (object? sender, CentrifugeDisconnectedEventArgs args) => disconnects.Writer.TryWrite(args);

            client.Connect();
            await connects.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            ITransport? oldTransport = NoPing.Transport(client);
            Assert.NotNull(oldTransport);

            // The connection ends and the client reconnects on a new transport.
            Server.CloseConnection();
            await connects.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            client.NoPing(oldTransport!);
            await client.LoopBarrierAsync();
            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.False(disconnects.Reader.TryRead(out _));
        }


        /// <summary>A zero allowed delay of the server ping is refused when the client is created.</summary>
        [Fact]
        public void Constructor_ZeroMaxServerPingDelay_Throws() {
            CentrifugeConfigurationException error = Assert.Throws<CentrifugeConfigurationException>(() =>
                new CentrifugeClient(Server.Url, new CentrifugeClientOptions { MaxServerPingDelay = TimeSpan.Zero }));
            Assert.Equal("MaxServerPingDelay must be positive", error.Message);
        }
    }
}
