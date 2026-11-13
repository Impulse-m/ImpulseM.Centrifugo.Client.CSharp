using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The rpc calls of the client. Every call goes through here. A call with a msg_id (a tracked call, on a connection that
    /// confirms RPC options and for a client with an instance) is answered from the server record after a lost reply:
    /// it is sent again with the same msg_id. Cancels and acks travel with the calls. All state is changed on the client
    /// loop, in steps only. Every command id comes from IClientHost.NextCommandId.
    /// </summary>
    internal sealed class RpcSession {
        private readonly IClientHost host;
        private readonly IStepTimerFactory timers;
        private readonly int maxPending;
        private readonly MsgIdGenerator msgIds;
        private readonly RpcQueue queue = new RpcQueue();
        private readonly Dictionary<uint, RpcCallState> wire = new Dictionary<uint, RpcCallState>();
        private readonly Dictionary<uint, CancelCommand> cancelCommands = new Dictionary<uint, CancelCommand>();
        private readonly List<ulong> keptCancels = new List<ulong>();
        private readonly Dictionary<uint, LateAttempt> lateAttempts = new Dictionary<uint, LateAttempt>();
        private readonly List<ulong> pendingAcks = new List<ulong>();
        private int slots;


        // A pending command of a cancel: its msg_id and the acks it carries.
        private sealed class CancelCommand {
            /// <summary>
            /// Creates the entry of one cancel command.
            /// </summary>
            /// <param name="msgId">The msg_id the cancel names.</param>
            /// <param name="acks">The acks the cancel carries.</param>
            public CancelCommand(ulong msgId, List<ulong> acks) {
                MsgId = msgId;
                Acks = acks;
            }


            /// <summary>The msg_id the cancel names.</summary>
            public ulong MsgId { get; }


            /// <summary>The acks the cancel carries.</summary>
            public List<ulong> Acks { get; }


            /// <summary>The reply timer of the cancel.</summary>
            public IStepTimer? Timer { get; set; }
        }


        // An attempt that was settled locally while it was on the wire: its reply is awaited for one call timeout.
        private sealed class LateAttempt {
            /// <summary>
            /// Creates the entry of one late attempt.
            /// </summary>
            /// <param name="msgId">The msg_id of the attempt.</param>
            /// <param name="tracked">The attempt carried the msg_id.</param>
            public LateAttempt(ulong msgId, bool tracked) {
                MsgId = msgId;
                Tracked = tracked;
            }


            /// <summary>The msg_id of the attempt.</summary>
            public ulong MsgId { get; }


            /// <summary>The attempt carried the msg_id.</summary>
            public bool Tracked { get; }


            /// <summary>The timer that ends the wait.</summary>
            public IStepTimer? Timer { get; set; }
        }


        /// <summary>
        /// Creates the rpc session of one client.
        /// </summary>
        /// <param name="host">The client seam.</param>
        /// <param name="timers">The step timer factory.</param>
        /// <param name="maxPending">Options.MaxPendingRpc.</param>
        /// <param name="random">The per-client random source. The msg_id lanes are drawn from it.</param>
        internal RpcSession(IClientHost host, IStepTimerFactory timers, int maxPending, Random random) {
            this.host = host;
            this.timers = timers;
            this.maxPending = maxPending;
            msgIds = new MsgIdGenerator(random);
        }


        /// <summary>
        /// Handles a reply whose id belongs to an rpc attempt, a cancel command or a late attempt.
        /// </summary>
        /// <param name="reply">The reply.</param>
        /// <returns>True when the reply answers a pending command, so that it may set the server time.</returns>
        public bool OnReply(Protocol.Reply reply) {
            if (wire.TryGetValue(reply.Id, out RpcCallState? call)) {
                wire.Remove(reply.Id);
                call.OnWire = false;
                Answer(call, reply);
                return true;
            }
            if (cancelCommands.TryGetValue(reply.Id, out CancelCommand? cancel)) {
                cancelCommands.Remove(reply.Id);
                cancel.Timer?.Cancel();
                return true;
            }
            if (lateAttempts.TryGetValue(reply.Id, out LateAttempt? late)) {
                lateAttempts.Remove(reply.Id);
                late.Timer?.Cancel();
                if (late.Tracked && !RefusesMsgId(reply)) {
                    pendingAcks.Add(late.MsgId);
                }
                return false;
            }
            return false;
        }


        /// <summary>
        /// Writes the queued calls to the transport. There is no in-flight or connected check here: CentrifugeClient.FlushRpc
        /// makes that check before it calls this method. The kept cancels go first.
        /// </summary>
        public void Flush() {
            if (!host.RpcOptionsSupported) {
                keptCancels.Clear();
            } else {
                List<ulong> kept = new List<ulong>(keptCancels);
                keptCancels.Clear();
                foreach (ulong msgId in kept) {
                    WriteCancel(msgId);
                }
            }
            foreach (RpcCallState call in queue.Drain()) {
                if (call.Done) {
                    continue;
                }
                if (call.MsgId != 0 && !host.RpcOptionsSupported) {
                    Settle(call, null, ConnectionLostError(call), false);
                    continue;
                }
                WriteCall(call);
            }
        }


        /// <summary>
        /// Settles the calls of a lost transport. With keep, the tracked calls wait for the next connection and the others
        /// fail. Without keep (a terminal loss), every call fails, and the cancels of the calls on the wire are kept for the
        /// next connection that confirms RPC options. Idempotent.
        /// </summary>
        /// <param name="keep">True when the client will reconnect.</param>
        public void OnTransportLost(bool keep) {
            ClearLateAttempts();
            List<CancelCommand> cancels = cancelCommands.Values.OrderBy((CancelCommand entry) => entry.MsgId).ToList();
            cancelCommands.Clear();
            foreach (CancelCommand entry in cancels) {
                entry.Timer?.Cancel();
                KeepCancel(entry.MsgId);
                PutBackAcks(entry.Acks);
            }
            List<RpcCallState> onWire = wire.Values.OrderBy((RpcCallState call) => call.MsgId).ToList();
            wire.Clear();
            foreach (RpcCallState call in onWire) {
                call.OnWire = false;
                PutBackAcks(call.CarriedAcks);
                call.CarriedAcks = null;
                if (keep && call.WireTracked) {
                    queue.Add(call);
                    continue;
                }
                if (!keep && call.WireTracked) {
                    KeepCancel(call.MsgId);
                }
                Settle(call, null, ConnectionLostError(call), false);
            }
            if (!keep) {
                pendingAcks.Clear();
                foreach (RpcCallState call in queue.Drain()) {
                    Settle(call, null, ClosedError(call), false);
                }
            }
        }


        /// <summary>Settles the pending calls when the client closes. The terminal loss has run already.</summary>
        public void OnClose() {
            OnTransportLost(false);
            keptCancels.Clear();
        }


        /// <summary>
        /// Starts a call (step). The deadline timer starts now. The call is queued and written when the client is connected
        /// and no refresh is in flight.
        /// </summary>
        /// <param name="call">The new call.</param>
        internal void Submit(RpcCallState call) {
            if (call.Done) {
                // The call was cancelled before its submit step ran (a cancelled token): it holds no slot and no timer.
                return;
            }
            if (host.IsClosed) {
                Settle(call, null, ClosedError(call), false);
                return;
            }
            if (host.HasInstance && slots >= maxPending) {
                Settle(call, null, new CentrifugeException(CentrifugeErrorCodes.LimitExceeded, "too many rpc calls are pending", false), false);
                return;
            }
            if (host.HasInstance) {
                call.HoldsSlot = true;
                slots++;
            }
            call.DeadlineTimer = timers.Start(call.TimeoutMs, () => OnDeadline(call));
            queue.Add(call);
            host.FlushRpc();
        }


        /// <summary>
        /// Cancels a call (step). A call that was not sent is never sent. A call that was sent is cancelled on the server
        /// when the client can carry the cancel, else the cancel is kept for the next connection.
        /// </summary>
        /// <param name="call">The call.</param>
        internal void Cancel(RpcCallState call) {
            if (call.Done) {
                return;
            }
            if (!call.EverSent) {
                Settle(call, null, CancelledError(), false);
                return;
            }
            if (!host.HasInstance) {
                Settle(call, null, new CentrifugeException(CentrifugeErrorCodes.Unsupported, "rpc cancel needs the instance option", false), true);
                return;
            }
            if (host.IsConnected && !host.RpcOptionsSupported) {
                Settle(call, null, new CentrifugeException(CentrifugeErrorCodes.Unsupported, "rpc cancel is not supported by the server", false), true);
                return;
            }
            bool wasWritten = call.MsgId != 0;
            Settle(call, null, CancelledError(), true);
            if (wasWritten) {
                SendCancel(call.MsgId);
            }
        }


        // The deadline of a call. A call written tracked that did not answer is cancelled on the server.
        private void OnDeadline(RpcCallState call) {
            call.DeadlineTimer = null;
            if (call.Done) {
                return;
            }
            bool wasWritten = call.MsgId != 0;
            Settle(call, null, new CentrifugeTimeoutException("timeout"), true);
            if (wasWritten) {
                SendCancel(call.MsgId);
            }
        }


        // Writes one queued call (the first write takes its msg_id when it is tracked).
        private void WriteCall(RpcCallState call) {
            bool supported = host.RpcOptionsSupported;
            if (call.Headers.Count > 0 && !supported) {
                Settle(call, null, new CentrifugeException(CentrifugeErrorCodes.Unsupported, "rpc headers are not supported by the server", false), false);
                return;
            }
            bool tracked = supported && host.HasInstance;
            if (tracked && call.MsgId == 0) {
                try {
                    call.MsgId = msgIds.Next(host.ServerTimeMillis());
                } catch (InvalidOperationException error) {
                    Settle(call, null, error, false);
                    return;
                }
            }
            uint commandId = host.NextCommandId();
            Protocol.RPCRequest rpc = new Protocol.RPCRequest {
                Method = call.Method,
                Data = ByteString.CopyFrom((call.Data ?? ReadOnlyMemory<byte>.Empty).Span),
            };
            foreach (KeyValuePair<string, string> header in call.Headers) {
                rpc.Headers.Add(header.Key, header.Value);
            }
            if (tracked) {
                rpc.MsgId = call.MsgId;
                rpc.Retransmit = call.EverSent;
                List<ulong> acks = TakeAcks();
                rpc.Ack.AddRange(acks);
                call.CarriedAcks = acks;
            }
            Protocol.Command command = new Protocol.Command {
                Id = commandId,
                Rpc = rpc,
            };
            // A refused first write leaves the call never sent: the flag is kept until the write is known to have failed.
            bool firstWrite = !call.EverSent;
            call.EverSent = true;
            call.CommandId = commandId;
            call.WireTracked = tracked;
            call.OnWire = true;
            wire[commandId] = call;
            TaskCompletionSource<bool> sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.QueueCommand(command, sent);
            WatchWrite(sent, (Exception error) => OnCallWriteFailed(call, commandId, firstWrite, error));
        }


        // The write of an attempt failed (or the transport refused it). A tracked call waits in the queue; another one fails.
        // A refused first write takes the call back to the state of a call never sent: its next write takes a new msg_id.
        private void OnCallWriteFailed(RpcCallState call, uint commandId, bool firstWrite, Exception error) {
            if (!wire.TryGetValue(commandId, out RpcCallState? current) || !ReferenceEquals(current, call)) {
                // The transport loss moved the call already.
                return;
            }
            wire.Remove(commandId);
            call.OnWire = false;
            PutBackAcks(call.CarriedAcks);
            call.CarriedAcks = null;
            if (call.WireTracked) {
                if (firstWrite) {
                    call.EverSent = false;
                    call.MsgId = 0;
                }
                queue.Add(call);
                return;
            }
            Settle(call, null, WriteFailure(call, error), false);
        }


        // The error of a failed write. A transport error (code 10 or 11) keeps its code, message, temporary flag and cause; any
        // other error becomes code 10 with the error as its cause. A call started through a fork entry point gets the loss type.
        private static Exception WriteFailure(RpcCallState call, Exception error) {
            if (error is CentrifugeException failure) {
                bool isLoss = failure.Code == CentrifugeErrorCodes.TransportWriteError || failure.Code == CentrifugeErrorCodes.ConnectionClosed;
                if (!call.ForkEntry || !isLoss || failure is CentrifugeConnectionLostException) {
                    return failure;
                }
                return new CentrifugeConnectionLostException(failure.Code, failure.Message, failure.Temporary, failure.InnerException);
            }
            return Loss(call, CentrifugeErrorCodes.TransportWriteError, "transport write error", true, error);
        }


        // Writes a cancel command for a msg_id. The cancel waits for its reply for one client timeout.
        private void WriteCancel(ulong msgId) {
            uint commandId = host.NextCommandId();
            List<ulong> acks = TakeAcks();
            Protocol.RPCRequest rpc = new Protocol.RPCRequest {
                Cancel = msgId,
            };
            rpc.Ack.AddRange(acks);
            Protocol.Command command = new Protocol.Command {
                Id = commandId,
                Rpc = rpc,
            };
            CancelCommand entry = new CancelCommand(msgId, acks);
            cancelCommands[commandId] = entry;
            entry.Timer = timers.Start(host.TimeoutMs, () => ForgetCancel(commandId));
            TaskCompletionSource<bool> sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.QueueCommand(command, sent);
            WatchWrite(sent, (Exception error) => OnCancelWriteFailed(commandId, error));
        }


        // The write of a cancel failed. The cancel is kept, its acks go back, and an unexpected cause is reported.
        private void OnCancelWriteFailed(uint commandId, Exception error) {
            if (!cancelCommands.TryGetValue(commandId, out CancelCommand? entry)) {
                // The transport loss kept the cancel already.
                return;
            }
            cancelCommands.Remove(commandId);
            entry.Timer?.Cancel();
            KeepCancel(entry.MsgId);
            PutBackAcks(entry.Acks);
            Exception? cause = error is CentrifugeException ? error.InnerException : error;
            if (cause != null && !(cause is IOException)) {
                host.ReportUnclassified(cause);
            }
        }


        // Sends the cancel now when the client is connected to a server with RPC options, else keeps it.
        private void SendCancel(ulong msgId) {
            if (host.IsConnected && host.RpcOptionsSupported) {
                WriteCancel(msgId);
            } else {
                KeepCancel(msgId);
            }
        }


        // Keeps a cancel for the next connection that confirms RPC options. At most maxPending are kept: the oldest is dropped.
        private void KeepCancel(ulong msgId) {
            if (keptCancels.Count >= maxPending) {
                keptCancels.RemoveAt(0);
            }
            keptCancels.Add(msgId);
        }


        // The cancel command got no reply within one client timeout: it is not sent again.
        private void ForgetCancel(uint commandId) {
            cancelCommands.Remove(commandId);
        }


        // Takes the reply of an attempt: the call settles with the result, or with the server error.
        private void Answer(RpcCallState call, Protocol.Reply reply) {
            call.CarriedAcks = null;
            if (call.WireTracked && !RefusesMsgId(reply)) {
                pendingAcks.Add(call.MsgId);
            }
            if (reply.Error != null) {
                Settle(call, null, CentrifugeException.FromReply(reply.Error), false);
                return;
            }
            byte[] data = reply.Rpc?.Data.ToByteArray() ?? Array.Empty<byte>();
            Settle(call, new CentrifugeRpcResult(data, reply.Rpc?.Cached ?? false), null, false);
        }


        // Decides the call: its completion is delivered after the step. The attempt on the wire is left as a late attempt
        // when lateAttempt is set, so that its reply is not waited for after the settle.
        private void Settle(RpcCallState call, CentrifugeRpcResult? result, Exception? error, bool lateAttempt) {
            if (call.Done) {
                return;
            }
            call.Done = true;
            queue.Remove(call);
            if (call.OnWire) {
                wire.Remove(call.CommandId);
                call.OnWire = false;
                if (lateAttempt) {
                    KeepLate(call.CommandId, call.MsgId, call.WireTracked, call.TimeoutMs);
                }
            }
            call.DeadlineTimer?.Cancel();
            call.DeadlineTimer = null;
            if (call.HoldsSlot) {
                call.HoldsSlot = false;
                slots--;
            }
            call.Data = null;
            TaskCompletionSource<CentrifugeRpcResult> completion = call.Completion;
            host.Loop.Complete(() => {
                if (result != null) {
                    completion.TrySetResult(result);
                } else {
                    completion.TrySetException(error!);
                }
            });
        }


        // The late attempt keeps its msg_id for one call timeout, so that its reply is acknowledged and sets no server time.
        private void KeepLate(uint commandId, ulong msgId, bool tracked, int timeoutMs) {
            LateAttempt late = new LateAttempt(msgId, tracked);
            late.Timer = timers.Start(timeoutMs, () => {
                lateAttempts.Remove(commandId);
            });
            lateAttempts[commandId] = late;
        }


        private void ClearLateAttempts() {
            foreach (LateAttempt late in lateAttempts.Values) {
                late.Timer?.Cancel();
            }
            lateAttempts.Clear();
        }


        // Takes the acks that the next tracked request or cancel carries.
        private List<ulong> TakeAcks() {
            List<ulong> acks = new List<ulong>(pendingAcks);
            pendingAcks.Clear();
            return acks;
        }


        // Puts acks back at the front, so that they go out with the next request.
        private void PutBackAcks(List<ulong>? acks) {
            if (acks != null && acks.Count > 0) {
                pendingAcks.InsertRange(0, acks);
            }
        }


        // A reply that refuses the msg_id is not acknowledged.
        private static bool RefusesMsgId(Protocol.Reply reply) {
            if (reply.Error == null) {
                return false;
            }
            string? signal = ProtocolExtensions.RetrySignal(reply.Error);
            return signal == ProtocolExtensions.RetryNewMsgId || signal == ProtocolExtensions.RetryLater;
        }


        // Runs the write result of a frame in a step. Only a failure needs a step.
        private void WatchWrite(TaskCompletionSource<bool> sent, Action<Exception> onFailed) {
            sent.Task.ContinueWith((Task task) => {
                if (!task.IsFaulted) {
                    return;
                }
                Exception error = task.Exception!.InnerException ?? task.Exception;
                host.Loop.Execute(() => onFailed(error));
            }, TaskScheduler.Default);
        }


        // The error of a call that was never written and is cancelled or closed: CANCELLED with an instance, else the connection error.
        private Exception ClosedError(RpcCallState call) {
            if (host.HasInstance && !call.EverSent) {
                return CancelledError();
            }
            return ConnectionLostError(call);
        }


        private static Exception CancelledError() {
            return new CentrifugeException(CentrifugeErrorCodes.Cancelled, "rpc call cancelled", false);
        }


        // The error of a connection that closed under the call. The call decides the type: see Loss.
        private static Exception ConnectionLostError(RpcCallState call) {
            return Loss(call, CentrifugeErrorCodes.ConnectionClosed, "connection closed", false, null);
        }


        // A client-side loss of the connection. A call started through a fork entry point gets CentrifugeConnectionLostException, so
        // that the rpc pool recognises it by the type; a plain call keeps the upstream CentrifugeException.
        private static Exception Loss(RpcCallState call, int code, string message, bool temporary, Exception? cause) {
            if (call.ForkEntry) {
                return new CentrifugeConnectionLostException(code, message, temporary, cause);
            }
            return new CentrifugeException(code, message, temporary, cause);
        }
    }
}
