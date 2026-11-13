// The connection token refresh of a client without a token getter or token exchange: the expiry timer does nothing, and a
// refresh reply with a client id stores it, while a reply without one keeps the stored id. The clock is the fake one.

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the expiry timer without a getter and the client id of the refresh replies.</summary>
    public sealed class AuthRefreshNoGetterTests : FakeServerTestBase {
        // The renewal delay of a token with a ttl of 100 seconds: 95 percent of it, in milliseconds.
        private const int RenewalDelayMs = 95000;

        // The field of the client that keeps the client id of the connection.
        private const string ClientIdField = "_clientId";


        // A timer without a token getter is silent: no error event, no refresh command and no session end. A renewal request
        // from the application is silent too.
        [Fact]
        public async Task RefreshTimer_WithoutATokenGetter_IsSilent() {
            int refreshes = 0;
            int errors = 0;
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh != null) {
                    Interlocked.Increment(ref refreshes);
                }
                return null;
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, new CentrifugeClientOptions { Token = "token-initial" });
            client.Error += (object? sender, CentrifugeErrorEventArgs args) => Interlocked.Increment(ref errors);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            timers.Advance(RenewalDelayMs);
            await client.LoopBarrierAsync();
            client.RefreshConnectionToken();
            await client.LoopBarrierAsync();

            Assert.Equal(0, Volatile.Read(ref errors));
            Assert.Equal(0, Volatile.Read(ref refreshes));
            Assert.Equal(0, timers.PendingCount);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        // A requested refresh reply that carries a client id replaces the stored one; a reply without a client id keeps the
        // stored one.
        [Fact]
        public async Task RefreshAsync_ReplyWithAClientId_StoresItAndAnEmptyOneKeepsIt() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                RefreshResult result = ConnectReplies.Refresh(false, 0, "u1", "");
                if (command.Refresh.Token == "token-2") {
                    result.Client = "client-2";
                }
                return new Reply { Id = command.Id, Refresh = result };
            };
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Assert.Equal("fake-client", ClientIdOf(client));

            await client.RefreshAsync("token-2");
            Assert.Equal("client-2", ClientIdOf(client));
            await client.RefreshAsync("token-3");
            Assert.Equal("client-2", ClientIdOf(client));
        }


        // A timer refresh reply that carries a client id stores it. The identity changes in the same reply: the auth event
        // is raised after the client id is stored, so the handler reads the stored id.
        [Fact]
        public async Task RefreshTimer_ReplyWithAClientId_StoresIt() {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                RefreshResult result = ConnectReplies.Refresh(true, 100, "u2", "bot");
                result.Client = "client-timer";
                return new Reply { Id = command.Id, Refresh = result };
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, new CentrifugeClientOptions {
                Token = "token-initial",
                GetToken = () => Task.FromResult("token-timer"),
            });
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            TaskCompletionSource<CentrifugeAuthEventArgs> identity =
                new TaskCompletionSource<CentrifugeAuthEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? clientIdInHandler = null;
            client.Auth += (object? sender, CentrifugeAuthEventArgs args) => {
                clientIdInHandler = ClientIdOf(client);
                identity.TrySetResult(args);
            };

            timers.Advance(RenewalDelayMs);
            CentrifugeAuthEventArgs authArgs = await identity.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("u2", authArgs.User);
            Assert.Equal("client-timer", clientIdInHandler);
            Assert.Equal("client-timer", ClientIdOf(client));
        }


        // The client id of the connection, read from the field the client keeps it in.
        private static string? ClientIdOf(CentrifugeClient client) {
            FieldInfo? field = typeof(CentrifugeClient).GetField(ClientIdField, BindingFlags.NonPublic | BindingFlags.Instance);
            return (string?)field!.GetValue(client);
        }
    }
}
