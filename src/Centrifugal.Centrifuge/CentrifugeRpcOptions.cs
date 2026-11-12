using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Centrifugal.Centrifuge {


    /// <summary>
    /// Options of one rpc call: headers that are sent with that call only, and a deadline that holds across all its
    /// attempts. Headers need a server that confirms RPC options (see <see cref="CentrifugeClientCapabilities.RpcOptionsSupported"/>).
    /// </summary>
    public sealed class CentrifugeRpcOptions {
        /// <summary>
        /// Gets the headers sent with the call, in the order they were added. A repeated key replaces the value and keeps
        /// its position. The returned dictionary is a snapshot: later changes of these options do not change it.
        /// </summary>
        public IReadOnlyDictionary<string, string> Headers {
            get {
                Dictionary<string, string> copy = new Dictionary<string, string>(headerList.Count);
                foreach (KeyValuePair<string, string> header in headerList) {
                    copy.Add(header.Key, header.Value);
                }
                return new ReadOnlyDictionary<string, string>(copy);
            }
        }


        /// <summary>
        /// Gets or sets the deadline of the call across all its attempts, counted from the moment the call is made. Null, the
        /// default, means the client Timeout. There is no upper bound. A value of zero or less is refused.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is zero or less.</exception>
        public TimeSpan? Timeout {
            get {
                return timeout;
            }
            set {
                if (value.HasValue && value.Value <= TimeSpan.Zero) {
                    throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be positive");
                }
                timeout = value;
            }
        }


        private readonly List<KeyValuePair<string, string>> headerList = new List<KeyValuePair<string, string>>();
        private TimeSpan? timeout;


        /// <summary>
        /// Creates empty options: no headers and the client timeout.
        /// </summary>
        public CentrifugeRpcOptions() {
        }


        /// <summary>
        /// Adds a header to the call. Adding a key that is already present replaces its value and keeps its position.
        /// </summary>
        /// <param name="key">The header name.</param>
        /// <param name="value">The header value.</param>
        /// <returns>These options, for chaining.</returns>
        /// <exception cref="ArgumentNullException">The key or the value is null.</exception>
        public CentrifugeRpcOptions WithHeader(string key, string value) {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(value);
            int index = headerList.FindIndex(header => header.Key == key);
            if (index >= 0) {
                headerList[index] = new KeyValuePair<string, string>(key, value);
            } else {
                headerList.Add(new KeyValuePair<string, string>(key, value));
            }
            return this;
        }


        /// <summary>
        /// Copies these options. The client takes a copy when a call is made, so that later changes of the caller's object
        /// do not reach the call. Call on the thread that the caller uses, before the call is posted to the event loop.
        /// </summary>
        /// <returns>A copy with the same headers and timeout.</returns>
        internal CentrifugeRpcOptions Snapshot() {
            CentrifugeRpcOptions copy = new CentrifugeRpcOptions();
            copy.headerList.AddRange(headerList);
            copy.timeout = timeout;
            return copy;
        }
    }
}
