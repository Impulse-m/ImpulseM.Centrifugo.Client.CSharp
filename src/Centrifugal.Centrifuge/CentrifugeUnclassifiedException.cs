using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The error an event handler or a callback raised outside the SDK's classified errors. Reported with the
    /// "unclassified" error type. The original exception is available through Error.
    /// </summary>
    public sealed class CentrifugeUnclassifiedException : Exception {
        /// <summary>
        /// Wraps the original exception.
        /// </summary>
        /// <param name="cause">The original exception.</param>
        public CentrifugeUnclassifiedException(Exception cause) : base(cause.Message, cause) {
        }

        /// <summary>The original exception.</summary>
        public Exception Error {
            get {
                return InnerException!;
            }
        }
    }
}
