// The gate of the rpc options: the instance on connect, the plain calls of a client without instance or without RPC options,
// and the instance read at creation.
// Not ported: RpcOptionsTest.capabilityIsOnWhenConnectReplyHasRpcOptionsBit and the other capability tests: they belong to
// ConnectCapabilityTests (the identity area).
// The upstream rule is kept: the connect request carries the instance only when it is set.

using System;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The instance and the plain-call gate of the rpc calls, against the fake server.</summary>
    [Collection("Integration")]
    public sealed class RpcOptionsGateTests : RpcTestBase {
        // Java: RpcExactlyOnceTest.configuredInstanceIsSentOnConnect.
        /// <summary>The connect request carries the instance when one is set.</summary>
        [Fact]
        public async Task Connect_WithInstance_SendsTheInstance() {
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;

            Protocol.Command connect = Assert.Single(Server.Received, (Protocol.Command cmd) => cmd.Connect != null);
            Assert.Equal(RpcTestHelpers.Instance, connect.Connect.Instance);
        }


        // Java: RpcExactlyOnceTest.unsetInstanceIsNotSentOnConnect.
        /// <summary>The connect request carries no instance when none is set.</summary>
        [Fact]
        public async Task Connect_WithoutInstance_SendsNoInstance() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;

            Protocol.Command connect = Assert.Single(Server.Received, (Protocol.Command cmd) => cmd.Connect != null);
            Assert.Equal(string.Empty, connect.Connect.Instance);
        }


        // Java: RpcExactlyOnceTest.instanceIsReadWhenTheClientIsCreated.
        /// <summary>The instance is read when the client is created: a later change of the options does not reach the client.</summary>
        [Fact]
        public async Task Client_ReadsTheInstanceWhenItIsCreated() {
            CentrifugeClientOptions options = new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance };
            CentrifugeClient client = NewClient(options);
            options.Instance = null!;
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;

            Protocol.Command connect = Assert.Single(Server.Received, (Protocol.Command cmd) => cmd.Connect != null);
            Assert.Equal(RpcTestHelpers.Instance, connect.Connect.Instance);
        }


        // Java: RpcOptionsTest.legacyServerGetsPlainCallsAndRejectsHeaders (plain half).
        /// <summary>On a connection without RPC options a call with an instance is still a plain request.</summary>
        [Fact]
        public async Task RpcAsync_LegacyServer_SendsPlainRequests() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);

            Protocol.Command request = Assert.Single(RpcTestHelpers.Requests(Server));
            Assert.Equal(0UL, request.Rpc.MsgId);
            Assert.Empty(request.Rpc.Ack);
            Assert.False(request.Rpc.Retransmit);
        }


        // Java: ConnectListenerTest.queuedCallIsWrittenBeforeTheSubscribedAndPublicationCallbacks (rpc result half).
        /// <summary>A call queued before the connect is answered with its result: the empty object of the server.</summary>
        [Fact]
        public async Task RpcAsync_QueuedBeforeConnect_ResolvesWithTheServerResult() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("{}");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });

            Task<CentrifugeRpcResult> queued = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);
            client.Connect();
            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(queued);

            Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(result.Data.Span));
        }
    }
}
