using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// Base of the tests that talk to the in-process fake server. It starts one fake server per test on the loopback
    /// interface, creates the clients of the test, and disposes them and the server afterwards.
    /// The upstream tests keep their own setup.
    /// </summary>
    public abstract class FakeServerTestBase : IAsyncLifetime {
        private readonly List<CentrifugeClient> clients = new List<CentrifugeClient>();


        /// <summary>The fake server of the test. Started before the test body runs. Internal: the fake is an internal type.</summary>
        internal FakeCentrifugoServer Server { get; } = new FakeCentrifugoServer();


        /// <summary>Starts the fake server.</summary>
        public virtual Task InitializeAsync() {
            return Server.StartAsync();
        }

        /// <summary>Disposes every client of the test, then the fake server.</summary>
        public virtual async Task DisposeAsync() {
            foreach (CentrifugeClient client in clients) {
                await client.DisposeAsync();
            }
            await Server.DisposeAsync();
        }

        /// <summary>
        /// Creates a client for the fake server. The client is not connected yet.
        /// </summary>
        /// <param name="options">The client options, or null for the defaults.</param>
        protected CentrifugeClient NewClient(CentrifugeClientOptions? options = null) {
            CentrifugeClient client = new CentrifugeClient(Server.Url, options ?? new CentrifugeClientOptions());
            clients.Add(client);
            return client;
        }


        /// <summary>
        /// Creates a client for the fake server whose fork timers, monotonic clock and random source the test controls.
        /// The subscriptions, the connection refresh, the rpc session and the ping watchdog run on these timers; the
        /// upstream timers of the client stay real. The client is not connected yet.
        /// </summary>
        /// <param name="timers">Receives the fake timers of the client. Advance moves the clock and fires due timers.</param>
        /// <param name="options">The client options, or null for the defaults.</param>
        /// <param name="random">The random source, or null for <see cref="FixedRandom.MinimumDraws"/>.</param>
        internal CentrifugeClient NewClientWithTestClock(
            out FakeStepTimerFactory timers,
            CentrifugeClientOptions? options = null,
            Random? random = null
        ) {
            CentrifugeClient client = NewClient(options);
            FakeStepTimerFactory fake = new FakeStepTimerFactory(client.Loop);
            client.UseTestClock(fake, () => fake.ElapsedMs, random ?? FixedRandom.MinimumDraws());
            timers = fake;
            return client;
        }


        /// <summary>
        /// Completes with the event args of the next Connected event of the client, or fails after five seconds.
        /// </summary>
        /// <param name="client">The client to watch.</param>
        protected static Task<CentrifugeConnectedEventArgs> WhenConnected(CentrifugeClient client) {
            TaskCompletionSource<CentrifugeConnectedEventArgs> connected =
                new TaskCompletionSource<CentrifugeConnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Connected += (object? sender, CentrifugeConnectedEventArgs args) => connected.TrySetResult(args);
            return connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
