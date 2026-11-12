using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// Records the refresh commands the fake server receives, in order, and completes a wait for the nth one. A test
    /// waits for a command instead of sleeping.
    /// </summary>
    internal sealed class RefreshArrivals {
        private readonly object gate = new object();
        private readonly List<Command> commands = new List<Command>();
        private readonly Dictionary<int, TaskCompletionSource<Command>> waiters = new Dictionary<int, TaskCompletionSource<Command>>();


        /// <summary>The tokens of the refresh commands received so far, in order.</summary>
        public string[] Tokens() {
            lock (gate) {
                return commands.Select(command => command.Refresh.Token).ToArray();
            }
        }


        /// <summary>
        /// Records one refresh command as the fake server receives it.
        /// </summary>
        /// <param name="command">The refresh command.</param>
        public void Add(Command command) {
            lock (gate) {
                commands.Add(command);
                int index = commands.Count - 1;
                if (waiters.TryGetValue(index, out TaskCompletionSource<Command>? waiter)) {
                    waiters.Remove(index);
                    waiter.TrySetResult(command);
                }
            }
        }


        /// <summary>
        /// Completes with the refresh command at the given index (zero based), or fails after five seconds.
        /// </summary>
        /// <param name="index">The index of the command in arrival order.</param>
        public Task<Command> Nth(int index) {
            lock (gate) {
                if (index < commands.Count) {
                    return Task.FromResult(commands[index]);
                }
                TaskCompletionSource<Command> waiter = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters[index] = waiter;
                return waiter.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
