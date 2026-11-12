using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;
using Xunit;

// Subscribe replies, token fetches, state getter, publish waits and unsubscribe of a subscription. No Java test pins these
// rules, so each expectation comes from the subscription rules of the port brief. The resubscribe retries and the publish
// debounce windows run on the fake clock: a test fires a retry with Advance, after a barrier (a publish that the fake server
// answers) that the failed step came before. The upstream reconnect and timeout timers stay real, and the tests that use
// them are the ones with a Timeout or a reconnect option.
namespace Centrifugal.Centrifuge.Tests {


    [Collection("Integration")]
    public class SubscribeFlowTests : FakeServerTestBase {
        private static readonly TimeSpan wait = TimeSpan.FromSeconds(5);
        private readonly CommandArrivals subscribes = new CommandArrivals(wait);
        private CentrifugeClient? client;
        private FakeStepTimerFactory timers = null!;


        private FakeCentrifugoServer server => Server;


        public override async Task InitializeAsync() {
            await base.InitializeAsync();
            server.BeforeCommand = cmd => {
                if (cmd.Subscribe != null) {
                    subscribes.Add();
                }
                return Task.CompletedTask;
            };
        }


        private static CentrifugeClientOptions FastOptions() {
            return new CentrifugeClientOptions {
                MinReconnectDelay = TimeSpan.FromMilliseconds(10),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(50)
            };
        }


        private static CentrifugeSubscriptionOptions FastResubscribe(CentrifugeSubscriptionOptions options) {
            options.MinResubscribeDelay = TimeSpan.FromMilliseconds(10);
            options.MaxResubscribeDelay = TimeSpan.FromMilliseconds(50);
            return options;
        }


        private static Reply ErrorFor(FakeCentrifugoServer fake, Command cmd, uint code, bool temporary) {
            Reply error = fake.ErrorReply(code, "subscribe failed", temporary, null);
            error.Id = cmd.Id;
            return error;
        }


        private async Task ConnectAsync() {
            client = NewClientWithTestClock(out timers, FastOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
        }


        private async Task Barrier() {
            await client!.PublishAsync("barrier", ReadOnlyMemory<byte>.Empty).WaitAsync(wait);
        }


        // Fires the retry that the last failed step armed. The barrier comes first, so that step is finished.
        private async Task FireRetryAsync() {
            await Barrier();
            timers.Advance(LastRequestedDelay());
        }


        private int LastRequestedDelay() {
            IReadOnlyList<int> delays = timers.RequestedDelays;
            return delays[delays.Count - 1];
        }


        private int CountCommands(Func<Command, bool> matches) {
            return server.Received.Count(matches);
        }


        private SubscribeRequest SubscribeOn(string channel) {
            return server.Received.Where(c => c.Subscribe != null && c.Subscribe.Channel == channel).Select(c => c.Subscribe).Last();
        }


        [Fact]
        // Code 112 without a state getter is not special: it unsubscribes with its code and reports no subscribe error.
        public async Task SubscribeError_112WithoutGetter_Unsubscribes() {
            server.OnCommand = cmd => cmd.Subscribe != null ? ErrorFor(server, cmd, 112, false) : null;
            await ConnectAsync();
            TaskCompletionSource<int> unsubscribed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Unsubscribed += (_, e) => unsubscribed.TrySetResult(e.Code);

            sub.Subscribe();

            Assert.Equal(112, await unsubscribed.Task.WaitAsync(wait));
            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);
        }


        [Fact]
        // A temporary subscribe error resubscribes after the backoff.
        public async Task SubscribeError_Temporary_Resubscribes() {
            server.OnCommand = cmd => {
                if (cmd.Subscribe == null) {
                    return null;
                }
                return subscribes.Count == 1 ? ErrorFor(server, cmd, 100, true) : null;
            };
            await ConnectAsync();
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch", FastResubscribe(new CentrifugeSubscriptionOptions()));
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);

            sub.Subscribe();
            await subscribes.Reached(1);
            await FireRetryAsync();

            await subscribed.Task.WaitAsync(wait);
            Assert.Equal(2, CountCommands(c => c.Subscribe != null));
        }


        [Fact]
        // A permanent subscribe error unsubscribes with its code and sends no unsubscribe.
        public async Task SubscribeError_Permanent_Unsubscribes() {
            server.OnCommand = cmd => cmd.Subscribe != null ? ErrorFor(server, cmd, 3000, false) : null;
            await ConnectAsync();
            TaskCompletionSource<int> unsubscribed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Unsubscribed += (_, e) => unsubscribed.TrySetResult(e.Code);

            sub.Subscribe();

            Assert.Equal(3000, await unsubscribed.Task.WaitAsync(wait));
            await Barrier();
            Assert.Equal(0, CountCommands(c => c.Unsubscribe != null));
        }


        [Fact]
        // Code 109 with a token getter clears the token, and the resubscribe fetches a fresh one.
        public async Task SubscribeError_109WithGetter_ResubscribesWithFreshToken() {
            int tokens = 0;
            server.OnCommand = cmd => {
                if (cmd.Subscribe == null) {
                    return null;
                }
                return subscribes.Count == 1 ? ErrorFor(server, cmd, 109, false) : null;
            };
            await ConnectAsync();
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch", FastResubscribe(new CentrifugeSubscriptionOptions {
                GetToken = _ => Task.FromResult($"t{Interlocked.Increment(ref tokens)}")
            }));
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);

            sub.Subscribe();
            await subscribes.Reached(1);
            await FireRetryAsync();

            await subscribed.Task.WaitAsync(wait);
            Assert.Equal("t2", server.LastSubscribe()!.Token);
        }


        [Fact]
        // A token getter that returns nothing ends the subscription, and nothing was sent, so nothing is unsubscribed.
        public async Task Subscribe_TokenFetch_NullTokenEndsWithoutUnsubscribe() {
            await ConnectAsync();
            TaskCompletionSource<int> unsubscribed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch", new CentrifugeSubscriptionOptions {
                GetToken = _ => Task.FromResult<string>(null!)
            });
            sub.Unsubscribed += (_, e) => unsubscribed.TrySetResult(e.Code);

            sub.Subscribe();

            Assert.Equal(CentrifugeUnsubscribedCodes.Unauthorized, await unsubscribed.Task.WaitAsync(wait));
            await Barrier();
            Assert.Equal(0, CountCommands(c => c.Subscribe != null));
            Assert.Equal(0, CountCommands(c => c.Unsubscribe != null));
        }


        [Fact]
        // A token getter that throws is delivered as its error, and the subscribe is retried.
        public async Task TokenGetter_Throws_DeliveredAsError() {
            int calls = 0;
            await ConnectAsync();
            TaskCompletionSource<string> errorType = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch", FastResubscribe(new CentrifugeSubscriptionOptions {
                GetToken = _ => {
                    if (Interlocked.Increment(ref calls) == 1) {
                        throw new InvalidOperationException("getter failed");
                    }
                    return Task.FromResult("t2");
                }
            }));
            sub.Error += (_, e) => errorType.TrySetResult(e.Type);
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);

            sub.Subscribe();

            Assert.Equal("subscribeToken", await errorType.Task.WaitAsync(wait));
            await FireRetryAsync();
            await subscribed.Task.WaitAsync(wait);
            Assert.Equal("t2", server.LastSubscribe()!.Token);
        }


        [Fact]
        // The subscribed event comes before the publications of the subscribe reply.
        public async Task MoveToSubscribed_EventThenReplayedPublications() {
            server.OnSubscribe = (_, _) => {
                SubscribeResult result = new SubscribeResult();
                result.Publications.Add(new Publication { Data = ByteString.CopyFromUtf8("replayed") });
                return result;
            };
            await ConnectAsync();
            List<string> order = new List<string>();
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Subscribed += (_, _) => order.Add("subscribed");
            sub.Publication += (_, _) => {
                order.Add("publication");
                done.TrySetResult(true);
            };

            sub.Subscribe();

            await done.Task.WaitAsync(wait);
            Assert.Equal(new List<string> { "subscribed", "publication" }, order);
        }


        [Fact]
        // Unsubscribe on an unsubscribed subscription does nothing and sends nothing.
        public async Task Unsubscribe_WhenUnsubscribed_IsNoOp() {
            await ConnectAsync();
            int unsubscribed = 0;
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Unsubscribed += (_, _) => Interlocked.Increment(ref unsubscribed);

            sub.Unsubscribe();
            await Barrier();

            Assert.Equal(0, Volatile.Read(ref unsubscribed));
            Assert.Equal(0, CountCommands(c => c.Unsubscribe != null));
        }


        [Fact]
        // An unsubscribe push with code 2500 and up resubscribes at once when connected, without the backoff.
        public async Task UnsubscribePush2500_ResubscribesWithoutBackoff() {
            await ConnectAsync();
            TaskCompletionSource<bool> firstSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> secondSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch", new CentrifugeSubscriptionOptions {
                MinResubscribeDelay = TimeSpan.FromMinutes(1),
                MaxResubscribeDelay = TimeSpan.FromMinutes(1)
            });
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
            Assert.Equal(2, CountCommands(c => c.Subscribe != null));
        }


        [Fact]
        // The subscribe flag: channel compaction always, and rejection of unrecovered positions with a state getter.
        public async Task SubscribeRequest_FlagBits() {
            await ConnectAsync();
            CentrifugeSubscription plain = client!.NewSubscription("plain");
            plain.Subscribe();
            await Barrier();
            Assert.Equal(CentrifugeSubscriptionFlags.ChannelCompaction, SubscribeOn("plain").Flag);

            CentrifugeSubscription withState = client.NewSubscription("state", new CentrifugeSubscriptionOptions {
                GetState = _ => Task.FromResult(new CentrifugeStreamPosition(0, string.Empty))
            });
            withState.Subscribe();
            await Barrier();

            Assert.Equal(CentrifugeSubscriptionFlags.ChannelCompaction | CentrifugeSubscriptionFlags.RejectUnrecovered, SubscribeOn("state").Flag);
        }


        [Fact]
        // The subscribe request carries the token and the data of the subscription.
        public async Task SubscribeRequest_TokenAndData() {
            await ConnectAsync();
            CentrifugeSubscription sub = client!.NewSubscription("ch", new CentrifugeSubscriptionOptions {
                Token = "tok",
                Data = new byte[] { 1, 2, 3 }
            });

            sub.Subscribe();
            await Barrier();

            SubscribeRequest request = server.LastSubscribe()!;
            Assert.Equal("tok", request.Token);
            Assert.Equal(new byte[] { 1, 2, 3 }, request.Data.ToByteArray());
        }


        [Fact]
        // A publish waits for the subscription, and fails with the client timeout. The timeout is the upstream one, a real timer.
        public async Task PublishAsync_NotSubscribed_TimesOut() {
            // The subscribe is held past the client timeout: the publish waits for SUBSCRIBED and fails with the timeout. The
            // subscribe times out while connected too, so the client reconnects (see SubscribeTimeout_WhileConnected_ReconnectsWithCode3).
            server.DeferCommand(cmd => cmd.Subscribe != null);
            client = NewClientWithTestClock(out timers, new CentrifugeClientOptions { Timeout = TimeSpan.FromMilliseconds(200) });
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            CentrifugeSubscription sub = client.NewSubscription("ch");
            sub.Subscribe();
            await server.AwaitHeldAsync();

            await Assert.ThrowsAsync<CentrifugeTimeoutException>(() => sub.PublishAsync(new byte[] { 1 }).WaitAsync(wait));
        }


        [Fact]
        // History waits for the subscription, and fails with the unsubscribed state error on unsubscribe.
        public async Task History_WaitsForSubscribed_FailsOnUnsubscribe() {
            server.DeferCommand(cmd => cmd.Subscribe != null);
            await ConnectAsync();
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Subscribe();
            await server.AwaitHeldAsync();
            Task history = sub.HistoryAsync();

            sub.Unsubscribe();

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => history.WaitAsync(wait));
            Assert.Equal(CentrifugeErrorCodes.SubscriptionUnsubscribed, error.Code);
        }


        [Fact]
        // A publish inside the window of the subscribe reply is sent at once; the rest waits for the window end, which the
        // test never reaches, so it is never sent.
        public async Task PublishAsync_WindowOn_UsesDebounce() {
            server.OnSubscribe = (_, _) => new SubscribeResult { PublishDebounce = 60000 };
            await ConnectAsync();
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);

            await sub.PublishAsync(new byte[] { 1 }).WaitAsync(wait);
            await sub.PublishAsync(new byte[] { 2 }).WaitAsync(wait);
            await Barrier();

            Assert.Equal(1, CountCommands(c => c.Publish != null && c.Publish.Channel == "ch"));
            sub.Unsubscribe();
        }


        [Fact]
        // A publish made while the subscribe is in flight waits for SUBSCRIBED, then is sent.
        public async Task PublishAsync_NotSubscribed_WaitsForSubscribed() {
            server.DeferCommand(cmd => cmd.Subscribe != null);
            await ConnectAsync();
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Subscribe();
            await server.AwaitHeldAsync();
            Task publish = sub.PublishAsync(new byte[] { 1 }).WaitAsync(wait);

            server.ReleaseDeferredMatching(cmd => cmd.Subscribe != null);

            await publish;
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
            Assert.Equal(1, CountCommands(c => c.Publish != null && c.Publish.Channel == "ch"));
        }


        [Fact]
        // A publish waiting for the subscribe fails with the unsubscribed state error when the subscription unsubscribes.
        public async Task PublishAsync_WaitingForSubscribe_FailsOnUnsubscribe() {
            server.DeferCommand(cmd => cmd.Subscribe != null);
            await ConnectAsync();
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Subscribe();
            await server.AwaitHeldAsync();
            Task publish = sub.PublishAsync(new byte[] { 1 });

            sub.Unsubscribe();

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => publish.WaitAsync(wait));
            Assert.Equal(CentrifugeErrorCodes.SubscriptionUnsubscribed, error.Code);
            Assert.Equal(0, CountCommands(c => c.Publish != null));
        }


        [Fact]
        // Unsubscribe drops the pending publish of the window: when the window ends, nothing more is sent.
        public async Task Unsubscribe_DropsPendingDebouncedPublish() {
            server.OnSubscribe = (_, _) => new SubscribeResult { PublishDebounce = 200 };
            await ConnectAsync();
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);
            await sub.PublishAsync(new byte[] { 1 }).WaitAsync(wait);
            await sub.PublishAsync(new byte[] { 2 }).WaitAsync(wait);

            sub.Unsubscribe();
            // Past the window end: a window that was not cancelled would send the pending value now.
            timers.Advance(500);
            await Barrier();

            Assert.Equal(1, CountCommands(c => c.Publish != null && c.Publish.Channel == "ch"));
        }


        [Fact]
        // A transport loss drops the pending publish of the window: the window end after the resubscribe sends nothing.
        public async Task TransportLoss_DropsPendingDebouncedPublish() {
            server.OnSubscribe = (_, _) => new SubscribeResult { PublishDebounce = 200 };
            await ConnectAsync();
            TaskCompletionSource<bool> firstSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> secondSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Subscribed += (_, _) => {
                if (!firstSubscribed.Task.IsCompleted) {
                    firstSubscribed.TrySetResult(true);
                } else {
                    secondSubscribed.TrySetResult(true);
                }
            };
            sub.Subscribe();
            await firstSubscribed.Task.WaitAsync(wait);
            await sub.PublishAsync(new byte[] { 1 }).WaitAsync(wait);
            await sub.PublishAsync(new byte[] { 2 }).WaitAsync(wait);

            server.CloseConnection();
            await secondSubscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Past the window end: a window that survived the transport loss would send the pending value now.
            timers.Advance(500);
            await Barrier();

            Assert.Equal(1, CountCommands(c => c.Publish != null && c.Publish.Channel == "ch"));
            sub.Unsubscribe();
        }


        [Fact]
        // Code 112 with a state getter loads the state again and resubscribes, and no error event is raised.
        public async Task SubscribeError_112WithStateGetter_LoadsStateAgainWithoutEvent() {
            server.OnCommand = cmd => cmd.Subscribe != null && subscribes.Count == 1 ? ErrorFor(server, cmd, 112, false) : null;
            await ConnectAsync();
            int stateCalls = 0;
            int errors = 0;
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch", FastResubscribe(new CentrifugeSubscriptionOptions {
                GetState = _ => {
                    Interlocked.Increment(ref stateCalls);
                    return Task.FromResult(new CentrifugeStreamPosition(0, string.Empty));
                }
            }));
            sub.Error += (_, _) => Interlocked.Increment(ref errors);
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);

            sub.Subscribe();
            await subscribes.Reached(1);
            await FireRetryAsync();

            await subscribed.Task.WaitAsync(wait);
            Assert.Equal(2, Volatile.Read(ref stateCalls));
            Assert.Equal(0, Volatile.Read(ref errors));
        }


        [Fact]
        // A state getter that throws reports a getState error, then the subscribe is retried with the state loaded again.
        public async Task StateGetter_Throws_ReportsAndResubscribes() {
            int calls = 0;
            await ConnectAsync();
            TaskCompletionSource<string> errorType = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch", FastResubscribe(new CentrifugeSubscriptionOptions {
                GetState = _ => {
                    if (Interlocked.Increment(ref calls) == 1) {
                        throw new InvalidOperationException("state failed");
                    }
                    return Task.FromResult(new CentrifugeStreamPosition(0, string.Empty));
                }
            }));
            sub.Error += (_, e) => errorType.TrySetResult(e.Type);
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);

            sub.Subscribe();

            Assert.Equal("getState", await errorType.Task.WaitAsync(wait));
            await FireRetryAsync();
            await subscribed.Task.WaitAsync(wait);
            Assert.Equal(2, Volatile.Read(ref calls));
        }


        [Fact]
        // A temporary subscribe error waits for its backoff across a reconnect: the connect does not resubscribe at once.
        public async Task Resubscribe_WaitsForBackoffAcrossReconnect() {
            server.OnCommand = cmd => cmd.Subscribe != null && subscribes.Count == 1 ? ErrorFor(server, cmd, 100, true) : null;
            await ConnectAsync();
            TaskCompletionSource<bool> errored = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> reconnecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch", new CentrifugeSubscriptionOptions {
                MinResubscribeDelay = TimeSpan.FromMinutes(1),
                MaxResubscribeDelay = TimeSpan.FromMinutes(1)
            });
            sub.Error += (_, e) => {
                if (e.Type == "subscribe") {
                    errored.TrySetResult(true);
                }
            };
            client!.Connecting += (_, _) => reconnecting.TrySetResult(true);
            sub.Subscribe();
            await errored.Task.WaitAsync(wait);

            server.CloseConnection();
            await reconnecting.Task.WaitAsync(wait);
            await client!.ReadyAsync().WaitAsync(wait);
            await Barrier();

            Assert.Equal(1, CountCommands(c => c.Subscribe != null));
        }


        [Fact]
        // A subscribe that gets no reply within the client timeout, while connected, reconnects the client with code 3.
        // The timeout and the reconnect are the upstream timers, so this test uses real time with the smallest options.
        public async Task SubscribeTimeout_WhileConnected_ReconnectsWithCode3() {
            server.DeferCommand(cmd => cmd.Subscribe != null);
            client = NewClientWithTestClock(out timers, new CentrifugeClientOptions {
                Timeout = TimeSpan.FromMilliseconds(200),
                MinReconnectDelay = TimeSpan.FromMilliseconds(10),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(50)
            });
            TaskCompletionSource<int> reconnecting = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Connecting += (_, e) => {
                if (e.Code == CentrifugeConnectingCodes.SubscribeTimeout) {
                    reconnecting.TrySetResult(e.Code);
                }
            };
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            CentrifugeSubscription sub = client.NewSubscription("ch");

            sub.Subscribe();

            Assert.Equal(CentrifugeConnectingCodes.SubscribeTimeout, await reconnecting.Task.WaitAsync(wait));
            await client.ReadyAsync().WaitAsync(wait);
            Assert.True(server.Received.Count(c => c.Connect != null) >= 2, "the client connects again after the subscribe timeout");
        }


        [Fact]
        // A server unsubscribe with code 2500 raises Subscribing once, and the resubscribe is sent at once without a backoff.
        // The window of the step itself sends nothing; the resubscribe that follows is the one R60 requires.
        public async Task MoveToSubscribing_EventOnceNoSend() {
            await ConnectAsync();
            int subscribingEvents = 0;
            int subscribingCode = 0;
            TaskCompletionSource<bool> firstSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> secondSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client!.NewSubscription("ch", new CentrifugeSubscriptionOptions {
                MinResubscribeDelay = TimeSpan.FromMinutes(1),
                MaxResubscribeDelay = TimeSpan.FromMinutes(1)
            });
            sub.Subscribing += (_, e) => {
                subscribingEvents++;
                subscribingCode = e.Code;
            };
            sub.Subscribed += (_, _) => {
                if (!firstSubscribed.Task.IsCompleted) {
                    firstSubscribed.TrySetResult(true);
                } else {
                    secondSubscribed.TrySetResult(true);
                }
            };
            sub.Subscribe();
            await firstSubscribed.Task.WaitAsync(wait);
            int eventsBefore = subscribingEvents;

            await server.SendPushAsync(new Push {
                Channel = "ch",
                Unsubscribe = new Unsubscribe { Code = 2500, Reason = "resubscribe" }
            });
            await secondSubscribed.Task.WaitAsync(wait);

            Assert.Equal(eventsBefore + 1, subscribingEvents);
            Assert.Equal(2500, subscribingCode);
            Assert.Equal(2, CountCommands(c => c.Subscribe != null));
        }


        [Fact]
        // The channel id of a subscribe is the id of its last subscribe: a push with an earlier id is dropped.
        public async Task PushId_RoutesById_AfterResubscribe() {
            int subscribeReplies = 0;
            server.OnSubscribe = (_, _) => new SubscribeResult { Id = Interlocked.Increment(ref subscribeReplies) == 1 ? 7 : 8 };
            await ConnectAsync();
            TaskCompletionSource<bool> firstSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> secondSubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> fresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            List<string> received = new List<string>();
            CentrifugeSubscription sub = client!.NewSubscription("ch");
            sub.Subscribed += (_, _) => {
                if (!firstSubscribed.Task.IsCompleted) {
                    firstSubscribed.TrySetResult(true);
                } else {
                    secondSubscribed.TrySetResult(true);
                }
            };
            sub.Publication += (_, e) => {
                string text = Encoding.UTF8.GetString(e.Data.Span);
                received.Add(text);
                if (text == "fresh") {
                    fresh.TrySetResult(true);
                }
            };
            sub.Subscribe();
            await firstSubscribed.Task.WaitAsync(wait);
            await server.SendPushAsync(new Push {
                Channel = "ch",
                Unsubscribe = new Unsubscribe { Code = 2500, Reason = "resubscribe" }
            });
            await secondSubscribed.Task.WaitAsync(wait);

            await server.PublishIdAsync(7, Encoding.UTF8.GetBytes("stale"));
            await server.PublishIdAsync(8, Encoding.UTF8.GetBytes("fresh"));
            await fresh.Task.WaitAsync(wait);

            Assert.Equal(new List<string> { "fresh" }, received);
        }
    }
}
