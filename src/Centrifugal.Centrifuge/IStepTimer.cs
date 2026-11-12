namespace Centrifugal.Centrifuge {


    /// <summary>A one-shot timer whose callback is a loop step. Cancel in a step. A fired callback that is still queued is then skipped.</summary>
    internal interface IStepTimer {
        /// <summary>Cancels the timer. Safe to call twice.</summary>
        void Cancel();
    }
}
