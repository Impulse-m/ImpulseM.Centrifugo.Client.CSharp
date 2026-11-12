using System;

namespace Centrifugal.Centrifuge {


    /// <summary>Submits work to the client loop once the client is open (Java submitOrIgnore and submitOrRun).</summary>
    internal static class Submission {
        /// <summary>
        /// Runs step on the loop. Dropped when the client is closed, at submit time or when it runs.
        /// </summary>
        /// <param name="host">The client seam.</param>
        /// <param name="step">The step.</param>
        public static void ExecuteOrIgnore(IClientHost host, Action step) {
            if (host.IsClosed) {
                return;
            }
            host.Loop.Execute(() => {
                if (!host.IsClosed) {
                    step();
                }
            });
        }

        /// <summary>
        /// Runs step on the loop. When the client is closed, onClosed runs instead: on the calling thread at submit time,
        /// or on the loop when the close happens first.
        /// </summary>
        /// <param name="host">The client seam.</param>
        /// <param name="step">The step.</param>
        /// <param name="onClosed">The fallback when the client is closed.</param>
        public static void ExecuteOrRun(IClientHost host, Action step, Action onClosed) {
            if (host.IsClosed) {
                onClosed();
                return;
            }
            host.Loop.Execute(() => {
                if (host.IsClosed) {
                    onClosed();
                } else {
                    step();
                }
            });
        }
    }
}
