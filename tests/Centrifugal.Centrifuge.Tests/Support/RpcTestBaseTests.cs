using System;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the rpc test base: the answers to rpc commands, the RPC options flag, the draws and the hold wait.</summary>
    public sealed class RpcTestBaseTests : RpcTestBase {
        /// <summary>AnswerRpcs answers an rpc call with the payload it was given.</summary>
        [Fact]
        public async Task AnswerRpcs_AnswersAnRpcCallWithThePayload() {
            AnswerRpcs("pong", 0);
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;
            CentrifugeRpcResult result = await client.RpcAsync("ping", Encoding.UTF8.GetBytes("x"));

            Assert.Equal("pong", Encoding.UTF8.GetString(result.Data.Span));
        }

        /// <summary>EnableRpcOptions sets the RPC options bit in the connect reply.</summary>
        [Fact]
        public void EnableRpcOptions_SetsTheRpcOptionsFlag() {
            EnableRpcOptions();

            Assert.Equal(ConnectCapabilities.RpcOptionsFlag, Server.ConnectResult.Flag);
        }

        /// <summary>The maximum draws are the top of the range, the minimum draws the bottom.</summary>
        [Fact]
        public void Draws_AreTheTopAndTheBottomOfTheRange() {
            Assert.True(MaximumDraws().NextDouble() > 0.999);
            Assert.Equal(0.0, MinimumDraws().NextDouble());
        }

        /// <summary>AwaitHeldAsync completes once the server holds a command.</summary>
        [Fact]
        public async Task AwaitHeldAsync_CompletesWhenACommandIsHeld() {
            Server.DeferCommand(cmd => cmd.Connect != null);
            CentrifugeClient client = NewClient();

            client.Connect();
            await AwaitHeldAsync(Server);

            Assert.Equal(1, Server.HeldCount);
            Server.ReleaseDeferredCommands();
        }
    }
}
