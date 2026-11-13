// Flush of the queued rpc calls on connect: a call made before the connection is written when the connect reply is processed.
// The upstream rule is kept: a call is never written before the client is connected.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The queued calls that the connect reply flushes.</summary>
    [Collection("Integration")]
    public sealed class RpcFlushTests : RpcTestBase {
        // Java: RpcOptionsTest.rpcWithHeadersQueuedWhileConnectingIsSentWhenConnectReplyConfirmsIt.
        /// <summary>A call with headers made while connecting is sent once the connect reply confirms RPC options.</summary>
        [Fact]
        public async Task RpcAsync_HeadersQueuedWhileConnecting_AreSentWhenTheConnectReplyConfirmsThem() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("queued");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });

            Task<CentrifugeRpcResult> queued = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty, new CentrifugeRpcOptions().WithHeader("k", "v"), default);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(queued);

            Assert.Equal("queued", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Equal("v", RpcTestHelpers.Requests(Server)[0].Rpc.Headers["k"]);
        }


        // Java: RpcOptionsTest.rpcWithHeadersQueuedWhileConnectingFailsWhenConnectReplyDoesNotConfirmIt.
        /// <summary>A call with headers made while connecting fails with code 15 when the connect reply does not confirm RPC options.</summary>
        [Fact]
        public async Task RpcAsync_HeadersQueuedWhileConnecting_FailWhenTheConnectReplyDoesNotConfirmThem() {
            Server.OnCommand = RpcTestHelpers.Serve("queued");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });

            Task<CentrifugeRpcResult> queued = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty, new CentrifugeRpcOptions().WithHeader("k", "v"), default);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(queued));
            Assert.Equal(CentrifugeErrorCodes.Unsupported, error.Code);
            Assert.Empty(RpcTestHelpers.Requests(Server));
        }


        // Java: RpcExactlyOnceTest.aCallDroppedFromTheBatchBeforeItsWriteIsSentAsAFirstSend.
        /// <summary>A call queued before the connection goes out as a first send, not as a retransmit.</summary>
        [Fact]
        public async Task RpcAsync_CallQueuedBeforeConnect_IsAFirstSend() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("queued");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });

            Task<CentrifugeRpcResult> queued = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            await RpcTestHelpers.Bounded(queued);

            List<Protocol.Command> requests = RpcTestHelpers.Requests(Server);
            Assert.Single(requests);
            Assert.NotEqual(0UL, requests[0].Rpc.MsgId);
            Assert.False(requests[0].Rpc.Retransmit);
        }
    }
}
