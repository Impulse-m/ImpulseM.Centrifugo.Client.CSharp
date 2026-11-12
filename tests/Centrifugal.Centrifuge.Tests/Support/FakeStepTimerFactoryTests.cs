using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks that the fake step timers fire when the test moves their clock, and in no other case.</summary>
    public sealed class FakeStepTimerFactoryTests {
        /// <summary>A timer fires once the clock reaches its due time, not before.</summary>
        [Fact]
        public void Timer_FiresOnlyWhenTheClockReachesItsDueTime() {
            EventLoop loop = new EventLoop(null, null);
            FakeStepTimerFactory timers = new FakeStepTimerFactory(loop);
            int fired = 0;

            timers.Start(100, () => fired++);
            timers.Advance(99);
            Assert.Equal(0, fired);
            timers.Advance(1);

            Assert.Equal(1, fired);
            Assert.Equal(0, timers.PendingCount);
            Assert.Equal(100, timers.ElapsedMs);
        }

        /// <summary>A cancelled timer never fires.</summary>
        [Fact]
        public void CancelledTimer_DoesNotFire() {
            EventLoop loop = new EventLoop(null, null);
            FakeStepTimerFactory timers = new FakeStepTimerFactory(loop);
            int fired = 0;
            IStepTimer timer = timers.Start(50, () => fired++);

            timer.Cancel();
            timer.Cancel();
            timers.Advance(100);

            Assert.Equal(0, fired);
            Assert.Equal(0, timers.PendingCount);
        }

        /// <summary>Timers fire in due order, not in the order they were started.</summary>
        [Fact]
        public void Timers_FireInDueOrder() {
            EventLoop loop = new EventLoop(null, null);
            FakeStepTimerFactory timers = new FakeStepTimerFactory(loop);
            List<int> order = new List<int>();

            timers.Start(30, () => order.Add(30));
            timers.Start(10, () => order.Add(10));
            timers.Advance(50);

            Assert.Equal(new int[] { 10, 30 }, order);
        }

        /// <summary>RequestedDelays keeps every delay in order, and PendingWithDelay counts the pending timers of one delay.</summary>
        [Fact]
        public void RequestedDelays_AndPendingWithDelay_CountTheTimersOfEachDelay() {
            EventLoop loop = new EventLoop(null, null);
            FakeStepTimerFactory timers = new FakeStepTimerFactory(loop);

            timers.Start(10, () => { });
            timers.Start(20, () => { });
            timers.Start(10, () => { });

            Assert.Equal(new int[] { 10, 20, 10 }, timers.RequestedDelays.ToArray());
            Assert.Equal(2, timers.PendingWithDelay(10));
            Assert.Equal(1, timers.PendingWithDelay(20));

            timers.Advance(10);

            Assert.Equal(0, timers.PendingWithDelay(10));
            Assert.Equal(1, timers.PendingWithDelay(20));
        }

        /// <summary>A timer's step runs as a step of the loop.</summary>
        [Fact]
        public void Timer_RunsItsStepInsideTheLoop() {
            EventLoop loop = new EventLoop(null, null);
            FakeStepTimerFactory timers = new FakeStepTimerFactory(loop);
            bool inStep = false;

            timers.Start(0, () => inStep = loop.InStep);
            timers.Advance(0);

            Assert.True(inStep);
        }

        /// <summary>A timer that a fired step starts is due at its own time, so it fires in the same advance.</summary>
        [Fact]
        public void TimerStartedByAFiredStep_FiresInTheSameAdvance() {
            EventLoop loop = new EventLoop(null, null);
            FakeStepTimerFactory timers = new FakeStepTimerFactory(loop);
            bool nested = false;

            timers.Start(10, () => timers.Start(5, () => nested = true));
            timers.Advance(20);

            Assert.True(nested);
            Assert.Equal(20, timers.ElapsedMs);
        }
    }
}
