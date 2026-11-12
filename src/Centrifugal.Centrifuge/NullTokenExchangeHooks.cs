using System;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>The hooks when no token exchange is set. Every method is a no-op, and the renewal delay is the upstream rule.</summary>
    internal sealed class NullTokenExchangeHooks : ITokenExchangeHooks {
        /// <summary>False: no token exchange is configured.</summary>
        public bool IsActive {
            get {
                return false;
            }
        }

        /// <summary>No-op.</summary>
        public void BeforeConnect() {
        }

        /// <summary>Never holds a reply.</summary>
        public bool InterceptConnect(Protocol.ConnectResult result) {
            return false;
        }

        /// <summary>Never holds a push.</summary>
        public bool HoldPush(Protocol.Push push) {
            return false;
        }

        /// <summary>No-op.</summary>
        public void Observe(bool expires, uint ttl) {
        }

        /// <summary>No-op.</summary>
        public void Reset(int code, bool terminal) {
        }

        /// <summary>No-op.</summary>
        public void TokenAccepted() {
        }

        /// <summary>Never expired: there is no expiry record.</summary>
        public bool IsTokenExpired(string token) {
            return false;
        }

        /// <summary>The upstream renewal delay: 95 percent of the ttl.</summary>
        public int RenewalDelayMilliseconds(uint ttl) {
            return Utilities.TtlToMilliseconds(ttl);
        }

        /// <summary>Never called: there is no token getter in the null object.</summary>
        public Task<string> RenewAsync(CancellationToken cancellationToken) {
            throw new InvalidOperationException("no token exchange is configured");
        }
    }
}
