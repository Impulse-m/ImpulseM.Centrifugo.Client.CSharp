using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The subscription members of the client. The subscriptions reach the timers of the client through these
    /// internal members; the publish, unsubscribe and disconnect calls they make are the client methods of Client.cs.
    /// </summary>
    public partial class CentrifugeClient {
        /// <summary>The step timer factory of the client. The subscriptions create their timers with it.</summary>
        internal IStepTimerFactory StepTimers {
            get {
                return _timers;
            }
        }
    }
}
