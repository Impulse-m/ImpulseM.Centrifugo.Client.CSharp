using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// A transport that forwards to a real one, with four switches a test sets: the open can be held (it never
    /// completes and never raises Opened), the close can be held (its close never completes, so the peer never
    /// answers it), Dispose can throw after it disposed the inner transport, and UsesEmulation can throw once a test
    /// arms it. Dispose is counted, and Disposed completes when Dispose has returned.
    /// </summary>
    internal sealed class ForwardingTransport : ITransport {
        private readonly ITransport inner;
        private readonly bool holdOpen;
        private readonly bool holdClose;
        private readonly bool throwOnDispose;
        private readonly TaskCompletionSource<bool> disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposeCount;
        private volatile bool throwOnUsesEmulation;


        /// <summary>
        /// Creates the forwarding transport.
        /// </summary>
        /// <param name="inner">The real transport the events and the sends go to.</param>
        /// <param name="holdOpen">True when the open never completes.</param>
        /// <param name="holdClose">True when the close never completes.</param>
        /// <param name="throwOnDispose">True when Dispose throws after the inner transport was disposed.</param>
        public ForwardingTransport(ITransport inner, bool holdOpen, bool holdClose, bool throwOnDispose = false) {
            this.inner = inner;
            this.holdOpen = holdOpen;
            this.holdClose = holdClose;
            this.throwOnDispose = throwOnDispose;
            inner.Opened += (object? sender, EventArgs args) => Opened?.Invoke(this, args);
            inner.MessageReceived += (object? sender, IReadOnlyList<byte[]> messages) => MessageReceived?.Invoke(this, messages);
            inner.Closed += (object? sender, TransportClosedEventArgs args) => Closed?.Invoke(this, args);
            inner.Error += (object? sender, Exception error) => Error?.Invoke(this, error);
        }


        /// <summary>The number of Dispose calls.</summary>
        public int DisposeCount {
            get {
                return Volatile.Read(ref disposeCount);
            }
        }


        /// <summary>Completes when Dispose has returned, or has thrown.</summary>
        public Task Disposed {
            get {
                return disposed.Task;
            }
        }


        /// <summary>
        /// When true, reading UsesEmulation throws. A test sets it to fail the handling of a reply that reads the flag.
        /// </summary>
        public bool ThrowOnUsesEmulation {
            get {
                return throwOnUsesEmulation;
            }
            set {
                throwOnUsesEmulation = value;
            }
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


        /// <summary>True when the inner transport uses emulation. Throws when <see cref="ThrowOnUsesEmulation"/> is set.</summary>
        public bool UsesEmulation {
            get {
                if (throwOnUsesEmulation) {
                    throw new InvalidOperationException("uses emulation failed");
                }
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


        /// <summary>Opens the inner transport, or never completes when the open is held.</summary>
        /// <param name="cancellationToken">The open deadline.</param>
        /// <param name="initialData">The initial data of an emulation transport.</param>
        public Task OpenAsync(CancellationToken cancellationToken = default, byte[]? initialData = null) {
            return holdOpen ? NeverCompletes() : inner.OpenAsync(cancellationToken, initialData);
        }


        /// <summary>Sends through the inner transport.</summary>
        /// <param name="data">The frame.</param>
        /// <param name="cancellationToken">The write deadline.</param>
        public Task SendAsync(byte[] data, CancellationToken cancellationToken = default) {
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


        /// <summary>Closes the inner transport, or never completes when the close is held.</summary>
        public Task CloseAsync() {
            return holdClose ? NeverCompletes() : inner.CloseAsync();
        }


        /// <summary>Disposes the inner transport and counts the call. Then Disposed completes.</summary>
        public void Dispose() {
            Interlocked.Increment(ref disposeCount);
            try {
                inner.Dispose();
                if (throwOnDispose) {
                    throw new InvalidOperationException("dispose failed");
                }
            } finally {
                disposed.TrySetResult(true);
            }
        }


        private static Task NeverCompletes() {
            return new TaskCompletionSource<bool>().Task;
        }
    }
}
