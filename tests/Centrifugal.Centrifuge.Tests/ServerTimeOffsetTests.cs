// The pure offset rule of the server time (rule R55 points 1 to 3), ported from the Java fork's ExtensionsTest and from the
// implausible-time case of ServerTimeTest. Status:
//   ExtensionsTest.serverTimeOffsetIsServerTimeMinusLocalTime: ported (ServerTimeOffsetFrom_ServerTimeMinusLocalTime).
//   ExtensionsTest.zeroServerTimeMeansNoOffset: ported (ServerTimeOffsetFrom_ZeroServerTime_ReturnsNull).
//   ServerTimeTest.serverTimeIgnoresAnImplausibleReplyTime: the pure part is ported
//     (ServerTimeOffsetFrom_ImplausibleTime_ReturnsNull). The integration part belongs to the rpc slices (the client keeps
//     the previous offset over a real reply).
// The Java ExtensionsTest estimate tests (estimatedServerTime...) are not applicable: the server time is one addition in
// CentrifugeClient.Host (local clock plus offset), with no separate function to test.
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


        // Java: serverTimeIgnoresAnImplausibleReplyTime (pure part)
        [Fact]
        public void ServerTimeOffsetFrom_ImplausibleTime_ReturnsNull() {
            Assert.Null(ProtocolExtensions.ServerTimeOffsetFrom(long.MaxValue, LocalMillis));
            Assert.Null(ProtocolExtensions.ServerTimeOffsetFrom(long.MinValue, LocalMillis));
            Assert.Null(ProtocolExtensions.ServerTimeOffsetFrom(LocalMillis + 400L * DayMillis, LocalMillis));
        }


        // Gap: the limit is inclusive. A server time exactly one year ahead is still a clock (rule R55 point 2: "more than").
        [Fact]
        public void ServerTimeOffsetFrom_ExactlyOneYearAhead_ReturnsTheOffset() {
            long oneYear = ProtocolExtensions.MaxServerTimeSkewMillis;

            Assert.Equal(oneYear, ProtocolExtensions.ServerTimeOffsetFrom(LocalMillis + oneYear, LocalMillis));
        }
    }
}
