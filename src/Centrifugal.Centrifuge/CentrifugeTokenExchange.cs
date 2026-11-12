using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The application supplies the storage and the exchange of the refresh token.
    /// Set through CentrifugeClientOptions.TokenExchange; exclusive with GetToken.
    /// </summary>
    public abstract class CentrifugeTokenExchange {
        /// <summary>
        /// Returns the stored refresh token, or null when there is none. Throws CentrifugeUnauthorizedException when the
        /// application session is over. Called on the client's loop before every exchange: keep it fast, do not block.
        /// </summary>
        public abstract string? Load();


        /// <summary>
        /// Stores the refresh token of an exchange which rotated it. Called on the loop.
        /// </summary>
        /// <param name="refreshToken">The new refresh token.</param>
        public abstract void Store(string refreshToken);


        /// <summary>
        /// Exchanges the refresh token for a token. Throws CentrifugeTokenExchangeRefusedException (or
        /// CentrifugeUnauthorizedException) when the issuer refused the refresh token. Any other exception is an ordinary
        /// failure, retried with backoff. The token is cancelled when the exchange times out or the client closes: the
        /// cancellation is cooperative, and a result which still arrives is stored.
        /// </summary>
        /// <param name="exchangeEvent">The exchange: the refresh token and the rpc of the connection.</param>
        /// <param name="cancellationToken">Cancelled when the exchange times out or the client closes.</param>
        public abstract Task<CentrifugeTokenExchangeResult> ExchangeAsync(CentrifugeTokenExchangeEvent exchangeEvent, CancellationToken cancellationToken);


        /// <summary>The server error app types that mean a refused refresh token (exact match). Default empty.</summary>
        public virtual IReadOnlyCollection<string> RefusalTypes {
            get {
                return Array.Empty<string>();
            }
        }

        /// <summary>The grace period in seconds, default 30. Negative is a configuration error. Clamped to 0..300 when used.</summary>
        public virtual int GraceSeconds {
            get {
                return 30;
            }
        }

        /// <summary>True to renew the token before its expiry, default false.</summary>
        public virtual bool EarlyRenewal {
            get {
                return false;
            }
        }
    }
}
