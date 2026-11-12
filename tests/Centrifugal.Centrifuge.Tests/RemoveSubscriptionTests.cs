using System;
using System.Threading.Tasks;
using Xunit;

// Removal of a subscription from its client. No server is needed: the client is never connected. Subscribing a removed
// subscription throws ObjectDisposedException, the upstream C# type.
// The other 8 ClientTest tests cover the client (construction, endpoints and OkHttp), not the removal, and are not ported here.
// ChannelFree_AfterRemoval also covers the Java test testNewSubscriptionAfterRemoval.
namespace Centrifugal.Centrifuge.Tests {


    public class RemoveSubscriptionTests {
        private static CentrifugeClient NewClient() {
            return new CentrifugeClient("ws://127.0.0.1:1/connection/websocket", new CentrifugeClientOptions());
        }


        [Fact]
        // Java: testRemovingOldSubscriptionAgainKeepsNewOne
        public async Task RemoveOld_KeepsNewSubscription() {
            CentrifugeClient client = NewClient();
            CentrifugeSubscription oldSub = client.NewSubscription("ch");
            client.RemoveSubscription(oldSub);
            CentrifugeSubscription newSub = client.NewSubscription("ch");

            client.RemoveSubscription(oldSub);

            Assert.Same(newSub, client.GetSubscription("ch"));
            await client.DisposeAsync();
        }


        [Fact]
        // Java: testSubscribeOnRemovedSubscriptionThrows
        public async Task Subscribe_AfterRemoval_ThrowsObjectDisposed() {
            CentrifugeClient client = NewClient();
            CentrifugeSubscription sub = client.NewSubscription("ch");
            client.RemoveSubscription(sub);

            Assert.Throws<ObjectDisposedException>(() => sub.Subscribe());
            await client.DisposeAsync();
        }


        [Fact]
        // Java: testRemoveSubscription
        // The channel is free for a new subscription after the removal.
        public async Task ChannelFree_AfterRemoval() {
            CentrifugeClient client = NewClient();
            CentrifugeSubscription sub = client.NewSubscription("ch");

            client.RemoveSubscription(sub);

            Assert.Null(client.GetSubscription("ch"));
            CentrifugeSubscription fresh = client.NewSubscription("ch");
            Assert.NotSame(sub, fresh);
            await client.DisposeAsync();
        }
    }
}
