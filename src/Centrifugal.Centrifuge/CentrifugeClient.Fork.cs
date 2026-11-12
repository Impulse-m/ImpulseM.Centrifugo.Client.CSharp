using System;
using Centrifugal.Centrifuge.Transports;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// The helpers of the fork: connection auth, rpc session, token exchange and ping watchdog. The fields keep the _ prefix
    /// because they are members of an upstream class, so the hook lines in CentrifugeClient stay unchanged. They are set
    /// by InitializeFork, which the constructor calls.
    /// </summary>
    public partial class CentrifugeClient
    {
        /// <summary>Lower bound of the token retry delay in milliseconds.</summary>
        private const int TokenRetryMinMs = 5000;


        /// <summary>Upper bound of the token retry delay in milliseconds.</summary>
        private const int TokenRetryMaxMs = 10000;


        private IClientHost _host = null!;


        private IStepTimerFactory _timers = null!;


        private Random _random = null!;


        private IRefreshRetryPolicy _refreshRetryPolicy = null!;


        private ConnectionIdentity _identity = null!;


        private ITokenExchangeHooks _tokenExchange = null!;


        private AuthRefresh _auth = null!;


        private RpcSession _rpcSession = null!;


        private PingWatchdog _watchdog = null!;


        private volatile bool _rpcOptionsSupported = false;


        private long _serverTimeOffsetMillis;


        private bool _hasInstance;


        private Func<long> _nowMs = null!;


        private Func<CentrifugeTransportType, string, ITransport>? _transportFactory;


        /// <summary>
        /// Test seam: rebuilds the fork helpers on the given timers, monotonic clock and random source. Call it before
        /// Connect and before the first subscription is created. The upstream timers of the client stay real.
        /// </summary>
        /// <param name="timers">The step timers of the fork helpers and the subscriptions.</param>
        /// <param name="nowMs">The monotonic clock in milliseconds.</param>
        /// <param name="random">The random source of the retry delays and the token exchange.</param>
        internal void UseTestClock(IStepTimerFactory timers, Func<long> nowMs, Random random)
        {
            BuildForkHelpers(timers, nowMs, random);
        }


        /// <summary>
        /// Test seam: the client creates its transports with the given factory instead of the built-in transports.
        /// Call it before Connect.
        /// </summary>
        /// <param name="factory">Creates the transport of one attempt from the transport type and the endpoint.</param>
        internal void UseTestTransport(Func<CentrifugeTransportType, string, ITransport> factory)
        {
            _transportFactory = factory;
        }


        /// <summary>Creates the fork helpers. Called once by the constructor, after the loop and the options exist.</summary>
        private void InitializeFork()
        {
            _host = new HostAdapter(this);
            _hasInstance = !string.IsNullOrEmpty(_options.Instance);
            BuildForkHelpers(new LoopTimerFactory(Loop), () => Clock.NowMs, new Random());
        }


        private void BuildForkHelpers(IStepTimerFactory timers, Func<long> nowMs, Random random)
        {
            _timers = timers;
            _nowMs = nowMs;
            _random = random;
            _refreshRetryPolicy = new RefreshRetryPolicy(_random, TokenRetryMinMs, TokenRetryMaxMs, (int)Math.Min(int.MaxValue, _options.TokenRetryCap.TotalMilliseconds), _options.StopOnRefusedToken);
            _identity = new ConnectionIdentity(_host);
            _tokenExchange = TokenExchangeFlow.Create(_host, _options.TokenExchange, _random);
            _auth = new AuthRefresh(_host, _identity, _tokenExchange, _timers, _refreshRetryPolicy);
            _rpcSession = new RpcSession(_host, _timers, _options.MaxPendingRpc, _random);
            _watchdog = new PingWatchdog(_host, _timers);
        }
    }
}
