using System;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Centrifugal.Centrifuge.Transports;
using Microsoft.Extensions.Logging;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// The transport helpers of the client: a plain command sent on a given transport, the cancel of a transport at once,
    /// and the raise of a server-side publication.
    /// </summary>
    public partial class CentrifugeClient
    {
        /// <summary>
        /// Sends one plain command on <paramref name="transport"/>, which must be the current one, and waits for its
        /// reply with the client timeout. A transport that is not current, or that is lost before the reply, fails the
        /// task with <see cref="CentrifugeConnectionLostException"/> (a server error reply and a timeout keep their own
        /// exceptions). The command is not held and not retransmitted. Call in a step.
        /// </summary>
        /// <param name="transport">The transport the command goes on.</param>
        /// <param name="command">The command to send.</param>
        /// <param name="cancellationToken">Cancels the call and removes it from the client.</param>
        internal Task<Reply> SendOnTransportCall(ITransport transport, Command command, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.IsCancellationRequested)
            {
                Loop.Complete(() => result.TrySetCanceled(cancellationToken));
                return result.Task;
            }
            if (!ReferenceEquals(transport, _transport) || _writer == null)
            {
                Loop.Complete(() => result.TrySetException(new CentrifugeConnectionLostException(
                    CentrifugeErrorCodes.ConnectionClosed, "connection closed", false, null)));
                return result.Task;
            }
            var id = RegisterCall(command, reply => Loop.Complete(() => result.TrySetResult(reply)),
                (error, replied) => Loop.Complete(() => result.TrySetException(
                    replied || error is CentrifugeTimeoutException ? error : LostOnTransport(error))));
            AddCommand(command, urgent: true);
            if (!cancellationToken.CanBeCanceled) return result.Task;
            var registration = cancellationToken.Register(() => Loop.Execute(() =>
            {
                if (!_calls.TryGetValue(id, out var call)) return;
                _calls.Remove(id);
                call.Timer?.Cancel();
                Loop.Complete(() => result.TrySetCanceled(cancellationToken));
            }));
            result.Task.ContinueWith(_ => registration.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
            return result.Task;
        }

        /// <summary>The loss of a transport as its own exception type: a write error keeps code 10, anything else is 11.</summary>
        /// <param name="error">The error of the call.</param>
        private static CentrifugeConnectionLostException LostOnTransport(CentrifugeException error) =>
            new CentrifugeConnectionLostException(
                error.Code == CentrifugeErrorCodes.TransportWriteError
                    ? CentrifugeErrorCodes.TransportWriteError
                    : CentrifugeErrorCodes.ConnectionClosed,
                error.Message,
                error.Temporary,
                error);

        /// <summary>
        /// Cancels <paramref name="transport"/> at once, without a close handshake: a held attempt, or a peer that
        /// does not answer the close. Its close event then ends the attempt through the usual path.
        /// </summary>
        /// <param name="transport">The transport to cancel.</param>
        internal void CancelTransportNow(ITransport transport)
        {
            try
            {
                transport.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Transport cancel failed");
            }
        }

        /// <summary>
        /// Raises one server-side publication without disconnecting: returns the handler's failure, or null.
        /// The position moves only when the handler did not fail. A publication whose handler threw isn't
        /// delivered: the position stays before it, so the next recovery delivers it again.
        /// </summary>
        /// <param name="channel">The channel.</param>
        /// <param name="pub">The publication.</param>
        /// <param name="connectPath">True for a recovered publication of a connect reply (its failure is reported as "unclassified").</param>
        private Exception? RaiseServerPublication(string channel, Publication pub, bool connectPath = false)
        {
            var args = CreatePublicationArgs(channel, pub);
            if (pub.Offset > 0) _pendingServerSubOffsets[channel] = pub.Offset;
            Exception? failure;
            try
            {
                failure = connectPath
                    ? RaiseConnectNotice(Publication, args)
                    : _host.RaiseNotice(Publication, args, "publication");
            }
            finally
            {
                _pendingServerSubOffsets.Remove(channel);
            }
            if (failure != null) return failure;
            if (!_serverSubs.TryGetValue(channel, out var serverSub)) return null;
            if (pub.Offset > 0) serverSub.Offset = pub.Offset;
            // The epoch of a channel that had no stream at subscribe time comes with its first publication.
            if (!string.IsNullOrEmpty(pub.Epoch)) serverSub.Epoch = pub.Epoch;
            return null;
        }
    }
}
