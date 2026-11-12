using System;

namespace Centrifugal.Centrifuge {


    /// <summary>Creates step timers. Production: LoopTimerFactory. Tests: FakeStepTimerFactory.</summary>
    internal interface IStepTimerFactory {
        /// <summary>Starts a timer. onFire runs as a loop step after delayMs.</summary>
        IStepTimer Start(int delayMs, Action onFire);
    }
}
