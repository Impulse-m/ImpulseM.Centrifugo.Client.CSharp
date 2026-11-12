// The requested connection token refresh: the identity the server confirms, the shared command of one token, a refusal
// that keeps the connection, and a refresh made before the connect that is sent with it.
// Not ported: aThrowingCallbackDoesNotStopTheOthersSharingItsRefresh: the waiters complete as tasks, so a continuation that
// throws cannot reach the loop or the other waiters; there is no callback that the client runs inline.
// Not ported: closingFailsEveryWaitingRefreshEvenWhenACallbackThrows: close completes the waiters as tasks; no caller
// callback runs inside the close step, so there is no throw to report.
// A cancellation after the call completed has no effect that the public API shows: the outcome is already final.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the requested connection token refresh against the fake server.</summary>
    public sealed class AuthRefreshTests : FakeServerTestBase {
        // Java: refreshWhileConnectedReturnsTheNewIdentity
        [Fact]
        public async Task RefreshAsync_WhileConnected_ReturnsTheIdentityTheServerConfirms() {
            Server.OnCommand = (Command command) => command.Refresh != null
                ? new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "bot") }
                : null;
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            CentrifugeAuthResult result = await client.RefreshAsync("token-2");

            Assert.Equal("u2", result.User);
            Assert.Equal("bot", result.AuthClass);
            Assert.False(result.Expires);
            Assert.Equal("token-2", Server.Received.Single(received => received.Refresh != null).Refresh.Token);
        }


        // Java: refreshWithTheSameTokenSharesOneCommand
        [Fact]
        public async Task RefreshAsync_SameTokenWhileInFlight_SharesOneCommand() {
            TaskCompletionSource<Command> arrived = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrived.TrySetResult(command);
                return FakeCentrifugoServer.NoReply;
            };
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeAuthResult> first = client.RefreshAsync("token-2");
            Task<CentrifugeAuthResult> second = client.RefreshAsync("token-2");
            Command command = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Server.SendReplyAsync(new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "bot") });

            CentrifugeAuthResult firstResult = await first;
            CentrifugeAuthResult secondResult = await second;
            Assert.Equal("u2", firstResult.User);
            Assert.Equal("u2", secondResult.User);
            Assert.Equal(new[] { "token-2" }, Server.Received.Where(received => received.Refresh != null).Select(received => received.Refresh.Token).ToArray());
        }


        // A refusal ends the call with the server error; the connection keeps its state and its identity.
        [Fact]
        public async Task RefreshAsync_RefusedByTheServer_FaultsAndKeepsTheConnection() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1", ping: 25);
            Server.OnCommand = (Command command) => command.Refresh != null
                ? new Reply { Id = command.Id, Error = new Error { Code = 101, Message = "refused" } }
                : null;
            CentrifugeClient client = NewClient();
            List<string> events = new List<string>();
            client.Auth += (_, args) => events.Add(args.User + "/" + args.AuthClass);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            await client.LoopBarrierAsync();

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => client.RefreshAsync("token-2"));

            Assert.Equal(101, error.Code);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal(new[] { "u1/" }, events.ToArray());
        }


        // Java: authEventFiresOnConnectAndWhenTheIdentityChanges (the refresh part). A refresh raises the auth event only when
        // the identity changes, in steps: the same identity, a new user, then only a new auth class.
        [Fact]
        public async Task RefreshAsync_IdentityChanges_RaisesTheAuthEventOnlyOnChange() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1", ping: 25);
            Server.OnCommand = (Command command) => command.Refresh != null
                ? new Reply { Id = command.Id, Refresh = RefreshFor(command.Refresh.Token) }
                : null;
            CentrifugeClient client = NewClient();
            List<string> events = new List<string>();
            client.Auth += (_, args) => events.Add(args.User + "/" + args.AuthClass);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            await client.LoopBarrierAsync();

            await client.RefreshAsync("same");
            await client.LoopBarrierAsync();
            Assert.Equal(new[] { "u1/" }, events.ToArray());

            await client.RefreshAsync("changed");
            await client.LoopBarrierAsync();
            Assert.Equal(new[] { "u1/", "u2/" }, events.ToArray());

            await client.RefreshAsync("bot");
            await client.LoopBarrierAsync();
            Assert.Equal(new[] { "u1/", "u2/", "u2/bot" }, events.ToArray());
        }


        // Java: refreshWhileDisconnectedIsSentAfterConnect
        [Fact]
        public async Task RefreshAsync_WhileDisconnected_IsSentWithTheNextConnect() {
            Server.OnCommand = (Command command) => command.Refresh != null
                ? new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "ud", "bot") }
                : null;
            CentrifugeClient client = NewClient();

            Task<CentrifugeAuthResult> pending = client.RefreshAsync("token-d");
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            CentrifugeAuthResult result = await pending;

            Assert.Equal("ud", result.User);
            Assert.Equal("token-d", Server.Received.Single(received => received.Refresh != null).Refresh.Token);
        }


        // The identity the fake server confirms for each refresh token: the same identity, a new user, a new auth class.
        private static RefreshResult RefreshFor(string token) {
            if (token == "bot") {
                return ConnectReplies.Refresh(false, 0, "u2", "bot");
            }
            if (token == "changed") {
                return ConnectReplies.Refresh(false, 0, "u2", "");
            }
            return ConnectReplies.Refresh(false, 0, "u1", "");
        }
    }
}
