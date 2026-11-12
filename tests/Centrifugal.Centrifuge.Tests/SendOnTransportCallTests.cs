// Tests of the internal seams of the connect path: SendOnTransportCall (the plain command of the token exchange),
// CancelTransportNow and ResumeHeldConnect. No Java test pins these by name; they follow the rules of the transport
// and the held connect reply. The timeout test uses the upstream call timer (a LoopTimer) with the smallest option that
// still leaves the connect time to finish, which is the one real-time wait of this file.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Centrifugal.Centrifuge.Transports;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>The plain command on a given transport, the transport cancel and the held connect reply.</summary>
    public sealed class SendOnTransportCallTests : FakeServerTestBase {
        private static readonly TimeSpan wait = TimeSpan.FromSeconds(10);


        /// <summary>A reply comes back on the current transport and completes the call with the reply.</summary>
        [Fact]
        public async Task SendOnTransportCall_OnTheCurrentTransport_CompletesWithTheReply() {
            CentrifugeClient client = NewClientRecordingTransport(out Task<ForwardingTransport> transportTask);
            client.Connect();
            ForwardingTransport transport = await transportTask.WaitAsync(wait);
            await client.ReadyAsync(wait);

            Task<Reply>? call = null;
            client.Loop.Invoke(() => call = client.SendOnTransportCall(transport, SubscribeCommand("plain"), CancellationToken.None));
            Reply reply = await call!.WaitAsync(wait);

            Assert.NotNull(reply.Subscribe);
        }


        /// <summary>A transport that is not the current one fails the call at once with a connection-lost exception.</summary>
        [Fact]
        public async Task SendOnTransportCall_OnAStaleTransport_FailsWithConnectionLost() {
            CentrifugeClient client = NewClient();
            client.Connect();
            await client.ReadyAsync(wait);
            ForwardingTransport stale = new ForwardingTransport(new WebSocketTransport(Server.Url), false, false);

            Task<Reply>? call = null;
            client.Loop.Invoke(() => call = client.SendOnTransportCall(stale, SubscribeCommand("stale"), CancellationToken.None));

            await Assert.ThrowsAsync<CentrifugeConnectionLostException>(() => call!.WaitAsync(wait));
            stale.Dispose();
        }


        /// <summary>The transport is lost while the call waits for its reply: the call fails with a connection-lost exception.</summary>
        [Fact]
        public async Task SendOnTransportCall_LostBeforeTheReply_FailsWithConnectionLost() {
            Server.OnCommand = (Command command) => command.Subscribe != null ? FakeCentrifugoServer.NoReply : null;
            CentrifugeClient client = NewClientRecordingTransport(out Task<ForwardingTransport> transportTask);
            client.Connect();
            ForwardingTransport transport = await transportTask.WaitAsync(wait);
            await client.ReadyAsync(wait);
            Task<bool> received = WhenCommandReceived("held");

            Task<Reply>? call = null;
            client.Loop.Invoke(() => call = client.SendOnTransportCall(transport, SubscribeCommand("held"), CancellationToken.None));
            await received.WaitAsync(wait);
            client.Disconnect();

            await Assert.ThrowsAsync<CentrifugeConnectionLostException>(() => call!.WaitAsync(wait));
        }


        /// <summary>A reply that does not come within the client timeout fails the call with a timeout exception.</summary>
        [Fact]
        public async Task SendOnTransportCall_NoReplyWithinTheTimeout_FailsWithTimeout() {
            // Real time: the call timer is an upstream LoopTimer. The timeout is the smallest that lets the connect finish.
            Server.OnCommand = (Command command) => command.Subscribe != null ? FakeCentrifugoServer.NoReply : null;
            CentrifugeClient client = NewClientRecordingTransport(out Task<ForwardingTransport> transportTask,
                new CentrifugeClientOptions { Timeout = TimeSpan.FromMilliseconds(300) });
            client.Connect();
            ForwardingTransport transport = await transportTask.WaitAsync(wait);
            await client.ReadyAsync(wait);

            Task<Reply>? call = null;
            client.Loop.Invoke(() => call = client.SendOnTransportCall(transport, SubscribeCommand("slow"), CancellationToken.None));

            await Assert.ThrowsAsync<CentrifugeTimeoutException>(() => call!.WaitAsync(wait));
        }


        /// <summary>Cancelling the token while the call waits cancels the call and removes it from the client.</summary>
        [Fact]
        public async Task SendOnTransportCall_CancelledWhileWaiting_IsCancelled() {
            Server.OnCommand = (Command command) => command.Subscribe != null ? FakeCentrifugoServer.NoReply : null;
            CentrifugeClient client = NewClientRecordingTransport(out Task<ForwardingTransport> transportTask);
            client.Connect();
            ForwardingTransport transport = await transportTask.WaitAsync(wait);
            await client.ReadyAsync(wait);
            Task<bool> received = WhenCommandReceived("cancel");
            using CancellationTokenSource cancellation = new CancellationTokenSource();

            Task<Reply>? call = null;
            client.Loop.Invoke(() => call = client.SendOnTransportCall(transport, SubscribeCommand("cancel"), cancellation.Token));
            await received.WaitAsync(wait);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call!.WaitAsync(wait));
        }


        /// <summary>A token that is cancelled before the call cancels it at once.</summary>
        [Fact]
        public async Task SendOnTransportCall_AlreadyCancelled_IsCancelled() {
            CentrifugeClient client = NewClientRecordingTransport(out Task<ForwardingTransport> transportTask);
            client.Connect();
            ForwardingTransport transport = await transportTask.WaitAsync(wait);
            await client.ReadyAsync(wait);
            CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Task<Reply>? call = null;
            client.Loop.Invoke(() => call = client.SendOnTransportCall(transport, SubscribeCommand("cancelled"), cancellation.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call!.WaitAsync(wait));
            cancellation.Dispose();
        }


        /// <summary>The transport cancel disposes the transport at once, without waiting for a close.</summary>
        [Fact]
        public async Task CancelTransportNow_DisposesTheTransport() {
            CentrifugeClient client = NewClientRecordingTransport(out Task<ForwardingTransport> transportTask);
            client.Connect();
            ForwardingTransport transport = await transportTask.WaitAsync(wait);
            await client.ReadyAsync(wait);

            client.Loop.Invoke(() => client.CancelTransportNow(transport));
            await client.LoopBarrierAsync();

            Assert.Equal(1, transport.DisposeCount);
        }


        /// <summary>A transport whose dispose throws does not throw out of the cancel, and no error event is raised for it.</summary>
        [Fact]
        public async Task CancelTransportNow_DisposeThrows_IsSwallowed() {
            CentrifugeClient client = NewClientRecordingTransport(out Task<ForwardingTransport> transportTask, throwOnDispose: true);
            List<string> errorTypes = new List<string>();
            client.Error += (_, e) => errorTypes.Add(e.Type);
            client.Connect();
            ForwardingTransport transport = await transportTask.WaitAsync(wait);
            await client.ReadyAsync(wait);

            client.Loop.Invoke(() => client.CancelTransportNow(transport));
            await client.LoopBarrierAsync();

            Assert.Equal(1, transport.DisposeCount);
            Assert.DoesNotContain("unclassified", errorTypes);
        }


        /// <summary>A connect reply held by the token exchange resumes the connect: the client connects with that reply.</summary>
        [Fact]
        public async Task ResumeHeldConnect_WhileConnecting_ConnectsWithTheHeldReply() {
            Server.OnCommand = (Command command) => command.Connect != null ? FakeCentrifugoServer.NoReply : null;
            CentrifugeClient client = NewClient();
            Task<CentrifugeConnectedEventArgs> connected = WhenConnected(client);
            Task<bool> received = WhenCommandReceived(null, connect: true);

            client.Connect();
            await received.WaitAsync(wait);
            client.Loop.Invoke(() => client.ResumeHeldConnect(ConnectReplies.Connect(client: "held")));
            CentrifugeConnectedEventArgs args = await connected;

            Assert.Equal("held", args.ClientId);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }


        /// <summary>A held connect reply that arrives after the client left Connecting does nothing.</summary>
        [Fact]
        public async Task ResumeHeldConnect_WhenNotConnecting_DoesNothing() {
            CentrifugeClient client = NewClient();

            client.Loop.Invoke(() => client.ResumeHeldConnect(ConnectReplies.Connect(client: "late")));
            await client.LoopBarrierAsync();

            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }


        // A client whose transports are recorded: the first one the client creates completes the returned task.
        private CentrifugeClient NewClientRecordingTransport(
            out Task<ForwardingTransport> transport,
            CentrifugeClientOptions? options = null,
            bool throwOnDispose = false
        ) {
            TaskCompletionSource<ForwardingTransport> created =
                new TaskCompletionSource<ForwardingTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeClient client = NewClient(options);
            client.UseTestTransport((CentrifugeTransportType type, string endpoint) => {
                ForwardingTransport forwarding = new ForwardingTransport(new WebSocketTransport(endpoint), false, false, throwOnDispose);
                created.TrySetResult(forwarding);
                return forwarding;
            });
            transport = created.Task;
            return client;
        }


        // Completes when the server receives a command of the given channel (or a connect command).
        private Task<bool> WhenCommandReceived(string? channel, bool connect = false) {
            TaskCompletionSource<bool> received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.BeforeCommand = (Command command) => {
                bool match = connect ? command.Connect != null : command.Subscribe != null && command.Subscribe.Channel == channel;
                if (match) {
                    received.TrySetResult(true);
                }
                return Task.CompletedTask;
            };
            return received.Task;
        }


        private static Command SubscribeCommand(string channel) {
            return new Command { Subscribe = new SubscribeRequest { Channel = channel } };
        }
    }
}
