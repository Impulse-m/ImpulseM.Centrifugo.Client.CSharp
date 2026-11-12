// Ported from the Java fork's LaterDelayTest (7 tests). Status: all 7 ported under the Java names, kept in a comment on
// each test. Nanoseconds of the Java test are TimeSpan values here.
using System;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Unit tests of the later resend delay: the bound per resend, the inclusive draw and the cap at the deadline.</summary>
    public sealed class LaterDelayTests {
        // Java: theBoundDoublesWithEachLaterResend
        [Fact]
        public void LaterDelay_BoundDoublesWithEachLaterResend() {
            Assert.Equal(TimeSpan.FromMilliseconds(250), RpcRetryDelays.LaterDelay(Maximum(), 0, TimeSpan.MaxValue));
            Assert.Equal(TimeSpan.FromMilliseconds(500), RpcRetryDelays.LaterDelay(Maximum(), 1, TimeSpan.MaxValue));
            Assert.Equal(TimeSpan.FromMilliseconds(1000), RpcRetryDelays.LaterDelay(Maximum(), 2, TimeSpan.MaxValue));
        }


        // Java: theDrawRangeIsZeroToTheBoundInclusive
        [Fact]
        public void LaterDelay_DrawRangeIsZeroToTheBoundInclusive() {
            Assert.Equal(251, DrawBoundOf(0));
            Assert.Equal(501, DrawBoundOf(1));
            Assert.Equal(1001, DrawBoundOf(2));
        }


        // Java: theLowestDrawGivesNoDelay
        [Fact]
        public void LaterDelay_LowestDraw_GivesNoDelay() {
            BoundRandom lowest = new BoundRandom(bound => 0);

            Assert.Equal(TimeSpan.Zero, RpcRetryDelays.LaterDelay(lowest, 2, TimeSpan.MaxValue));
        }


        // Java: laterResendDrawRangeIsCappedAtTheRemainingDeadline
        // The cap limits the range of the draw: with 300 ms left, the third resend draws over 301 values, not 1001.
        [Fact]
        public void LaterDelay_DrawRange_IsCappedAtTheRemainingDeadline() {
            BoundRandom capped = new BoundRandom(bound => 0);

            RpcRetryDelays.LaterDelay(capped, 2, TimeSpan.FromMilliseconds(300));

            Assert.Equal(301, capped.LastBound);
        }


        // Java: laterResendIntermediateDrawIsNotChangedByTheCap
        // The intermediate draw is kept as drawn when it lies inside the capped range.
        [Fact]
        public void LaterDelay_IntermediateDraw_IsNotChangedByTheCap() {
            BoundRandom middle = new BoundRandom(bound => 150);

            Assert.Equal(TimeSpan.FromMilliseconds(150), RpcRetryDelays.LaterDelay(middle, 2, TimeSpan.FromMilliseconds(300)));
        }


        // Java: laterResendDelayIsCappedAtTheRemainingDeadline
        // With draw 1 the delay is the remaining deadline, not the bound of the resend.
        [Fact]
        public void LaterDelay_LargestDraw_IsCappedAtTheRemainingDeadline() {
            Assert.Equal(TimeSpan.FromMilliseconds(300), RpcRetryDelays.LaterDelay(Maximum(), 1, TimeSpan.FromMilliseconds(300)));
            Assert.Equal(TimeSpan.FromMilliseconds(500), RpcRetryDelays.LaterDelay(Maximum(), 1, TimeSpan.FromMilliseconds(2000)));
        }


        // Java: laterResendCapRoundsTheRemainingDeadlineDownToWholeMilliseconds
        // The cap is taken in whole milliseconds, rounded down: with 2.5 ms left the largest resend is 2 ms, never past the deadline.
        [Fact]
        public void LaterDelay_RemainingDeadline_IsRoundedDownToWholeMilliseconds() {
            TimeSpan remaining = TimeSpan.FromTicks(25_000L);

            Assert.Equal(TimeSpan.FromMilliseconds(2), RpcRetryDelays.LaterDelay(Maximum(), 3, remaining));
        }


        // Gap: the budget and the base of the later resends (rule R48).
        [Fact]
        public void Constants_AreThreeLaterResendsOfAQuarterSecondBase() {
            Assert.Equal(3, RpcRetryDelays.LaterResends);
            Assert.Equal(250, RpcRetryDelays.LaterBaseMillis);
        }


        // A random source which always returns the largest value of the range.
        private static Random Maximum() {
            return new BoundRandom(bound => bound - 1);
        }


        // The bound of the draw of the given later resend, as the random source was asked for it.
        private static int DrawBoundOf(int resends) {
            BoundRandom recorder = new BoundRandom(bound => 0);
            RpcRetryDelays.LaterDelay(recorder, resends, TimeSpan.MaxValue);
            return recorder.LastBound;
        }


        // A random source whose draw is given by a function of the bound, which records the bound of the last draw.
        private sealed class BoundRandom : Random {
            private readonly Func<int, int> draw;


            /// <summary>
            /// Creates the source.
            /// </summary>
            /// <param name="draw">Returns the draw for an exclusive upper bound.</param>
            public BoundRandom(Func<int, int> draw) {
                this.draw = draw;
            }


            /// <summary>The bound of the last draw.</summary>
            public int LastBound { get; private set; }


            /// <summary>Returns the draw of the function and records the bound.</summary>
            /// <param name="maxValue">The exclusive upper bound of the draw.</param>
            public override int Next(int maxValue) {
                LastBound = maxValue;
                return draw(maxValue);
            }
        }
    }
}
