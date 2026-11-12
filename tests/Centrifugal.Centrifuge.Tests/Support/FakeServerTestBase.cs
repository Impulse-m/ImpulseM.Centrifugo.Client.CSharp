using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// Base of the tests that talk to the in-process fake server. It starts one fake server per test on the loopback
    /// interface, creates the clients of the test, and disposes them and the server afterwards.
    /// Replaces FakeServerTestBase.java. The upstream tests keep their own setup.
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
    }
}
