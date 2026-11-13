// Event waits, counters and loop barriers of the connection refresh tests. Every wait fails after five seconds, and a
// negative check runs after a loop barrier, never after a sleep.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Waits for the events and the timer state the connection refresh tests check.</summary>
    internal static class RefreshTestEvents {
        /// <summary>Counts the error events of one type and completes a wait that the count reaches.</summary>
        public sealed class ErrorCounter {
            private readonly object gate = new object();
            private readonly string type;
            private readonly List<(int Target, TaskCompletionSource<bool> Source)> waiters = new List<(int Target, TaskCompletionSource<bool> Source)>();
            private int count;


            /// <summary>
            /// Starts counting the error events of the type.
            /// </summary>
            /// <param name="client">The client whose error events are counted.</param>
            /// <param name="type">The error type to count.</param>
            public ErrorCounter(CentrifugeClient client, string type) {
                this.type = type;
                client.Error += (object? sender, CentrifugeErrorEventArgs args) => OnError(args);
            }


            /// <summary>
            /// Completes when the count is at least n, or fails after five seconds.
            /// </summary>
            /// <param name="n">The count to reach.</param>
            public Task Reached(int n) {
                lock (gate) {
                    if (count >= n) {
                        return Task.CompletedTask;
                    }
                    TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    waiters.Add((n, source));
                    return source.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }


            private void OnError(CentrifugeErrorEventArgs args) {
                if (args.Type != type) {
                    return;
                }
                lock (gate) {
                    count++;
                    foreach ((int target, TaskCompletionSource<bool> source) in waiters.ToArray()) {
                        if (count >= target) {
                            source.TrySetResult(true);
                            waiters.Remove((target, source));
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Completes with the next error event of the type, or fails after five seconds.
        /// </summary>
        /// <param name="client">The client to watch.</param>
        /// <param name="type">The error type to wait for.</param>
        public static Task<CentrifugeErrorEventArgs> WhenErrorOfType(CentrifugeClient client, string type) {
            TaskCompletionSource<CentrifugeErrorEventArgs> source =
                new TaskCompletionSource<CentrifugeErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Error += (object? sender, CentrifugeErrorEventArgs args) => {
                if (args.Type == type) {
                    source.TrySetResult(args);
                }
            };
            return source.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }


        /// <summary>
        /// Completes with the next auth event of the client, or fails after five seconds.
        /// </summary>
        /// <param name="client">The client to watch.</param>
        public static Task<CentrifugeAuthEventArgs> WhenAuth(CentrifugeClient client) {
            TaskCompletionSource<CentrifugeAuthEventArgs> source =
                new TaskCompletionSource<CentrifugeAuthEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Auth += (object? sender, CentrifugeAuthEventArgs args) => source.TrySetResult(args);
            return source.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }


        /// <summary>
        /// The delay of the timer that was started last, in milliseconds.
        /// </summary>
        /// <param name="timers">The fake timers of the client.</param>
        public static int LastDelay(FakeStepTimerFactory timers) {
            IReadOnlyList<int> delays = timers.RequestedDelays;
            return delays[delays.Count - 1];
        }


        /// <summary>
        /// Runs loop barriers, and yields the thread between them, until the condition holds or a bounded number of
        /// attempts is used up. The continuations of the test tasks run on the thread pool, so a barrier alone may pass
        /// before them. The caller checks the result.
        /// </summary>
        /// <param name="client">The client whose loop is barriered.</param>
        /// <param name="condition">The condition to wait for.</param>
        public static async Task UntilAsync(CentrifugeClient client, Func<bool> condition) {
            for (int attempt = 0; attempt < 20000 && !condition(); attempt++) {
                await client.LoopBarrierAsync();
                Thread.Yield();
            }
        }


        /// <summary>
        /// The connect tokens the fake server received, in order.
        /// </summary>
        /// <param name="server">The fake server.</param>
        public static List<string> ConnectTokens(FakeCentrifugoServer server) {
            List<string> tokens = new List<string>();
            foreach (Command received in server.Received) {
                if (received.Connect != null) {
                    tokens.Add(received.Connect.Token);
                }
            }
            return tokens;
        }
    }
}
