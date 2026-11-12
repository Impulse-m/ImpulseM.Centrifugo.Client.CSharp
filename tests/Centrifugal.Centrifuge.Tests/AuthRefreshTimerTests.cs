// The expiry timer of the connection token, driven by the fake clock: the timer fires at 95 percent of the ttl, fetches a
// token from the getter and refreshes it. A refusal for permission keeps the connection, another server code ends it, and
// an accepted refresh arms the next expiry. The fetch itself is answered by the getter on another thread; the tests wait
// for the refresh command to reach the fake server, not for a delay.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the expiry timer of the connection token against the fake server and the fake clock.</summary>
    public sealed class AuthRefreshTimerTests : FakeServerTestBase {
        // The renewal delay of a token with a ttl of 100 seconds: 95 percent of it, in milliseconds.
        private const int RenewalDelayMs = 95000;

        // Java: refusedTimerRefreshKeepsTheConnection
        [Fact]
        public async Task RefreshTimer_RefusedForPermission_KeepsTheConnection() {
            TaskCompletionSource<uint> arrived = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            ArmExpiringConnection(arrived);
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, TokenOptions());
            List<string> authLog = new List<string>();
            int disconnects = 0;
            client.Auth += (_, args) => authLog.Add(args.User + "/" + args.AuthClass);
            client.Disconnected += (_, args) => disconnects++;
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeErrorEventArgs> refreshError = WhenErrorOfType(client, "refresh");

            timers.Advance(RenewalDelayMs);
            uint id = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Server.SendReplyAsync(new Reply { Id = id, Error = new Error { Code = 103, Message = "permission denied" } });
            CentrifugeErrorEventArgs error = await refreshError;
            await client.LoopBarrierAsync();

            Assert.Equal(103, error.Code);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal(0, disconnects);
            Assert.Equal(new[] { "u1/" }, authLog.ToArray());
        }


        // Java: refusedTimerRefreshIsNotRetried. A refusal for permission arms no timer: the fake clock has no pending
        // timer once the refusal is handled.
        [Fact]
        public async Task RefreshTimer_RefusedForPermission_IsNotRetried() {
            TaskCompletionSource<uint> arrived = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            ArmExpiringConnection(arrived);
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, TokenOptions());
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeErrorEventArgs> refreshError = WhenErrorOfType(client, "refresh");

            timers.Advance(RenewalDelayMs);
            uint id = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Server.SendReplyAsync(new Reply { Id = id, Error = new Error { Code = 103, Message = "permission denied" } });
            await refreshError;
            await client.LoopBarrierAsync();

            Assert.Equal(0, timers.PendingCount);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        // Java: timerRefreshRefusedWithAnotherCodeDisconnectsWithIt
        [Fact]
        public async Task RefreshTimer_RefusedWithAnotherCode_DisconnectsWithThatCode() {
            TaskCompletionSource<uint> arrived = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            ArmExpiringConnection(arrived);
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, TokenOptions());
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);

            timers.Advance(RenewalDelayMs);
            uint id = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Server.SendReplyAsync(new Reply { Id = id, Error = new Error { Code = 101, Message = "refused" } });
            CentrifugeDisconnectedEventArgs args = await disconnected.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(101, args.Code);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }


        // An accepted timer refresh stores the token, raises the identity change and arms the next expiry.
        [Fact]
        public async Task RefreshTimer_Accepted_ArmsTheNextExpiry() {
            TaskCompletionSource<uint> arrived = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            ArmExpiringConnection(arrived);
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, TokenOptions());
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeAuthEventArgs> identityChanged = WhenAuth(client);

            timers.Advance(RenewalDelayMs);
            uint id = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Server.SendReplyAsync(new Reply { Id = id, Refresh = ConnectReplies.Refresh(true, 100, "u2", "bot") });
            CentrifugeAuthEventArgs auth = await identityChanged;

            Assert.Equal("u2", auth.User);
            Assert.Equal(1, timers.PendingWithDelay(RenewalDelayMs));
        }


        // A listener that throws on the identity change of a timer refresh does not stop the connection: a requested
        // refresh that follows is answered, and the client stays Connected.
        // Java: refreshIsAnsweredAfterTheAuthListenerThrowsOnATimerRefresh
        [Fact]
        public async Task RefreshTimer_AuthListenerThrows_RequestedRefreshIsStillAnswered() {
            TaskCompletionSource<uint> arrived = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            ArmExpiringConnection(arrived);
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, TokenOptions());
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            client.Auth += (_, args) => {
                if (args.User == "u2") {
                    throw new InvalidOperationException("listener failed");
                }
            };

            Task<CentrifugeErrorEventArgs> listenerError = WhenErrorOfType(client, "auth");

            timers.Advance(RenewalDelayMs);
            uint id = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Server.SendReplyAsync(new Reply { Id = id, Refresh = ConnectReplies.Refresh(true, 100, "u2", "bot") });
            await listenerError;
            Assert.Equal(CentrifugeClientState.Connected, client.State);

            Server.OnCommand = (Command command) => command.Refresh != null
                ? new Reply { Id = command.Id, Refresh = ConnectReplies.Refresh(false, 0, "u3", "bot") }
                : null;
            CentrifugeAuthResult result = await client.RefreshAsync("token-3");

            Assert.Equal("u3", result.User);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        // A renewal asked for while the timer fetch is pending starts no second fetch. The first refresh is refused with a
        // retryable code, which would otherwise run the queued timer entry at once, without the retry delay.
        [Fact]
        public async Task RefreshConnectionToken_WhileTimerFetchIsPending_StartsNoSecondFetch() {
            TaskCompletionSource<uint> arrived = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            ArmExpiringConnection(arrived);
            TaskCompletionSource<string> gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int getterCalls = 0;
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                // The timeout is above every delay of this test, so a timer of the timeout is never the one that fires.
                Timeout = TimeSpan.FromSeconds(60),
                // Call 1 is the token of the connect, call 2 the timer fetch that waits for the gate.
                GetToken = () => {
                    int call = Interlocked.Increment(ref getterCalls);
                    if (call == 2) {
                        return gate.Task;
                    }
                    return Task.FromResult(call == 1 ? "token-connect" : "token-second");
                },
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            timers.Advance(RenewalDelayMs);
            await client.LoopBarrierAsync();
            client.RefreshConnectionToken();
            timers.Advance(0);
            await client.LoopBarrierAsync();
            Task<CentrifugeErrorEventArgs> refreshError = WhenErrorOfType(client, "refresh");
            gate.SetResult("token-timer");
            uint id = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Server.SendReplyAsync(new Reply { Id = id, Error = new Error { Code = 109, Message = "expired" } });
            await refreshError;
            await client.LoopBarrierAsync();

            // A second fetch would have started in the step that handled the refusal: its timeout timer would be pending
            // now. The retry of the refusal is the only timer pending, and it waits for its delay.
            Assert.Equal(0, timers.PendingWithDelay(60000));
            Assert.Equal(1, timers.PendingWithDelay(5000));
            Assert.Equal(2, Volatile.Read(ref getterCalls));
        }


        // The connection expires in 100 seconds and the getter returns "token-timer". The user of the connect is "u1".
        private void ArmExpiringConnection(TaskCompletionSource<uint> arrived) {
            Server.ConnectResult = ConnectReplies.Connect(client: "fake-client", expires: true, ttl: 100, user: "u1");
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrived.TrySetResult(command.Id);
                return FakeCentrifugoServer.NoReply;
            };
        }


        private static CentrifugeClientOptions TokenOptions() {
            return new CentrifugeClientOptions {
                GetToken = () => Task.FromResult("token-timer"),
            };
        }


        private static Task<CentrifugeErrorEventArgs> WhenErrorOfType(CentrifugeClient client, string type) {
            TaskCompletionSource<CentrifugeErrorEventArgs> source = new TaskCompletionSource<CentrifugeErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Error += (_, args) => {
                if (args.Type == type) {
                    source.TrySetResult(args);
                }
            };
            return source.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }


        private static Task<CentrifugeAuthEventArgs> WhenAuth(CentrifugeClient client) {
            TaskCompletionSource<CentrifugeAuthEventArgs> source = new TaskCompletionSource<CentrifugeAuthEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Auth += (_, args) => source.TrySetResult(args);
            return source.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
