using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

// An unsubscribe that gets no reply. The timeout reply is handled on the loop, so the client reconnects with the
// unsubscribe error code.
namespace Centrifugal.Centrifuge.Tests {


    [Collection("Integration")]
    public class UnsubscribeTimeoutTests : IAsyncLifetime {
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


        [Fact]
        // Java: testUnsubscribeTimeoutRunsOnClientExecutor
        public async Task UnsubscribeTimeout_RunsOnLoop_Reconnects() {
            server.DeferCommand(cmd => cmd.Unsubscribe != null);
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions {
                Timeout = TimeSpan.FromMilliseconds(300),
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
            Assert.True(server.Received.Count(c => c.Connect != null) >= 2, "the client connects again after the timeout");
        }
    }
}
