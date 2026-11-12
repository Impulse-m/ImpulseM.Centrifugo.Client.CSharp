using System;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The publish debounce of one subscription. The first publish of an idle window is sent at once and opens the
    /// window. The publishes made inside the window replace each other, and the latest one is sent when the window
    /// ends. A window that ends with nothing pending closes, so the next publish is sent at once. Runs on the loop
    /// of its client: every member is called in a step.
    /// </summary>
    internal sealed class PublishDebounce {
        private readonly EventLoop loop;
        private readonly IStepTimerFactory timers;
        private readonly Func<byte[], CancellationToken, Task> publishToChannel;
        private byte[]? pending;
        private IStepTimer? timer;
        private int windowMillis;
        private long generation;


        /// <summary>
        /// Creates the debounce of one subscription.
        /// </summary>
        /// <param name="loop">The loop of the client; the completions of the publishes wait for the end of the step.</param>
        /// <param name="timers">Creates the window timers.</param>
        /// <param name="publishToChannel">Sends one publish of the channel of the subscription.</param>
        internal PublishDebounce(
            EventLoop loop,
            IStepTimerFactory timers,
            Func<byte[], CancellationToken, Task> publishToChannel
        ) {
            this.loop = loop;
            this.timers = timers;
            this.publishToChannel = publishToChannel;
        }


        /// <summary>
        /// Publishes data inside the debounce window. An idle window sends the data at once and returns that send, then opens
        /// a window of windowMillis. An open window keeps the data as the latest pending value and returns a task that
        /// completes with success at once.
        /// </summary>
        /// <param name="data">The data to publish.</param>
        /// <param name="windowMillis">The window in milliseconds, from the server (above zero).</param>
        /// <param name="cancellationToken">Cancels the send of an idle publish.</param>
        internal Task Publish(byte[] data, int windowMillis, CancellationToken cancellationToken) {
            if (timer != null) {
                pending = data;
                return CompletedAtEndOfStep();
            }

            long before = generation;
            Task sent = Send(data, cancellationToken);
            // A cancel during the send (a nested unsubscribe or disconnect) wins: no window is armed after it.
            if (windowMillis > 0 && generation == before) {
                this.windowMillis = windowMillis;
                Arm(windowMillis);
            }
            return sent;
        }


        /// <summary>
        /// Stops the window and drops the pending value. A timer that fired before is ignored when its step runs.
        /// </summary>
        internal void Cancel() {
            timer?.Cancel();
            timer = null;
            pending = null;
            generation++;
        }


        /// <summary>
        /// The window of the server as the subscription keeps it: a value above the signed range is capped at int.MaxValue.
        /// </summary>
        /// <param name="windowMillis">The window from the subscribe reply, in milliseconds.</param>
        internal static int CapWindow(uint windowMillis) {
            return windowMillis > int.MaxValue ? int.MaxValue : (int)windowMillis;
        }


        private void Arm(int delayMs) {
            long armed = generation;
            timer = timers.Start(delayMs, () => Fire(armed));
        }


        private void Fire(long armed) {
            if (armed != generation) {
                return;
            }
            timer = null;
            byte[]? data = pending;
            if (data == null) {
                return;
            }
            pending = null;
            long before = generation;
            // The window end is not reported: the publish of the window end is sent and its outcome is observed only.
            ObserveFailure(Send(data, CancellationToken.None));
            // A cancel during the send wins: the window is not armed again after it.
            if (before == generation) {
                Arm(windowMillis);
            }
        }


        private Task Send(byte[] data, CancellationToken cancellationToken) {
            try {
                return publishToChannel(data, cancellationToken);
            } catch (Exception ex) {
                return Task.FromException(ex);
            }
        }


        private static void ObserveFailure(Task sent) {
            sent.ContinueWith(
                static task => { _ = task.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }


        private Task CompletedAtEndOfStep() {
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            loop.Complete(() => done.TrySetResult(true));
            return done.Task;
        }
    }
}
