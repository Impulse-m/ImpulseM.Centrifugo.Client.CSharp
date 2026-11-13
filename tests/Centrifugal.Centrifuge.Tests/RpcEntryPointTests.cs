// The entry points of the rpc calls. The fork entry points (the overloads with per-call options, and RpcCall) fault a client-side
// loss with CentrifugeConnectionLostException and a token cancelled before the call with code 17. The upstream overload with a
// token keeps the plain CentrifugeException for a loss (RpcTransportLossTests pins it) and OperationCanceledException for a token
// cancelled before the call (OrderedReplyTests pins it).
// The upstream rule is kept: a plain call is never sent again after the transport was lost.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The entry points of the rpc calls: the failure types of each overload, and the token cancelled before the call.</summary>
    [Collection("Integration")]
    public sealed class RpcEntryPointTests : RpcTestBase {
        /// <summary>The overload with options and no token sends the call with its headers.</summary>
        [Fact]
        public async Task RpcAsync_WithOptionsAndNoToken_SendsTheCallWithItsHeaders() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(client.RpcAsync("method", ReadOnlyMemory<byte>.Empty, new CentrifugeRpcOptions().WithHeader("k", "v")));

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Equal("v", RpcTestHelpers.Requests(Server).Single().Rpc.Headers["k"]);
        }


        /// <summary>A call started through RpcCall that is lost with its connection fails with CentrifugeConnectionLostException.</summary>
        [Fact]
        public async Task RpcCall_LostWithItsConnection_FailsWithTheLossType() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            RpcTestHelpers.Defer(Server, (Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Method == "held");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeRpcCall call = client.RpcCall("held", ReadOnlyMemory<byte>.Empty);
            await Server.AwaitHeldAsync();
            Server.CloseConnection();

            CentrifugeConnectionLostException error = await Assert.ThrowsAsync<CentrifugeConnectionLostException>(() => RpcTestHelpers.Bounded(call.Result));
            Assert.Equal(CentrifugeErrorCodes.ConnectionClosed, error.Code);
            Assert.Single(RpcTestHelpers.Requests(Server));
        }


        /// <summary>
        /// A token cancelled before a fork call is made: the call faults with code 17 at once, holds no slot of the bound, and
        /// nothing is sent. The next call is accepted with a bound of one.
        /// </summary>
        [Fact]
        public async Task RpcAsync_TokenCancelledBeforeTheCall_FailsWith17AndSendsNothing() {
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance, MaxPendingRpc = 1 });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            using CancellationTokenSource cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(client.RpcAsync("method", ReadOnlyMemory<byte>.Empty, new CentrifugeRpcOptions(), cancelled.Token)));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, error.Code);

            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty));

            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Protocol.Command request = Assert.Single(RpcTestHelpers.Requests(Server));
            Assert.Equal("barrier", request.Rpc.Method);
            Assert.Empty(RpcTestHelpers.Cancels(Server));
        }


        /// <summary>
        /// A fork call whose token is cancelled before the call is made, and whose client closes before the queued cancel
        /// runs: the cancel still settles the call with code 17, and nothing is sent. The loop is held by a step on another
        /// thread, so the cancel and the close are both queued behind it, in that order.
        /// </summary>
        [Fact]
        public async Task RpcAsync_TokenCancelledBeforeTheCallAndClosedBeforeTheCancelRuns_FailsWith17() {
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;
            TaskCompletionSource<bool> held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim release = new ManualResetEventSlim(false);
            Task holder = Task.Run(() => client.Loop.Invoke(() => {
                held.TrySetResult(true);
                release.Wait();
            }));
            await RpcTestHelpers.Bounded(held.Task);

            using CancellationTokenSource cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Task<CentrifugeRpcResult> call = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty, new CentrifugeRpcOptions(), cancelled.Token);
            Task<bool> closing = client.CloseAsync(TimeSpan.Zero);
            release.Set();
            await holder.WaitAsync(RpcTestHelpers.Wait);

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, error.Code);
            Assert.False(await closing);
            Assert.Empty(RpcTestHelpers.Requests(Server));
        }
    }
}
