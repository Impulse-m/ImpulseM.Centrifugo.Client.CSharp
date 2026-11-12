using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

// The subscription token refresh timer and its refusals, against the fake server. The timer is the full TTL of the
// subscribe reply (one second here). Ported in part from SubscriptionRefreshRetryTest.java: the integration part of
// subscriptionRefreshWith103KeepsTheConnection (Refresh_103_KeepsSubscription) uses the same ttl 1.
// Gap tests (no Java test pins them): Refresh_OtherCode_Unsubscribes and Refresh_NoTokenGetter_KeepsUpstreamBehaviour.
// The retry of a refused token waits for the connection's retry delay (5 to 10 seconds), so it is covered by the unit
// tests in SubscriptionRefreshRetryTests instead.
namespace Centrifugal.Centrifuge.Tests {


    [Collection("Integration")]
    public class SubscriptionRefreshTests : IAsyncLifetime {
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


        private int CountRefreshes() {
            return server.Received.Count(c => c.SubRefresh != null);
        }


        private async Task BarrierAsync() {
            await client!.PublishAsync("barrier", ReadOnlyMemory<byte>.Empty).WaitAsync(wait);
        }


        [Fact]
        // Java: subscriptionRefreshWith103KeepsTheConnection (integration part, ttl 1)
        public async Task Refresh_103_KeepsSubscription() {
            server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                Reply error = server.ErrorReply(103, "permission denied", false, null);
                error.Id = cmd.Id;
                return error;
            };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<int> refreshError = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Error += (_, e) => {
                if (e.Type == "refresh") {
                    refreshError.TrySetResult(e.Code);
                }
            };
            sub.Subscribe();

            Assert.Equal(103, await refreshError.Task.WaitAsync(wait));
            await BarrierAsync();
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
            Assert.Equal(1, CountRefreshes());
        }


        [Fact]
        // A refresh refused with another code unsubscribes with that code and message.
        public async Task Refresh_OtherCode_Unsubscribes() {
            server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                Reply error = server.ErrorReply(3000, "forbidden", false, null);
                error.Id = cmd.Id;
                return error;
            };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<int> unsubscribed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Unsubscribed += (_, e) => unsubscribed.TrySetResult(e.Code);
            sub.Subscribe();

            Assert.Equal(3000, await unsubscribed.Task.WaitAsync(wait));
        }


        [Fact]
        // A refresh without a token getter is a configuration error, then unauthorized.
        public async Task Refresh_NoTokenGetter_KeepsUpstreamBehaviour() {
            server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<int> unsubscribed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions { Token = "static" });
            sub.Unsubscribed += (_, e) => unsubscribed.TrySetResult(e.Code);
            sub.Subscribe();

            Assert.Equal(CentrifugeUnsubscribedCodes.Unauthorized, await unsubscribed.Task.WaitAsync(wait));
        }


        [Fact]
        // A token getter that throws during a refresh reports a refreshToken error and keeps the subscription subscribed.
        public async Task Refresh_TokenGetterFails_ReportsAndKeepsSubscription() {
            server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            int calls = 0;
            TaskCompletionSource<string> errorType = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions {
                GetToken = _ => {
                    if (Interlocked.Increment(ref calls) == 2) {
                        throw new InvalidOperationException("token failed");
                    }
                    return Task.FromResult("t1");
                }
            });
            sub.Error += (_, e) => {
                if (e.Type == "refreshToken") {
                    errorType.TrySetResult(e.Type);
                }
            };
            sub.Subscribe();

            Assert.Equal("refreshToken", await errorType.Task.WaitAsync(wait));
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
            Assert.Equal(0, CountRefreshes());
        }


        [Fact]
        // A refresh error is reported before the subscription classifies it: the error event comes before the unsubscribe.
        public async Task Refresh_ErrorIsReportedBeforeClassification() {
            server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                Reply error = server.ErrorReply(3000, "forbidden", false, null);
                error.Id = cmd.Id;
                return error;
            };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            List<string> order = new List<string>();
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Error += (_, e) => {
                if (e.Type == "refresh") {
                    order.Add("error");
                }
            };
            sub.Unsubscribed += (_, _) => {
                order.Add("unsubscribed");
                done.TrySetResult(true);
            };
            sub.Subscribe();

            await done.Task.WaitAsync(wait);
            Assert.Equal(new List<string> { "error", "unsubscribed" }, order);
        }


        [Fact]
        // Java: subscriptionRefreshDoesNotResendARefusedToken (integration part). A token refused with 109 is retried after the
        // connection's retry delay of 5 to 10 seconds. The getter returns the same token, so it is not sent again. The test waits
        // past the longest retry delay.
        public async Task Refresh_Refused109_SameTokenNotSentAgain() {
            server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                Reply error = server.ErrorReply(109, "token expired", false, null);
                error.Id = cmd.Id;
                return error;
            };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("same-token") });
            sub.Subscribe();

            await Task.Delay(TimeSpan.FromSeconds(12));
            await BarrierAsync();

            Assert.Equal(1, CountRefreshes());
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
        }


        [Fact]
        // A refresh reply that arrives after the subscription unsubscribed is dropped: no refresh is sent after it.
        public async Task Refresh_ReplyAfterUnsubscribe_IsDropped() {
            server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            TaskCompletionSource<Command> held = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OnCommand = cmd => {
                if (cmd.SubRefresh == null) {
                    return null;
                }
                held.TrySetResult(cmd);
                return FakeCentrifugoServer.NoReply;
            };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Subscribe();
            Command refresh = await held.Task.WaitAsync(TimeSpan.FromSeconds(10));

            sub.Unsubscribe();
            await server.SendReplyAsync(new Reply { Id = refresh.Id, SubRefresh = new SubRefreshResult { Expires = true, Ttl = 1 } });
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            await BarrierAsync();

            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);
            Assert.Equal(1, CountRefreshes());
        }


        [Fact]
        // A refresh accepted without expires arms no further refresh.
        public async Task Refresh_AcceptedWithoutExpires_ArmsNothing() {
            server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            server.OnCommand = cmd => cmd.SubRefresh == null ? null : new Reply { Id = cmd.Id, SubRefresh = new SubRefreshResult() };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Subscribe();

            await Task.Delay(TimeSpan.FromMilliseconds(2500));

            Assert.Equal(1, CountRefreshes());
        }


        [Fact]
        // One refresh timer at a time: each accepted refresh arms one timer, so the refreshes come once per TTL.
        public async Task Refresh_ReplacesTimer_NoDuplicate() {
            server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 1 };
            server.OnCommand = cmd => cmd.SubRefresh == null ? null : new Reply { Id = cmd.Id, SubRefresh = new SubRefreshResult { Expires = true, Ttl = 1 } };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Subscribe();

            await Task.Delay(TimeSpan.FromMilliseconds(2300));

            Assert.Equal(2, CountRefreshes());
        }


        [Fact]
        // Leaving SUBSCRIBED drops the refresh timer: the resubscribe reply has no expires, so no refresh is sent.
        public async Task Leaving_Subscribed_DropsRefreshTimer() {
            int subscribes = 0;
            server.OnSubscribe = (_, _) => Interlocked.Increment(ref subscribes) == 1
                ? new SubscribeResult { Expires = true, Ttl = 1 }
                : new SubscribeResult();
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<bool> firstSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> secondSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client.NewSubscription("ch", new CentrifugeSubscriptionOptions { GetToken = _ => Task.FromResult("t1") });
            sub.Subscribed += (_, _) => {
                if (!firstSubscribed.Task.IsCompleted) {
                    firstSubscribed.TrySetResult(true);
                } else {
                    secondSubscribed.TrySetResult(true);
                }
            };
            sub.Subscribe();
            await firstSubscribed.Task.WaitAsync(wait);

            await server.SendPushAsync(new Push {
                Channel = "ch",
                Unsubscribe = new Unsubscribe { Code = 2500, Reason = "resubscribe" }
            });
            await secondSubscribed.Task.WaitAsync(wait);
            await Task.Delay(TimeSpan.FromMilliseconds(1500));

            Assert.Equal(0, CountRefreshes());
        }


        [Fact]
        // The refresh is due at the full TTL in milliseconds, capped at int.MaxValue.
        public void TtlToFullMilliseconds_IsFullTtlCapped() {
            Assert.Equal(1000, CentrifugeSubscription.TtlToFullMilliseconds(1));
            Assert.Equal(int.MaxValue, CentrifugeSubscription.TtlToFullMilliseconds(uint.MaxValue));
        }
    }
}
