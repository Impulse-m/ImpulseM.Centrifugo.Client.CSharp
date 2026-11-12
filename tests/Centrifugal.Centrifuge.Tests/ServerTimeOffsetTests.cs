// The pure offset rule of the server time: the offset is the server time minus the local time, and a zero or implausible
// server time gives no offset. The client keeps the previous offset over a reply with an implausible time.
// Not ported: ServerTimeTest.serverTimeIgnoresAnImplausibleReplyTime: the check needs a real reply over the rpc path, which these pure tests do not run.
// Not ported: ExtensionsTest.estimatedServerTime...: the server time is one addition in CentrifugeClient.Host (local clock plus offset), with no separate function to test.
using System;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Tests of the server clock offset: the difference, the zero time and the implausible times.</summary>
    public sealed class ServerTimeOffsetTests {
        private const long LocalMillis = 1_000_000L;
        private const long DayMillis = 24L * 60L * 60L * 1000L;


        // Java: serverTimeOffsetIsServerTimeMinusLocalTime
        [Fact]
        public void ServerTimeOffsetFrom_ServerTimeMinusLocalTime() {
            Assert.Equal(500L, ProtocolExtensions.ServerTimeOffsetFrom(1_000_500L, LocalMillis));
            Assert.Equal(-250L, ProtocolExtensions.ServerTimeOffsetFrom(999_750L, LocalMillis));
        }


        // Java: zeroServerTimeMeansNoOffset
        [Fact]
        public void ServerTimeOffsetFrom_ZeroServerTime_ReturnsNull() {
            Assert.Null(ProtocolExtensions.ServerTimeOffsetFrom(0L, LocalMillis));
        }


        // Java: serverTimeIgnoresAnImplausibleReplyTime
        [Fact]
        public void ServerTimeOffsetFrom_ImplausibleTime_ReturnsNull() {
            Assert.Null(ProtocolExtensions.ServerTimeOffsetFrom(long.MaxValue, LocalMillis));
            Assert.Null(ProtocolExtensions.ServerTimeOffsetFrom(long.MinValue, LocalMillis));
            Assert.Null(ProtocolExtensions.ServerTimeOffsetFrom(LocalMillis + 400L * DayMillis, LocalMillis));
        }


        // Gap: the limit is inclusive. A server time exactly one year ahead is still a clock: the rule is "more than".
        [Fact]
        public void ServerTimeOffsetFrom_ExactlyOneYearAhead_ReturnsTheOffset() {
            long oneYear = ProtocolExtensions.MaxServerTimeSkewMillis;

            Assert.Equal(oneYear, ProtocolExtensions.ServerTimeOffsetFrom(LocalMillis + oneYear, LocalMillis));
        }
    }
}
