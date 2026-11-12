// The requested connection token refresh as a queue: one command in flight at a time, the queued refreshes sent in order,
// the deadline of a command, a refusal while disconnected that restores the previous token, and a close that fails the
// refresh in flight and the queued ones.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the order and the single flight of the requested connection token refreshes.</summary>
    public sealed class AuthRefreshQueueTests : FakeServerTestBase {
        // Java: twoConcurrentRefreshCallsAreSentOneAfterAnother
        [Fact]
        public async Task RefreshAsync_TwoConcurrentCalls_AreSentOneAfterAnother() {
            RefreshArrivals arrivals = new RefreshArrivals();
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return FakeCentrifugoServer.NoReply;
            };
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeAuthResult> first = client.RefreshAsync("token-a");
            Task<CentrifugeAuthResult> second = client.RefreshAsync("token-b");
            Command firstCommand = await arrivals.Nth(0);
            await client.LoopBarrierAsync();
            Assert.Equal(new[] { "token-a" }, arrivals.Tokens());

            await Server.SendReplyAsync(new Reply { Id = firstCommand.Id, Refresh = ConnectReplies.Refresh(false, 0, "u1", "") });
            Command secondCommand = await arrivals.Nth(1);
            await Server.SendReplyAsync(new Reply { Id = secondCommand.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") });

            Assert.Equal("u1", (await first).User);
            Assert.Equal("u2", (await second).User);
            Assert.Equal(new[] { "token-a", "token-b" }, arrivals.Tokens());
        }


        // Java: refreshWithTheTokenInFlightSharesItPastAQueuedOtherToken
        [Fact]
        public async Task RefreshAsync_SameTokenAfterAnotherQueued_SharesTheCommandInFlight() {
            RefreshArrivals arrivals = new RefreshArrivals();
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return FakeCentrifugoServer.NoReply;
            };
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeAuthResult> first = client.RefreshAsync("token-a");
            Task<CentrifugeAuthResult> queued = client.RefreshAsync("token-b");
            Task<CentrifugeAuthResult> shared = client.RefreshAsync("token-a");
            Command firstCommand = await arrivals.Nth(0);
            await client.LoopBarrierAsync();
            Assert.Equal(new[] { "token-a" }, arrivals.Tokens());

            await Server.SendReplyAsync(new Reply { Id = firstCommand.Id, Refresh = ConnectReplies.Refresh(false, 0, "u1", "") });
            Command secondCommand = await arrivals.Nth(1);
            await Server.SendReplyAsync(new Reply { Id = secondCommand.Id, Refresh = ConnectReplies.Refresh(false, 0, "u2", "") });

            await Task.WhenAll(first, queued, shared);
            Assert.Equal(new[] { "token-a", "token-b" }, arrivals.Tokens());
        }


        // Java: refreshDeadlineIsReportedAsTimeoutException. The deadline runs on the fake clock.
        [Fact]
        public async Task RefreshAsync_CommandPastItsDeadline_FaultsWithTimeout() {
            RefreshArrivals arrivals = new RefreshArrivals();
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return FakeCentrifugoServer.NoReply;
            };
            CentrifugeClientOptions options = new CentrifugeClientOptions {
                Timeout = TimeSpan.FromMilliseconds(500),
            };
            CentrifugeClient client = NewClientWithTestClock(out FakeStepTimerFactory timers, options);
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeAuthResult> call = client.RefreshAsync("token-2");
            await arrivals.Nth(0);

            timers.Advance(500);

            await Assert.ThrowsAsync<CentrifugeTimeoutException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        }


        // Java: refusedRefreshWhileDisconnectedRestoresThePreviousToken
        [Fact]
        public async Task RefreshAsync_RefusedAfterAConnectWhileDisconnected_RestoresThePreviousToken() {
            Server.OnCommand = (Command command) => {
                if (command.Refresh != null) {
                    return new Reply { Id = command.Id, Error = new Error { Code = 103, Message = "permission denied" } };
                }
                return null;
            };
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Token = "token-1" });
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            client.Disconnect();
            await client.LoopBarrierAsync();

            Task<CentrifugeAuthResult> refused = client.RefreshAsync("token-2");
            Task reconnected = WhenConnected(client);
            client.Connect();
            await reconnected;

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => refused);
            Assert.Equal(103, error.Code);
            client.Disconnect();
            await client.LoopBarrierAsync();
            Task reconnectedAgain = WhenConnected(client);
            client.Connect();
            await reconnectedAgain;

            List<string> connectTokens = Server.Received
                .Where(received => received.Connect != null)
                .Select(received => received.Connect.Token)
                .ToList();
            Assert.Equal("token-1", connectTokens.Last());
        }


        // Java: refreshesWaitingForAnInFlightRefreshFailOnClose
        [Fact]
        public async Task RefreshAsync_ClosedWhileOneIsInFlight_FailsTheInFlightAndTheQueued() {
            RefreshArrivals arrivals = new RefreshArrivals();
            Server.OnCommand = (Command command) => {
                if (command.Refresh == null) {
                    return null;
                }
                arrivals.Add(command);
                return FakeCentrifugoServer.NoReply;
            };
            CentrifugeClient client = NewClient();
            Task connected = WhenConnected(client);
            client.Connect();
            await connected;
            Task<CentrifugeAuthResult> first = client.RefreshAsync("token-a");
            Task<CentrifugeAuthResult> second = client.RefreshAsync("token-b");
            await arrivals.Nth(0);

            await client.DisposeAsync();

            await Assert.ThrowsAnyAsync<CentrifugeException>(() => first);
            await Assert.ThrowsAnyAsync<CentrifugeException>(() => second);
            Assert.Equal(new[] { "token-a" }, arrivals.Tokens());
        }
    }
}
