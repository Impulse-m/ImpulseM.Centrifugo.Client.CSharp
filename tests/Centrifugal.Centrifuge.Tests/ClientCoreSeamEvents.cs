using System.Threading.Tasks;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>Event waiters shared by the client core tests.</summary>
    internal static class ClientCoreSeamEvents {
        /// <summary>
        /// Completes with the event args of the next Disconnected event of the client.
        /// </summary>
        /// <param name="client">The client to watch.</param>
        public static Task<CentrifugeDisconnectedEventArgs> WhenDisconnected(CentrifugeClient client) {
            TaskCompletionSource<CentrifugeDisconnectedEventArgs> source =
                new TaskCompletionSource<CentrifugeDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += (_, e) => source.TrySetResult(e);
            return source.Task;
        }
    }
}
