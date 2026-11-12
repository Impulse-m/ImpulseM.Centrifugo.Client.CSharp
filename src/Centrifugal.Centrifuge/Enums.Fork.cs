namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// The error codes the fork adds to the client-side codes.
    /// </summary>
    public static partial class CentrifugeErrorCodes
    {
        /// <summary>
        /// Client-side: the server does not support the RPC headers or fields of the call (the connection did not
        /// confirm RPC options). The call is not sent.
        /// </summary>
        public const int Unsupported = 15;


        /// <summary>
        /// Client-side: a client limit was reached (for example, the pool's queue of calls is full). Code 16 has
        /// other owners too; the message tells them apart.
        /// </summary>
        public const int LimitExceeded = 16;


        /// <summary>
        /// Client-side: the call was cancelled by the application.
        /// </summary>
        public const int Cancelled = 17;


        /// <summary>
        /// Client-side: the rpc pool could not fetch the redirect target of the call.
        /// </summary>
        public const int RedirectFailed = 18;
    }
}
