// The upstream rule is kept: a throwing error handler disconnects the client, as a failing handler of any other event does.
// The upstream rule is kept: a connect error reply with a permanent code (100 and up, not 109) raises no error event; the client
// only disconnects with the server's code. ConnectErrorReply_DisconnectsWithTheServerCode pins the disconnect and the absence
// of an unclassified event; it does not pin a raw error type.
// The upstream rule is kept: code 109 on a connect reply keeps the upstream code and reconnects, and the next attempt asks for
// a new token.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>The error path: a step that fails is reported as an unclassified error event, and the logs stay free of secrets.</summary>
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

        /// <summary>
        /// A connect that the server refuses with a non-temporary error disconnects with the server's code and reason, and
        /// raises no unclassified event (the loop barrier lets a late error event arrive first).
        /// </summary>
        [Fact]
        public async Task ConnectErrorReply_DisconnectsWithTheServerCode() {
            Server.OnCommand = (Command command) => command.Connect != null
                ? new Reply { Id = command.Id, Error = new Protocol.Error { Code = 107, Message = "bad request" } }
                : null;
            CentrifugeClient client = NewClient();
            List<string> errorTypes = new List<string>();
            client.Error += (_, e) => errorTypes.Add(e.Type);
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);

            client.Connect();
            CentrifugeDisconnectedEventArgs args = await disconnected;
            await client.LoopBarrierAsync();

            Assert.Equal(107, args.Code);
            Assert.Equal("bad request", args.Reason);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            Assert.DoesNotContain("unclassified", errorTypes);
        }

        /// <summary>Neither the token nor the instance of the client appears in a log line or an exception message.</summary>
        [Fact]
        public async Task Logs_CarryNoTokenAndNoInstance() {
            CapturingLogger logger = new CapturingLogger();
            CentrifugeClient client = NewClient(new CentrifugeClientOptions {
                Token = "token-secret-value",
                Instance = "instance-secret-1234567890",
                Logger = logger,
            });
            // A throwing Connected handler makes the client log its failure (EventDispatch: "connect handler threw"), so the
            // logs hold at least one line that the secrets could appear in.
            Task<CentrifugeDisconnectedEventArgs> disconnected = ClientCoreSeamEvents.WhenDisconnected(client);
            client.Connected += (_, _) => throw new InvalidOperationException("handler failed");
            client.Connect();
            await disconnected.WaitAsync(TimeSpan.FromSeconds(10));
            await client.LoopBarrierAsync();

            Assert.NotEmpty(logger.Texts);
            foreach (string text in logger.Texts) {
                Assert.DoesNotContain("token-secret-value", text);
                Assert.DoesNotContain("instance-secret-1234567890", text);
            }
        }

        // Records every log line of a client as text, with the messages of its exceptions.
        private sealed class CapturingLogger : ILogger {
            /// <summary>Every log line and exception text, in order.</summary>
            public ConcurrentQueue<string> Texts { get; } = new ConcurrentQueue<string>();

            /// <summary>No scopes are used.</summary>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
                return null;
            }

            /// <summary>Every level is recorded.</summary>
            public bool IsEnabled(LogLevel logLevel) {
                return true;
            }

            /// <summary>Records the formatted line and the exception.</summary>
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                Texts.Enqueue(formatter(state, exception));
                if (exception != null) {
                    Texts.Enqueue(exception.ToString());
                }
            }
        }
    }
}
