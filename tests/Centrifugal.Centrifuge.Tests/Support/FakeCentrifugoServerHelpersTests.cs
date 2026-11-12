using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the helpers the partial file adds to the fake server: holds, releases, close codes and replies.</summary>
    public sealed class FakeCentrifugoServerHelpersTests : FakeServerTestBase {
        /// <summary>A held connect is not answered until a release, and the release answers it.</summary>
        [Fact]
        public async Task DeferCommand_HoldsTheConnectUntilReleased() {
            Server.DeferCommand((Command cmd) => cmd.Connect != null);
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await Server.AwaitHeldAsync();
            Assert.Equal(1, Server.HeldCount);
            Server.ReleaseDeferredCommands();
            await connected;

            Assert.Equal(0, Server.HeldCount);
        }

        /// <summary>ReleaseDeferredMatching answers only the held commands that match.</summary>
        [Fact]
        public async Task ReleaseDeferredMatching_AnswersOnlyTheMatchingCommands() {
            Server.DeferCommand((Command cmd) => cmd.Connect != null);
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await Server.AwaitHeldAsync();
            Server.ReleaseDeferredMatching((Command cmd) => cmd.Subscribe != null);
            Assert.Equal(1, Server.HeldCount);
            Server.ReleaseDeferredMatching((Command cmd) => cmd.Connect != null);
            await connected;

            Assert.Equal(0, Server.HeldCount);
        }

        /// <summary>ReleaseDeferredCommandsOnTheirSockets answers each held command on the socket it arrived on.</summary>
        [Fact]
        public async Task ReleaseDeferredCommandsOnTheirSockets_AnswersOnTheArrivalSocket() {
            Server.DeferCommand((Command cmd) => cmd.Connect != null);
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await Server.AwaitHeldAsync();
            Server.ReleaseDeferredCommandsOnTheirSockets();
            await connected;

            Assert.Equal(0, Server.HeldCount);
        }

        /// <summary>ConnectAs sets the user and the auth class that the connect reply carries.</summary>
        [Fact]
        public async Task ConnectAs_SetsTheUserAndAuthClassOfTheConnectReply() {
            Server.ConnectAs("alice", "email");
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            CentrifugeConnectedEventArgs args = await connected;

            Assert.Equal("alice", args.User);
            Assert.Equal("email", args.AuthClass);
        }

        /// <summary>A token in UsersByToken makes the connect reply carry that user.</summary>
        [Fact]
        public async Task UsersByToken_GivesTheConnectReplyTheUserOfTheToken() {
            Server.UsersByToken["secret"] = "bob";
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Token = "secret" });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            CentrifugeConnectedEventArgs args = await connected;

            Assert.Equal("bob", args.User);
        }

        /// <summary>A ping sent by the server leaves the connection usable.</summary>
        [Fact]
        public async Task SendPingAsync_LeavesTheConnectionUsable() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;
            await Server.SendPingAsync();
            await client.PublishAsync("news", new byte[] { 1 });

            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }

        /// <summary>A close frame with a terminal code ends the client with that code.</summary>
        [Fact]
        public async Task CloseWithCodeAsync_ClosesWithTheCodeAndReason() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            Task<CentrifugeDisconnectedEventArgs> disconnected = WhenDisconnected(client);

            client.Connect();
            await connected;
            await Server.CloseWithCodeAsync(4501, "closed by test");
            CentrifugeDisconnectedEventArgs args = await disconnected;

            Assert.Equal(4501, args.Code);
            Assert.Equal("closed by test", args.Reason);
        }

        /// <summary>A disconnect push ends the client with the push code.</summary>
        [Fact]
        public async Task SendDisconnectPushAsync_EndsTheClientWithThePushCode() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            Task<CentrifugeDisconnectedEventArgs> disconnected = WhenDisconnected(client);

            client.Connect();
            await connected;
            await Server.SendDisconnectPushAsync(3501, "gone");
            CentrifugeDisconnectedEventArgs args = await disconnected;

            Assert.Equal(3501, args.Code);
            Assert.Equal("gone", args.Reason);
        }

        /// <summary>LastPublish returns the last publish the client sent.</summary>
        [Fact]
        public async Task LastPublish_ReturnsTheLastPublishOfTheClient() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            await connected;
            await client.PublishAsync("news", new byte[] { 1 });
            Command? publish = Server.LastPublish();

            Assert.NotNull(publish);
            Assert.Equal("news", publish!.Publish.Channel);
        }

        /// <summary>ErrorReply carries the code, the message, the temporary flag and the metadata.</summary>
        [Fact]
        public void ErrorReply_CarriesCodeMessageTemporaryAndMetadata() {
            Dictionary<string, string> metadata = new Dictionary<string, string> { { "retry", "later" } };

            Reply reply = Server.ErrorReply(3, "boom", true, metadata);

            Assert.Equal(3u, reply.Error.Code);
            Assert.Equal("boom", reply.Error.Message);
            Assert.True(reply.Error.Temporary);
            Assert.Equal("later", reply.Error.Metadata["retry"]);
        }

        private static Task<CentrifugeDisconnectedEventArgs> WhenDisconnected(CentrifugeClient client) {
            TaskCompletionSource<CentrifugeDisconnectedEventArgs> disconnected =
                new TaskCompletionSource<CentrifugeDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += (object? sender, CentrifugeDisconnectedEventArgs args) => disconnected.TrySetResult(args);
            return disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
