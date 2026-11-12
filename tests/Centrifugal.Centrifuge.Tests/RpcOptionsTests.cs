// Ported from the Java fork's OptionsExactlyOnceTest (8 tests, the value part of the options): all 8 ported under the Java
// names, kept in a comment on each test. The instance is refused by the setter, as in Java; the Java name "refused at
// create" is kept for the test that checks the refusal.
// Gap tests (no Java test pins them): the header replace and order rules, the header snapshot, the timeout without an
// upper bound, and the snapshot a call takes of its options.
// The connect flag, the capability and the per-call wire checks of the Java RpcOptionsTest are not in this file: they
// belong to the connect capability and rpc slices.
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


        // Java: timeoutIsReadBackAndDefaultsToZero (the Java zero default is the null of the TimeSpan option)
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


        // Gap: the per-call timeout has no upper bound (rule R8, the Java behaviour; the JS refusal is not ported).
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


        // Gap: a repeated header key replaces the value and keeps the key's first position (rule R9).
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


        // Gap: headers come back in the order they were added (rule R9).
        [Fact]
        public void Headers_AreReturnedInTheOrderTheyWereAdded() {
            CentrifugeRpcOptions options = new CentrifugeRpcOptions()
                .WithHeader("b", "1")
                .WithHeader("a", "2")
                .WithHeader("c", "3");

            Assert.Equal(new string[] { "b", "a", "c" }, new List<string>(options.Headers.Keys));
        }


        // Gap: a null header key or value is refused at the call (rule R9).
        [Fact]
        public void WithHeader_NullKeyOrValue_IsRefused() {
            CentrifugeRpcOptions options = new CentrifugeRpcOptions();

            Assert.Throws<ArgumentNullException>(() => options.WithHeader(null!, "value"));
            Assert.Throws<ArgumentNullException>(() => options.WithHeader("key", null!));
            Assert.Empty(options.Headers);
        }


        // Gap: a headers dictionary already returned is a snapshot and does not change with later WithHeader calls.
        [Fact]
        public void Headers_ReturnedDictionary_IsASnapshot() {
            CentrifugeRpcOptions options = new CentrifugeRpcOptions().WithHeader("a", "1");
            IReadOnlyDictionary<string, string> before = options.Headers;

            options.WithHeader("b", "2");

            Assert.Single(before);
            Assert.Equal(2, options.Headers.Count);
        }


        // Gap: a call takes a snapshot of its options, so a later change by the caller does not reach it.
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
