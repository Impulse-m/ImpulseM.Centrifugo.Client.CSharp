using System;
using System.Collections.Generic;
using System.Linq;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// Step timers whose clock the test moves. Advance fires each timer that falls due, in due order, as a step of the
    /// loop, on the thread that advances the clock. A test waits for no real delay. Replaces ManualScheduler.java.
    /// </summary>
    internal sealed class FakeStepTimerFactory : IStepTimerFactory {
        private readonly EventLoop loop;
        private readonly object gate = new object();
        private readonly List<FakeStepTimer> pending = new List<FakeStepTimer>();
        private readonly List<int> requestedDelays = new List<int>();
        private long elapsedMs;
        private long sequence;


        // One timer of the factory. A cancelled timer whose step is already queued does not run, as with LoopTimer.
        private sealed class FakeStepTimer : IStepTimer {
            private readonly FakeStepTimerFactory owner;
            private readonly Action onFire;
            private bool cancelled;


            /// <summary>
            /// Creates one timer of the factory.
            /// </summary>
            /// <param name="owner">The factory that owns the timer.</param>
            /// <param name="delayMs">The delay the timer was started with, in milliseconds.</param>
            /// <param name="dueMs">The clock time at which the timer falls due.</param>
            /// <param name="sequence">The start order of the timer, used to break ties in due order.</param>
            /// <param name="onFire">The step to run when the timer falls due.</param>
            public FakeStepTimer(FakeStepTimerFactory owner, int delayMs, long dueMs, long sequence, Action onFire) {
                this.owner = owner;
                DelayMs = delayMs;
                DueMs = dueMs;
                Sequence = sequence;
                this.onFire = onFire;
            }


            /// <summary>The delay the timer was started with, in milliseconds.</summary>
            public int DelayMs { get; }


            /// <summary>The clock time at which the timer falls due.</summary>
            public long DueMs { get; }


            /// <summary>The start order of the timer, used to break ties in due order.</summary>
            public long Sequence { get; }


            /// <summary>Cancels the timer. Safe to call twice.</summary>
            public void Cancel() {
                lock (owner.gate) {
                    cancelled = true;
                }
                owner.Remove(this);
            }


            /// <summary>Runs the step unless the timer was cancelled. Called on the loop.</summary>
            public void Fire() {
                lock (owner.gate) {
                    if (cancelled) {
                        return;
                    }
                }
                onFire();
            }
        }


        /// <summary>
        /// Creates the factory of one loop.
        /// </summary>
        /// <param name="loop">The loop the timers run their steps on.</param>
        public FakeStepTimerFactory(EventLoop loop) {
            this.loop = loop;
        }


        /// <summary>The time the clock has moved since the factory was created, in milliseconds.</summary>
        public long ElapsedMs {
            get {
                lock (gate) {
                    return elapsedMs;
                }
            }
        }


        /// <summary>The number of timers that neither fired nor were cancelled.</summary>
        public int PendingCount {
            get {
                lock (gate) {
                    return pending.Count;
                }
            }
        }


        /// <summary>Every delay passed to Start, in order.</summary>
        public IReadOnlyList<int> RequestedDelays {
            get {
                lock (gate) {
                    return requestedDelays.ToArray();
                }
            }
        }


        /// <summary>
        /// Starts a timer that is due after the delay, measured on the clock of this factory.
        /// </summary>
        /// <param name="delayMs">The delay in milliseconds.</param>
        /// <param name="onFire">The step to run when the timer falls due.</param>
        public IStepTimer Start(int delayMs, Action onFire) {
            lock (gate) {
                int delay = Math.Max(0, delayMs);
                FakeStepTimer timer = new FakeStepTimer(this, delay, elapsedMs + delay, sequence, onFire);
                sequence++;
                pending.Add(timer);
                requestedDelays.Add(delayMs);
                return timer;
            }
        }


        /// <summary>
        /// The number of pending timers that were started with the given delay.
        /// </summary>
        /// <param name="delayMs">The delay in milliseconds.</param>
        public int PendingWithDelay(int delayMs) {
            lock (gate) {
                return pending.Count(timer => timer.DelayMs == Math.Max(0, delayMs));
            }
        }


        /// <summary>
        /// Moves the clock forward and fires every timer that falls due by the new time, earliest first. A timer started
        /// by a fired step is due at the time it was started, so it fires in this call when that time is reached.
        /// </summary>
        /// <param name="ms">The milliseconds to move the clock.</param>
        public void Advance(int ms) {
            long target;
            lock (gate) {
                target = elapsedMs + ms;
            }
            while (true) {
                FakeStepTimer? next;
                lock (gate) {
                    next = NextDue(target);
                    if (next == null) {
                        elapsedMs = target;
                        return;
                    }
                    elapsedMs = next.DueMs;
                    pending.Remove(next);
                }
                FakeStepTimer due = next;
                loop.Execute(() => due.Fire());
            }
        }


        private FakeStepTimer? NextDue(long targetMs) {
            FakeStepTimer? next = null;
            foreach (FakeStepTimer timer in pending) {
                if (timer.DueMs > targetMs) {
                    continue;
                }
                if (next == null || timer.DueMs < next.DueMs || (timer.DueMs == next.DueMs && timer.Sequence < next.Sequence)) {
                    next = timer;
                }
            }
            return next;
        }


        private void Remove(FakeStepTimer timer) {
            lock (gate) {
                pending.Remove(timer);
            }
        }
    }
}
