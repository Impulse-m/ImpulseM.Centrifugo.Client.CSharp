// No Java test pins the error path by name. Not ported: the Java rule that a throwing error handler is
// dropped without a disconnect. The upstream rule (a throwing Error handler disconnects) is kept, as the
// foundation specifies; reported as a mismatch.
using System;
using System.Collections.Generic;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>The error path: a step that fails is reported as an unclassified error event.</summary>
    public sealed class ErrorReportingTests : FakeServerTestBase {
        /// <summary>A step that throws is reported as an unclassified error event with the cause wrapped, and the client keeps running.</summary>
        [Fact]
        public void Step_Throws_RaisesUnclassifiedErrorWithTheCause() {
            CentrifugeClient client = NewClient();
            List<CentrifugeErrorEventArgs> errors = new List<CentrifugeErrorEventArgs>();
            client.Error += (_, e) => errors.Add(e);
            InvalidOperationException cause = new InvalidOperationException("step failed");

            client.Loop.Execute(() => throw cause);

            Assert.Single(errors);
            Assert.Equal("unclassified", errors[0].Type);
            CentrifugeUnclassifiedException wrapped = Assert.IsType<CentrifugeUnclassifiedException>(errors[0].Exception);
            Assert.Same(cause, wrapped.Error);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        /// <summary>The unclassified wrapper keeps the cause message and the cause as its inner exception.</summary>
        [Fact]
        public void Unclassified_KeepsTheCauseMessage() {
            InvalidOperationException cause = new InvalidOperationException("the cause");

            CentrifugeUnclassifiedException wrapped = new CentrifugeUnclassifiedException(cause);

            Assert.Equal("the cause", wrapped.Message);
            Assert.Same(cause, wrapped.InnerException);
        }
    }
}
