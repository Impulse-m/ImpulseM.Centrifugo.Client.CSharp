using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The server ping watchdog. A connection stays open while the server sends replies: each decoded reply marks
    /// it alive. A check runs every half window; the second check in a row that finds no reply closes the connection
    /// with the no-ping code. The checks are steps of the client loop, so a check that was queued before its
    /// connection ended finds another generation and exits.
    /// </summary>
    internal sealed class PingWatchdog {
        /// <summary>The number of checks in a row without a reply that close the connection.</summary>
        internal const int MissedChecksToClose = 2;


        private readonly IClientHost host;


        private readonly IStepTimerFactory timers;


        private long generation;


        private bool seen;


        private long halfWindowMs;


        private IStepTimer? checkTimer;


        /// <summary>
        /// Creates the watchdog of one client.
        /// </summary>
        /// <param name="host">The client seam.</param>
        /// <param name="timers">The step timer factory.</param>
        internal PingWatchdog(IClientHost host, IStepTimerFactory timers) {
            this.host = host;
            this.timers = timers;
        }


        /// <summary>
        /// The half window in milliseconds, rounded up: (ping interval + allowed delay + 1) / 2. The window is the
        /// ping interval plus the allowed delay.
        /// </summary>
        /// <param name="pingIntervalMilliseconds">The ping interval of the connect reply, in milliseconds.</param>
        /// <param name="maxServerPingDelayMilliseconds">The allowed delay of the server ping, in milliseconds.</param>
        internal static long HalfWindowMilliseconds(long pingIntervalMilliseconds, long maxServerPingDelayMilliseconds) {
            return (pingIntervalMilliseconds + maxServerPingDelayMilliseconds + 1) / 2;
        }


        /// <summary>
        /// Starts the checks of a new connection. Cancels the checks of the previous one, clears the reply mark, and
        /// schedules the first check one half window later. A zero ping interval arms nothing. Call in a step.
        /// </summary>
        /// <param name="pingSeconds">The ping interval of the connect reply, in seconds.</param>
        public void Start(uint pingSeconds) {
            Cancel();
            seen = false;
            if (pingSeconds == 0) {
                return;
            }
            long pingIntervalMs = (long)pingSeconds * 1000L;
            long maxDelayMs = (long)host.MaxServerPingDelay.TotalMilliseconds;
            halfWindowMs = HalfWindowMilliseconds(pingIntervalMs, maxDelayMs);
            ScheduleCheck(0);
        }


        /// <summary>
        /// Cancels the checks: the scheduled check is removed, and a check that is already queued finds another
        /// generation and exits. Call in a step.
        /// </summary>
        public void Cancel() {
            generation++;
            checkTimer?.Cancel();
            checkTimer = null;
        }


        /// <summary>
        /// Marks that a reply was decoded, so that the next check does not count as missed. Called for every decoded
        /// reply, before the reply is handled. Call in a step.
        /// </summary>
        public void MarkReply() {
            seen = true;
        }


        // Schedules the next check. previousIdle is the number of checks in a row without a reply before it.
        private void ScheduleCheck(int previousIdle) {
            long armedGeneration = generation;
            long delayMs = Math.Min(halfWindowMs, int.MaxValue);
            checkTimer = timers.Start((int)delayMs, () => RunCheck(armedGeneration, previousIdle));
        }


        // One check. Runs only for the generation it was armed for, and only while the client is connected.
        private void RunCheck(long armedGeneration, int previousIdle) {
            if (armedGeneration != generation) {
                return;
            }
            if (host.State != CentrifugeClientState.Connected) {
                return;
            }
            int idle;
            if (seen) {
                seen = false;
                idle = 0;
            } else {
                idle = previousIdle + 1;
            }
            if (idle < MissedChecksToClose) {
                ScheduleCheck(idle);
                return;
            }
            host.DisconnectWith(CentrifugeConnectingCodes.NoPing, "no ping", true);
        }
    }
}
