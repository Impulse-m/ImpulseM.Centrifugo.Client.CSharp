using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// A client-side loss of the connection under a call: the write failed (code 10) or the connection closed (code 11).
    /// The rpc pool recognises a loss by this type, not by the code.
    /// </summary>
    internal sealed class CentrifugeConnectionLostException : CentrifugeException {
        /// <summary>
        /// Creates the loss error.
        /// </summary>
        /// <param name="code">CentrifugeErrorCodes.TransportWriteError or CentrifugeErrorCodes.ConnectionClosed.</param>
        /// <param name="message">The message.</param>
        /// <param name="temporary">The error is temporary.</param>
        /// <param name="innerException">The cause, or null.</param>
        public CentrifugeConnectionLostException(int code, string message, bool temporary, Exception? innerException)
            : base(ValidateCode(code), message, temporary, innerException) {
        }

        private static int ValidateCode(int code) {
            if (code != CentrifugeErrorCodes.TransportWriteError && code != CentrifugeErrorCodes.ConnectionClosed) {
                throw new ArgumentOutOfRangeException(nameof(code), "a connection loss has code 10 or 11");
            }
            return code;
        }
    }
}
