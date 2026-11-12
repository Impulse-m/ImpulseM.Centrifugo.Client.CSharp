using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// A TCP relay to the fake server. It counts the client connections it relays, can blackhole the live ones, and
    /// completes a wait once the number of open connections reaches the awaited value.
    /// </summary>
    internal sealed class CountingRelay : IDisposable {
        private readonly TcpListener listener;
        private readonly string upstreamHost;
        private readonly int upstreamPort;
        private readonly object gate = new object();
        private readonly List<RelayPair> pairs = new List<RelayPair>();
        private readonly List<OpenWaiter> waiters = new List<OpenWaiter>();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private int open;


        /// <summary>
        /// Starts the relay on the loopback interface, on a free port.
        /// </summary>
        /// <param name="upstreamHost">The host of the fake server.</param>
        /// <param name="upstreamPort">The port of the fake server.</param>
        public CountingRelay(string upstreamHost, int upstreamPort) {
            this.upstreamHost = upstreamHost;
            this.upstreamPort = upstreamPort;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }


        /// <summary>The port the clients connect to.</summary>
        public int Port { get; }


        /// <summary>Makes every live connection drop what it carries from now on. New connections work.</summary>
        public void BlackholeExisting() {
            lock (gate) {
                foreach (RelayPair pair in pairs) {
                    pair.Blackholed = true;
                }
            }
        }


        /// <summary>
        /// Completes when the number of open connections is the expected one. Fails with a timeout otherwise.
        /// </summary>
        /// <param name="expected">The number of open connections to wait for.</param>
        /// <param name="timeout">How long to wait.</param>
        public Task WaitForOpenAsync(int expected, TimeSpan timeout) {
            TaskCompletionSource<bool> reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) {
                if (open == expected) {
                    reached.TrySetResult(true);
                } else {
                    waiters.Add(new OpenWaiter(expected, reached));
                }
            }
            return reached.Task.WaitAsync(timeout);
        }


        /// <summary>Stops the relay and closes its connections.</summary>
        public void Dispose() {
            stop.Cancel();
            listener.Stop();
            List<RelayPair> all;
            lock (gate) {
                all = new List<RelayPair>(pairs);
            }
            foreach (RelayPair pair in all) {
                Close(pair);
            }
        }


        // Completes the waiters that the current count reached. Called under the gate. The completion runs its
        // continuations asynchronously, so none of them runs inside the lock.
        private void ReleaseWaitersLocked() {
            for (int index = waiters.Count - 1; index >= 0; index--) {
                if (waiters[index].Expected == open) {
                    waiters[index].Reached.TrySetResult(true);
                    waiters.RemoveAt(index);
                }
            }
        }


        private async Task AcceptLoopAsync() {
            try {
                while (!stop.IsCancellationRequested) {
                    TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                    TcpClient upstream = new TcpClient();
                    await upstream.ConnectAsync(upstreamHost, upstreamPort);
                    RelayPair pair = new RelayPair(client, upstream);
                    lock (gate) {
                        pairs.Add(pair);
                        open++;
                        ReleaseWaitersLocked();
                    }
                    _ = PumpAsync(pair);
                }
            } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) {
                // The relay stopped.
            }
        }


        private async Task PumpAsync(RelayPair pair) {
            Task forward = CopyAsync(pair.Client, pair.Upstream, pair);
            Task backward = CopyAsync(pair.Upstream, pair.Client, pair);
            await Task.WhenAny(forward, backward);
            Close(pair);
        }


        private async Task CopyAsync(TcpClient from, TcpClient to, RelayPair pair) {
            byte[] buffer = new byte[8192];
            try {
                NetworkStream input = from.GetStream();
                NetworkStream output = to.GetStream();
                while (true) {
                    int count = await input.ReadAsync(buffer.AsMemory());
                    if (count == 0) {
                        return;
                    }
                    bool hole;
                    lock (gate) {
                        hole = pair.Blackholed;
                    }
                    if (!hole) {
                        await output.WriteAsync(buffer.AsMemory(0, count));
                    }
                }
            } catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) {
                // The connection ended.
            }
        }


        private void Close(RelayPair pair) {
            bool first;
            lock (gate) {
                first = !pair.Closed;
                pair.Closed = true;
                if (first) {
                    open--;
                    ReleaseWaitersLocked();
                }
            }
            if (first) {
                pair.Client.Close();
                pair.Upstream.Close();
            }
        }


        /// <summary>The two sockets of one relayed connection.</summary>
        private sealed class RelayPair {
            /// <summary>
            /// Creates the pair.
            /// </summary>
            /// <param name="client">The client side socket.</param>
            /// <param name="upstream">The server side socket.</param>
            public RelayPair(TcpClient client, TcpClient upstream) {
                Client = client;
                Upstream = upstream;
            }


            /// <summary>The client side socket.</summary>
            public TcpClient Client { get; }


            /// <summary>The server side socket.</summary>
            public TcpClient Upstream { get; }


            /// <summary>True when the relay drops what the connection carries.</summary>
            public bool Blackholed { get; set; }


            /// <summary>True once the connection was closed by the relay.</summary>
            public bool Closed { get; set; }
        }


        /// <summary>A wait for the open count to reach a value.</summary>
        private sealed class OpenWaiter {
            /// <summary>
            /// Creates the wait.
            /// </summary>
            /// <param name="expected">The open count to wait for.</param>
            /// <param name="reached">Completed when the open count is the expected one.</param>
            public OpenWaiter(int expected, TaskCompletionSource<bool> reached) {
                Expected = expected;
                Reached = reached;
            }


            /// <summary>The open count to wait for.</summary>
            public int Expected { get; }


            /// <summary>Completed when the open count is the expected one.</summary>
            public TaskCompletionSource<bool> Reached { get; }
        }
    }
}
