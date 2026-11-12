// Java ConnectListenerTest, not ported yet (open in the matrix, owned by S01; they need the recovered publication
// fixture of the subscriptions (S05) and the rpc answer of the rpc session (S07)):
// - listenerFailureInOnConnectedLeavesTheHandshakeWhole
// - errorFromOnConnectedStillDeliversTheRecoveredPublication
// - queuedCallIsWrittenBeforeTheSubscribedAndPublicationCallbacks
// Java AppExceptionTest.testThrowingConnectedHandlerStopsVisibly is ported below.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>The connect path around the Connected event: a failure disconnects once, visibly, with code 2.</summary>
    public sealed class ClientCoreSeamTests : FakeServerTestBase {
        // Java: testThrowingConnectedHandlerStopsVisibly
        /// <summary>A Connected handler that throws disconnects the client once with code 2 and reason "exception in event handler".</summary>
        [Fact]
        public async Task ConnectedHandler_Throws_DisconnectsCode2NoReconnect() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);
            client.Connected += (_, _) => throw new InvalidOperationException("handler failed");

            client.Connect();
            CentrifugeDisconnectedEventArgs args = await disconnected;

            Assert.Equal(2, args.Code);
            Assert.StartsWith("exception in event handler", args.Reason);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        /// <summary>The failure of a Connected handler is reported as an error event of type "connected".</summary>
        [Fact]
        public async Task ConnectedHandler_Throws_RaisesErrorOfTypeConnected() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);
            List<string> errorTypes = new List<string>();
            client.Error += (_, e) => errorTypes.Add(e.Type);
            client.Connected += (_, _) => throw new InvalidOperationException("handler failed");

            client.Connect();
            await disconnected;

            Assert.Contains("connected", errorTypes);
        }

        /// <summary>A Connected handler that returns normally leaves the client connected.</summary>
        [Fact]
        public async Task ConnectedHandler_ReturnsNormally_StaysConnected() {
            CentrifugeClient client = NewClient();
            client.Connect();

            await client.ReadyAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }
    }
}
