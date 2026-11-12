// Port status of the Java tests of this file (connection-identity-and-capabilities, S02, AuthTest.java):
// - authEventFiresOnConnectAndWhenTheIdentityChanges: ported, connect part. The identity changes come from the
//   reconnect replies of the fake server. The refresh steps of the Java test need the refresh path (not in this slice).
// - authEventsFireOnTheClientExecutor: ported, connect part. The refresh part needs the refresh path (not in this slice).
// - connectedEventCarriesTheIdentity: ported, renamed Connected_WithIdentity_CarriesUserAndAuthClass.
// - legacyServerReportsEmptyIdentity: ported, connect part. The refresh part needs the refresh path (not in this slice).
// - refreshIsAnsweredAfterTheAuthListenerThrowsOnATimerRefresh: not in this file. It needs the timer refresh (not in this slice).
// - The connect-path handler failure (disconnect with BadProtocol) waits for the connect notice list: not in this file.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the auth event and the identity of the Connected event against the fake server.</summary>
    [Collection("Integration")]
    public sealed class AuthEventTests : FakeServerTestBase {
        /// <summary>The entries of one test's auth events, with waits on the number of entries.</summary>
        private sealed class AuthLog {
            private readonly object gate = new object();
            private readonly List<string> entries = new List<string>();
            private readonly List<(int Count, TaskCompletionSource<bool> Done)> waiters = new List<(int Count, TaskCompletionSource<bool> Done)>();


            /// <summary>The entries so far, in order.</summary>
            public IReadOnlyList<string> Entries {
                get {
                    lock (gate) {
                        return new List<string>(entries);
                    }
                }
            }


            /// <summary>Adds an entry and completes the waits that it satisfies.</summary>
            /// <param name="entry">The entry.</param>
            public void Add(string entry) {
                lock (gate) {
                    entries.Add(entry);
                    for (int i = waiters.Count - 1; i >= 0; i--) {
                        if (entries.Count >= waiters[i].Count) {
                            waiters[i].Done.TrySetResult(true);
                            waiters.RemoveAt(i);
                        }
                    }
                }
            }


            /// <summary>Completes when at least count entries exist, or fails after five seconds.</summary>
            /// <param name="count">The number of entries to wait for.</param>
            public Task WhenCountAsync(int count) {
                TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (gate) {
                    if (entries.Count >= count) {
                        done.TrySetResult(true);
                    } else {
                        waiters.Add((count, done));
                    }
                }
                return done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }


        // Java: AuthTest.authEventFiresOnConnectAndWhenTheIdentityChanges (connect part).
        /// <summary>The auth event fires on every connect, also when the identity did not change, and carries each connect's identity.</summary>
        [Fact]
        public async Task Auth_OnConnectAndReconnect_RaisesOnEveryConnectWithTheIdentity() {
            Server.ConnectAs("u1", "authenticated");
            CentrifugeClient client = NewClient();
            AuthLog log = new AuthLog();
            client.Auth += (object? sender, CentrifugeAuthEventArgs args) => log.Add(args.User + "/" + args.AuthClass);

            client.Connect();
            await log.WhenCountAsync(1);
            Assert.Equal(new[] { "u1/authenticated" }, log.Entries);

            Server.CloseConnection();
            await log.WhenCountAsync(2);
            Assert.Equal(new[] { "u1/authenticated", "u1/authenticated" }, log.Entries);

            Server.ConnectAs("u2", "bot");
            Server.CloseConnection();
            await log.WhenCountAsync(3);
            Assert.Equal(new[] { "u1/authenticated", "u1/authenticated", "u2/bot" }, log.Entries);
        }


        // Java: AuthTest.authEventsFireOnTheClientExecutor (connect part).
        /// <summary>The auth event is raised inside a loop step, on the client executor.</summary>
        [Fact]
        public async Task Auth_OnConnect_RunsInsideTheLoopStep() {
            Server.ConnectAs("u1", "authenticated");
            CentrifugeClient client = NewClient();
            ConcurrentQueue<bool> inStep = new ConcurrentQueue<bool>();
            AuthLog log = new AuthLog();
            client.Auth += (object? sender, CentrifugeAuthEventArgs args) => {
                inStep.Enqueue(client.Loop.InStep);
                log.Add(args.User);
            };

            client.Connect();
            await log.WhenCountAsync(1);

            Assert.Equal(new[] { true }, inStep.ToArray());
        }


        // Java: AuthTest.connectedEventCarriesTheIdentity (renamed).
        /// <summary>The Connected event carries the user and the auth class of the connect reply.</summary>
        [Fact]
        public async Task Connected_WithIdentity_CarriesUserAndAuthClass() {
            Server.ConnectAs("u1", "authenticated");
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            CentrifugeConnectedEventArgs args = await connected;

            Assert.Equal("u1", args.User);
            Assert.Equal("authenticated", args.AuthClass);
        }


        // Java: AuthTest.legacyServerReportsEmptyIdentity (connect part).
        /// <summary>A server that sends no identity gives empty values on the Connected event, and one auth event with them.</summary>
        [Fact]
        public async Task Auth_WithLegacyServer_RaisesEmptyIdentity() {
            CentrifugeClient client = NewClient();
            AuthLog log = new AuthLog();
            client.Auth += (object? sender, CentrifugeAuthEventArgs args) => log.Add("/" + args.User + args.AuthClass);
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);

            client.Connect();
            CentrifugeConnectedEventArgs args = await connected;
            await log.WhenCountAsync(1);

            Assert.Equal(string.Empty, args.User);
            Assert.Equal(string.Empty, args.AuthClass);
            Assert.Equal(new[] { "/" }, log.Entries);
        }
    }
}
