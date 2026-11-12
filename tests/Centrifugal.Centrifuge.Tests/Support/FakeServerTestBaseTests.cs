using System;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks that the fake server base starts the server and gives clients that connect to it.</summary>
    public sealed class FakeServerTestBaseTests : FakeServerTestBase {
        /// <summary>A client made by NewClient connects to the started fake server.</summary>
        [Fact]
        public async Task NewClient_ConnectsToTheStartedServer() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            CentrifugeConnectedEventArgs args = await connected;

            Assert.Equal("fake-client", args.ClientId);
            Assert.StartsWith("ws://127.0.0.1", Server.Url);
        }

        /// <summary>A client made with options keeps the options it was given.</summary>
        [Fact]
        public void NewClient_WithOptions_ReturnsAClientNotYetConnected() {
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Token = "token" });

            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        /// <summary>A client made with a test clock gives its subscriptions the fake timers and still connects.</summary>
        [Fact]
        public async Task NewClientWithTestClock_UsesTheFakeTimersAndConnects() {
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;

            Assert.Same(timers, client.StepTimers);
        }
    }
}
