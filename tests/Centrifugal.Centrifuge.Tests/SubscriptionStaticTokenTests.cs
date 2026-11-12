using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

// A subscription with a static token: when the server expires it and there is no GetToken, the subscription unsubscribes
// as unauthorized.
// Not ported: testStateInvalidationKeepsStaticToken: it tests the connection token, not the subscription token.
// Not ported: testStateInvalidationReconnectsAnonymousClient: it tests the connection token, not the subscription token.
// Not ported: testSetTokenAfterExpiredTokenWithoutGetter: it tests the connection token, not the subscription token.
namespace Centrifugal.Centrifuge.Tests {


    [Collection("Integration")]
    public class SubscriptionStaticTokenTests : IAsyncLifetime {
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


        [Fact]
        // Java: testExpiredStaticSubscriptionTokenUnsubscribesUnauthorized
        public async Task Subscribe_109WithoutGetter_UnsubscribesUnauthorized() {
            server.OnCommand = cmd => {
                if (cmd.Subscribe == null) {
                    return null;
                }
                Reply error = server.ErrorReply(109, "token expired", false, null);
                error.Id = cmd.Id;
                return error;
            };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
            TaskCompletionSource<int> unsubscribed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            List<string> errorTypes = new List<string>();
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions { Token = "static-token" });
            sub.Unsubscribed += (_, e) => unsubscribed.TrySetResult(e.Code);
            sub.Error += (_, e) => errorTypes.Add(e.Type);

            sub.Subscribe();

            Assert.Equal(CentrifugeUnsubscribedCodes.Unauthorized, await unsubscribed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);
            Assert.Contains("configuration", errorTypes);
            Assert.Single(server.Received, c => c.Subscribe != null);
        }
    }
}
