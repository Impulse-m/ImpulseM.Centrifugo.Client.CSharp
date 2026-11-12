// No Java test pins these rules by name; they follow the close rules of the Java client (close with an
// awaited termination, the return value for a zero wait, the close reason "client closed").
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>Close with a wait, and the calls after close.</summary>
    public sealed class CloseTests : FakeServerTestBase {
        /// <summary>Called from an event handler (inside a step), CloseAsync does not wait and returns false, and the close happens.</summary>
        [Fact]
        public async Task CloseAsync_InsideStep_ReturnsFalseAndCloses() {
            CentrifugeClient client = NewClient();
            Task<bool>? result = null;

            client.Loop.Invoke(() => result = client.CloseAsync(TimeSpan.FromSeconds(5)));

            Assert.NotNull(result);
            Assert.True(result!.IsCompletedSuccessfully);
            Assert.False(await result);
            Assert.Throws<ObjectDisposedException>(() => client.Connect());
        }

        /// <summary>Zero wait: the close still happens, and the result is false (Java close(0) returns false).</summary>
        [Fact]
        public async Task CloseAsync_WithZeroWait_ReturnsFalseAndCloses() {
            CentrifugeClient client = NewClient();

            bool completed = await client.CloseAsync(TimeSpan.Zero);

            Assert.False(completed);
            Assert.Throws<ObjectDisposedException>(() => client.Connect());
        }

        /// <summary>A close with a wait completes within it, and returns true.</summary>
        [Fact]
        public async Task CloseAsync_WithWait_ReturnsTrue() {
            CentrifugeClient client = NewClient();

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
        }

        /// <summary>A connected client closed with CloseAsync disconnects with code 0 and reason "client closed" (Java).</summary>
        [Fact]
        public async Task CloseFromConnected_RaisesDisconnectedZeroClientClosed() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));
            CentrifugeDisconnectedEventArgs args = await disconnected;

            Assert.True(completed);
            Assert.Equal(0, args.Code);
            Assert.Equal("client closed", args.Reason);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        /// <summary>A connected client with a server-side subscription raises ServerSubscribing before Disconnected code 0.</summary>
        [Fact]
        public async Task CloseFromConnected_RaisesSubscribingThenDisconnectedZero() {
            CentrifugeClient client = NewClient();
            ConnectResult reply = ConnectReplies.Connect();
            reply.Subs["srv"] = new SubscribeResult();
            Server.ConnectResult = reply;
            ConcurrentQueue<string> events = new ConcurrentQueue<string>();
            client.ServerSubscribing += (_, e) => events.Enqueue("subscribing:" + e.Channel);
            client.Disconnected += (_, e) => events.Enqueue("disconnected:" + e.Code);
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
            Assert.Equal(new[] { "subscribing:srv", "disconnected:0" }, events.ToArray());
        }

        /// <summary>After the close, the ping watchdog of the connection is cancelled: no reconnect and no further events.</summary>
        [Fact]
        public async Task Close_CancelsLiveTimers() {
            Server.ConnectResult = ConnectReplies.Connect(ping: 1);
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { MaxServerPingDelay = TimeSpan.FromMilliseconds(100) });
            ConcurrentQueue<string> events = new ConcurrentQueue<string>();
            client.Connecting += (_, _) => events.Enqueue("connecting");
            client.Disconnected += (_, _) => events.Enqueue("disconnected");
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));
            events.Clear();

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(1600));

            Assert.True(completed);
            Assert.Equal(new[] { "disconnected" }, events.ToArray());
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        /// <summary>DisposeAsync starts the close, so a later CloseAsync with a wait returns true.</summary>
        [Fact]
        public async Task DisposeAsync_ThenCloseAsync_ReturnsTrue() {
            CentrifugeClient client = NewClient();
            await client.DisposeAsync();

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
        }

        /// <summary>Dispose starts the close, so a later CloseAsync with a wait returns true.</summary>
        [Fact]
        public async Task Dispose_ThenCloseAsync_ReturnsTrue() {
            CentrifugeClient client = NewClient();
            client.Dispose();

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
        }

        /// <summary>A second close call with a wait waits for the close that already started, and returns true.</summary>
        [Fact]
        public async Task CloseAsync_SecondCall_WaitsForTheFirstClose() {
            CentrifugeClient client = NewClient();
            await client.CloseAsync(TimeSpan.Zero);

            bool completed = await client.CloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(completed);
        }
    }
}
