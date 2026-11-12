using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;

using HeldCommand = System.ValueTuple<Centrifugal.Centrifuge.Protocol.Command, System.Net.WebSockets.WebSocket>;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// Test helpers of the fake server: held commands, typed replies, close codes and the connection identity.
    /// The helpers live in this partial file so that the upstream members of the fake stay unchanged.
    /// </summary>
    internal sealed partial class FakeCentrifugoServer {
        /// <summary>The user the connect or refresh reply carries, per token. A token not in the map gets the default reply.</summary>
        public IDictionary<string, string> UsersByToken { get; } = new Dictionary<string, string>();


        /// <summary>The number of commands held and not released yet.</summary>
        public int HeldCount {
            get {
                lock (_lock) {
                    return heldCommands.Count;
                }
            }
        }

        private static readonly TimeSpan awaitHeldTimeout = TimeSpan.FromSeconds(5);
        private readonly List<HeldCommand> heldCommands = new List<HeldCommand>();
        private Func<Command, bool>? holdPredicate;
        private TaskCompletionSource<bool>? heldWaiter;
        private Task releaseTail = Task.CompletedTask;


        /// <summary>
        /// Keeps each command for which the predicate is true, with the socket it arrived on, and does not answer it
        /// until a release. Null clears the predicate. The check runs after the OnCommand hook.
        /// </summary>
        /// <param name="holds">The predicate of the commands to hold.</param>
        public void DeferCommand(Func<Command, bool>? holds) {
            lock (_lock) {
                holdPredicate = holds;
            }
        }

        /// <summary>Completes when HeldCount is at least 1, or fails after five seconds. Set by the dispatch path, not by polling.</summary>
        public Task AwaitHeldAsync() {
            Task waiting;
            lock (_lock) {
                if (heldCommands.Count > 0) {
                    return Task.CompletedTask;
                }
                if (heldWaiter == null) {
                    heldWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                waiting = heldWaiter.Task;
            }
            return waiting.WaitAsync(awaitHeldTimeout);
        }

        /// <summary>Answers every held command in the order received, all on the current connection.</summary>
        public void ReleaseDeferredCommands() {
            List<HeldCommand> pending = TakeHeld(null);
            WebSocket? current;
            lock (_lock) {
                current = _current;
            }
            if (current == null) {
                return;
            }
            foreach (HeldCommand item in pending) {
                SendHeldAnswer(current, item.Item1);
            }
        }

        /// <summary>Answers the held commands that match, in the order received, each on the socket it arrived on. The others stay held.</summary>
        /// <param name="matches">The predicate of the commands to answer.</param>
        public void ReleaseDeferredMatching(Func<Command, bool> matches) {
            foreach (HeldCommand item in TakeHeld(matches)) {
                SendHeldAnswer(item.Item2, item.Item1);
            }
        }

        /// <summary>Answers every held command on the socket it arrived on, so an answer for a replaced connection still arrives.</summary>
        public void ReleaseDeferredCommandsOnTheirSockets() {
            foreach (HeldCommand item in TakeHeld(null)) {
                SendHeldAnswer(item.Item2, item.Item1);
            }
        }

        /// <summary>Sends a WebSocket close frame with the given code on the active connection.</summary>
        /// <param name="code">The WebSocket close code.</param>
        /// <param name="reason">The close reason.</param>
        public async Task CloseWithCodeAsync(ushort code, string reason) {
            WebSocket socket;
            lock (_lock) {
                socket = _current ?? throw new InvalidOperationException("no active connection");
            }
            await _sendLock.WaitAsync();
            try {
                await socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
            } catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) {
                // The client closed the connection first.
            } finally {
                _sendLock.Release();
            }
        }

        /// <summary>Sends a push that carries a Disconnect with the code and reason.</summary>
        /// <param name="code">The disconnect code.</param>
        /// <param name="reason">The disconnect reason.</param>
        public Task SendDisconnectPushAsync(uint code, string reason) {
            return SendPushAsync(new Push { Disconnect = new Disconnect { Code = code, Reason = reason } });
        }

        /// <summary>Sends one ping: a single zero byte, which is an empty command.</summary>
        public Task SendPingAsync() {
            return SendRawAsync(new byte[] { 0 });
        }

        /// <summary>Makes the connect reply confirm RPC options (flag bit 1 shifted by 32).</summary>
        public void EnableRpcOptions() {
            ConnectResult.Flag = ConnectCapabilities.RpcOptionsFlag;
        }

        /// <summary>Sets the user and the auth class of the connect reply.</summary>
        /// <param name="user">The user id.</param>
        /// <param name="authClass">The auth class.</param>
        public void ConnectAs(string user, string authClass) {
            ConnectResult.User = user;
            ConnectResult.AuthClass = authClass;
        }

        /// <summary>
        /// Builds an error reply. The caller sets the Id of the reply to the id of the command it answers.
        /// </summary>
        /// <param name="code">The error code.</param>
        /// <param name="message">The error message.</param>
        /// <param name="temporary">The temporary flag.</param>
        /// <param name="metadata">The metadata of the error, or null.</param>
        public Reply ErrorReply(uint code, string message, bool temporary, IDictionary<string, string>? metadata) {
            Error error = new Error { Code = code, Message = message, Temporary = temporary };
            if (metadata != null) {
                foreach (KeyValuePair<string, string> pair in metadata) {
                    error.Metadata[pair.Key] = pair.Value;
                }
            }
            return new Reply { Error = error };
        }

        /// <summary>The last received command that carries a publish, or null.</summary>
        public Command? LastPublish() {
            lock (_lock) {
                for (int i = _received.Count - 1; i >= 0; i--) {
                    if (_received[i].Publish != null) {
                        return _received[i];
                    }
                }
                return null;
            }
        }

        private bool HoldIfDeferred(WebSocket socket, Command cmd) {
            lock (_lock) {
                if (holdPredicate == null || !holdPredicate(cmd)) {
                    return false;
                }
                heldCommands.Add((cmd, socket));
                heldWaiter?.TrySetResult(true);
                heldWaiter = null;
                return true;
            }
        }

        private List<HeldCommand> TakeHeld(Func<Command, bool>? matches) {
            List<HeldCommand> taken = new List<HeldCommand>();
            lock (_lock) {
                List<HeldCommand> kept = new List<HeldCommand>();
                foreach (HeldCommand item in heldCommands) {
                    if (matches == null || matches(item.Item1)) {
                        taken.Add(item);
                    } else {
                        kept.Add(item);
                    }
                }
                heldCommands.Clear();
                heldCommands.AddRange(kept);
            }
            return taken;
        }

        private void SendHeldAnswer(WebSocket socket, Command command) {
            Reply? reply = DefaultReply(command);
            if (reply == null) {
                return;
            }
            Reply answer = reply;
            lock (_lock) {
                releaseTail = releaseTail.ContinueWith(_ => SendIgnoringClosed(socket, answer)).Unwrap();
            }
        }

        private async Task SendIgnoringClosed(WebSocket socket, Reply reply) {
            try {
                await SendAsync(socket, reply);
            } catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) {
                // The connection closed before the answer; the client sees no answer, as on a closed socket.
            }
        }

        /// <summary>
        /// The answer the fake gives when no hook answers: the connect and the refresh of a token in the user map,
        /// then the connect, subscribe and unsubscribe defaults, then an empty reply to any other command with an id.
        /// Null when the command gets no answer.
        /// </summary>
        private Reply? DefaultReply(Command cmd) {
            if (cmd.Connect != null) {
                if (UsersByToken.TryGetValue(cmd.Connect.Token, out string? connectUser)) {
                    ConnectResult withUser = ConnectResult.Clone();
                    withUser.User = connectUser;
                    return new Reply { Id = cmd.Id, Connect = withUser };
                }
                return new Reply { Id = cmd.Id, Connect = ConnectResult };
            }
            if (cmd.Refresh != null && UsersByToken.TryGetValue(cmd.Refresh.Token, out string? refreshUser)) {
                return new Reply { Id = cmd.Id, Refresh = new RefreshResult { User = refreshUser } };
            }
            if (cmd.Subscribe != null) {
                SubscribeResult result = OnSubscribe != null
                    ? OnSubscribe(cmd.Subscribe.Channel, cmd.Subscribe)
                    : new SubscribeResult();
                return new Reply { Id = cmd.Id, Subscribe = result };
            }
            if (cmd.Unsubscribe != null) {
                return new Reply { Id = cmd.Id, Unsubscribe = new UnsubscribeResult() };
            }
            if (cmd.Id != 0) {
                // Reply to anything else with an empty result to avoid client timeouts.
                return new Reply { Id = cmd.Id };
            }
            return null;
        }
    }
}
