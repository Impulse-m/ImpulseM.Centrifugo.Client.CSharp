namespace Centrifugal.Centrifuge {


    /// <summary>The connect flag bits of the fork (Java Extensions.RPC_OPTIONS_FLAG and hasFlag).</summary>
    internal static class ConnectCapabilities {
        /// <summary>Bit 32 of ConnectRequest.flag and ConnectResult.flag: the client understands RPC options (per-call headers and fields).</summary>
        public const long RpcOptionsFlag = 1L << 32;


        /// <summary>Whether bit flag is set in flags.</summary>
        /// <param name="flags">The flags of a connect message.</param>
        /// <param name="flag">The bit to test.</param>
        public static bool HasFlag(long flags, long flag) {
            return (flags & flag) != 0;
        }
    }
}
