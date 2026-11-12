using Centrifugal.Centrifuge.Protocol;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// Builders of the connect and refresh replies the tests give the fake server. Each builder sets only the fields a test
    /// names, so the reply it builds is the one the test reads.
    /// </summary>
    public static class ConnectReplies {
        /// <summary>
        /// Builds a connect reply.
        /// </summary>
        /// <param name="client">The client id.</param>
        /// <param name="expires">The expires flag: the connection expires after ttl seconds.</param>
        /// <param name="ttl">The time to live of the connection in seconds, read when expires is set.</param>
        /// <param name="user">The user id.</param>
        /// <param name="authClass">The auth class.</param>
        /// <param name="flag">The capability flags, such as the RPC options bit.</param>
        /// <param name="ping">The ping interval in seconds; 0 leaves the server pings off.</param>
        public static ConnectResult Connect(
            string client = "c",
            bool expires = false,
            uint ttl = 0,
            string user = "",
            string authClass = "",
            long flag = 0,
            uint ping = 0) {
            return new ConnectResult {
                Client = client,
                Expires = expires,
                Ttl = ttl,
                User = user,
                AuthClass = authClass,
                Flag = flag,
                Ping = ping,
            };
        }

        /// <summary>
        /// Builds a refresh reply.
        /// </summary>
        /// <param name="expires">The expires flag.</param>
        /// <param name="ttl">The time to live in seconds, read when expires is set.</param>
        /// <param name="user">The user id.</param>
        /// <param name="authClass">The auth class.</param>
        public static RefreshResult Refresh(bool expires, uint ttl, string user = "", string authClass = "") {
            return new RefreshResult {
                Expires = expires,
                Ttl = ttl,
                User = user,
                AuthClass = authClass,
            };
        }
    }
}
