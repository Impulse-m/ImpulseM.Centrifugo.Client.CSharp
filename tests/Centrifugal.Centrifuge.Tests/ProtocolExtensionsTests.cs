// Protocol extension tests: the RPC options flag arithmetic, the instance check, the error metadata and the retry signal.
// The flag helpers live in ConnectCapabilities. The server time offset tests are in ServerTimeOffsetTests.cs.
// Not ported: ExtensionsTest.estimatedServerTimeAddsOffsetToLocalTime and ExtensionsTest.estimatedServerTimeIsLocalTimeWithoutOffset: the estimate is one addition in CentrifugeClient.Host, with no function of its own to test.
// Gap tests: the retry signal key and its values, and the instance check of a null value.
using System;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Tests of the protocol extension helpers: the connect flag bits, the instance pattern, the reply of a call the server cannot take and the retry signal.</summary>
    public sealed class ProtocolExtensionsTests {
        // Java: rpcOptionsFlagIsBit32
        [Fact]
        public void RpcOptionsFlag_IsBit32() {
            Assert.Equal(4294967296L, ConnectCapabilities.RpcOptionsFlag);
        }


        // Java: hasFlagFindsBit32AmongOtherBits
        [Fact]
        public void HasFlag_FindsBit32AmongOtherBits() {
            Assert.True(ConnectCapabilities.HasFlag((1L << 32) | 1L, ConnectCapabilities.RpcOptionsFlag));
            Assert.True(ConnectCapabilities.HasFlag(1L << 32, ConnectCapabilities.RpcOptionsFlag));
        }


        // Java: hasFlagIsFalseWithoutTheBit
        [Fact]
        public void HasFlag_IsFalseWithoutTheBit() {
            Assert.False(ConnectCapabilities.HasFlag(0L, ConnectCapabilities.RpcOptionsFlag));
            Assert.False(ConnectCapabilities.HasFlag(1L, ConnectCapabilities.RpcOptionsFlag));
            Assert.False(ConnectCapabilities.HasFlag(1L << 31, ConnectCapabilities.RpcOptionsFlag));
        }


        // Java: unsupportedRpcReplyWithoutIdHasNoCommandIdAndCodeFifteen
        [Fact]
        public void UnsupportedRpcReply_HasNoCommandIdAndCodeFifteen() {
            Protocol.Reply reply = ProtocolExtensions.UnsupportedRpcReply();

            Assert.Equal(0U, reply.Id);
            Assert.Equal((uint)CentrifugeErrorCodes.Unsupported, reply.Error.Code);
            Assert.Equal("rpc headers are not supported by the server", reply.Error.Message);
        }


        // Java: instanceAcceptsTwentyTwoToSixtyFourUrlSafeCharacters
        [Fact]
        public void IsValidInstance_AcceptsTwentyTwoToSixtyFourUrlSafeCharacters() {
            Assert.True(ProtocolExtensions.IsValidInstance("abcdefghijklmnopqrstuv"));
            Assert.True(ProtocolExtensions.IsValidInstance("ABCDEFGHIJKLMNOPQRSTUV_-"));
            Assert.True(ProtocolExtensions.IsValidInstance(Repeat('x', 64)));
        }


        // Java: instanceRejectsOtherLengthsAndCharacters
        [Fact]
        public void IsValidInstance_RejectsOtherLengthsAndCharacters() {
            Assert.False(ProtocolExtensions.IsValidInstance(Repeat('x', 21)));
            Assert.False(ProtocolExtensions.IsValidInstance(Repeat('x', 65)));
            Assert.False(ProtocolExtensions.IsValidInstance("abcdefghijklmnopqrstu+"));
            Assert.False(ProtocolExtensions.IsValidInstance("abcdefghijklmnopqrstuv\n"));
            Assert.False(ProtocolExtensions.IsValidInstance(string.Empty));
        }


        // Gap: a missing value is not an instance.
        [Fact]
        public void IsValidInstance_Null_IsFalse() {
            Assert.False(ProtocolExtensions.IsValidInstance(null));
        }


        // Gap: the retry signal is the value of the error metadata key.
        [Fact]
        public void RetrySignal_ReadsTheMetadataKey() {
            Protocol.Error error = new Protocol.Error();
            error.Metadata[ProtocolExtensions.RetryMetadataKey] = ProtocolExtensions.RetryLater;

            Assert.Equal("later", ProtocolExtensions.RetrySignal(error));
            Assert.Equal("x-centrifugo-retry", ProtocolExtensions.RetryMetadataKey);
            Assert.Equal("new_msg_id", ProtocolExtensions.RetryNewMsgId);
        }


        // Gap: an error without the key carries no retry signal.
        [Fact]
        public void RetrySignal_WithoutTheKey_IsNull() {
            Protocol.Error error = new Protocol.Error();
            error.Metadata["other"] = "value";

            Assert.Null(ProtocolExtensions.RetrySignal(error));
        }


        private static string Repeat(char c, int count) {
            return new string(c, count);
        }
    }
}
