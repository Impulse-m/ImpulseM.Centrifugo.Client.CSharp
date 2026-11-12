// Unit tests of the connect notice list. The notices run in order, a failure skips the notices that do not
// continue after a failure, a throwing notice is reported as unclassified, and a superseded reply stops the list.
using System;
using System.Collections.Generic;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>Unit tests of the connect notice list.</summary>
    public sealed class ConnectNoticesTests {
        /// <summary>Notices run in the order they were added.</summary>
        [Fact]
        public void Run_RunsTheNoticesInOrder() {
            FakeClientHost host = new FakeClientHost();
            List<string> order = new List<string>();
            ConnectNotices notices = new ConnectNotices(host, 0);
            notices.Add(() => {
                order.Add("first");
                return null;
            }, false);
            notices.Add(() => {
                order.Add("second");
                return null;
            }, false);

            Exception? failure = notices.Run();

            Assert.Null(failure);
            Assert.Equal(new[] { "first", "second" }, order);
        }


        /// <summary>After a failure only the notices added with continueAfterFailure run; the first failure is returned.</summary>
        [Fact]
        public void Run_AfterAFailure_SkipsNoticesWithoutContinueFlag() {
            FakeClientHost host = new FakeClientHost();
            Exception handlerFailure = new InvalidOperationException("handler");
            List<string> order = new List<string>();
            ConnectNotices notices = new ConnectNotices(host, 0);
            notices.Add(() => {
                order.Add("connected");
                return handlerFailure;
            }, false);
            notices.Add(() => {
                order.Add("subscribed");
                return null;
            }, false);
            notices.Add(() => {
                order.Add("publication");
                return null;
            }, true);

            Exception? failure = notices.Run();

            Assert.Same(handlerFailure, failure);
            Assert.Equal(new[] { "connected", "publication" }, order);
        }


        /// <summary>A notice that throws is reported as unclassified, counts as a failure, and the list goes on.</summary>
        [Fact]
        public void Run_NoticeThrows_ReportsUnclassifiedAndCountsAsFailure() {
            FakeClientHost host = new FakeClientHost();
            InvalidOperationException thrown = new InvalidOperationException("boom");
            ConnectNotices notices = new ConnectNotices(host, 0);
            notices.Add(() => throw thrown, true);

            Exception? failure = notices.Run();

            Assert.Same(thrown, failure);
            Assert.Single(host.Unclassified);
            Assert.Same(thrown, host.Unclassified[0]);
        }


        /// <summary>When an application call superseded the reply, no notice runs.</summary>
        [Fact]
        public void Run_WhenSuperseded_RunsNothing() {
            FakeClientHost host = new FakeClientHost();
            host.TransitionSupersededResult = true;
            int runs = 0;
            ConnectNotices notices = new ConnectNotices(host, 1);
            notices.Add(() => {
                runs++;
                return null;
            }, true);

            Exception? failure = notices.Run();

            Assert.Null(failure);
            Assert.Equal(0, runs);
        }
    }
}
