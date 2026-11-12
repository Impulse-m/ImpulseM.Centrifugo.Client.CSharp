// Ported from Java FrameEncoderTest (3 tests). The expected bytes come from Google.Protobuf's delimited write, so the
// test does not depend on the encoder it checks. AnEmptyCommandIsOneZeroByte is a port-only test for rule R3 (the pong).
using System;
using System.IO;
using Google.Protobuf;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// The frame bytes of one command are the varint length prefix and the serialized command, as the delimited write.
    /// </summary>
    public sealed class FrameEncoderTests {
        /// <summary>A small command is the same bytes as its delimited write.</summary>
        [Fact]
        // Java: aSmallCommandIsTheDelimitedBytes
        public void ASmallCommandIsTheDelimitedBytes() {
            AssertSameAsDelimited(Command(new byte[] { 1, 2, 3 }));
        }


        /// <summary>A command with an empty payload is the same bytes as its delimited write.</summary>
        [Fact]
        // Java: anEmptyPayloadIsTheDelimitedBytes
        public void AnEmptyPayloadIsTheDelimitedBytes() {
            AssertSameAsDelimited(Command(Array.Empty<byte>()));
        }


        /// <summary>A command with a one megabyte payload is the same bytes as its delimited write.</summary>
        [Fact]
        // Java: aMegabytePayloadIsTheDelimitedBytes
        public void AMegabytePayloadIsTheDelimitedBytes() {
            byte[] payload = new byte[1024 * 1024];
            Array.Fill(payload, (byte)7);
            AssertSameAsDelimited(Command(payload));
        }


        /// <summary>An empty command is one zero byte: the length prefix of an empty message, and no body.</summary>
        [Fact]
        public void AnEmptyCommandIsOneZeroByte() {
            Assert.Equal(new byte[] { 0 }, FrameEncoder.Encode(new Protocol.Command()));
        }


        // Compares the encoder with the delimited write of the same command.
        private static void AssertSameAsDelimited(Protocol.Command command) {
            using MemoryStream expected = new MemoryStream();
            command.WriteDelimitedTo(expected);
            Assert.Equal(expected.ToArray(), FrameEncoder.Encode(command));
        }


        // A publish-free rpc command with id 1: the same shape as the Java test.
        private static Protocol.Command Command(byte[] payload) {
            return new Protocol.Command {
                Id = 1,
                Rpc = new Protocol.RPCRequest {
                    Method = "blob.writePart",
                    Data = ByteString.CopyFrom(payload),
                },
            };
        }
    }
}
