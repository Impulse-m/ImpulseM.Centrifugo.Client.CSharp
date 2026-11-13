// The public boundary of the connection token refresh: the null check, the closed client, the bound of calls in progress,
// and a refresh queued while disconnected that the close fails once.
// Not ported: refresh after an executor rejection: the client's loop cannot reject a step, so a refresh after close
// always takes the closed path of refreshAfterCloseCallsBackWithAnError.
// Not ported: the token source is told of each connect attempt: the pool token source is not in the client yet, so there
// is no source to notify.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the public refresh call of the client without a connection.</summary>
    public sealed class RefreshAsyncTests : FakeServerTestBase {
        // Java: refreshRejectsNullToken
        // A null token is refused at once, before anything is queued.
        [Fact]
        public void RefreshAsync_NullToken_ThrowsArgumentNullException() {
            CentrifugeClient client = NewClient();

            Exception? error = Record.Exception(() => {
                client.RefreshAsync(null!);
            });

            Assert.IsType<ArgumentNullException>(error);
        }


        // Java: refreshAfterCloseCallsBackWithAnError
        [Fact]
        public async Task RefreshAsync_AfterClose_FaultsWithClientClosed() {
            CentrifugeClient client = NewClient();
            await client.DisposeAsync();

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => client.RefreshAsync("token-1"));

            Assert.Equal(CentrifugeErrorCodes.ClientClosed, error.Code);
        }


        // Java: refreshWhileDisconnectedIsFailedOnceByClose
        [Fact]
        public async Task RefreshAsync_WhileDisconnectedThenClosed_FailsOnceWithClientClosed() {
            CentrifugeClient client = NewClient();
            Task<CentrifugeAuthResult> pending = client.RefreshAsync("token-1");

            await client.DisposeAsync();

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => pending);
            Assert.Equal(CentrifugeErrorCodes.ClientClosed, error.Code);
        }


        // Java: refreshBeyondThe32InProgressIsRefusedWithLimitExceeded. The 33rd refresh in progress is refused at once with
        // LimitExceeded; the 32 accepted ones are still waiting.
        [Fact]
        public async Task RefreshAsync_BeyondThe32InProgress_FaultsWithLimitExceeded() {
            CentrifugeClient client = NewClient();
            List<Task<CentrifugeAuthResult>> accepted = new List<Task<CentrifugeAuthResult>>();
            for (int index = 0; index < 32; index++) {
                accepted.Add(client.RefreshAsync("token-" + index));
            }

            CentrifugeException error = await Assert.ThrowsAsync<CentrifugeException>(() => client.RefreshAsync("token-32"));

            Assert.Equal(CentrifugeErrorCodes.LimitExceeded, error.Code);
            foreach (Task<CentrifugeAuthResult> task in accepted) {
                Assert.False(task.IsCompleted);
            }
        }
    }
}
