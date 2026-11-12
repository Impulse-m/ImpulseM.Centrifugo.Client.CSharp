using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the answers and the records of the fake client host.</summary>
    public sealed class FakeClientHostTests {
        /// <summary>Command ids start at 1 and never repeat or reach 0.</summary>
        [Fact]
        public void NextCommandId_StartsAtOneAndIncreases() {
            FakeClientHost host = new FakeClientHost();

            Assert.Equal(1u, host.NextCommandId());
            Assert.Equal(2u, host.NextCommandId());
        }

        /// <summary>Without a handler a token request is recorded and not answered.</summary>
        [Fact]
        public void RequestConnectionToken_WithoutHandler_RecordsTheRequest() {
            FakeClientHost host = new FakeClientHost();
            bool answered = false;

            host.RequestConnectionToken((string? token) => answered = true, (Exception error) => answered = true);

            Assert.Single(host.ConnectionTokenRequests);
            Assert.False(answered);
        }

        /// <summary>With a handler the token request is answered through the handler.</summary>
        [Fact]
        public void RequestConnectionToken_WithHandler_AnswersThroughTheHandler() {
            FakeClientHost host = new FakeClientHost();
            host.RequestConnectionTokenHandler = (Action<string?> onToken, Action<Exception> onError) => onToken("token-1");
            string? received = null;

            host.RequestConnectionToken((string? token) => received = token, (Exception error) => { });

            Assert.Equal("token-1", received);
            Assert.Empty(host.ConnectionTokenRequests);
        }

        /// <summary>FailUnauthorized counts the call and records a disconnect with code 1 and no reconnect.</summary>
        [Fact]
        public void FailUnauthorized_RecordsADisconnectWithCodeOneAndNoReconnect() {
            FakeClientHost host = new FakeClientHost();

            host.FailUnauthorized();

            Assert.Equal(1, host.FailedUnauthorized);
            Assert.Single(host.Disconnects);
            Assert.Equal((1, "unauthorized", false), host.Disconnects[0]);
        }

        /// <summary>Without a handler an on-transport command fails as a lost connection (code 11).</summary>
        [Fact]
        public async Task SendOnTransportAsync_WithoutHandler_FailsAsLostConnection() {
            FakeClientHost host = new FakeClientHost();

            Task<Protocol.Reply> pending = host.SendOnTransportAsync(null!, new Protocol.Command(), CancellationToken.None);
            CentrifugeConnectionLostException error = await Assert.ThrowsAsync<CentrifugeConnectionLostException>(() => pending);

            Assert.Equal(11, error.Code);
        }

        /// <summary>A queued command is recorded, and its write result completes as written.</summary>
        [Fact]
        public async Task QueueCommand_RecordsTheCommandAndCompletesTheWrite() {
            FakeClientHost host = new FakeClientHost();
            TaskCompletionSource<bool> sent = new TaskCompletionSource<bool>();

            host.QueueCommand(new Protocol.Command { Id = 7 }, sent);

            Assert.Single(host.QueuedCommands);
            Assert.Equal(7u, host.QueuedCommands[0].Id);
            Assert.True(await sent.Task);
        }

        /// <summary>The timer factory of the host is the fake one, so the clock of the test moves the host's timers.</summary>
        [Fact]
        public void Timers_AreTheFakeTimersOfTheHost() {
            FakeClientHost host = new FakeClientHost();

            host.Timers.Start(5000, () => { });

            Assert.Equal(1, host.FakeTimers.PendingWithDelay(5000));
            Assert.Equal(1, host.FakeTimers.PendingCount);
        }

        /// <summary>The default retry policy waits the bottom of its bounds (5000 ms) with the zero draws of its random source.</summary>
        [Fact]
        public void RefreshRetryPolicy_DefaultsToTheMinimumDraws() {
            FakeClientHost host = new FakeClientHost();

            Assert.Equal(5000, host.RefreshRetryPolicy.RetryDelayMillis(0));
        }
    }
}
