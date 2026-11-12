using System;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// Issues the msg_ids of the rpc calls. A msg_id is the server-corrected unix second shifted left by
    /// <see cref="SequenceBits"/>, with a sequence in the low bits, so it fits 53 bits. The sequence of a second starts at
    /// the lane, which is drawn at construction and on a re-sync, so two generators which share an instance rarely issue the same msg_id. A backward correction of
    /// the clock holds the time part, so the msg_ids stay unique within the scope. <see cref="Resync"/> may lower the
    /// time part when the server refused a msg_id as outside its window. Used on the client loop only.
    /// </summary>
    internal sealed class MsgIdGenerator {
        /// <summary>Bits of the sequence part of a msg_id.</summary>
        internal const int SequenceBits = 20;

        /// <summary>The sequence of a second moves on to the next second when it reaches this value.</summary>
        internal const long SequenceLimit = 1L << SequenceBits;

        /// <summary>Largest time part a msg_id carries: 33 bits of unix seconds.</summary>
        internal const long MaxSeconds = (1L << 33) - 1;

        // A lane is below this bound, so a second holds at least 2^19 msg_ids from its lane on.
        private const int LaneBound = 1 << 19;

        // Draws the lanes. It is a parameter so that the tests pick the lanes.
        private readonly Random random;

        // The last msg_id issued is lastSeconds and lastSeq.
        private long lastSeconds;
        private long lastSeq;
        private long lane;


        /// <summary>
        /// Creates the generator and draws its lane.
        /// </summary>
        /// <param name="random">The source of the lanes.</param>
        internal MsgIdGenerator(Random random) {
            this.random = random;
            lane = DrawLane();
        }


        /// <summary>
        /// The msg_id of a call written now, for the server time in milliseconds. Greater than the msg_id issued before it,
        /// unless the generator was re-synced (see <see cref="Resync"/>). The time part never moves back on its own: a server
        /// time behind the last time part keeps that time part, and the sequence goes on. A time part above
        /// <see cref="MaxSeconds"/> is clamped to it. A time before the Unix epoch counts as the epoch.
        /// </summary>
        /// <param name="serverTimeMillis">The server time in Unix milliseconds.</param>
        /// <returns>The msg_id.</returns>
        /// <exception cref="InvalidOperationException">The largest second has issued every sequence value (the msg_id space is exhausted).</exception>
        internal ulong Next(long serverTimeMillis) {
            long requested = Math.Min(Math.Max(0L, serverTimeMillis / 1000L), MaxSeconds);
            long seconds = Math.Max(requested, lastSeconds);
            long seq;
            if (seconds == lastSeconds) {
                seq = lastSeq + 1;
            } else {
                // The lane is not redrawn at a new second: a second that follows keeps the lane (Java laterSecondRestartsAtTheLane).
                seq = lane;
            }
            if (seq >= SequenceLimit) {
                if (seconds == MaxSeconds) {
                    throw new InvalidOperationException("msg_id space exhausted");
                }
                seconds++;
                seq = lane;
            }
            lastSeconds = seconds;
            lastSeq = seq;
            return ((ulong)seconds << SequenceBits) | (ulong)seq;
        }


        /// <summary>
        /// Re-syncs the generator after the server refused a msg_id as outside its window, to the server second of the
        /// refusal. A second which differs from the last one becomes the time part, even when it is lower, and the sequence
        /// starts at a new lane. The same second keeps the sequence, so no msg_id of that second is issued twice.
        /// </summary>
        /// <param name="replyTimeMillis">The server time of the refusal in Unix milliseconds.</param>
        internal void Resync(long replyTimeMillis) {
            long seconds = Math.Min(Math.Max(0L, replyTimeMillis / 1000L), MaxSeconds);
            if (seconds == lastSeconds) {
                return;
            }
            lastSeconds = seconds;
            lane = DrawLane();
            lastSeq = lane - 1;
        }


        private long DrawLane() {
            return random.Next(LaneBound);
        }
    }
}
