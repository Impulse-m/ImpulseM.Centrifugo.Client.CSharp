using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Centrifugal.Centrifuge.Transports;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// A transport that forwards to a real one. A test can refuse an outgoing frame: the frame then fails its send, with the
    /// error returned by the refusal, either as a faulted task or as a synchronous throw, and it never reaches the server. A
    /// test can also act once each incoming frame has been handed to the client.
    /// </summary>
    internal sealed class RpcWireTransport : ITransport {
        private readonly ITransport inner;
        private readonly Func<IReadOnlyList<Command>, Exception?>? refuse;
        private readonly bool throwOnRefuse;
        private readonly Action<IReadOnlyList<byte[]>>? afterReceived;


        /// <summary>
        /// Creates the transport.
        /// </summary>
        /// <param name="inner">The real transport the events and the sends go to.</param>
        /// <param name="refuse">Returns the error that fails a frame, or null to send it. Receives the commands of the frame.</param>
        /// <param name="throwOnRefuse">True when a refused frame throws from SendAsync instead of returning a faulted task.</param>
        /// <param name="afterReceived">Called after each incoming frame has been handed to the client, or null.</param>
        public RpcWireTransport(
            ITransport inner,
            Func<IReadOnlyList<Command>, Exception?>? refuse,
            bool throwOnRefuse,
            Action<IReadOnlyList<byte[]>>? afterReceived) {
            this.inner = inner;
            this.refuse = refuse;
            this.throwOnRefuse = throwOnRefuse;
            this.afterReceived = afterReceived;
            inner.Opened += (object? sender, EventArgs args) => Opened?.Invoke(this, args);
            inner.MessageReceived += (object? sender, IReadOnlyList<byte[]> messages) => {
                MessageReceived?.Invoke(this, messages);
                afterReceived?.Invoke(messages);
            };
            inner.Closed += (object? sender, TransportClosedEventArgs args) => Closed?.Invoke(this, args);
            inner.Error += (object? sender, Exception error) => Error?.Invoke(this, error);
        }


        /// <summary>The transport type of the inner transport.</summary>
        public CentrifugeTransportType Type {
            get {
                return inner.Type;
            }
        }


        /// <summary>The name of the inner transport.</summary>
        public string Name {
            get {
                return inner.Name;
            }
        }


        /// <summary>True when the inner transport uses emulation.</summary>
        public bool UsesEmulation {
            get {
                return inner.UsesEmulation;
            }
        }


        /// <summary>Raised when the inner transport opens.</summary>
        public event EventHandler? Opened;


        /// <summary>Raised for each frame of the inner transport.</summary>
        public event EventHandler<IReadOnlyList<byte[]>>? MessageReceived;


        /// <summary>Raised when the inner transport closes.</summary>
        public event EventHandler<TransportClosedEventArgs>? Closed;


        /// <summary>Raised when the inner transport fails.</summary>
        public event EventHandler<Exception>? Error;


        /// <summary>Opens the inner transport.</summary>
        /// <param name="cancellationToken">The open deadline.</param>
        /// <param name="initialData">The initial data of an emulation transport.</param>
        public Task OpenAsync(CancellationToken cancellationToken = default, byte[]? initialData = null) {
            return inner.OpenAsync(cancellationToken, initialData);
        }


        /// <summary>Sends a frame through the inner transport, or fails it when the refusal names an error.</summary>
        /// <param name="data">The frame.</param>
        /// <param name="cancellationToken">The write deadline.</param>
        public Task SendAsync(byte[] data, CancellationToken cancellationToken = default) {
            Exception? failure = refuse?.Invoke(ParseFrame(data));
            if (failure != null) {
                if (throwOnRefuse) {
                    throw failure;
                }
                return Task.FromException(failure);
            }
            return inner.SendAsync(data, cancellationToken);
        }


        /// <summary>Sends an emulation request through the inner transport.</summary>
        /// <param name="data">The frame.</param>
        /// <param name="session">The session of the connect result.</param>
        /// <param name="node">The node of the connect result.</param>
        /// <param name="emulationEndpoint">The emulation endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public Task SendEmulationAsync(byte[] data, string session, string node, string emulationEndpoint, CancellationToken cancellationToken = default) {
            return inner.SendEmulationAsync(data, session, node, emulationEndpoint, cancellationToken);
        }


        /// <summary>Closes the inner transport.</summary>
        public Task CloseAsync() {
            return inner.CloseAsync();
        }


        /// <summary>Disposes the inner transport.</summary>
        public void Dispose() {
            inner.Dispose();
        }


        /// <summary>
        /// The commands of an outgoing frame: each one is written with its length in front.
        /// </summary>
        /// <param name="data">The frame.</param>
        internal static List<Command> ParseFrame(byte[] data) {
            List<Command> commands = new List<Command>();
            using MemoryStream stream = new MemoryStream(data);
            while (stream.Position < stream.Length) {
                commands.Add(Command.Parser.ParseDelimitedFrom(stream));
            }
            return commands;
        }


        /// <summary>
        /// True when an incoming frame carries an rpc result.
        /// </summary>
        /// <param name="messages">The messages of the frame.</param>
        internal static bool CarriesRpcResult(IReadOnlyList<byte[]> messages) {
            foreach (byte[] message in messages) {
                if (Reply.Parser.ParseFrom(message).Rpc != null) {
                    return true;
                }
            }
            return false;
        }
    }
}
