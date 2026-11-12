using System;
using System.Text.RegularExpressions;

namespace Centrifugal.Centrifuge {


    /// <summary>Extension wire rules of the fork (Java Extensions). Pure functions and constants.</summary>
    internal static class ProtocolExtensions {
        /// <summary>Metadata key of the retry signal on an error reply.</summary>
        public const string RetryMetadataKey = "x-centrifugo-retry";


        /// <summary>Retry signal value: the call was not executed; a new msg_id is required.</summary>
        public const string RetryNewMsgId = "new_msg_id";


        /// <summary>Retry signal value: the call was not executed; retry later.</summary>
        public const string RetryLater = "later";


        /// <summary>A server time further than this from the local clock is ignored: 365 days in milliseconds.</summary>
        public const long MaxServerTimeSkewMillis = 365L * 24L * 60L * 60L * 1000L;


        private static readonly Regex instancePattern =new Regex("^[A-Za-z0-9_-]{22,64}\\z", RegexOptions.CultureInvariant);


        /// <summary>Whole-string match of the instance rule. Null is invalid.</summary>
        /// <param name="value">The instance value.</param>
        public static bool IsValidInstance(string? value) {
            return value != null && instancePattern.IsMatch(value);
        }

        /// <summary>
        /// Offset of the server clock: serverTime - localTime. Null when serverTime is 0, or when it is more than
        /// MaxServerTimeSkewMillis away from localTime (Java serverTimeOffsetFrom).
        /// </summary>
        /// <param name="serverTime">The server time in Unix milliseconds.</param>
        /// <param name="localTime">The local wall clock in Unix milliseconds.</param>
        public static long? ServerTimeOffsetFrom(long serverTime, long localTime) {
            if (serverTime == 0) {
                return null;
            }
            if (serverTime < localTime - MaxServerTimeSkewMillis || serverTime > localTime + MaxServerTimeSkewMillis) {
                return null;
            }
            return serverTime - localTime;
        }

        /// <summary>The retry signal of an error reply, or null when the server set none.</summary>
        /// <param name="error">The error of the reply.</param>
        public static string? RetrySignal(Protocol.Error error) {
            return error.Metadata.TryGetValue(RetryMetadataKey, out string? value) ? value : null;
        }

        /// <summary>The local reply of a call whose headers the server does not support: no id, error code 15.</summary>
        public static Protocol.Reply UnsupportedRpcReply() {
            return new Protocol.Reply {
                Error = new Protocol.Error {
                    Code = (uint)CentrifugeErrorCodes.Unsupported,
                    Message = "rpc headers are not supported by the server",
                },
            };
        }
    }
}
