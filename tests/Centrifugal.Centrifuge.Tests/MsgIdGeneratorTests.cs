// Ported from the Java fork's MsgIdGeneratorTest (14 tests). Status: all 14 ported under the Java names, kept in a comment
// on each test. The lanes are scripted, so every msg_id is known.
using System;
using System.Collections.Generic;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Unit tests of the msg_id generator: the time part, the sequence, the resync and the exhaustion.</summary>
    public sealed class MsgIdGeneratorTests {
        // Largest msg_id a JSON number carries exactly: 53 bits.
        private const ulong MsgIdMax = (1UL << 53) - 1UL;
        private const long ServerSecond = 1_700_000_000L;
        private const long ServerMillis = ServerSecond * 1000L;


        // Java: sameSecondIncrements
        [Fact]
        public void Next_SameSecond_Increments() {
            MsgIdGenerator generator = new MsgIdGenerator(Lanes(7));

            ulong first = generator.Next(ServerMillis + 100L);

            Assert.Equal(((ulong)ServerSecond << MsgIdGenerator.SequenceBits) | 7UL, first);
            Assert.Equal(first + 1UL, generator.Next(ServerMillis + 900L));
        }


        // Java: laterSecondRestartsAtTheLane
        [Fact]
        public void Next_LaterSecond_RestartsAtTheLane() {
            MsgIdGenerator generator = new MsgIdGenerator(Lanes(7));
            generator.Next(ServerMillis);

            ulong later = generator.Next(ServerMillis + 1_000L);

            Assert.Equal((((ulong)ServerSecond + 1UL) << MsgIdGenerator.SequenceBits) | 7UL, later);
        }


        // Java: backwardCorrectionHoldsTheTimePartAndKeepsIncreasing
        [Fact]
        public void Next_BackwardCorrection_HoldsTheTimePartAndKeepsIncreasing() {
            MsgIdGenerator generator = new MsgIdGenerator(Lanes(3));
            ulong ahead = generator.Next(ServerMillis + 120_000L);

            ulong behind = generator.Next(ServerMillis);
            ulong behindAgain = generator.Next(ServerMillis);
            ulong caughtUp = generator.Next(ServerMillis + 121_000L);

            ulong aheadSecond = (ulong)ServerSecond + 120UL;
            Assert.Equal((aheadSecond << MsgIdGenerator.SequenceBits) | 3UL, ahead);
            Assert.Equal((aheadSecond << MsgIdGenerator.SequenceBits) | 4UL, behind);
            Assert.Equal((aheadSecond << MsgIdGenerator.SequenceBits) | 5UL, behindAgain);
            Assert.Equal((((ulong)ServerSecond + 121UL) << MsgIdGenerator.SequenceBits) | 3UL, caughtUp);
        }


        // Java: overflowMovesToTheNextSecond
        [Fact]
        public void Next_SequenceOverflow_MovesToTheNextSecond() {
            ulong lane = 5UL;
            MsgIdGenerator generator = new MsgIdGenerator(Lanes((int)lane));
            ulong last = 0UL;
            // The lane takes the first sequence of the second, so the sequence reaches its last value after
            // 2^20 - lane calls in all.
            for (long i = 0L; i < MsgIdGenerator.SequenceLimit - (long)lane; i++) {
                last = generator.Next(ServerMillis);
            }
            Assert.Equal(((ulong)ServerSecond << MsgIdGenerator.SequenceBits) | ((ulong)MsgIdGenerator.SequenceLimit - 1UL), last);

            ulong overflow = generator.Next(ServerMillis);

            Assert.Equal((((ulong)ServerSecond + 1UL) << MsgIdGenerator.SequenceBits) | lane, overflow);
        }


        // Java: resyncLowersTheTimePartAndChangesTheLane
        [Fact]
        public void Resync_LowerSecond_LowersTheTimePartAndChangesTheLane() {
            MsgIdGenerator generator = new MsgIdGenerator(Lanes(100, 200));
            ulong ahead = generator.Next(ServerMillis + 120_000L);
            Assert.Equal((((ulong)ServerSecond + 120UL) << MsgIdGenerator.SequenceBits) | 100UL, ahead);

            generator.Resync(ServerMillis);
            ulong refused = generator.Next(ServerMillis);

            Assert.Equal(((ulong)ServerSecond << MsgIdGenerator.SequenceBits) | 200UL, refused);
        }


        // Java: resyncToTheSameSecondContinuesTheSequence
        [Fact]
        public void Resync_SameSecond_ContinuesTheSequence() {
            MsgIdGenerator generator = new MsgIdGenerator(Lanes(7, 100));
            ulong first = generator.Next(ServerMillis + 100L);
            ulong second = generator.Next(ServerMillis + 200L);

            generator.Resync(ServerMillis + 300L);
            ulong afterResync = generator.Next(ServerMillis + 300L);

            Assert.Equal(first + 1UL, second);
            Assert.Equal(second + 1UL, afterResync);
        }


        // Java: twoScriptedGeneratorsInTheSameSecondGiveDifferentFirstIds
        [Fact]
        public void Next_TwoGeneratorsWithOtherLanes_GiveDifferentFirstIds() {
            MsgIdGenerator first = new MsgIdGenerator(Lanes(7));
            MsgIdGenerator second = new MsgIdGenerator(Lanes(9));

            Assert.NotEqual(first.Next(ServerMillis), second.Next(ServerMillis));
        }


        // Java: twoDefaultGeneratorsInTheSameSecondGiveDifferentFirstIds
        // The lanes are random here, so a constant lane would fail this test. A collision is rare: each pair has a chance
        // below 2^-19.
        [Fact]
        public void Next_TwoRandomGeneratorsInTheSameSecond_GiveDifferentFirstIds() {
            for (int i = 0; i < 20; i++) {
                MsgIdGenerator first = new MsgIdGenerator(new Random());
                MsgIdGenerator second = new MsgIdGenerator(new Random());

                Assert.NotEqual(first.Next(ServerMillis), second.Next(ServerMillis));
            }
        }


        // Java: defaultLaneIsBelowTheBound
        [Fact]
        public void Next_RandomLane_IsBelowTheBound() {
            for (int i = 0; i < 20; i++) {
                ulong sequence = new MsgIdGenerator(new Random()).Next(ServerMillis) & ((ulong)MsgIdGenerator.SequenceLimit - 1UL);

                Assert.True(sequence < (1UL << 19), "the random lane is below 2^19: " + sequence);
            }
        }


        // Java: msgIdsStayUniqueAndIncreasingAcrossBackwardCorrections
        [Fact]
        public void Next_BackwardCorrections_StayUniqueAndIncreasing() {
            long[] clocksMillis = {
                1_700_000_005_000L,
                1_700_000_005_000L,
                1_700_000_007_000L,
                1_700_000_003_000L,
                1_700_000_003_000L,
                1_700_000_005_000L,
                1_700_000_005_000L,
                1_700_000_008_000L,
                1_700_000_001_000L,
                1_700_000_008_000L,
            };
            MsgIdGenerator generator = new MsgIdGenerator(Lanes(42));
            HashSet<ulong> issued = new HashSet<ulong>();
            ulong last = 0UL;
            foreach (long clock in clocksMillis) {
                ulong next = generator.Next(clock);

                Assert.True(next > last);
                Assert.True(issued.Add(next), "msg_id issued twice: " + next);
                last = next;
            }
        }


        // Java: largestTimePartFitsFiftyThreeBits
        [Fact]
        public void Next_LargestTimePart_FitsFiftyThreeBits() {
            MsgIdGenerator generator = new MsgIdGenerator(Lanes(1));

            ulong msgId = generator.Next(MsgIdGenerator.MaxSeconds * 1000L);

            Assert.Equal((ulong)MsgIdGenerator.MaxSeconds << MsgIdGenerator.SequenceBits | 1UL, msgId);
            Assert.True(msgId <= MsgIdMax);
        }


        // Java: timePartBeyondThirtyThreeBitsIsClamped
        [Fact]
        public void Next_TimePartBeyondThirtyThreeBits_IsClamped() {
            MsgIdGenerator generator = new MsgIdGenerator(Lanes(1));

            ulong msgId = generator.Next((MsgIdGenerator.MaxSeconds + 1L) * 1000L);

            Assert.Equal((ulong)MsgIdGenerator.MaxSeconds << MsgIdGenerator.SequenceBits | 1UL, msgId);
        }


        // Java: generatorRefusesWhenTheLargestSecondIsExhausted
        [Fact]
        public void Next_LargestSecondExhausted_Throws() {
            long lane = 5L;
            MsgIdGenerator generator = new MsgIdGenerator(Lanes((int)lane));
            long largestMillis = MsgIdGenerator.MaxSeconds * 1000L;
            for (long i = 0L; i < MsgIdGenerator.SequenceLimit - lane; i++) {
                generator.Next(largestMillis);
            }

            // The largest second has issued every sequence value: the next call is refused.
            Assert.Throws<InvalidOperationException>(() => generator.Next(largestMillis));
        }


        // Java: laneIsDrawnFromTheNineteenBitsRange
        [Fact]
        public void Constructor_LaneIsDrawnFromTheNineteenBitsRange() {
            HighestRandom highest = new HighestRandom();

            ulong highestLane = new MsgIdGenerator(highest).Next(ServerMillis) & ((ulong)MsgIdGenerator.SequenceLimit - 1UL);

            Assert.Equal(1 << 19, highest.LastBound);
            Assert.Equal((1UL << 19) - 1UL, highestLane);
            Assert.Equal(0UL, new MsgIdGenerator(Lanes(0)).Next(ServerMillis) & ((ulong)MsgIdGenerator.SequenceLimit - 1UL));
        }


        // Gap: a time before the Unix epoch counts as the epoch, so the time part is never negative (the unsigned shift
        // would wrap). Java has no such input rule; the case is outside the server's clock range.
        [Fact]
        public void Next_TimeBeforeTheEpoch_CountsAsTheEpoch() {
            MsgIdGenerator generator = new MsgIdGenerator(Lanes(9));

            ulong msgId = generator.Next(-5_000L);

            Assert.Equal(0UL, msgId >> MsgIdGenerator.SequenceBits);
        }


        // A random source which returns the values in order, one per draw of a lane.
        private static Random Lanes(params int[] values) {
            return new ScriptedRandom(values);
        }


        // The random source of the lanes: the values are returned in order, one per draw.
        private sealed class ScriptedRandom : Random {
            private readonly int[] values;
            private int index;


            /// <summary>
            /// Creates the source.
            /// </summary>
            /// <param name="values">The values returned by the draws, in order.</param>
            public ScriptedRandom(int[] values) {
                this.values = values;
            }


            /// <summary>Returns the next scripted value.</summary>
            /// <param name="maxValue">The exclusive upper bound of the draw.</param>
            public override int Next(int maxValue) {
                return values[index++];
            }
        }


        // A random source which returns the top of each range and records the bound of the last draw.
        private sealed class HighestRandom : Random {
            /// <summary>The bound of the last draw.</summary>
            public int LastBound { get; private set; }


            /// <summary>Returns bound minus one and records the bound.</summary>
            /// <param name="maxValue">The exclusive upper bound of the draw.</param>
            public override int Next(int maxValue) {
                LastBound = maxValue;
                return maxValue - 1;
            }
        }
    }
}
