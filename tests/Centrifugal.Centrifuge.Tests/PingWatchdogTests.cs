// Ported from Java PingWatchdogTest (8 tests) and ServerPingDisabledTest (1 test). The Java tests move a ManualScheduler
// clock; these move the fake step timers of the host, so no real time passes. The reply kinds of the Java tests
// (server ping, push, command reply) all reach the watchdog through MarkReply, so the watchdog sees them alike. The
// client-level reply kinds are checked in PingWatchdogClientTests.
// Rule tests: R9 (half window), R39 (large ping interval), R10 (ping disabled), R13 to R16 (checks, close, stale check).
using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// The ping watchdog on the fake step timers. The ping interval is DefaultPingSeconds (25 s) given to the watchdog
    /// directly, and each test sets the allowed server ping delay of the fake host (10 s by default, so the window is
    /// 35 s and the half window 17.5 s).
    /// </summary>
    public sealed class PingWatchdogTests {
        // Java: ODD_MAX_SERVER_PING_DELAY_MILLIS
        private const int OddMaxServerPingDelayMillis = 10_001;
        // Java: MAX_CHECKS
        private const int MaxChecks = 10;
        private const uint DefaultPingSeconds = 25;


        /// <summary>A reply just before each check keeps the connection, however many checks follow.</summary>
        [Fact]
        // Java: replyJustBeforeEachCheckKeepsTheConnection
        public void ReplyJustBeforeEachCheckKeepsTheConnection() {
            Rig rig = new Rig(TimeSpan.FromSeconds(10), DefaultPingSeconds);
            for (int check = 1; check <= 6; check++) {
                MoveTo(rig, check * rig.HalfWindowMs - 1);
                rig.Watchdog.MarkReply();
                MoveTo(rig, check * rig.HalfWindowMs);
                Assert.Equal(CentrifugeClientState.Connected, rig.Host.State);
            }
            Assert.Empty(rig.Host.Disconnects);
        }


        /// <summary>
        /// After the last reply with no more traffic, the connection closes within the bounds. The Java test runs the
        /// three reply kinds one after another; the watchdog sees every kind as a MarkReply, so one reply stands for them.
        /// </summary>
        [Fact]
        // Java: silenceAfterAnyReplyClosesWithinOneWindow
        public void SilenceAfterAReplyClosesWithinOneWindow() {
            Rig rig = new Rig(TimeSpan.FromSeconds(10), DefaultPingSeconds);
            AssertClosedWithinBounds(rig, 5_000L);
        }


        /// <summary>A reply after 90 percent of the window restarts the watchdog: the close comes a window after that reply.</summary>
        [Fact]
        // Java: pingWatchdogRestartsOnAnyReply
        public void PingWatchdogRestartsOnAnyReply() {
            Rig rig = new Rig(TimeSpan.FromSeconds(10), DefaultPingSeconds);
            AssertClosedWithinBounds(rig, rig.WindowMs * 9 / 10);
        }


        /// <summary>The last reply falls just after a check, the case with the latest close.</summary>
        [Fact]
        // Java: silenceAfterAReplyJustAfterACheckClosesWithinTheBounds
        public void SilenceAfterAReplyJustAfterACheckClosesWithinTheBounds() {
            Rig rig = new Rig(TimeSpan.FromSeconds(10), DefaultPingSeconds);
            AssertClosedWithinBounds(rig, rig.HalfWindowMs + 1);
        }


        /// <summary>The same case with an odd window: the half window is rounded up and the bounds still hold.</summary>
        [Fact]
        // Java: oddWindowAfterAReplyJustAfterACheckClosesWithinTheBounds
        public void OddWindowAfterAReplyJustAfterACheckClosesWithinTheBounds() {
            Rig rig = new Rig(TimeSpan.FromMilliseconds(OddMaxServerPingDelayMillis), DefaultPingSeconds);
            Assert.Equal(1, rig.WindowMs % 2);
            AssertClosedWithinBounds(rig, rig.HalfWindowMs + 1);
        }


        /// <summary>A stale check, queued before its connection ended, exits without closing and adds no check.</summary>
        [Fact]
        // Java: staleCheckFromEndedConnectionExits
        public void StaleCheckFromEndedConnectionExits() {
            Rig rig = new Rig(TimeSpan.FromSeconds(10), DefaultPingSeconds);
            MoveTo(rig, rig.HalfWindowMs);
            Assert.Equal(1, rig.Host.FakeTimers.PendingWithDelay((int)rig.HalfWindowMs));

            // The connection ends: the client cancels the watchdog and no check is left.
            rig.Host.State = CentrifugeClientState.Disconnected;
            rig.Watchdog.Cancel();
            MoveTo(rig, 3 * rig.HalfWindowMs);
            Assert.Empty(rig.Host.Disconnects);
            Assert.Equal(0, rig.Host.FakeTimers.PendingCount);

            // The reconnect starts the watchdog again: one check is in flight.
            rig.Host.State = CentrifugeClientState.Connected;
            rig.Watchdog.Start(DefaultPingSeconds);
            Assert.Equal(1, rig.Host.FakeTimers.PendingWithDelay((int)rig.HalfWindowMs));
            Assert.Equal(1, rig.Host.FakeTimers.PendingCount);
        }


        /// <summary>A check that would close the old connection, queued behind the end of that connection, leaves the new one open.</summary>
        [Fact]
        // Java: staleCheckThatWouldCloseLeavesTheNewConnectionOpen
        public void StaleCheckThatWouldCloseLeavesTheNewConnectionOpen() {
            Rig rig = new Rig(TimeSpan.FromSeconds(10), DefaultPingSeconds);
            // The first check finds no reply and arms the second one.
            MoveTo(rig, rig.HalfWindowMs);

            // In one step, which holds the loop: the second check falls due and waits behind this step, the old
            // connection ends, and the new one starts. The queued check then finds the state changed.
            rig.Host.Loop.Execute(() => {
                rig.Host.FakeTimers.Advance((int)rig.HalfWindowMs);
                rig.Host.State = CentrifugeClientState.Disconnected;
                rig.Watchdog.Cancel();
                rig.Host.State = CentrifugeClientState.Connected;
                rig.Watchdog.Start(DefaultPingSeconds);
            });

            // One half window after the new connection: its first check finds no reply, and it stays open.
            MoveTo(rig, 3 * rig.HalfWindowMs);
            Assert.Equal(CentrifugeClientState.Connected, rig.Host.State);
            Assert.Empty(rig.Host.Disconnects);
        }


        /// <summary>A ping interval of zero arms nothing, so a connection with pings disabled is never closed for silence.</summary>
        [Fact]
        // Java: testConnectionStaysWithoutServerPings (ServerPingDisabledTest)
        public void PingDisabledNeverCloses() {
            Rig rig = new Rig(TimeSpan.FromSeconds(10), 0);
            Assert.Equal(0, rig.Host.FakeTimers.PendingCount);
            MoveTo(rig, 3 * rig.WindowMs + 1);
            Assert.Equal(CentrifugeClientState.Connected, rig.Host.State);
            Assert.Empty(rig.Host.Disconnects);
        }


        /// <summary>The half window is the window rounded up to whole milliseconds.</summary>
        [Fact]
        public void HalfWindowMilliseconds_RoundsUp() {
            Assert.Equal(600L, PingWatchdog.HalfWindowMilliseconds(1000, 200));
            Assert.Equal(601L, PingWatchdog.HalfWindowMilliseconds(1000, 201));
        }


        /// <summary>A very large ping interval does not overflow: its check delay is clamped to the int range.</summary>
        [Fact]
        public void Start_LargePingInterval_DoesNotOverflow() {
            Rig rig = new Rig(TimeSpan.FromSeconds(10), uint.MaxValue);
            Assert.Equal(1, rig.Host.FakeTimers.PendingWithDelay(int.MaxValue));
        }


        /// <summary>A watchdog on a fake host whose clock the test moves.</summary>
        private sealed class Rig {
            /// <summary>
            /// Creates the rig and starts the watchdog with the given ping interval.
            /// </summary>
            /// <param name="maxServerPingDelay">The allowed delay of the server ping.</param>
            /// <param name="pingSeconds">The ping interval of the connect reply, in seconds. Zero disables the ping.</param>
            public Rig(TimeSpan maxServerPingDelay, uint pingSeconds) {
                Host = new FakeClientHost { State = CentrifugeClientState.Connected, MaxServerPingDelay = maxServerPingDelay };
                Watchdog = new PingWatchdog(Host, Host.Timers);
                long pingMs = (long)pingSeconds * 1000L;
                WindowMs = pingMs + (long)maxServerPingDelay.TotalMilliseconds;
                HalfWindowMs = PingWatchdog.HalfWindowMilliseconds(pingMs, (long)maxServerPingDelay.TotalMilliseconds);
                Watchdog.Start(pingSeconds);
            }


            /// <summary>The fake host of the watchdog.</summary>
            public FakeClientHost Host { get; }


            /// <summary>The watchdog under test.</summary>
            public PingWatchdog Watchdog { get; }


            /// <summary>The window: ping interval plus allowed delay, in milliseconds.</summary>
            public long WindowMs { get; }


            /// <summary>The half window, in milliseconds.</summary>
            public long HalfWindowMs { get; }


            /// <summary>The clock of the fake timers, in milliseconds.</summary>
            public long NowMs => Host.FakeTimers.ElapsedMs;
        }


        /// <summary>
        /// Moves the clock to the given time and runs the steps that fell due. The steps queued behind a fired timer
        /// are waited for, so each check has run when this returns.
        /// </summary>
        /// <param name="rig">The rig.</param>
        /// <param name="timeMs">The time to move to, in milliseconds. Must not be before the clock.</param>
        private static void MoveTo(Rig rig, long timeMs) {
            rig.Host.FakeTimers.Advance((int)(timeMs - rig.NowMs));
            Drain(rig.Host.Loop);
        }


        /// <summary>Waits until the steps queued on the loop before this call have run.</summary>
        /// <param name="loop">The loop.</param>
        private static void Drain(EventLoop loop) {
            using ManualResetEventSlim done = new ManualResetEventSlim(false);
            loop.Execute(() => done.Set());
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "the loop did not drain");
        }


        /// <summary>
        /// Moves check by check after the last reply until one check closes the connection, and returns the time of
        /// that check. Fails when no check closes it within the limit of checks.
        /// </summary>
        /// <param name="rig">The rig.</param>
        /// <param name="lastReplyMs">The time of the last reply.</param>
        private static long MovesUntilClosed(Rig rig, long lastReplyMs) {
            for (int check = 1; check <= MaxChecks; check++) {
                long checkMs = check * rig.HalfWindowMs;
                if (checkMs <= lastReplyMs) {
                    continue;
                }
                MoveTo(rig, checkMs - 1);
                Assert.Empty(rig.Host.Disconnects);
                MoveTo(rig, checkMs);
                if (rig.Host.Disconnects.Count > 0) {
                    return checkMs;
                }
            }
            throw new InvalidOperationException("no disconnect within " + MaxChecks + " checks");
        }


        /// <summary>
        /// Sends the last reply at the given time, then moves check by check until one closes the connection. The close
        /// comes no earlier than one window after the reply and no later than three half windows after it.
        /// </summary>
        /// <param name="rig">The rig.</param>
        /// <param name="lastReplyMs">The time of the last reply.</param>
        private static void AssertClosedWithinBounds(Rig rig, long lastReplyMs) {
            MoveTo(rig, lastReplyMs);
            rig.Watchdog.MarkReply();
            long closedAtMs = MovesUntilClosed(rig, lastReplyMs);
            Assert.True(closedAtMs - lastReplyMs >= rig.WindowMs, "closed earlier than one window after the last reply");
            Assert.True(closedAtMs - lastReplyMs <= 3 * rig.HalfWindowMs, "closed later than three half windows after the last reply");
            Assert.Equal(new Tuple<int, string, bool>(CentrifugeConnectingCodes.NoPing, "no ping", true), Last(rig.Host.Disconnects));
        }


        /// <summary>The last entry of the disconnect records, as a tuple, so that a test can compare it.</summary>
        /// <param name="disconnects">The disconnect records.</param>
        private static Tuple<int, string, bool> Last(List<(int Code, string Reason, bool Reconnect)> disconnects) {
            (int Code, string Reason, bool Reconnect) last = disconnects[disconnects.Count - 1];
            return new Tuple<int, string, bool>(last.Code, last.Reason, last.Reconnect);
        }
    }
}
