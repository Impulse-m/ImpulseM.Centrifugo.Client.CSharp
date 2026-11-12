using System;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The hooks the connection auth and the connect path call. Implemented by TokenExchangeFlow when a token exchange is
    /// set. The null object NullTokenExchangeHooks is used when no exchange is set.
    /// </summary>
    internal interface ITokenExchangeHooks {
        /// <summary>True when a token exchange is configured. False in the null object.</summary>
        bool IsActive { get; }

        /// <summary>Called once per connect attempt, before the connect command is built (Java beforeConnect).</summary>
        void BeforeConnect();

        /// <summary>
        /// Called by the connect reply path before the Connected state is set. true: the reply is held by the bootstrap and
        /// resumed later through IClientHost.ResumeConnect. The caller returns at once.
        /// </summary>
        bool InterceptConnect(Protocol.ConnectResult result);

        /// <summary>Called first in the push path. true: the push is held and is not dispatched now (see IClientHost.DispatchHeldPush).</summary>
        bool HoldPush(Protocol.Push push);

        /// <summary>Records the expiry of the connection token. expires false clears the record.</summary>
        void Observe(bool expires, uint ttl);

        /// <summary>Called from every disconnect that reaches the teardown. terminal is true when the session ends.</summary>
        void Reset(int code, bool terminal);

        /// <summary>Called when a requested refresh is accepted (Java tokenAccepted).</summary>
        void TokenAccepted();

        /// <summary>True when the token has an expiry record that is past.</summary>
        bool IsTokenExpired(string token);

        /// <summary>Renewal delay in milliseconds for a ttl in seconds (Java renewalDelayMillis). Without an exchange: Utilities.TtlToMilliseconds(ttl) (95 percent).</summary>
        int RenewalDelayMilliseconds(uint ttl);

        /// <summary>The renewal fetch of the timer refresh. The null object never gets it (no getter). The result is applied in a step.</summary>
        Task<string> RenewAsync(CancellationToken cancellationToken);
    }
}
