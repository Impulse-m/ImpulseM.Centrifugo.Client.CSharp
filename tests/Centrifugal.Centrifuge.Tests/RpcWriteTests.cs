// Write path of the exactly-once rpc calls: the msg_id, the acks, the headers and the plain calls, checked against the fake server.
// Not ported: RpcExactlyOnceTest.nullMethodOrDataIsRefusedAtTheCallAndTheQueuedCallsStillGoOut, the data half: a payload is a
// ReadOnlyMemory, which cannot be null. The method half is kept (ArgumentNullException at the call).
// The refused writes (and the acks they take back) are in RpcWriteFailureTests.
// The upstream rule is kept: a plain call carries no msg_id, no acks and no retransmit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The fields of the rpc requests on the write path, against the fake server.</summary>
    [Collection("Integration")]
    public sealed class RpcWriteTests : RpcTestBase {
        // Java: RpcExactlyOnceTest.repliesAreAckedWithTheNextCall.
        /// <summary>The second tracked call carries the msg_id of the first one as its ack.</summary>
        [Fact]
        public async Task RpcAsync_SecondTrackedCall_CarriesTheFirstMsgIdAsAck() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await client.RpcAsync("first", ReadOnlyMemory<byte>.Empty);
            await client.RpcAsync("second", ReadOnlyMemory<byte>.Empty);

            List<Protocol.Command> requests = RpcTestHelpers.Requests(Server);
            Assert.Equal(2, requests.Count);
            Assert.Empty(requests[0].Rpc.Ack);
            Assert.Equal(new ulong[] { requests[0].Rpc.MsgId }, requests[1].Rpc.Ack.ToArray());
        }


        // Java: RpcOptionsTest.capabilityWithoutInstanceSendsPlainCalls.
        /// <summary>With RPC options but no instance, every call is a plain request: no msg_id, no ack, no retransmit.</summary>
        [Fact]
        public async Task RpcAsync_CapabilityWithoutInstance_SendsPlainRequests() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await client.RpcAsync("first", ReadOnlyMemory<byte>.Empty);
            await client.RpcAsync("second", ReadOnlyMemory<byte>.Empty);

            List<Protocol.Command> requests = RpcTestHelpers.Requests(Server);
            Assert.Equal(2, requests.Count);
            foreach (Protocol.Command request in requests) {
                Assert.Equal(0UL, request.Rpc.MsgId);
                Assert.Empty(request.Rpc.Ack);
                Assert.False(request.Rpc.Retransmit);
            }
        }


        // Java: RpcOptionsTest.plainRpcWorksWithoutCapability.
        /// <summary>A plain call answers on a connection without RPC options, with no msg_id.</summary>
        [Fact]
        public async Task RpcAsync_WithoutCapability_IsAnsweredAsAPlainCall() {
            Server.OnCommand = RpcTestHelpers.Serve("plain");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcResult result = await client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);

            Assert.Equal("plain", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Equal(0UL, RpcTestHelpers.Requests(Server)[0].Rpc.MsgId);
        }


        // Java: RpcOptionsTest.emptyOptionsAreAPlainCallWithoutCapability.
        /// <summary>Empty options make a plain call: the same request as no options.</summary>
        [Fact]
        public async Task RpcAsync_EmptyOptions_IsAPlainCall() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await client.RpcAsync("method", ReadOnlyMemory<byte>.Empty, new CentrifugeRpcOptions(), default);

            Assert.Equal(0UL, RpcTestHelpers.Requests(Server)[0].Rpc.MsgId);
            Assert.Empty(RpcTestHelpers.Requests(Server)[0].Rpc.Headers);
        }


        // Java: RpcOptionsTest.rpcWithHeadersIsSentAndResultDelivered.
        /// <summary>Headers of a call are sent with it on a connection with RPC options, and the result is delivered.</summary>
        [Fact]
        public async Task RpcAsync_WithHeadersAndCapability_SendsHeadersAndDeliversTheResult() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("headed");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcOptions options = new CentrifugeRpcOptions().WithHeader("x-request-source", "test");
            CentrifugeRpcResult result = await client.RpcAsync("method", ReadOnlyMemory<byte>.Empty, options, default);

            Assert.Equal("headed", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Equal("test", RpcTestHelpers.Requests(Server)[0].Rpc.Headers["x-request-source"]);
        }


        // Java: RpcOptionsTest.capabilityWithoutInstanceStillSendsHeaders.
        /// <summary>With RPC options and no instance, the headers are still sent.</summary>
        [Fact]
        public async Task RpcAsync_CapabilityWithoutInstance_StillSendsHeaders() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await client.RpcAsync("method", ReadOnlyMemory<byte>.Empty, new CentrifugeRpcOptions().WithHeader("x-request-source", "test"), default);

            Assert.Equal("test", RpcTestHelpers.Requests(Server)[0].Rpc.Headers["x-request-source"]);
        }


        // Java: RpcOptionsTest.rpcWithHeadersFailsWithoutCapabilityAndIsNotSent.
        /// <summary>Headers on a connection without RPC options fail with code 15 at once, and nothing is sent for them.</summary>
        [Fact]
        public async Task RpcAsync_HeadersWithoutCapability_FailsWith15AndIsNotSent() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(client.RpcAsync("method", ReadOnlyMemory<byte>.Empty, new CentrifugeRpcOptions().WithHeader("k", "v"), default)));
            Assert.Equal(CentrifugeErrorCodes.Unsupported, error.Code);
            Assert.Equal("rpc headers are not supported by the server", error.Message);

            // A plain call after it: its answer means the headed call had its chance to be written first.
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);
            Assert.Single(RpcTestHelpers.Requests(Server));
        }


        // Java: RpcExactlyOnceTest.msgIdUsesTheServerClockForACallQueuedBeforeConnect.
        /// <summary>
        /// A tracked call queued before the connect takes the time part of its msg_id from the server time that the connect reply
        /// carries, not from the local clock, which runs 40 seconds ahead here.
        /// </summary>
        [Fact]
        public async Task RpcAsync_CallQueuedBeforeConnect_TakesItsTimePartFromServerTime() {
            EnableRpcOptions();
            long localMillis = 1_700_000_000_000L;
            long serverMillis = localMillis - 40_000L;
            Server.OnCommand = (Protocol.Command cmd) => {
                if (cmd.Connect != null) {
                    return new Protocol.Reply { Id = cmd.Id, Time = serverMillis, Connect = Server.ConnectResult };
                }
                return RpcTestHelpers.Serve("ok", time: serverMillis)(cmd);
            };
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            client.WallClockMs = () => localMillis;
            Task<CentrifugeRpcResult> queued = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            await RpcTestHelpers.Bounded(queued);

            ulong seconds = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "method").Rpc.MsgId >> MsgIdGenerator.SequenceBits;
            Assert.True(Math.Abs((long)seconds - serverMillis / 1000L) <= 5);
        }


        // Java: RpcExactlyOnceTest.msgIdsIncreaseWhileTheClockIsSteadyAndFitFiftyThreeBits.
        /// <summary>Consecutive msg_ids increase, and their time part stays the server second while the clock is steady.</summary>
        [Fact]
        public async Task RpcAsync_TrackedCalls_IncreaseAndFitFiftyThreeBits() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            long localMillis = 1_700_000_000_000L;
            client.WallClockMs = () => localMillis;
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            for (int i = 0; i < 5; i++) {
                await client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);
            }

            List<ulong> msgIds = RpcTestHelpers.Requests(Server).Select((Protocol.Command cmd) => cmd.Rpc.MsgId).ToList();
            Assert.Equal(5, msgIds.Count);
            for (int i = 1; i < msgIds.Count; i++) {
                Assert.True(msgIds[i] > msgIds[i - 1]);
            }
            foreach (ulong msgId in msgIds) {
                Assert.True(msgId < (1UL << 53));
                Assert.Equal((ulong)(localMillis / 1000), msgId >> MsgIdGenerator.SequenceBits);
            }
        }


        // Java: RpcExactlyOnceTest.nullMethodOrDataIsRefusedAtTheCallAndTheQueuedCallsStillGoOut.
        /// <summary>
        /// A null method is refused at the call, and nothing is queued for it. A call queued before it still gets its answer,
        /// and the server receives that one request only.
        /// </summary>
        [Fact]
        public async Task RpcAsync_NullMethod_IsRefusedAtTheCallAndTheQueuedCallStillGoesOut() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            CentrifugeRpcCall queued = client.RpcCall("queued", ReadOnlyMemory<byte>.Empty);

            // The argument check throws before the task exists, so the .Result of the refused call is never read.
            Assert.Throws<ArgumentNullException>(() => client.RpcAsync(null!, ReadOnlyMemory<byte>.Empty, null, default).Result);
            Assert.Throws<ArgumentNullException>(() => client.RpcCall(null!, ReadOnlyMemory<byte>.Empty));

            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(queued.Result);

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Equal("queued", Assert.Single(RpcTestHelpers.Requests(Server)).Rpc.Method);
        }
    }
}
