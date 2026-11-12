using System;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// The options of the fork: instance, rpc pool and token exchange. The fields keep the _ prefix because they are
    /// members of an upstream class.
    /// </summary>
    public partial class CentrifugeClientOptions
    {
        /// <summary>
        /// Gets or sets the instance of the client (22 to 64 characters: A-Z, a-z, 0-9, _ or -). When set, the connect
        /// request carries it. Empty by default. The value is never logged.
        /// </summary>
        public string Instance
        {
            get
            {
                return _instance;
            }
            set
            {
                string next = value ?? string.Empty;
                if (next.Length > 0 && !ProtocolExtensions.IsValidInstance(next))
                {
                    throw new ArgumentException("Instance must be 22 to 64 characters: A-Z, a-z, 0-9, _ or -", nameof(value));
                }
                _instance = next;
            }
        }


        /// <summary>
        /// Gets or sets the maximum number of rpc calls with per-call options that the client keeps pending.
        /// Default is 32. Must be at least 1.
        /// </summary>
        public int MaxPendingRpc
        {
            get
            {
                return _maxPendingRpc;
            }
            set
            {
                if (value < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "MaxPendingRpc must be at least 1");
                }
                _maxPendingRpc = value;
            }
        }


        /// <summary>
        /// Gets or sets the longest delay of a failed token refresh retry. Default is 5 minutes.
        /// </summary>
        public TimeSpan TokenRetryCap { get; set; } = TimeSpan.FromMinutes(5);


        /// <summary>
        /// Gets or sets whether a refused token stops the refresh retries (the token is not retried). Default is true.
        /// </summary>
        public bool StopOnRefusedToken { get; set; } = true;


        /// <summary>
        /// Gets or sets the token exchange: the refresh token is stored by the application and exchanged for a connection
        /// token. Default null. Exclusive with GetToken. Read once, when the client is constructed.
        /// </summary>
        public CentrifugeTokenExchange? TokenExchange { get; set; }


        private string _instance = string.Empty;


        private int _maxPendingRpc = 32;


        /// <summary>Checks the fork options. Called by Validate.</summary>
        private void ValidateFork()
        {
            if (TokenExchange != null && GetToken != null)
            {
                throw new CentrifugeConfigurationException("TokenExchange and GetToken are exclusive");
            }
        }
    }
}
