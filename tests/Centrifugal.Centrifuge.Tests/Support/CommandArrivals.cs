using System;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// Counts the commands of one kind that the fake server has received, and lets a test wait until the count is reached.
    /// The count moves in the server's receive path, before the command is answered.
    /// </summary>
    internal sealed class CommandArrivals {
        /// <summary>The number of arrivals so far.</summary>
        public int Count {
            get {
                lock (gate) {
                    return count;
                }
            }
        }


        private readonly object gate = new object();
        private readonly TimeSpan timeout;
        private int count;
        private int target;
        private TaskCompletionSource<bool>? waiter;


        /// <summary>
        /// Creates the counter.
        /// </summary>
        /// <param name="timeout">How long a wait may take before it fails.</param>
        public CommandArrivals(TimeSpan timeout) {
            this.timeout = timeout;
        }


        /// <summary>Counts one arrival, and completes a wait that the count reaches.</summary>
        public void Add() {
            lock (gate) {
                count++;
                if (waiter != null && count >= target) {
                    waiter.TrySetResult(true);
                    waiter = null;
                }
            }
        }


        /// <summary>Completes when the count is at least n, or fails when the timeout passes.</summary>
        /// <param name="n">The count to reach.</param>
        public Task Reached(int n) {
            lock (gate) {
                if (count >= n) {
                    return Task.CompletedTask;
                }
                target = n;
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return waiter.Task.WaitAsync(timeout);
            }
        }
    }
}
