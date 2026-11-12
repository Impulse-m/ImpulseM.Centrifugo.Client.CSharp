using System;

namespace Centrifugal.Centrifuge {


    /// <summary>Production step timers: wraps the upstream LoopTimer.</summary>
    internal sealed class LoopTimerFactory : IStepTimerFactory {
        private readonly EventLoop loop;


        /// <summary>
        /// Creates the factory for one loop.
        /// </summary>
        /// <param name="loop">The loop the timers run their steps on.</param>
        public LoopTimerFactory(EventLoop loop) {
            this.loop = loop;
        }

        /// <summary>Starts a timer. onFire runs as a loop step after delayMs.</summary>
        public IStepTimer Start(int delayMs, Action onFire) {
            return LoopTimer.Start(loop, delayMs, onFire);
        }
    }
}
