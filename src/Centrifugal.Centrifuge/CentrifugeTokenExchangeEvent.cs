using System;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// One exchange of the token exchange: the refresh token loaded for it, and the plain rpc over the connection the
    /// exchange runs on. Created by the flow, not by the application.
    /// </summary>
    public sealed class CentrifugeTokenExchangeEvent {
        private readonly Func<string, ReadOnlyMemory<byte>, CancellationToken, Task<CentrifugeRpcResult>> rpc;


        /// <summary>
        /// Creates the event for one exchange.
        /// </summary>
        /// <param name="refreshToken">The refresh token loaded for this exchange.</param>
        /// <param name="rpc">The plain rpc of the exchange connection: method, payload and cancellation token.</param>
        internal CentrifugeTokenExchangeEvent(
            string refreshToken,
            Func<string, ReadOnlyMemory<byte>, CancellationToken, Task<CentrifugeRpcResult>> rpc
        ) {
            RefreshToken = refreshToken;
            this.rpc = rpc;
        }


        /// <summary>The refresh token loaded for this exchange.</summary>
        public string RefreshToken { get; }


        /// <summary>
        /// Sends a plain rpc over the connection the exchange runs on, with no message id and no retransmit. Callable from
        /// any thread. Throws ArgumentNullException for a null method. Fails with CentrifugeTokenExchangeRefusedException
        /// when the server refuses the call, CentrifugeException code 11 when the connection is lost, and
        /// CentrifugeTimeoutException after the client Timeout.
        /// </summary>
        /// <param name="method">The rpc method.</param>
        /// <param name="data">The payload.</param>
        /// <param name="cancellationToken">Cancels the call.</param>
        public Task<CentrifugeRpcResult> RpcAsync(string method, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) {
            throw new NotImplementedException();
        }
    }
}
