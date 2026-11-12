using System;
using System.Threading;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The identity and capability members of the client. The Auth event and the Capabilities snapshot are the public
    /// surface; the rest is internal.
    /// </summary>
    public partial class CentrifugeClient {
        /// <summary>Raised on each connect, and on a refresh only when the identity changed. Carries the user and the auth class only.</summary>
        public event EventHandler<CentrifugeAuthEventArgs>? Auth;


        /// <summary>A snapshot of what the server confirmed for this connection.</summary>
        public CentrifugeClientCapabilities Capabilities {
            get {
                return new CentrifugeClientCapabilities(_rpcOptionsSupported);
            }
        }

        /// <summary>The retry policy of this client (internal, used by the subscriptions).</summary>
        internal IRefreshRetryPolicy RefreshRetryPolicy {
            get {
                return _refreshRetryPolicy;
            }
        }

        /// <summary>The wall clock for server time. Null in production (DateTimeOffset.UtcNow). Tests set it before connecting.</summary>
        internal Func<long>? WallClockMs { get; set; }


        /// <summary>The wall clock in Unix milliseconds: WallClockMs when set, else the system clock.</summary>
        private long WallNowMs() {
            return WallClockMs?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>Stores the server clock offset of a reply when the server time is plausible (Java observeServerTime).</summary>
        /// <param name="reply">The reply with the server time.</param>
        private void ObserveServerTime(Protocol.Reply reply) {
            long? offset = ProtocolExtensions.ServerTimeOffsetFrom(reply.Time, WallNowMs());
            if (offset.HasValue) {
                Interlocked.Exchange(ref _serverTimeOffsetMillis, offset.Value);
            }
        }
    }
}
