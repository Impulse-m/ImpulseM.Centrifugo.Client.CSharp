// The order of the rpc calls that wait to be written: the calls with a msg_id first, in msg_id order, then the calls that never got one, in the order they were added.
// The upstream rule is kept: the queue is used on the client loop only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The resend order of the rpc queue, checked on the queue alone.</summary>
    public sealed class RpcQueueTests {
        /// <summary>
        /// Drain returns the calls with a msg_id first, in msg_id order, then the calls that never got one, in the order they were
        /// added. The queue is empty afterwards.
        /// </summary>
        [Fact]
        public void Drain_ReturnsTrackedCallsByMsgIdThenNeverSentCallsInAddOrder() {
            RpcQueue queue = new RpcQueue();
            queue.Add(NewCall("first", 0));
            queue.Add(NewCall("nine", 9));
            queue.Add(NewCall("three", 3));
            queue.Add(NewCall("second", 0));

            List<RpcCallState> drained = queue.Drain();

            Assert.Equal(new[] { "three", "nine", "first", "second" }, drained.Select((RpcCallState call) => call.Method).ToArray());
            Assert.Equal(0, queue.Count);
        }


        /// <summary>Remove takes a call out of the queue, by reference, so that Drain does not return it.</summary>
        [Fact]
        public void Remove_TakesTheCallOutOfTheQueue() {
            RpcQueue queue = new RpcQueue();
            RpcCallState kept = NewCall("kept", 0);
            RpcCallState removed = NewCall("removed", 5);
            queue.Add(kept);
            queue.Add(removed);

            queue.Remove(removed);

            Assert.Equal(new[] { "kept" }, queue.Drain().Select((RpcCallState call) => call.Method).ToArray());
        }


        private static RpcCallState NewCall(string method, ulong msgId) {
            RpcCallState call = new RpcCallState(
                method,
                ReadOnlyMemory<byte>.Empty,
                new Dictionary<string, string>(),
                1000,
                new TaskCompletionSource<CentrifugeRpcResult>());
            call.MsgId = msgId;
            return call;
        }
    }
}
