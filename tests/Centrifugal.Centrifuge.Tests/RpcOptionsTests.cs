// Not ported: connectRequestAdvertisesRpcOptionsBit: the connect request advertises the handshake flag, not an option.
// Not ported: capabilityIsOnWhenConnectReplyHasRpcOptionsBit: the capability is read from the connect reply, a connection detail.
// Not ported: capabilityIsOnWhenRpcOptionsBitComesWithOtherBits: the capability is read from the connect reply flags, a connection detail.
// Not ported: capabilityIsOffWhenConnectReplyHasNoRpcOptionsBit: the capability is read from the connect reply flags, a connection detail.
// Not ported: capabilityIsOffWhenConnectReplyHasNoFlag: the capability is read from the connect reply flags, a connection detail.
// Not ported: capabilityIsClearedOnReconnectUntilConnectReplyConfirmsIt: the capability across a reconnect is a connection detail.
// Not ported: capabilityIsClearedOnDisconnect: the capability after a disconnect is a connection detail.
// Not ported: capabilitySnapshotDoesNotChangeAfterwards: the capability snapshot across a disconnect is a connection detail.
// Not ported: rpcWithHeadersIsSentAndResultDelivered: the wire frame of a call with headers is a wire-format check.
// Not ported: rpcWithHeadersFailsWithoutCapabilityAndIsNotSent: the wire refusal of headers without the capability is a wire check.
// Not ported: plainRpcWorksWithoutCapability: the wire frame of a plain call is a wire-format check.
// Not ported: plainRpcWorksWithCapability: the wire frame of a plain call is a wire-format check.
// Not ported: emptyOptionsAreAPlainCallWithoutCapability: the wire frame of an empty option set is a wire-format check.
// Not ported: rpcWithHeadersQueuedWhileConnectingIsSentWhenConnectReplyConfirmsIt: a call queued during connect is a wire-timing check.
// Not ported: rpcWithHeadersQueuedWhileConnectingFailsWhenConnectReplyDoesNotConfirmIt: a call queued during connect is a wire-timing check.
using System;
using System.Collections.Generic;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Tests of the options of the exactly-once rpc calls: the instance, the queue bound, the call timeout and the headers.</summary>
    public sealed class RpcOptionsTests {
        // Java: instanceIsEmptyByDefaultAndNullClearsIt
        [Fact]
        public void Instance_IsEmptyByDefaultAndNullClearsIt() {
            CentrifugeClientOptions options = new CentrifugeClientOptions();
            Assert.Equal(string.Empty, options.Instance);

            options.Instance = "abcdefghijklmnopqrstuv";
            options.Instance = null!;

            Assert.Equal(string.Empty, options.Instance);
        }


        // Java: setInstanceKeepsAValidValue
        [Fact]
        public void Instance_ValidValue_IsKept() {
            CentrifugeClientOptions options = new CentrifugeClientOptions();

            options.Instance = "abcdefghijklmnopqrstuv";

            Assert.Equal("abcdefghijklmnopqrstuv", options.Instance);
        }


        // Java: setInstanceRejectsAValueOutsideThePattern
        [Fact]
        public void Instance_ValueOutsideThePattern_IsRefusedAndLeavesTheValue() {
            CentrifugeClientOptions options = new CentrifugeClientOptions();

            Assert.Throws<ArgumentException>(() => options.Instance = "too-short");

            Assert.Equal(string.Empty, options.Instance);
        }


        // Java: invalidInstanceIsRefusedAtCreate
        // The instance is refused by its setter before any client is created, and the value is kept unchanged.
        [Fact]
        public void Instance_InvalidValues_AreRefusedBeforeAnyClientIsCreated() {
            string tooLong = "abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyzabcde";
            string badCharacter = "abcdefghijklmnopqrstuv!";
            foreach (string instance in new string[] { tooLong, badCharacter }) {
                CentrifugeClientOptions options = new CentrifugeClientOptions();

                Assert.Throws<ArgumentException>(() => options.Instance = instance);

                Assert.Equal(string.Empty, options.Instance);
            }
        }


        // Java: timeoutIsReadBackAndDefaultsToZero
        // The default is null, which means the client timeout.
        [Fact]
        public void Timeout_IsReadBackAndDefaultsToNull() {
            CentrifugeRpcOptions defaults = new CentrifugeRpcOptions();
            Assert.Null(defaults.Timeout);

            CentrifugeRpcOptions withTimeout = new CentrifugeRpcOptions {
                Timeout = TimeSpan.FromMilliseconds(750),
            };

            Assert.Equal(TimeSpan.FromMilliseconds(750), withTimeout.Timeout);
        }


        // Java: timeoutRejectsANonPositiveValue
        [Fact]
        public void Timeout_ZeroOrNegative_IsRefused() {
            CentrifugeRpcOptions options = new CentrifugeRpcOptions();

            ArgumentOutOfRangeException zeroError = Assert.Throws<ArgumentOutOfRangeException>(() => options.Timeout = TimeSpan.Zero);
            Assert.Equal("value", zeroError.ParamName);
            Assert.Contains("Timeout", zeroError.Message);

            ArgumentOutOfRangeException negativeError = Assert.Throws<ArgumentOutOfRangeException>(() => options.Timeout = TimeSpan.FromMilliseconds(-1));
            Assert.Equal("value", negativeError.ParamName);
            Assert.Contains("Timeout", negativeError.Message);

            Assert.Null(options.Timeout);
        }


        // The per-call timeout has no upper bound: a call may outlast the client timeout.
        [Fact]
        public void Timeout_AboveTheClientTimeout_IsAccepted() {
            CentrifugeRpcOptions options = new CentrifugeRpcOptions {
                Timeout = TimeSpan.FromMinutes(10),
            };

            Assert.Equal(TimeSpan.FromMinutes(10), options.Timeout);
        }


        // Java: maxPendingRpcDefaultsToThirtyTwo
        [Fact]
        public void MaxPendingRpc_DefaultsToThirtyTwo() {
            Assert.Equal(32, new CentrifugeClientOptions().MaxPendingRpc);
        }


        // Java: setMaxPendingRpcRejectsABoundBelowOne
        [Fact]
        public void MaxPendingRpc_BelowOne_IsRefusedAndKeepsTheDefault() {
            CentrifugeClientOptions options = new CentrifugeClientOptions();

            Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxPendingRpc = 0);

            Assert.Equal(32, options.MaxPendingRpc);
        }


        // A repeated header key replaces the value and keeps the key's first position.
        [Fact]
        public void WithHeader_RepeatedKey_ReplacesTheValueAndKeepsItsPosition() {
            CentrifugeRpcOptions options = new CentrifugeRpcOptions()
                .WithHeader("first", "1")
                .WithHeader("second", "2")
                .WithHeader("first", "3");

            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>(options.Headers);
            Assert.Equal(2, headers.Count);
            Assert.Equal("first", headers[0].Key);
            Assert.Equal("3", headers[0].Value);
            Assert.Equal("second", headers[1].Key);
            Assert.Equal("2", headers[1].Value);
        }


        // Headers come back in the order they were added.
        [Fact]
        public void Headers_AreReturnedInTheOrderTheyWereAdded() {
            CentrifugeRpcOptions options = new CentrifugeRpcOptions()
                .WithHeader("b", "1")
                .WithHeader("a", "2")
                .WithHeader("c", "3");

            Assert.Equal(new string[] { "b", "a", "c" }, new List<string>(options.Headers.Keys));
        }


        // A null header key or value is refused when it is added, so no call is made with it.
        [Fact]
        public void WithHeader_NullKeyOrValue_IsRefused() {
            CentrifugeRpcOptions options = new CentrifugeRpcOptions();

            Assert.Throws<ArgumentNullException>(() => options.WithHeader(null!, "value"));
            Assert.Throws<ArgumentNullException>(() => options.WithHeader("key", null!));
            Assert.Empty(options.Headers);
        }


        // A headers dictionary already returned is a snapshot and does not change with later WithHeader calls.
        [Fact]
        public void Headers_ReturnedDictionary_IsASnapshot() {
            CentrifugeRpcOptions options = new CentrifugeRpcOptions().WithHeader("a", "1");
            IReadOnlyDictionary<string, string> before = options.Headers;

            options.WithHeader("b", "2");

            Assert.Single(before);
            Assert.Equal(2, options.Headers.Count);
        }


        // A call takes a snapshot of its options, so a later change by the caller does not reach it.
        [Fact]
        public void Snapshot_LaterChangesDoNotReachTheCopy() {
            CentrifugeRpcOptions options = new CentrifugeRpcOptions {
                Timeout = TimeSpan.FromSeconds(3),
            };
            options.WithHeader("a", "1");

            CentrifugeRpcOptions copy = options.Snapshot();
            options.WithHeader("b", "2");
            options.Timeout = TimeSpan.FromSeconds(9);

            Assert.Single(copy.Headers);
            Assert.Equal(TimeSpan.FromSeconds(3), copy.Timeout);
        }
    }
}
