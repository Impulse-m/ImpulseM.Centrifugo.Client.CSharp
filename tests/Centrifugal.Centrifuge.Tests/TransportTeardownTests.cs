// Teardown tests on real sockets: a TCP relay that can blackhole a live link (CountingRelay), and a server that accepts
// TCP and never answers. The silent server is private to this file.
// Waits are waits for events, for relay state or for a socket close, each with a timeout. No fixed delays.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// The client tears its transport down itself without waiting for the peer, and a WebSocket handshake is bounded by
    /// the client timeout.
    /// </summary>
    [Collection("Integration")]
    public sealed class TransportTeardownTests : FakeServerTestBase {
        /// <summary>After "no ping" on a dead link, the client reconnects within its backoff, not after the peer's close.</summary>
        [Fact]
        // Java: testReconnectAfterNoPingDoesNotWaitForPeerClose
        // The fake server answers the close frame, so the dead link of the Java test (no answer to the close) is not
        // reproduced. The test checks two connections, each within 5 s.
        public async Task ReconnectAfterNoPingReconnectsWithinFiveSeconds() {
            Server.ConnectResult.Ping = 1;
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                MaxServerPingDelay = TimeSpan.FromMilliseconds(300),
                MinReconnectDelay = TimeSpan.FromMilliseconds(100),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
            });
            Channel<int> codes = Channel.CreateUnbounded<int>();
            Channel<bool> connected = Channel.CreateUnbounded<bool>();
            client.Connecting += (object? sender, CentrifugeConnectingEventArgs args) => codes.Writer.TryWrite(args.Code);
            client.Connected += (object? sender, CentrifugeConnectedEventArgs args) => connected.Writer.TryWrite(true);

            client.Connect();
            await AwaitCodeAsync(codes.Reader, CentrifugeConnectingCodes.NoPing);
            // Two connections in all: the first one, and the reconnect after the no-ping close.
            await connected.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await connected.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }


        /// <summary>After "no ping" on a dead link, the dead connection's socket is released, next to the new one's.</summary>
        [Fact]
        // Java: testDeadConnectionSocketIsReleased
        public async Task DeadConnectionSocketIsReleased() {
            Server.ConnectResult.Ping = 1;
            Uri upstream = new Uri(Server.Url);
            using CountingRelay relay = new CountingRelay(upstream.Host, upstream.Port);
            CentrifugeClient client = new CentrifugeClient("ws://127.0.0.1:" + relay.Port + "/connection/websocket", new CentrifugeClientOptions {
                MaxServerPingDelay = TimeSpan.FromMilliseconds(300),
                MinReconnectDelay = TimeSpan.FromMilliseconds(100),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
            });
            try {
                Channel<int> codes = Channel.CreateUnbounded<int>();
                Channel<bool> connected = Channel.CreateUnbounded<bool>();
                client.Connecting += (object? sender, CentrifugeConnectingEventArgs args) => codes.Writer.TryWrite(args.Code);
                client.Connected += (object? sender, CentrifugeConnectedEventArgs args) => connected.Writer.TryWrite(true);

                client.Connect();
                await connected.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                // From the second connection on, the server pings rarely: one no-ping only.
                Server.ConnectResult = new Protocol.ConnectResult { Client = "fake-client", Version = "0.0.0", Ping = 25 };
                // The link of the first connection goes dead.
                relay.BlackholeExisting();
                await AwaitCodeAsync(codes.Reader, CentrifugeConnectingCodes.NoPing);
                await connected.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await relay.WaitForOpenAsync(1, TimeSpan.FromSeconds(3));
            } finally {
                await client.DisposeAsync();
            }
        }


        /// <summary>A WebSocket handshake that the server never answers fails after the client timeout, and the client tries again.</summary>
        [Fact]
        // Java: testHungHandshakeFailsAfterClientTimeout
        // The upstream rule is kept: the client raises a CentrifugeException with code TransportClosed, not a TimeoutException,
        // so only the error type name and the message are asserted here.
        public async Task HungHandshakeFailsAfterClientTimeout() {
            using SilentServer silent = new SilentServer();
            CentrifugeClient client = new CentrifugeClient("ws://127.0.0.1:" + silent.Port + "/connection/websocket", new CentrifugeClientOptions {
                Timeout = TimeSpan.FromMilliseconds(500),
                MinReconnectDelay = TimeSpan.FromMilliseconds(100),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
            });
            try {
                Channel<CentrifugeErrorEventArgs> errors = Channel.CreateUnbounded<CentrifugeErrorEventArgs>();
                client.Error += (object? sender, CentrifugeErrorEventArgs args) => errors.Writer.TryWrite(args);

                Stopwatch watch = Stopwatch.StartNew();
                client.Connect();
                CentrifugeErrorEventArgs error = await errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal("transport", error.Type);
                Assert.Equal("connect timeout", error.Message);
                Assert.True(watch.ElapsedMilliseconds >= 450, "the error came before the client timeout");

                // The first handshake, then a new attempt.
                await silent.Sockets.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                await silent.Sockets.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(CentrifugeClientState.Connecting, client.State);
            } finally {
                await client.DisposeAsync();
            }
        }


        /// <summary>disconnect() during a hung handshake closes its socket at once.</summary>
        [Fact]
        // Java: testDisconnectAbortsHungHandshake
        public async Task DisconnectAbortsHungHandshake() {
            using SilentServer silent = new SilentServer();
            CentrifugeClient client = new CentrifugeClient("ws://127.0.0.1:" + silent.Port + "/connection/websocket", new CentrifugeClientOptions {
                Timeout = TimeSpan.FromMilliseconds(5000),
            });
            try {
                Channel<bool> disconnected = Channel.CreateUnbounded<bool>();
                client.Disconnected += (object? sender, CentrifugeDisconnectedEventArgs args) => disconnected.Writer.TryWrite(true);

                client.Connect();
                TcpClient socket = await silent.Sockets.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                client.Disconnect();
                await disconnected.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

                bool closed = await WaitForCloseAsync(socket.GetStream(), TimeSpan.FromMilliseconds(1500));
                Assert.True(closed, "the client closed the handshake socket within 1.5 s of disconnect()");
            } finally {
                await client.DisposeAsync();
            }
        }


        // Reads the codes of the connecting events until the wanted one comes.
        private static async Task AwaitCodeAsync(ChannelReader<int> codes, int wanted) {
            while (true) {
                int code = await codes.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (code == wanted) {
                    return;
                }
            }
        }


        // Reads from the socket until the peer closes it. True when it closed within the timeout.
        private static async Task<bool> WaitForCloseAsync(NetworkStream stream, TimeSpan timeout) {
            using CancellationTokenSource cancellation = new CancellationTokenSource(timeout);
            byte[] buffer = new byte[4096];
            try {
                while (await stream.ReadAsync(buffer.AsMemory(), cancellation.Token) > 0) {
                    // The request bytes of the handshake: read until the client closes.
                }
                return true;
            } catch (IOException) {
                return true;
            } catch (OperationCanceledException) {
                return false;
            }
        }


        /// <summary>A server that accepts TCP connections and never answers them. Each accepted socket is queued.</summary>
        private sealed class SilentServer : IDisposable {
            private readonly TcpListener listener;
            private readonly object gate = new object();
            private readonly List<TcpClient> accepted = new List<TcpClient>();


            /// <summary>
            /// Starts the server on the loopback interface, on a free port.
            /// </summary>
            public SilentServer() {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Sockets = Channel.CreateUnbounded<TcpClient>();
                _ = AcceptAsync();
            }


            /// <summary>The port the clients connect to.</summary>
            public int Port { get; }


            /// <summary>The accepted sockets, in order.</summary>
            public Channel<TcpClient> Sockets { get; }


            /// <summary>Stops the server and closes the accepted sockets.</summary>
            public void Dispose() {
                listener.Stop();
                lock (gate) {
                    foreach (TcpClient socket in accepted) {
                        socket.Close();
                    }
                }
            }


            private async Task AcceptAsync() {
                try {
                    while (true) {
                        TcpClient socket = await listener.AcceptTcpClientAsync();
                        lock (gate) {
                            accepted.Add(socket);
                        }
                        Sockets.Writer.TryWrite(socket);
                    }
                } catch (Exception ex) when (ex is ObjectDisposedException or SocketException) {
                    // The server stopped.
                }
            }
        }
    }
}
