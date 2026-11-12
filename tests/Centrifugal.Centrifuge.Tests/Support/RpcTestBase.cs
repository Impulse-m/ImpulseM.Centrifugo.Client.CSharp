using System;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// Base of the tests of the rpc pool and of the exactly-once calls. On top of FakeServerTestBase it confirms RPC
    /// options, answers rpc commands, and gives the random sources that fix the retry delays.
    /// Replaces RpcTestBase.java.
    /// </summary>
    public abstract class RpcTestBase : FakeServerTestBase {
        /// <summary>Makes the fake server confirm RPC options in its connect reply.</summary>
        protected void EnableRpcOptions() {
            Server.EnableRpcOptions();
        }

        /// <summary>
        /// Answers every rpc command with a result that carries the payload and the server time. Other commands keep the
        /// default answers.
        /// </summary>
        /// <param name="payload">The payload of the result.</param>
        /// <param name="time">The server time of the reply, in unix milliseconds.</param>
        protected void AnswerRpcs(string payload, long time = 0) {
            Server.OnCommand = (Command cmd) => {
                if (cmd.Rpc == null) {
                    return null;
                }
                return new Reply {
                    Id = cmd.Id,
                    Time = time,
                    Rpc = new RPCResult { Data = ByteString.CopyFromUtf8(payload) },
                };
            };
        }

        /// <summary>The random source whose draws are the top of their range: each retry waits its largest delay.</summary>
        protected static Random MaximumDraws() {
            return FixedRandom.MaximumDraws();
        }

        /// <summary>The random source whose draws are the bottom of their range: each retry waits its smallest delay.</summary>
        protected static Random MinimumDraws() {
            return FixedRandom.MinimumDraws();
        }

        /// <summary>Completes when the fake server holds a command. See FakeCentrifugoServer.AwaitHeldAsync.</summary>
        /// <param name="server">The fake server.</param>
        internal static Task AwaitHeldAsync(FakeCentrifugoServer server) {
            return server.AwaitHeldAsync();
        }
    }
}
