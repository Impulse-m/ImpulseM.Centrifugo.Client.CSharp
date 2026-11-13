// The submit step of the rpc session, run against the fake client host without a server: a call that was cancelled before its
// submit step ran holds no slot of the bound, and the calls after it are accepted. The race is set up in one loop step, so
// the test is deterministic and waits for no real time.

using System;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The submit step of the rpc session, on the fake host.</summary>
    public sealed class RpcSessionSubmitTests {
        /// <summary>
        /// A call cancelled before its submit step runs settles at once and takes no slot, so its submit does nothing. With a
        /// bound of one, the next call is accepted and holds the slot.
        /// </summary>
        [Fact]
        public void Submit_OfACallCancelledBeforeIt_TakesNoSlot() {
            FakeClientHost host = new FakeClientHost { HasInstance = true };
            RpcSession session = new RpcSession(host, host.Timers, 1, FixedRandom.MinimumDraws());
            RpcCallState cancelled = NewCall("cancelled");
            RpcCallState next = NewCall("next");

            host.Loop.Invoke(() => {
                session.Cancel(cancelled);
                session.Submit(cancelled);
                session.Submit(next);
            });

            Assert.True(cancelled.Done);
            Assert.False(cancelled.HoldsSlot);
            Assert.False(next.Done);
            Assert.True(next.HoldsSlot);
        }


        // Java: RpcExactlyOnceTest.aSettledCallDropsItsPayload.
        /// <summary>A call that settles drops its payload: after the settle, the call holds no data.</summary>
        [Fact]
        public async Task Settle_DropsThePayloadOfTheCall() {
            FakeClientHost host = new FakeClientHost { HasInstance = true };
            RpcSession session = new RpcSession(host, host.Timers, 1, FixedRandom.MinimumDraws());
            RpcCallState call = new RpcCallState(
                "method",
                new byte[] { 1, 2, 3 },
                new System.Collections.Generic.Dictionary<string, string>(),
                1000,
                new TaskCompletionSource<CentrifugeRpcResult>());

            host.Loop.Invoke(() => {
                session.Submit(call);
                Assert.True(call.Data.HasValue);
                session.Cancel(call);
            });

            Assert.True(call.Done);
            Assert.Null(call.Data);
            // The step has ended, so the completion is delivered: the cancelled call completes with CANCELLED.
            CentrifugeException failure = await Assert.ThrowsAsync<CentrifugeException>(() => RpcTestHelpers.Bounded(call.Completion.Task));
            Assert.Equal(CentrifugeErrorCodes.Cancelled, failure.Code);
        }


        private static RpcCallState NewCall(string method) {
            return new RpcCallState(
                method,
                ReadOnlyMemory<byte>.Empty,
                new System.Collections.Generic.Dictionary<string, string>(),
                1000,
                new TaskCompletionSource<CentrifugeRpcResult>());
        }
    }
}
