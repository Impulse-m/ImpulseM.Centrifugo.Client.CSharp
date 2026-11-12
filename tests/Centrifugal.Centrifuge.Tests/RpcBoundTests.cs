// The bound of the unsettled rpc calls (maxPendingRpc) and the slots it counts, against the fake server.
// Not ported: RpcExactlyOnceTest.aSettledCallFreesItsSlotBeforeTheExecutorDeliversIt: it holds the loop in a long step,
// which the harness cannot do. The slot release in the settling step is checked by the accepted call of settledCallsFreeTheirSlots.
// The upstream rule is kept: without an instance there is no bound.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The slots of the bound: a call holds one from its submit until it settles.</summary>
    [Collection("Integration")]
    public sealed class RpcBoundTests : RpcTestBase {
        // Java: RpcExactlyOnceTest.callsBeyondTheQueueBoundFailWithLimitExceeded.
        /// <summary>A call beyond the bound fails with code 16 at once; the calls within the bound are kept.</summary>
        [Fact]
        public async Task RpcAsync_BeyondTheBound_FailsWith16AtOnce() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance, MaxPendingRpc = 2 });
            CentrifugeRpcCall first = client.RpcCall("a", ReadOnlyMemory<byte>.Empty);
            CentrifugeRpcCall second = client.RpcCall("b", ReadOnlyMemory<byte>.Empty);

            CentrifugeRpcCall third = client.RpcCall("c", ReadOnlyMemory<byte>.Empty);

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(third.Result));
            Assert.Equal(CentrifugeErrorCodes.LimitExceeded, error.Code);
            Assert.Equal("too many rpc calls are pending", error.Message);
            Assert.False(first.Result.IsCompleted);
            Assert.False(second.Result.IsCompleted);

            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            CentrifugeRpcResult firstResult = await RpcTestHelpers.Bounded(first.Result);
            CentrifugeRpcResult secondResult = await RpcTestHelpers.Bounded(second.Result);

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(firstResult.Data.Span));
            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(secondResult.Data.Span));
            Assert.Equal(2, RpcTestHelpers.Requests(Server).Count);
        }


        // Java: RpcExactlyOnceTest.aRefusedCallHoldsNoSlotOfTheBound.
        /// <summary>A refused call holds no slot: once the first call settles, the next call is accepted.</summary>
        [Fact]
        public async Task RpcAsync_RefusedCall_HoldsNoSlot() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance, MaxPendingRpc = 1 });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall held = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            CentrifugeException refused = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(client.RpcCall("b", ReadOnlyMemory<byte>.Empty).Result));
            Assert.Equal(CentrifugeErrorCodes.LimitExceeded, refused.Code);

            await RpcTestHelpers.ReleaseAsync(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            await RpcTestHelpers.Bounded(held.Result);
            CentrifugeRpcResult result = await client.RpcAsync("c", ReadOnlyMemory<byte>.Empty);

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
        }


        // Java: RpcExactlyOnceTest.settledCallsFreeTheirSlots.
        /// <summary>The slots of settled calls come back: a later call is accepted.</summary>
        [Fact]
        public async Task RpcAsync_SettledCalls_FreeTheirSlots() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance, MaxPendingRpc = 2 });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await RpcTestHelpers.Bounded(Task.WhenAll(client.RpcAsync("a", ReadOnlyMemory<byte>.Empty), client.RpcAsync("b", ReadOnlyMemory<byte>.Empty)));
            CentrifugeRpcResult third = await client.RpcAsync("c", ReadOnlyMemory<byte>.Empty);

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(third.Data.Span));
        }


        // Java: RpcExactlyOnceTest.cancelledCallsFreeTheirSlots.
        /// <summary>
        /// Cancelled calls give their slots back while the client is disconnected, so a later call is accepted, and the two cancels
        /// go out with the next connection, for the msg_ids of the cancelled calls. Real time: the reconnect is an upstream
        /// LoopTimer, so its delays are the smallest the options allow.
        /// </summary>
        [Fact]
        public async Task RpcAsync_CancelledCalls_FreeTheirSlotsAndTheirCancelsGoWithTheNextConnect() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                MaxPendingRpc = 2,
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Connect != null || (cmd.Rpc != null && cmd.Rpc.Method == "hold"));
            CommandArrivals holds = new CommandArrivals(RpcTestHelpers.Wait);
            CommandArrivals reconnects = new CommandArrivals(RpcTestHelpers.Wait);
            RpcTestHelpers.CountArrivals(Server, holds, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            RpcTestHelpers.CountArrivals(Server, reconnects, (Protocol.Command cmd) => cmd.Connect != null);
            CentrifugeRpcCall first = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            CentrifugeRpcCall second = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await holds.Reached(2);
            List<ulong> msgIds = RpcTestHelpers.Requests(Server).Where((Protocol.Command cmd) => cmd.Rpc.Method == "hold").Select((Protocol.Command cmd) => cmd.Rpc.MsgId).ToList();

            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnects.Reached(1);
            first.Cancel();
            second.Cancel();
            await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(first.Result));
            await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(second.Result));

            Task<CentrifugeRpcResult> third = client.RpcAsync("c", ReadOnlyMemory<byte>.Empty);
            Server.ReleaseDeferredMatching((Protocol.Command cmd) => cmd.Connect != null);
            await reconnected;
            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(third);

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Equal(msgIds.ToArray(), RpcTestHelpers.Cancels(Server).Select((Protocol.Command cmd) => cmd.Rpc.Cancel).ToArray());
        }


        /// <summary>
        /// A call whose token is cancelled before the call is made is refused with the cancellation and sends nothing, and it
        /// holds no slot: with a bound of one, the next call is accepted.
        /// </summary>
        [Fact]
        public async Task RpcAsync_PreCancelledToken_IsRefusedWithCancellationAndHoldsNoSlot() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance, MaxPendingRpc = 1 });
            CancellationTokenSource source = new CancellationTokenSource();
            source.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RpcTestHelpers.Bounded(client.RpcAsync("cancelled", ReadOnlyMemory<byte>.Empty, null, source.Token)));

            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(client.RpcAsync("next", ReadOnlyMemory<byte>.Empty));

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Equal("next", Assert.Single(RpcTestHelpers.Requests(Server)).Rpc.Method);
        }


        // Java: RpcExactlyOnceTest.thirtyThirdCallWhileDisconnectedIsRefusedAtOnce.
        /// <summary>The bound counts the calls that wait for a connection: the 33rd is refused and the 32 others are sent on connect.</summary>
        [Fact]
        public async Task RpcAsync_ThirtyThirdCallWhileDisconnected_IsRefusedAtOnce() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            List<CentrifugeRpcCall> calls = new List<CentrifugeRpcCall>();
            for (int i = 0; i < 32; i++) {
                calls.Add(client.RpcCall("m", ReadOnlyMemory<byte>.Empty));
            }

            CentrifugeException refused = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(client.RpcCall("m", ReadOnlyMemory<byte>.Empty).Result));
            Assert.Equal(CentrifugeErrorCodes.LimitExceeded, refused.Code);

            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            await RpcTestHelpers.Bounded(Task.WhenAll(calls.Select((CentrifugeRpcCall call) => call.Result)));

            Assert.Equal(32, RpcTestHelpers.Requests(Server).Count);
        }


        // Java: RpcExactlyOnceTest.withoutAnInstanceThereIsNoBound.
        /// <summary>Without an instance there is no bound: forty calls are accepted and all are answered.</summary>
        [Fact]
        public async Task RpcAsync_WithoutAnInstance_ThereIsNoBound() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { MaxPendingRpc = 1 });
            List<Task<CentrifugeRpcResult>> calls = new List<Task<CentrifugeRpcResult>>();
            for (int i = 0; i < 40; i++) {
                calls.Add(client.RpcAsync("m", ReadOnlyMemory<byte>.Empty));
            }
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await RpcTestHelpers.Bounded(Task.WhenAll(calls));

            Assert.Equal(40, calls.Count((Task<CentrifugeRpcResult> call) => call.IsCompletedSuccessfully));
        }


        // Java: RpcExactlyOnceTest.legacyCallHoldsItsSlotUntilItSettles.
        /// <summary>A call on a connection without RPC options holds its slot until it settles.</summary>
        [Fact]
        public async Task RpcAsync_LegacyCall_HoldsItsSlotUntilItSettles() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance, MaxPendingRpc = 1 });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall held = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            CentrifugeException refused = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(client.RpcCall("b", ReadOnlyMemory<byte>.Empty).Result));
            Assert.Equal(CentrifugeErrorCodes.LimitExceeded, refused.Code);

            await RpcTestHelpers.ReleaseAsync(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(held.Result);

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
        }


        // Java: RpcExactlyOnceTest.lostCallKeepsItsSlotAcrossTheReconnect.
        /// <summary>
        /// A call lost with its connection keeps its slot while the client reconnects: a call made meanwhile is refused with code
        /// 16, and the lost call is answered after the reconnect. The reconnect is held, so the client stays connecting for the
        /// refusal. Real time: the reconnect is an upstream LoopTimer, so its delays are the smallest the options allow.
        /// </summary>
        [Fact]
        public async Task RpcAsync_LostCallKeepsItsSlotAcrossTheReconnect() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Instance = RpcTestHelpers.Instance,
                MaxPendingRpc = 1,
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Connect != null || (cmd.Rpc != null && cmd.Rpc.Method == "hold"));
            CommandArrivals holds = new CommandArrivals(RpcTestHelpers.Wait);
            CommandArrivals reconnects = new CommandArrivals(RpcTestHelpers.Wait);
            RpcTestHelpers.CountArrivals(Server, holds, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "hold");
            RpcTestHelpers.CountArrivals(Server, reconnects, (Protocol.Command cmd) => cmd.Connect != null);
            CentrifugeRpcCall first = client.RpcCall("hold", ReadOnlyMemory<byte>.Empty);
            await holds.Reached(1);

            Task<CentrifugeConnectedEventArgs> reconnected = WhenConnected(client);
            Server.CloseConnection();
            await reconnects.Reached(1);
            CentrifugeRpcCall second = client.RpcCall("b", ReadOnlyMemory<byte>.Empty);

            CentrifugeException refused = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(second.Result));
            Assert.Equal(CentrifugeErrorCodes.LimitExceeded, refused.Code);
            Assert.False(first.Result.IsCompleted);

            Server.ReleaseDeferredMatching((Protocol.Command cmd) => cmd.Connect != null);
            await reconnected;
            await holds.Reached(2);
            Protocol.Command resend = RpcTestHelpers.Requests(Server).Where((Protocol.Command cmd) => cmd.Rpc.Method == "hold").Last();
            await RpcTestHelpers.AnswerAsync(Server, resend.Id, "ok");
            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(first.Result);

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.True(resend.Rpc.Retransmit);
        }
    }
}
