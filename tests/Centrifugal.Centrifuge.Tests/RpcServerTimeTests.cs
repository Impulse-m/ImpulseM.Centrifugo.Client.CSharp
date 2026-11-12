// The server time that the rpc calls take their msg_id clock from, and the replies of a legacy and an rpc call on one connection.
// Not ported: the push half of ServerTimeTest.serverTimeIsTakenOnlyFromReplies: the fake server cannot send a push that carries a time.
// The reply half (a reply that answers no pending command sets no offset) is ServerTime_UnsolicitedReply_DoesNotSetTheOffset.
// Not ported: ServerTimeTest.lateReplyToATimedOutCallDoesNotSetTheOffset and lateReplyToATrackedTimedOutCallDoesNotSetTheOffset:
// they need a reply that arrives after the deadline, which the fake server cannot time against the real loop.
// Not ported: CommandIdTest.commandIdWrapsWithoutTrapping: the id counter is upstream code and the port does not change it.

using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The server time of the replies, and the replies of rpc and legacy calls, against the fake server.</summary>
    [Collection("Integration")]
    public sealed class RpcServerTimeTests : RpcTestBase {
        private const long LocalMillis = 1_700_000_000_000L;
        private const long MinuteMillis = 60_000L;


        // Java: ServerTimeTest.serverTimeFollowsReplyTime.
        /// <summary>After a reply that carries a server time, the next msg_id takes its time part from the server time.</summary>
        [Fact]
        public async Task ServerTime_FollowsTheReplyTime() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok", time: LocalMillis + MinuteMillis);
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            client.WallClockMs = () => LocalMillis;
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await client.RpcAsync("first", ReadOnlyMemory<byte>.Empty);
            await client.RpcAsync("second", ReadOnlyMemory<byte>.Empty);

            ulong msgId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "second").Rpc.MsgId;
            Assert.Equal((ulong)((LocalMillis + MinuteMillis) / 1000), msgId >> MsgIdGenerator.SequenceBits);
        }


        // Java: ServerTimeTest.replyWithoutServerTimeKeepsLastOffset.
        /// <summary>A reply without a server time keeps the offset of the last reply that carried one.</summary>
        [Fact]
        public async Task ServerTime_ReplyWithoutTime_KeepsTheLastOffset() {
            EnableRpcOptions();
            Server.OnCommand = (Protocol.Command cmd) => {
                if (cmd.Rpc == null) {
                    return null;
                }
                long time = cmd.Rpc.Method == "untimed" ? 0 : LocalMillis + MinuteMillis;
                return new Protocol.Reply { Id = cmd.Id, Time = time, Rpc = new Protocol.RPCResult() };
            };
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            client.WallClockMs = () => LocalMillis;
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await client.RpcAsync("timed", ReadOnlyMemory<byte>.Empty);
            await client.RpcAsync("untimed", ReadOnlyMemory<byte>.Empty);
            await client.RpcAsync("after", ReadOnlyMemory<byte>.Empty);

            ulong msgId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "after").Rpc.MsgId;
            Assert.Equal((ulong)((LocalMillis + MinuteMillis) / 1000), msgId >> MsgIdGenerator.SequenceBits);
        }


        /// <summary>A reply time more than a year away from the local clock is ignored, on the rpc path.</summary>
        [Fact]
        // Java: serverTimeIgnoresAnImplausibleReplyTime
        public async Task ServerTime_ImplausibleReplyTime_IsIgnored() {
            EnableRpcOptions();
            long implausible = LocalMillis + 400L * 24L * 60L * 60L * 1000L;
            Server.OnCommand = RpcTestHelpers.Serve("ok", time: implausible);
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            client.WallClockMs = () => LocalMillis;
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await client.RpcAsync("first", ReadOnlyMemory<byte>.Empty);
            await client.RpcAsync("second", ReadOnlyMemory<byte>.Empty);

            ulong msgId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "second").Rpc.MsgId;
            Assert.Equal((ulong)(LocalMillis / 1000), msgId >> MsgIdGenerator.SequenceBits);
        }


        /// <summary>Before any reply carries a server time, the public server time is the local clock.</summary>
        [Fact]
        public void ServerTime_BeforeAnyReply_IsTheLocalClock() {
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            client.WallClockMs = () => LocalMillis;

            Assert.Equal(LocalMillis, client.ServerTimeMillis());
        }


        /// <summary>
        /// A reply that answers no pending command does not set the server time, even when its time is an hour away from the
        /// local clock: the next msg_id keeps the local second.
        /// </summary>
        [Fact]
        public async Task ServerTime_UnsolicitedReply_DoesNotSetTheOffset() {
            EnableRpcOptions();
            Server.OnCommand = RpcTestHelpers.Serve("ok");
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            client.WallClockMs = () => LocalMillis;
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            await client.RpcAsync("first", ReadOnlyMemory<byte>.Empty);
            await Server.SendReplyAsync(new Protocol.Reply { Id = 1_000_000, Time = LocalMillis + 60 * MinuteMillis });
            // The barrier is answered after the unsolicited reply on the same socket, so the reply is handled before the next call.
            await client.RpcAsync("barrier", ReadOnlyMemory<byte>.Empty);
            await client.RpcAsync("second", ReadOnlyMemory<byte>.Empty);

            ulong msgId = RpcTestHelpers.Requests(Server).Single((Protocol.Command cmd) => cmd.Rpc.Method == "second").Rpc.MsgId;
            Assert.Equal((ulong)(LocalMillis / 1000), msgId >> MsgIdGenerator.SequenceBits);
        }


        /// <summary>A legacy call and an rpc call on one connection each get their own reply.</summary>
        [Fact]
        public async Task LegacyAndRpcCalls_OnOneConnection_GetTheirOwnReplies() {
            EnableRpcOptions();
            Func<Protocol.Command, Protocol.Reply?> serveRpc = RpcTestHelpers.Serve("rpc");
            Server.OnCommand = (Protocol.Command cmd) => {
                if (cmd.Presence != null) {
                    return new Protocol.Reply { Id = cmd.Id, Presence = new Protocol.PresenceResult() };
                }
                return serveRpc(cmd);
            };
            CentrifugeClient client = NewClient(new CentrifugeClientOptions { Instance = RpcTestHelpers.Instance });
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            client.Connect();
            await connected;

            Task<CentrifugeRpcResult> rpc = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);
            Task<CentrifugePresenceResult> presence = client.PresenceAsync("channel");

            CentrifugeRpcResult result = await RpcTestHelpers.Bounded(rpc);
            await RpcTestHelpers.Bounded(presence);

            Assert.Equal("rpc", System.Text.Encoding.UTF8.GetString(result.Data.Span));
            Assert.Single(RpcTestHelpers.Requests(Server), (Protocol.Command cmd) => cmd.Rpc.Method == "method");
        }
    }
}
