// Port status of the Java tests of this file (connection-identity-and-capabilities, S02):
// - RpcOptionsTest.java (capability part), ported with the Java name in a // comment on each test:
//   connectRequestAdvertisesRpcOptionsBit, capabilityIsOnWhenConnectReplyHasRpcOptionsBit,
//   capabilityIsOnWhenRpcOptionsBitComesWithOtherBits, capabilityIsOffWhenConnectReplyHasNoRpcOptionsBit,
//   capabilityIsOffWhenConnectReplyHasNoFlag, capabilityIsClearedOnReconnectUntilConnectReplyConfirmsIt,
//   capabilityIsClearedOnDisconnect, capabilitySnapshotDoesNotChangeAfterwards.
// - ExtensionsTest.java, ported in ConnectCapabilityFlagTests: rpcOptionsFlagIsBit32, hasFlagFindsBit32AmongOtherBits,
//   hasFlagIsFalseWithoutTheBit. Not ported here: the estimated server time tests (estimatedServerTimeAddsOffsetToLocalTime,
//   estimatedServerTimeIsLocalTimeWithoutOffset). F0 has no pure estimate function, and ServerTimeMillis is private to the
//   client seam, so they need a seam decision first. The two offset tests belong to the server time slice.
// - No Java test: Connect_WithErrorReply_LeavesCapabilityOff (rule: an error connect reply does not confirm the capability).
// - Not in this file: the HTTP stream connect flag. It needs an emulation fixture that the fake server does not have.
// - Known gap: the clear in InitializeTransport (the reconnect point of rule R23, point 1) is not pinned. Removing it
//   leaves every test here green, because the disconnect path clears the flag first. Pinned when the HTTP stream
//   connect case (Q11) has an emulation fixture. Java RpcOptionsTest.capabilityIsClearedOnReconnectUntilConnectReplyConfirmsIt
//   has the same limit.

using System;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The bit arithmetic of the RPC options flag, without a server.</summary>
    public sealed class ConnectCapabilityFlagTests {
        // Java: ExtensionsTest.rpcOptionsFlagIsBit32.
        /// <summary>The RPC options flag is bit 32.</summary>
        [Fact]
        public void RpcOptionsFlag_IsBit32() {
            Assert.Equal(4294967296L, ConnectCapabilities.RpcOptionsFlag);
        }


        // Java: ExtensionsTest.hasFlagFindsBit32AmongOtherBits.
        /// <summary>The bit is found among other bits.</summary>
        [Fact]
        public void HasFlag_FindsBit32AmongOtherBits() {
            Assert.True(ConnectCapabilities.HasFlag((1L << 32) | 1L, ConnectCapabilities.RpcOptionsFlag));
            Assert.True(ConnectCapabilities.HasFlag(1L << 32, ConnectCapabilities.RpcOptionsFlag));
        }


        // Java: ExtensionsTest.hasFlagIsFalseWithoutTheBit.
        /// <summary>Without the bit the flag test is false.</summary>
        [Fact]
        public void HasFlag_IsFalseWithoutTheBit() {
            Assert.False(ConnectCapabilities.HasFlag(0L, ConnectCapabilities.RpcOptionsFlag));
            Assert.False(ConnectCapabilities.HasFlag(1L, ConnectCapabilities.RpcOptionsFlag));
            Assert.False(ConnectCapabilities.HasFlag(1L << 31, ConnectCapabilities.RpcOptionsFlag));
        }
    }


    /// <summary>The RPC options capability that the connect reply confirms, checked against the fake server.</summary>
    [Collection("Integration")]
    public sealed class ConnectCapabilityTests : FakeServerTestBase {
        // Java: RpcOptionsTest.connectRequestAdvertisesRpcOptionsBit.
        /// <summary>The connect request advertises the RPC options bit on every connect.</summary>
        [Fact]
        public async Task Connect_Always_AdvertisesTheRpcOptionsBit() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;

            long sentFlag = 0;
            foreach (Protocol.Command command in Server.Received) {
                if (command.Connect != null) {
                    sentFlag = command.Connect.Flag;
                }
            }
            Assert.Equal(ConnectCapabilities.RpcOptionsFlag, sentFlag);
        }


        // Java: RpcOptionsTest.capabilityIsOnWhenConnectReplyHasRpcOptionsBit.
        /// <summary>The capability is on when the connect reply has the bit.</summary>
        [Fact]
        public async Task Capabilities_WhenConnectReplyHasTheBit_IsOn() {
            Server.EnableRpcOptions();
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;

            Assert.True(client.Capabilities.RpcOptionsSupported);
        }


        // Java: RpcOptionsTest.capabilityIsOnWhenRpcOptionsBitComesWithOtherBits.
        /// <summary>The capability is on when the bit comes with other bits.</summary>
        [Fact]
        public async Task Capabilities_WhenBitComesWithOtherBits_IsOn() {
            Server.ConnectResult.Flag = ConnectCapabilities.RpcOptionsFlag | 1L;
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;

            Assert.True(client.Capabilities.RpcOptionsSupported);
        }


        // Java: RpcOptionsTest.capabilityIsOffWhenConnectReplyHasNoRpcOptionsBit.
        /// <summary>The capability is off when the reply has other flags only.</summary>
        [Fact]
        public async Task Capabilities_WhenReplyHasNoRpcOptionsBit_IsOff() {
            Server.ConnectResult.Flag = 1L;
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;

            Assert.False(client.Capabilities.RpcOptionsSupported);
        }


        // Java: RpcOptionsTest.capabilityIsOffWhenConnectReplyHasNoFlag.
        /// <summary>The capability is off when the reply has no flag.</summary>
        [Fact]
        public async Task Capabilities_WhenReplyHasNoFlag_IsOff() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;

            Assert.False(client.Capabilities.RpcOptionsSupported);
        }


        // Java: RpcOptionsTest.capabilityIsClearedOnReconnectUntilConnectReplyConfirmsIt.
        /// <summary>The capability is cleared on a reconnect until the new connect reply confirms it.</summary>
        [Fact]
        public async Task Capabilities_OnReconnect_IsClearedUntilTheConnectReplyConfirmsIt() {
            Server.EnableRpcOptions();
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            Assert.True(client.Capabilities.RpcOptionsSupported);

            // The reconnect's connect reply is held, so the capability is read while the new transport is open but unconfirmed.
            Task held = Server.AwaitHeldAsync();
            Server.DeferCommand((Protocol.Command command) => command.Connect != null);
            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await held;
            Assert.False(client.Capabilities.RpcOptionsSupported);

            Server.DeferCommand(null);
            Server.ReleaseDeferredCommands();
            await reconnected;
            Assert.True(client.Capabilities.RpcOptionsSupported);
        }


        // Java: RpcOptionsTest.capabilityIsClearedOnDisconnect.
        /// <summary>The capability is cleared on disconnect.</summary>
        [Fact]
        public async Task Capabilities_OnDisconnect_IsCleared() {
            Server.EnableRpcOptions();
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            Assert.True(client.Capabilities.RpcOptionsSupported);

            Task<CentrifugeDisconnectedEventArgs> disconnected = WhenDisconnected(client);
            client.Disconnect();
            await disconnected;

            Assert.False(client.Capabilities.RpcOptionsSupported);
        }


        // Java: RpcOptionsTest.capabilitySnapshotDoesNotChangeAfterwards.
        /// <summary>A snapshot taken before a disconnect keeps its value, and a new read shows the new value.</summary>
        [Fact]
        public async Task Capabilities_Snapshot_DoesNotChangeAfterwards() {
            Server.EnableRpcOptions();
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            CentrifugeClientCapabilities before = client.Capabilities;

            Task<CentrifugeDisconnectedEventArgs> disconnected = WhenDisconnected(client);
            client.Disconnect();
            await disconnected;

            Assert.True(before.RpcOptionsSupported);
            Assert.False(client.Capabilities.RpcOptionsSupported);
        }


        // No Java counterpart.
        /// <summary>
        /// A connect answered with an error ends the connection with the reply's code, and leaves the capability off.
        /// The capability was cleared when the transport was set up, and the error reply does not confirm it.
        /// </summary>
        [Fact]
        public async Task Connect_WithErrorReply_LeavesCapabilityOff() {
            Server.OnCommand = (Protocol.Command command) => {
                if (command.Connect == null) {
                    return null;
                }
                Protocol.Reply reply = Server.ErrorReply(3000, "refused", false, null);
                reply.Id = command.Id;
                return reply;
            };
            CentrifugeClient client = NewClient();
            Task<CentrifugeDisconnectedEventArgs> disconnected = WhenDisconnected(client);

            client.Connect();
            CentrifugeDisconnectedEventArgs args = await disconnected;

            Assert.Equal(3000, args.Code);
            Assert.False(client.Capabilities.RpcOptionsSupported);
        }


        /// <summary>Completes with the event args of the next Disconnected event of the client, or fails after five seconds.</summary>
        /// <param name="client">The client to watch.</param>
        private static Task<CentrifugeDisconnectedEventArgs> WhenDisconnected(CentrifugeClient client) {
            TaskCompletionSource<CentrifugeDisconnectedEventArgs> disconnected =
                new TaskCompletionSource<CentrifugeDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += (object? sender, CentrifugeDisconnectedEventArgs args) => disconnected.TrySetResult(args);
            return disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
