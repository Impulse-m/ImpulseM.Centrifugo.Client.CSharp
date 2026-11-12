namespace Centrifugal.Centrifuge {


    /// <summary>The main client phase as the rpc pools see it.</summary>
    internal enum RpcPoolMainState {
        Connected,
        Connecting,
        Disconnected,
        Ended,
    }
}
