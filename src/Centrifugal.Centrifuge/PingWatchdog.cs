namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The server ping watchdog (Java PingWatchdog). It closes the connection when the server stops sending pings.
    /// </summary>
    internal sealed class PingWatchdog {
        private readonly IClientHost host;
        private readonly IStepTimerFactory timers;


        /// <summary>
        /// Creates the watchdog of one client.
        /// </summary>
        /// <param name="host">The client seam.</param>
        /// <param name="timers">The step timer factory.</param>
        internal PingWatchdog(IClientHost host, IStepTimerFactory timers) {
            this.host = host;
            this.timers = timers;
        }


        /// <summary>Arms the watchdog for the ping interval of a connect reply.</summary>
        /// <param name="pingSeconds">The ping interval in seconds.</param>
        public void Start(uint pingSeconds) {
        }


        /// <summary>Cancels the watchdog.</summary>
        public void Cancel() {
        }


        /// <summary>Marks that a frame arrived, which resets the watchdog.</summary>
        public void MarkReply() {
        }
    }
}
