using System;
using System.Linq;
using Centrifugal.Centrifuge.Protocol;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// The connect path: the accepted connect reply, its server-side subscriptions and the notices of the event handlers
    /// it raises, and the resume of a connect reply that the token exchange held.
    /// </summary>
    public partial class CentrifugeClient
    {
        /// <summary>
        /// The connect path after the reply is accepted (the held reply of the token exchange resumes here too).
        /// Order: the connection fields and the Connected state, then the expiry, the ping fields, the queued
        /// subscribe and rpc commands and the ping timer, and only then the event handlers, as a list of notices
        /// (<see cref="ConnectNotices"/>). A handler that fails disconnects the client once, after the list ran.
        /// </summary>
        private void AcceptConnect(ConnectResult result)
        {
            _clientId = result.Client;
            _connection++;
            // Before the events: a handler's commands go to the emulation session of this connection.
            _session = result.Session;
            _node = result.Node;
            var transition = SetState(CentrifugeClientState.Connected);
            // A StateChanged handler may have disconnected already.
            if (TransitionSuperseded(transition)) return;

            _auth.ScheduleExpiry(result.Expires, result.Ttl);

            _serverPing = result.Ping;
            _sendPong = result.Pong;
            SendSubscribeCommands();
            _host.FlushRpc();
            if (TransitionSuperseded(transition)) return;
            if (result.Ping > 0) WaitServerPing();

            var notices = new ConnectNotices(_host, transition);
            notices.Add(() => RaiseConnectNotice(Connected, new CentrifugeConnectedEventArgs(result.Client,
                _transport?.Name ?? string.Empty, result.Data.ToByteArray(), result.User, result.AuthClass)), false);
            // Runs after a failing Connected handler: the identity is recorded either way.
            notices.Add(() => ReportConnectFailure(ConnectIdentityNotice(result)), true);
            AddServerSubNotices(notices, result.Subs);

            var failure = notices.Run();
            if (failure != null && !TransitionSuperseded(transition)) HandlerFailed(failure);
            if (!TransitionSuperseded(transition)) _promises.ResolveAll();
        }

        /// <summary>
        /// Runs one step of the connect reply path: the state work before the notices, the subscribe commands and the flush
        /// after them. A failure outside the notice list is reported as an error of type "unclassified" wrapping the
        /// exception, and the client disconnects once with code 2 (BadProtocol) and no reconnect. The failures of the
        /// event handlers are handled by the notice list, not here.
        /// </summary>
        /// <param name="step">The step of the connect reply path.</param>
        private void RunConnectReplyStep(Action step)
        {
            try
            {
                step();
            }
            catch (Exception error)
            {
                EmitErrorEvent("unclassified", new CentrifugeUnclassifiedException(error));
                DisconnectInternal(CentrifugeDisconnectedCodes.BadProtocol, "bad protocol (message)", false);
            }
        }

        /// <summary>
        /// Reports a failure of a connect notice as an error of type "unclassified", wrapping the handler's exception.
        /// Returns the failure unchanged; the connect path disconnects once after the list ran.
        /// </summary>
        /// <param name="failure">The handler failure, or null.</param>
        private Exception? ReportConnectFailure(Exception? failure)
        {
            if (failure != null) EmitErrorEvent("unclassified", new CentrifugeUnclassifiedException(failure));
            return failure;
        }

        /// <summary>
        /// Raises one handler of the connect notice list. A failure is reported as "unclassified" (see <see cref="ReportConnectFailure"/>),
        /// not under the handler's own event type, and returned.
        /// </summary>
        private Exception? RaiseConnectNotice<TArgs>(EventHandler<TArgs>? handler, TArgs args)
        {
            return ReportConnectFailure(EventDispatch.Raise(this, handler, args, "connect", null, _logger));
        }

        /// <summary>
        /// The identity notice of a connect reply. The auth event is raised without its own error report, so the
        /// failure of its handler is reported once, as "unclassified", by the caller.
        /// </summary>
        /// <param name="result">The connect reply.</param>
        private Exception? ConnectIdentityNotice(ConnectResult result)
        {
            _connectAuthRaise = true;
            try
            {
                return _identity.OnConnectIdentity(result);
            }
            finally
            {
                _connectAuthRaise = false;
            }
        }

        /// <summary>
        /// Adds the server-side notices of a connect reply: each channel subscribed (in map order), then the
        /// recovered publications, then the channels the reply doesn't have any longer, unsubscribed. The
        /// registry entry of a subscribed channel is stored when its notice runs, so a skipped notice stores nothing.
        /// </summary>
        /// <param name="notices">The notice list of the reply.</param>
        /// <param name="subs">The server-side subscriptions of the reply, by channel.</param>
        private void AddServerSubNotices(ConnectNotices notices, Google.Protobuf.Collections.MapField<string, SubscribeResult> subs)
        {
            foreach (var kvp in subs)
            {
                var channel = kvp.Key;
                var sub = kvp.Value;
                notices.Add(() =>
                {
                    _serverSubs[channel] = new ServerSubscription(sub.Offset, sub.Epoch, sub.Recoverable) { Subscribed = true };
                    return RaiseConnectNotice(ServerSubscribed, ServerSubscribedArgs(channel, sub.WasRecovering, sub.Recovered,
                        sub.Recoverable, sub.Positioned, sub.Offset, sub.Epoch, sub.Data));
                }, false);
            }

            foreach (var kvp in subs)
            {
                if (!kvp.Value.Recovered) continue;
                var channel = kvp.Key;
                foreach (var pub in kvp.Value.Publications)
                {
                    var publication = pub;
                    notices.Add(() => RaiseServerPublication(channel, publication, true), true);
                }
            }

            foreach (var channel in _serverSubs.Keys.ToList())
            {
                if (subs.ContainsKey(channel)) continue;
                var removed = channel;
                notices.Add(() =>
                {
                    _serverSubs.Remove(removed);
                    return RaiseConnectNotice(ServerUnsubscribed, new CentrifugeServerUnsubscribedEventArgs(removed));
                }, false);
            }
        }

        /// <summary>
        /// Resumes a connect reply that the token exchange held (see <see cref="IClientHost.ResumeConnect"/>).
        /// Does nothing unless the client is still connecting.
        /// </summary>
        /// <param name="result">The held connect reply.</param>
        internal void ResumeHeldConnect(ConnectResult result)
        {
            if (_state != CentrifugeClientState.Connecting) return;
            RunConnectReplyStep(() => AcceptConnect(result));
        }
    }
}
