using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// Encodes one command as a frame: a varint length prefix, then the serialized command. The array is sized once
    /// and the command is written into it, so the payload is copied once. The bytes are the same as the delimited
    /// write of the command.
    /// </summary>
    internal static class FrameEncoder {
        /// <summary>
        /// The frame bytes of one command. An empty command is one zero byte (the pong).
        /// </summary>
        /// <param name="command">The command to encode.</param>
        internal static byte[] Encode(Command command) {
            int size = command.CalculateSize();
            int prefixSize = CodedOutputStream.ComputeUInt32Size((uint)size);
            byte[] buffer = new byte[prefixSize + size];
            CodedOutputStream output = new CodedOutputStream(buffer);
            output.WriteUInt32((uint)size);
            command.WriteTo(output);
            output.CheckNoSpaceLeft();
            return buffer;
        }
    }
}
