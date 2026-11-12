// The connect path: the subscribe that follows a reconnect's connect, the reconnect delay, a refresh with no token getter,
// the connect timeout, and the disconnect push codes. The reconnect and the connect timeout are upstream timers with no test
// seam, so the tests that wait for them use real time with the smallest option values (named in each test).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>The connect path: the subscribe written on reconnect, the token refresh with no getter, the connect timeout and the disconnect push codes.</summary>
    public sealed class ConnectPathTests : FakeServerTestBase {
        /// <summary>Reconnect delays of the upstream reconnect timer in real time: the smallest the options allow for a test.</summary>
        private static readonly TimeSpan fastReconnect = TimeSpan.FromMilliseconds(50);


        /// <summary>After a reconnect the subscribe of a client-side subscription follows the connect command of the same connection.</summary>
        [Fact]
        public async Task ResubscribeOnReconnect_SendsTheSubscribeAfterTheConnect() {
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { MinReconnectDelay = fastReconnect, MaxReconnectDelay = fastReconnect });
            CentrifugeSubscription sub = client.NewSubscription("rs");
            client.Connect();
            sub.Subscribe();
            await sub.ReadyAsync(TimeSpan.FromSeconds(10));
            ConcurrentQueue<string> order = new ConcurrentQueue<string>();
            TaskCompletionSource<bool> resubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.BeforeCommand = (Command command) => {
                if (command.Connect != null) {
                    order.Enqueue("connect");
                }
                if (command.Subscribe != null && command.Subscribe.Channel == "rs") {
                    order.Enqueue("subscribe");
                    resubscribed.TrySetResult(true);
                }
                return Task.CompletedTask;
            };

            Server.CloseConnection();
            await resubscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(new[] { "connect", "subscribe" }, order.ToArray());
        }


        /// <summary>
        /// A refresh is required (the connect was answered with code 109) and the client has no token getter: the client
        /// raises a configuration error and disconnects with code 1 and reason "unauthorized", without reconnecting.
        /// </summary>
        [Fact]
        public async Task RefreshRequiredWithoutGetter_RaisesConfigurationAndDisconnectsUnauthorized() {
            int connects = 0;
            Server.OnCommand = (Command command) => {
                if (command.Connect == null) {
                    return null;
                }
                connects++;
                return connects == 1
                    ? new Reply { Id = command.Id, Error = new Protocol.Error { Code = 109, Message = "token expired" } }
                    : null;
            };
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { MinReconnectDelay = fastReconnect, MaxReconnectDelay = fastReconnect });
            List<string> errorTypes = new List<string>();
            client.Error += (_, e) => errorTypes.Add(e.Type);
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);

            client.Connect();
            CentrifugeDisconnectedEventArgs args = await disconnected.WaitAsync(TimeSpan.FromSeconds(10));
            await client.LoopBarrierAsync();

            Assert.Contains("configuration", errorTypes);
            Assert.Equal(1, args.Code);
            Assert.Equal("unauthorized", args.Reason);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }


        /// <summary>
        /// A failed connect attempt is retried after the reconnect delay: the arrivals of the connect commands on the fake server
        /// are at least the delay apart. Real time: the reconnect timer is an upstream LoopTimer, so the delays are set to 150 ms and
        /// the attempts time out after 50 ms. Only the minimum wait is checked here.
        /// </summary>
        [Fact]
        public async Task Reconnect_WaitsTheReconnectDelayBetweenAttempts() {
            const int delayMs = 150;
            List<long> arrivals = new List<long>();
            TaskCompletionSource<bool> third = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.BeforeCommand = (Command command) => {
                if (command.Connect != null) {
                    lock (arrivals) {
                        arrivals.Add(Stopwatch.GetTimestamp());
                        if (arrivals.Count == 3) {
                            third.TrySetResult(true);
                        }
                    }
                }
                return Task.CompletedTask;
            };
            Server.OnCommand = (Command command) => command.Connect != null ? FakeCentrifugoServer.NoReply : null;
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Timeout = TimeSpan.FromMilliseconds(50),
                MinReconnectDelay = TimeSpan.FromMilliseconds(delayMs),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(delayMs),
            });

            client.Connect();
            await third.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long[] times;
            lock (arrivals) {
                times = arrivals.ToArray();
            }

            for (int i = 1; i < times.Length; i++) {
                double gapMs = (times[i] - times[i - 1]) * 1000.0 / Stopwatch.Frequency;
                Assert.True(gapMs >= delayMs, "the reconnect came " + gapMs + " ms after the previous attempt");
            }
        }


        /// <summary>
        /// A connect call that gets no reply in time fails the attempt: the client raises a connect error with the timeout
        /// and makes a new attempt. Real time: the call timer and the reconnect are upstream LoopTimers, with the smallest
        /// option values. A failed attempt raises no Connecting event (the state stays Connecting).
        /// </summary>
        [Fact]
        public async Task ConnectWithoutReply_TimesOut_AndMakesANewAttempt() {
            int connects = 0;
            TaskCompletionSource<bool> secondConnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.BeforeCommand = (Command command) => {
                if (command.Connect != null && Interlocked.Increment(ref connects) == 2) {
                    secondConnect.TrySetResult(true);
                }
                return Task.CompletedTask;
            };
            Server.OnCommand = (Command command) => command.Connect != null ? FakeCentrifugoServer.NoReply : null;
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Timeout = TimeSpan.FromMilliseconds(200),
                MinReconnectDelay = fastReconnect,
                MaxReconnectDelay = fastReconnect,
            });
            TaskCompletionSource<CentrifugeErrorEventArgs> failed =
                new TaskCompletionSource<CentrifugeErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Error += (_, e) => {
                if (e.Type == "connect") {
                    failed.TrySetResult(e);
                }
            };

            client.Connect();
            CentrifugeErrorEventArgs error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await secondConnect.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.IsType<CentrifugeTimeoutException>(error.Exception);
            Assert.Equal(CentrifugeClientState.Connecting, client.State);
        }


        /// <summary>
        /// A send issued while the connect is in flight is written after the connect reply, and its call completes.
        /// The fake server holds the connect until the test has issued the send.
        /// </summary>
        [Fact]
        public async Task SendWhileConnecting_IsWrittenAfterTheConnectAndCompletes() {
            TaskCompletionSource<bool> connectHeld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> sendReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.BeforeCommand = (Command command) => {
                if (command.Connect != null) {
                    connectHeld.TrySetResult(true);
                    return release.Task;
                }
                if (command.Send != null) {
                    sendReceived.TrySetResult(true);
                }
                return Task.CompletedTask;
            };
            CentrifugeClient client = NewClient();

            client.Connect();
            await connectHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task sent = client.SendAsync(new byte[] { 1 });
            release.TrySetResult(true);

            await sent.WaitAsync(TimeSpan.FromSeconds(10));
            await sendReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await client.ReadyAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        /// <summary>A disconnect push with a code that does not reconnect ends the session: Disconnected with that code, no reconnect.</summary>
        [Fact]
        public async Task DisconnectPush_PermanentCode_DisconnectsWithoutReconnect() {
            CentrifugeClient client = NewClient();
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);

            await Server.SendPushAsync(new Push { Disconnect = new Disconnect { Code = 3500, Reason = "gone" } });
            CentrifugeDisconnectedEventArgs args = await disconnected.WaitAsync(TimeSpan.FromSeconds(10));
            await client.LoopBarrierAsync();

            Assert.Equal(3500, args.Code);
            Assert.Equal("gone", args.Reason);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }


        /// <summary>A disconnect push with a code that reconnects moves the client to Connecting with that code.</summary>
        [Fact]
        public async Task DisconnectPush_ReconnectCode_RaisesConnectingWithTheCode() {
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { MinReconnectDelay = fastReconnect, MaxReconnectDelay = fastReconnect });
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(10));
            TaskCompletionSource<CentrifugeConnectingEventArgs> connecting =
                new TaskCompletionSource<CentrifugeConnectingEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Connecting += (_, e) => {
                if (e.Code == 4000) {
                    connecting.TrySetResult(e);
                }
            };

            await Server.SendPushAsync(new Push { Disconnect = new Disconnect { Code = 4000, Reason = "reconnect" } });
            CentrifugeConnectingEventArgs args = await connecting.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(4000, args.Code);
            Assert.Equal("reconnect", args.Reason);
        }
    }
}
