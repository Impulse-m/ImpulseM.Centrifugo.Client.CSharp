using System;
using System.Collections.Generic;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Base exception for Centrifuge client errors.
    /// </summary>
    public class CentrifugeException : Exception
    {
        /// <summary>
        /// Gets the error code.
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// Gets whether this error is temporary.
        /// </summary>
        public bool Temporary { get; }

        /// <summary>
        /// Gets the application error code of a server error (0 when none).
        /// </summary>
        public int AppCode { get; }

        /// <summary>
        /// Gets the application error type of a server error (empty when none).
        /// </summary>
        public string AppType { get; }

        /// <summary>
        /// Gets a read-only copy of the metadata of a server error (empty when none).
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeException"/> class.
        /// </summary>
        public CentrifugeException(int code, string message, bool temporary = false, Exception? innerException = null)
            : this(code, message, temporary, innerException, 0, string.Empty, null)
        {
        }

        /// <summary>
        /// Initializes an exception with the fields of a server error reply.
        /// </summary>
        internal CentrifugeException(
            int code,
            string message,
            bool temporary,
            Exception? innerException,
            int appCode,
            string appType,
            IReadOnlyDictionary<string, string>? metadata)
            : base(message, innerException)
        {
            Code = code;
            Temporary = temporary;
            AppCode = appCode;
            AppType = appType ?? string.Empty;
            IReadOnlyDictionary<string, string> copy = metadata == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(metadata);
            Metadata = copy;
        }

        /// <summary>Wraps the error carried by a server reply.</summary>
        internal static CentrifugeException FromReply(Protocol.Error error)
        {
            Dictionary<string, string> metadata = new Dictionary<string, string>(error.Metadata);
            return new CentrifugeException((int)error.Code, error.Message, error.Temporary, null, error.AppCode, error.AppType, metadata);
        }
    }

    /// <summary>
    /// Exception thrown when authentication fails.
    /// </summary>
    public class CentrifugeUnauthorizedException : CentrifugeException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeUnauthorizedException"/> class.
        /// </summary>
        public CentrifugeUnauthorizedException(string message = "Unauthorized")
            : base(CentrifugeDisconnectedCodes.Unauthorized, message, false)
        {
        }
    }

    /// <summary>
    /// Exception thrown when the GetState callback of a subscription fails.
    /// The original callback error is available via InnerException.
    /// </summary>
    public class CentrifugeGetStateException : CentrifugeException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeGetStateException"/> class.
        /// </summary>
        public CentrifugeGetStateException(Exception innerException)
            : base(CentrifugeErrorCodes.SubscriptionGetState, "get state error: " + innerException.Message, true, innerException)
        {
        }
    }

    /// <summary>
    /// Exception thrown when operation times out.
    /// </summary>
    public class CentrifugeTimeoutException : CentrifugeException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeTimeoutException"/> class.
        /// </summary>
        public CentrifugeTimeoutException(string message = "Operation timeout")
            : base(CentrifugeErrorCodes.Timeout, message, true)
        {
        }
    }

    /// <summary>
    /// Exception thrown when configuration is invalid.
    /// </summary>
    public class CentrifugeConfigurationException : CentrifugeException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeConfigurationException"/> class.
        /// </summary>
        public CentrifugeConfigurationException(string message)
            : base(CentrifugeErrorCodes.BadConfiguration, message, false)
        {
        }
    }

    /// <summary>
    /// Exception thrown when attempting to create a duplicate subscription.
    /// </summary>
    public class CentrifugeDuplicateSubscriptionException : CentrifugeException
    {
        /// <summary>
        /// Gets the channel name that already has a subscription.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeDuplicateSubscriptionException"/> class.
        /// </summary>
        /// <param name="channel">The channel name.</param>
        public CentrifugeDuplicateSubscriptionException(string channel)
            : base(0, $"Subscription to channel '{channel}' already exists", false)
        {
            Channel = channel;
        }
    }
}
