using System;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// The error path of the client: a failure of a loop step is reported as an error event, wrapped as
    /// <see cref="CentrifugeUnclassifiedException"/>. The client keeps running: a step failure does not disconnect.
    /// </summary>
    public partial class CentrifugeClient
    {
        /// <summary>
        /// Reports an exception a loop step threw (a bug in the SDK or in a callback of the loop). Raised as the
        /// error event of type "unclassified". Does not disconnect, unless the error handler itself throws.
        /// </summary>
        /// <param name="error">The exception the step threw.</param>
        private void ReportStepFailure(Exception error)
        {
            EmitErrorEvent("unclassified", new CentrifugeUnclassifiedException(error));
        }
    }
}
