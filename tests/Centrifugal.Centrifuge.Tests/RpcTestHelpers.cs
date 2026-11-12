using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// Helpers of the exactly-once rpc tests: the answers of the fake server, the requests it received and a valid instance.
    /// </summary>
    internal static class RpcTestHelpers {
        /// <summary>A valid connection instance: 30 characters of the instance pattern.</summary>
        internal const string Instance = "abcdefghijklmnopqrstuvwxyz0123";

        /// <summary>The longest wait of a test step.</summary>
        internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);


        /// <summary>
        /// The answers of the fake server for rpc commands. A command for which hold is true gets no answer. Every other rpc
        /// command gets the payload; a retransmit gets cached=true when cachedOnRetransmit is set.
        /// </summary>
        /// <param name="payload">The payload of the answer.</param>
        /// <param name="hold">The commands that stay unanswered, or null.</param>
        /// <param name="time">The server time of the answers, in unix milliseconds.</param>
        /// <param name="cachedOnRetransmit">A retransmit is answered from the record (cached).</param>
        internal static Func<Protocol.Command, Protocol.Reply?> Serve(string payload, Func<Protocol.Command, bool>? hold = null, long time = 0, bool cachedOnRetransmit = false) {
            return (Protocol.Command cmd) => {
                if (cmd.Rpc == null) {
                    return null;
                }
                if (hold != null && hold(cmd)) {
                    return FakeCentrifugoServer.NoReply;
                }
                return new Protocol.Reply {
                    Id = cmd.Id,
                    Time = time,
                    Rpc = new Protocol.RPCResult {
                        Data = ByteString.CopyFromUtf8(payload),
                        Cached = cachedOnRetransmit && cmd.Rpc.Retransmit,
                    },
                };
            };
        }


        /// <summary>
        /// Holds the commands for which holds is true: the fake server keeps them unanswered until a release. The answer hook
        /// returns no answer for them, because the fake server holds a command only when the answer hook gives none.
        /// </summary>
        /// <param name="server">The fake server.</param>
        /// <param name="holds">The commands to hold. Connect commands are not held unless the predicate says so.</param>
        internal static void Defer(FakeCentrifugoServer server, Func<Protocol.Command, bool> holds) {
            Func<Protocol.Command, Protocol.Reply?>? previous = server.OnCommand;
            server.OnCommand = (Protocol.Command cmd) => holds(cmd) ? null : previous?.Invoke(cmd);
            server.DeferCommand(holds);
        }


        /// <summary>
        /// Counts the commands that match as the fake server receives them. The counter is installed on top of the answer
        /// hook, so it is installed after <see cref="Defer"/> and then also counts the commands that are held.
        /// </summary>
        /// <param name="server">The fake server.</param>
        /// <param name="arrivals">The counter that receives one arrival per matching command.</param>
        /// <param name="matches">The commands to count.</param>
        internal static void CountArrivals(FakeCentrifugoServer server, CommandArrivals arrivals, Func<Protocol.Command, bool> matches) {
            Func<Protocol.Command, Protocol.Reply?>? previous = server.OnCommand;
            server.OnCommand = (Protocol.Command cmd) => {
                if (matches(cmd)) {
                    arrivals.Add();
                }
                return previous?.Invoke(cmd);
            };
        }


        /// <summary>
        /// Answers the held commands that match, with the payload "ok", in the order received.
        /// </summary>
        /// <param name="server">The fake server.</param>
        /// <param name="matches">The commands to answer.</param>
        internal static async Task ReleaseAsync(FakeCentrifugoServer server, Func<Protocol.Command, bool> matches) {
            foreach (Protocol.Command cmd in server.Received.Where(matches).ToList()) {
                await server.SendReplyAsync(new Protocol.Reply {
                    Id = cmd.Id,
                    Rpc = new Protocol.RPCResult { Data = ByteString.CopyFromUtf8("ok") },
                });
            }
        }


        /// <summary>The rpc requests the fake server received, without cancels, in order.</summary>
        /// <param name="server">The fake server.</param>
        internal static List<Protocol.Command> Requests(FakeCentrifugoServer server) {
            return server.Received.Where((Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Cancel == 0).ToList();
        }


        /// <summary>The cancel commands the fake server received, in order.</summary>
        /// <param name="server">The fake server.</param>
        internal static List<Protocol.Command> Cancels(FakeCentrifugoServer server) {
            return server.Received.Where((Protocol.Command cmd) => cmd.Rpc != null && cmd.Rpc.Cancel != 0).ToList();
        }


        /// <summary>
        /// Answers a held command, by its command id, with a payload.
        /// </summary>
        /// <param name="server">The fake server.</param>
        /// <param name="commandId">The id of the held command.</param>
        /// <param name="payload">The payload of the answer.</param>
        internal static Task AnswerAsync(FakeCentrifugoServer server, uint commandId, string payload) {
            return server.SendReplyAsync(new Protocol.Reply {
                Id = commandId,
                Rpc = new Protocol.RPCResult { Data = ByteString.CopyFromUtf8(payload) },
            });
        }


        /// <summary>
        /// The value of a task, failing after the wait of the tests. Keeps a stuck test from hanging the run.
        /// </summary>
        /// <param name="task">The task.</param>
        internal static Task<T> Bounded<T>(Task<T> task) {
            return task.WaitAsync(Wait);
        }
    }
}
