using System;
using System.Threading.Tasks;
using Xunit;

// Removal of a subscription from its client. No server is needed: the client is never connected.
// Ported from RemoveSubscriptionTest.java (2 tests): testRemovingOldSubscriptionAgainKeepsNewOne -> RemoveOld_KeepsNewSubscription,
// testSubscribeOnRemovedSubscriptionThrows -> Subscribe_AfterRemoval_ThrowsObjectDisposed (the exception is ObjectDisposedException,
// the upstream C# type). From ClientTest.java, 2 of 10 tests: testRemoveSubscription and testNewSubscriptionAfterRemoval are
// ported as ChannelFree_AfterRemoval. The other 8 ClientTest tests (construction, endpoints, OkHttp) belong to other areas.
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
        // Java: testRemoveSubscription and testNewSubscriptionAfterRemoval (ClientTest.java)
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
