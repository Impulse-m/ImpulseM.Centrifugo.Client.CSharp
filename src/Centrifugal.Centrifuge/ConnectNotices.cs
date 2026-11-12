using System;
using System.Collections.Generic;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// The callbacks of one connect reply, run in order. Before each callback the list stops when an application call
    /// superseded the reply. A callback that throws is reported as unclassified and counts as a failure. After a
    /// failure, a callback runs only when it was added with continueAfterFailure true. The first failure is returned.
    /// The caller decides on the disconnect.
    /// </summary>
    internal sealed class ConnectNotices {
        private readonly IClientHost host;
        private readonly int transition;
        private readonly List<NoticeEntry> entries = new List<NoticeEntry>();


        private sealed class NoticeEntry {
            /// <summary>
            /// Creates one entry.
            /// </summary>
            /// <param name="notice">The callback.</param>
            /// <param name="continueAfterFailure">True when the callback runs after an earlier failure.</param>
            public NoticeEntry(Func<Exception?> notice, bool continueAfterFailure) {
                Notice = notice;
                ContinueAfterFailure = continueAfterFailure;
            }


            /// <summary>The callback.</summary>
            public Func<Exception?> Notice { get; }


            /// <summary>True when the callback runs after an earlier failure.</summary>
            public bool ContinueAfterFailure { get; }
        }


        /// <summary>
        /// Creates the list of one reply.
        /// </summary>
        /// <param name="host">The client seam.</param>
        /// <param name="transition">The transition id of the reply.</param>
        public ConnectNotices(IClientHost host, int transition) {
            this.host = host;
            this.transition = transition;
        }


        /// <summary>
        /// Adds a callback. continueAfterFailure: it still runs after an earlier failure.
        /// </summary>
        /// <param name="notice">The callback. It returns the handler failure, or null.</param>
        /// <param name="continueAfterFailure">True when the callback runs after an earlier failure.</param>
        public void Add(Func<Exception?> notice, bool continueAfterFailure) {
            entries.Add(new NoticeEntry(notice, continueAfterFailure));
        }


        /// <summary>Runs the callbacks. Returns the first failure, or null when none failed.</summary>
        public Exception? Run() {
            Exception? first = null;
            foreach (NoticeEntry entry in entries) {
                if (host.TransitionSuperseded(transition)) {
                    break;
                }
                if (first != null && !entry.ContinueAfterFailure) {
                    continue;
                }
                Exception? failure;
                try {
                    failure = entry.Notice();
                } catch (Exception error) {
                    host.ReportUnclassified(error);
                    failure = error;
                }
                first ??= failure;
            }
            return first;
        }
    }
}
