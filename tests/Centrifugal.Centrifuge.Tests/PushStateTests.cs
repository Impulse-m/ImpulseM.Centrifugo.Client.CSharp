using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

// Unit-level routing of unsubscribe and publication pushes against the subscription state. Ported from PushStateTest.java (4 tests):
// testUnsubscribeErrorReplyReconnects -> UnsubscribeErrorReply_Reconnects,
// testPublicationAfterUnsubscribeIsNotDelivered -> PublicationAfterUnsubscribe_IsNotDelivered,
// testResubscribePushAfterUnsubscribeIsIgnored -> ResubscribePush_AfterUnsubscribe_IsIgnored,
// testServerSidePublicationIsNotTakenByUnsubscribedClientSideObject -> ServerSidePublication_NotTakenByUnsubscribedClientObject.
// A barrier (a publish, answered by the fake server) follows each push, so the test sees the push processed.
namespace Centrifugal.Centrifuge.Tests {


    [Collection("Integration")]
    public class PushStateTests : IAsyncLifetime {
        private static readonly TimeSpan wait = TimeSpan.FromSeconds(5);
        private readonly FakeCentrifugoServer server = new FakeCentrifugoServer();
        private CentrifugeClient? client;


        public async Task InitializeAsync() {
            await server.StartAsync();
        }


        public async Task DisposeAsync() {
            if (client != null) {
                await client.DisposeAsync();
            }
            await server.DisposeAsync();
        }


        private async Task Barrier() {
            await client!.PublishAsync("barrier", ReadOnlyMemory<byte>.Empty).WaitAsync(wait);
        }


        private int CountCommands(Func<Command, bool> matches) {
            return server.Received.Count(matches);
        }


        [Fact]
        // Java: testUnsubscribeErrorReplyReconnects
        public async Task UnsubscribeErrorReply_Reconnects() {
            server.OnCommand = cmd => {
                if (cmd.Unsubscribe == null) {
                    return null;
                }
                Reply error = server.ErrorReply(1000, "unsubscribe failed", false, null);
                error.Id = cmd.Id;
                return error;
            };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions {
                MinReconnectDelay = TimeSpan.FromMilliseconds(10),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(50)
            });
            // A reconnect goes through Connecting with the code of the disconnect (Disconnected is raised only when the client stops).
            TaskCompletionSource<int> reconnecting = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Connecting += (_, e) => {
                if (e.Code == CentrifugeConnectingCodes.UnsubscribeError) {
                    reconnecting.TrySetResult(e.Code);
                }
            };
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client.NewSubscription("ch");
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            sub.Unsubscribe();

            Assert.Equal(CentrifugeConnectingCodes.UnsubscribeError, await reconnecting.Task.WaitAsync(wait));
            await client.ReadyAsync().WaitAsync(wait);
            Assert.True(CountCommands(c => c.Connect != null) >= 2, "the client connects again after the unsubscribe error");
        }


        [Fact]
        // Java: testPublicationAfterUnsubscribeIsNotDelivered
        public async Task PublicationAfterUnsubscribe_IsNotDelivered() {
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client.NewSubscription("ch");
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            int delivered = 0;
            sub.Publication += (_, _) => Interlocked.Increment(ref delivered);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);
            sub.Unsubscribe();

            await server.PublishChannelAsync("ch", new byte[] { 1 });
            await Barrier();

            Assert.Equal(0, Volatile.Read(ref delivered));
            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);
        }


        [Fact]
        // Java: testResubscribePushAfterUnsubscribeIsIgnored
        public async Task ResubscribePush_AfterUnsubscribe_IsIgnored() {
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client.NewSubscription("ch");
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);
            sub.Unsubscribe();
            int subscribesBefore = CountCommands(c => c.Subscribe != null);

            await server.SendPushAsync(new Push {
                Channel = "ch",
                Unsubscribe = new Unsubscribe { Code = CentrifugeUnsubscribedCodes.StateInvalidated - 2, Reason = "resubscribe" }
            });
            await Barrier();

            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);
            Assert.Equal(subscribesBefore, CountCommands(c => c.Subscribe != null));
        }


        [Fact]
        // Java: testServerSidePublicationIsNotTakenByUnsubscribedClientSideObject
        public async Task ServerSidePublication_NotTakenByUnsubscribedClientObject() {
            server.ConnectResult = new ConnectResult {
                Client = "fake-client",
                Version = "0.0.0",
                Ping = 25,
                Subs = { ["ch"] = new SubscribeResult { Positioned = true, Offset = 0, Epoch = "e" } }
            };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            int serverPublished = 0;
            int clientPublished = 0;
            client.Publication += (_, _) => Interlocked.Increment(ref serverPublished);
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            CentrifugeSubscription sub = client.NewSubscription("ch");
            sub.Publication += (_, _) => Interlocked.Increment(ref clientPublished);

            await server.PublishChannelAsync("ch", new byte[] { 1 });
            await Barrier();

            Assert.Equal(1, Volatile.Read(ref serverPublished));
            Assert.Equal(0, Volatile.Read(ref clientPublished));
        }
    }
}
