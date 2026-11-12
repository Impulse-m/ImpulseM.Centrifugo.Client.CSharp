using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

// Tests of the publish debounce window (PublishDebounce). Ported from PublishDebounceTest.java (8 tests):
// publishFirstIsSentAtOnceAndWindowCoalescesTheRest -> Publish_FirstSentAtOnce_WindowCoalescesRest,
// idleWindowEndsAndNextPublishIsSentAtOnce -> Publish_AfterIdleWindows_SendsAtOnce,
// unsubscribeDropsPendingValue -> Unsubscribe_DropsPending, reconnectDropsPendingValue -> Reconnect_DropsPending,
// zeroWindowSendsEveryPublish -> Publish_ZeroWindow_SendsEveryPublish,
// windowAboveSignedIntRangeIsCapped -> CapWindow_AboveSignedRange_ReturnsIntMax,
// timerDrivenPublishRefusedByServerDoesNotStopTheNextWindow -> WindowEnd_Refused_NextWindowStillSends,
// throwingCallbackOfCoalescedPublishRunsOnce -> Publish_InsideWindow_CompletesOnce (renamed: the coalesced caller gets a task, not a callback).
// The windows run on FakeStepTimerFactory, so no test waits for real time.
namespace Centrifugal.Centrifuge.Tests {


    public class PublishDebounceTests {
        private readonly EventLoop loop = new EventLoop(null, null);
        private readonly FakeStepTimerFactory timers;
        private readonly List<byte[]> sent = new List<byte[]>();
        private Func<int, Task> answer = index => Task.CompletedTask;


        public PublishDebounceTests() {
            timers = new FakeStepTimerFactory(loop);
        }


        private PublishDebounce NewDebounce() {
            return new PublishDebounce(loop, timers, (data, cancellationToken) => {
                int index = sent.Count;
                sent.Add(data);
                return answer(index);
            });
        }


        private Task PublishInStep(PublishDebounce debounce, string text, int windowMillis) {
            Task result = Task.CompletedTask;
            loop.Execute(() => {
                result = debounce.Publish(Encoding.UTF8.GetBytes(text), windowMillis, CancellationToken.None);
            });
            return result;
        }


        private void CancelInStep(PublishDebounce debounce) {
            loop.Execute(() => {
                debounce.Cancel();
            });
        }


        private List<string> SentTexts() {
            List<string> texts = new List<string>();
            foreach (byte[] data in sent) {
                texts.Add(Encoding.UTF8.GetString(data));
            }
            return texts;
        }


        [Fact]
        public void Publish_IdleWindow_SendsAtOnce() {
            PublishDebounce debounce = NewDebounce();

            Task first = PublishInStep(debounce, "a", 100);

            Assert.True(first.IsCompletedSuccessfully);
            Assert.Equal(new List<string> { "a" }, SentTexts());
            Assert.Equal(1, timers.PendingWithDelay(100));
        }


        [Fact]
        // Java: publishFirstIsSentAtOnceAndWindowCoalescesTheRest
        public void Publish_FirstSentAtOnce_WindowCoalescesRest() {
            PublishDebounce debounce = NewDebounce();

            PublishInStep(debounce, "a", 100);
            PublishInStep(debounce, "b", 100);
            PublishInStep(debounce, "c", 100);
            Assert.Equal(new List<string> { "a" }, SentTexts());

            timers.Advance(100);
            Assert.Equal(new List<string> { "a", "c" }, SentTexts());

            // The window that sent the last value is empty: it closes, and the next publish is sent at once.
            timers.Advance(100);
            Assert.Equal(new List<string> { "a", "c" }, SentTexts());
            PublishInStep(debounce, "d", 100);
            Assert.Equal(new List<string> { "a", "c", "d" }, SentTexts());
        }


        [Fact]
        // Java: idleWindowEndsAndNextPublishIsSentAtOnce
        public void Publish_AfterIdleWindows_SendsAtOnce() {
            PublishDebounce debounce = NewDebounce();

            PublishInStep(debounce, "a", 100);
            timers.Advance(100);
            Assert.Equal(0, timers.PendingCount);

            PublishInStep(debounce, "b", 100);
            Assert.Equal(new List<string> { "a", "b" }, SentTexts());
        }


        [Fact]
        // Java: unsubscribeDropsPendingValue (the subscription's unsubscribe calls Cancel)
        public void Unsubscribe_DropsPending() {
            PublishDebounce debounce = NewDebounce();
            PublishInStep(debounce, "a", 100);
            PublishInStep(debounce, "b", 100);

            CancelInStep(debounce);
            timers.Advance(200);

            Assert.Equal(new List<string> { "a" }, SentTexts());
            Assert.Equal(0, timers.PendingCount);
        }


        [Fact]
        // Java: reconnectDropsPendingValue (a transport loss moves the subscription to subscribing, which calls Cancel)
        public void Reconnect_DropsPending() {
            PublishDebounce debounce = NewDebounce();
            PublishInStep(debounce, "a", 100);
            PublishInStep(debounce, "b", 100);

            CancelInStep(debounce);
            PublishInStep(debounce, "c", 100);
            timers.Advance(100);

            Assert.Equal(new List<string> { "a", "c" }, SentTexts());
        }


        [Fact]
        // Java: zeroWindowSendsEveryPublish (a zero window arms nothing, so each publish is sent at once)
        public void Publish_ZeroWindow_SendsEveryPublish() {
            PublishDebounce debounce = NewDebounce();

            PublishInStep(debounce, "a", 0);
            PublishInStep(debounce, "b", 0);

            Assert.Equal(new List<string> { "a", "b" }, SentTexts());
            Assert.Equal(0, timers.PendingCount);
        }


        [Fact]
        // Java: windowAboveSignedIntRangeIsCapped (the wire value 2147483649 is read as unsigned)
        public void CapWindow_AboveSignedRange_ReturnsIntMax() {
            Assert.Equal(int.MaxValue, PublishDebounce.CapWindow(2147483649u));
            Assert.Equal(int.MaxValue, PublishDebounce.CapWindow(uint.MaxValue));
            Assert.Equal(0, PublishDebounce.CapWindow(0u));
            Assert.Equal(250, PublishDebounce.CapWindow(250u));
        }


        [Fact]
        // Java: timerDrivenPublishRefusedByServerDoesNotStopTheNextWindow
        public void WindowEnd_Refused_NextWindowStillSends() {
            answer = index => index == 1
                ? Task.FromException(new InvalidOperationException("refused"))
                : Task.CompletedTask;
            PublishDebounce debounce = NewDebounce();

            PublishInStep(debounce, "a", 100);
            PublishInStep(debounce, "b", 100);
            timers.Advance(100);
            Assert.Equal(new List<string> { "a", "b" }, SentTexts());
            Assert.Equal(1, timers.PendingWithDelay(100));

            PublishInStep(debounce, "c", 100);
            timers.Advance(100);
            Assert.Equal(new List<string> { "a", "b", "c" }, SentTexts());
        }


        [Fact]
        // Java: throwingCallbackOfCoalescedPublishRunsOnce (renamed: the coalesced caller gets a task, not a callback)
        public void Publish_InsideWindow_CompletesOnce() {
            PublishDebounce debounce = NewDebounce();
            PublishInStep(debounce, "a", 100);

            Task coalesced = PublishInStep(debounce, "b", 100);

            Assert.True(coalesced.IsCompletedSuccessfully);
            Assert.Single(sent);
            timers.Advance(100);
            Assert.Equal(new List<string> { "a", "b" }, SentTexts());
        }


        [Fact]
        public void WindowEnd_SendsPendingValueOnly() {
            PublishDebounce debounce = NewDebounce();
            PublishInStep(debounce, "a", 100);
            PublishInStep(debounce, "b", 100);
            PublishInStep(debounce, "c", 100);

            timers.Advance(100);

            Assert.Equal(new List<string> { "a", "c" }, SentTexts());
        }


        [Fact]
        public void Window_ValueFixedUntilNextIdle() {
            PublishDebounce debounce = NewDebounce();
            PublishInStep(debounce, "a", 100);
            // A value from a later reply inside the window does not change the window that is armed.
            PublishInStep(debounce, "b", 500);

            timers.Advance(100);

            Assert.Equal(100, timers.RequestedDelays[timers.RequestedDelays.Count - 1]);
            Assert.Equal(1, timers.PendingWithDelay(100));
        }


        [Fact]
        public void Publish_AfterCancel_OpensNewWindowWithoutStaleValue() {
            PublishDebounce debounce = NewDebounce();
            PublishInStep(debounce, "a", 100);
            PublishInStep(debounce, "b", 100);
            CancelInStep(debounce);

            PublishInStep(debounce, "c", 100);
            PublishInStep(debounce, "d", 100);
            timers.Advance(100);

            Assert.Equal(new List<string> { "a", "c", "d" }, SentTexts());
        }


        [Fact]
        // A cancel during the idle send (a nested unsubscribe or disconnect) wins: no window is armed after it.
        public void Publish_CancelDuringSend_ArmsNoWindow() {
            PublishDebounce debounce = null!;
            debounce = new PublishDebounce(loop, timers, (data, cancellationToken) => {
                debounce.Cancel();
                return Task.CompletedTask;
            });

            PublishInStep(debounce, "a", 100);

            Assert.Equal(0, timers.PendingCount);
        }


        [Fact]
        // R5: a cancelled window that the clock has passed does not run.
        public void Window_CancelledThenAdvancedPastDue_NothingRuns() {
            PublishDebounce debounce = NewDebounce();
            PublishInStep(debounce, "a", 100);
            PublishInStep(debounce, "b", 100);
            CancelInStep(debounce);

            timers.Advance(200);

            Assert.Equal(new List<string> { "a" }, SentTexts());
            Assert.Equal(0, timers.PendingCount);
        }


        [Fact]
        // R5: a window end that the clock queued before a cancel and a re-arm does nothing. Only the publish made after the
        // cancel is sent, and its own window is the one left armed.
        public async Task Window_FireQueuedBeforeCancel_DoesNothing() {
            PublishDebounce debounce = NewDebounce();
            _ = PublishInStep(debounce, "a", 100);
            _ = PublishInStep(debounce, "b", 100);
            loop.Execute(() => {
                // The window end is queued behind this step, so the cancel and the re-arm below run first.
                timers.Advance(100);
                debounce.Cancel();
                _ = debounce.Publish(Encoding.UTF8.GetBytes("c"), 100, CancellationToken.None);
            });
            // A barrier that the loop runs after the queued window end, which is then known to have run.
            TaskCompletionSource<bool> passed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            loop.Execute(() => passed.TrySetResult(true));
            await passed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new List<string> { "a", "c" }, SentTexts());
            Assert.Equal(1, timers.PendingWithDelay(100));
        }
    }
}
