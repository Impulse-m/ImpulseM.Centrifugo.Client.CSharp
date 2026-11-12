// Ported from Java ConnectFailureHandlingTest (2 tests). The Java tests check the executor thread of the callbacks; the
// C# client runs its callbacks as steps of one loop, so the port checks the error type and the absence of a connect
// error after disconnect() instead of the thread.
using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// A connect command that never gets a reply fails by timing out, and a disconnect during connect reports no error.
    /// </summary>
    [Collection("Integration")]
    public sealed class ConnectFailureHandlingTests : FakeServerTestBase {
        /// <summary>A connect that never gets a reply is reported as a connect timeout error.</summary>
        [Fact]
        // Java: testConnectTimeoutHandledOnExecutorThread
        public async Task ConnectTimeoutHandledOnExecutorThread() {
            // Never answer connect, so the command times out.
            Server.DeferCommand(command => command.Connect != null);
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Timeout = TimeSpan.FromMilliseconds(300),
                // Keep the follow-up reconnect out of the way of the assertions.
                MinReconnectDelay = TimeSpan.FromSeconds(60),
                MaxReconnectDelay = TimeSpan.FromSeconds(60),
            });
            Channel<CentrifugeErrorEventArgs> errors = Channel.CreateUnbounded<CentrifugeErrorEventArgs>();
            client.Error += (object? sender, CentrifugeErrorEventArgs args) => errors.Writer.TryWrite(args);

            client.Connect();
            CentrifugeErrorEventArgs error = await errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("connect", error.Type);
            Assert.IsType<CentrifugeTimeoutException>(error.Exception);
        }


        /// <summary>A disconnect while the connect is pending reports no connect error.</summary>
        [Fact]
        // Java: testNoConnectErrorWhenDisconnectCalledDuringConnect
        public async Task NoConnectErrorWhenDisconnectCalledDuringConnect() {
            TaskCompletionSource<bool> connectSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.DeferCommand(command => {
                if (command.Connect != null) {
                    connectSent.TrySetResult(true);
                    return true;
                }
                return false;
            });
            CentrifugeClient client = NewClient();
            Channel<CentrifugeErrorEventArgs> errors = Channel.CreateUnbounded<CentrifugeErrorEventArgs>();
            Channel<CentrifugeDisconnectedEventArgs> disconnected = Channel.CreateUnbounded<CentrifugeDisconnectedEventArgs>();
            client.Error += (object? sender, CentrifugeErrorEventArgs args) => errors.Writer.TryWrite(args);
            client.Disconnected += (object? sender, CentrifugeDisconnectedEventArgs args) => disconnected.Writer.TryWrite(args);

            client.Connect();
            await connectSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            client.Disconnect();
            await disconnected.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await client.LoopBarrierAsync();

            Assert.False(errors.Reader.TryRead(out _), "disconnect() during connect must not report a connect error");
        }
    }
}
