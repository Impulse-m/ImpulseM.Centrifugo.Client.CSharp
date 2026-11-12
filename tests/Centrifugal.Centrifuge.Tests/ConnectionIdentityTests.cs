// Identity tests: the identity the connection records, the token sink it notifies, and the token confirmed with the identity.
// No Java test pins these unit rules: they cover the apply, connect step, refresh step, token sink and logging of the token.
// The connect-path tests of the identity are in AuthEventTests.cs.
// Not ported: AuthTest.refreshIsAnsweredAfterTheAuthListenerThrowsOnATimerRefresh: it needs the timer refresh.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the identity the connection records and the token sink it notifies.</summary>
    public sealed class ConnectionIdentityTests {
        /// <summary>A token sink that records every call.</summary>
        private sealed class RecordingSink : IConnectionTokenSink {
            /// <summary>The OnAuth calls, as (user, auth class, always), in order.</summary>
            public List<(string User, string AuthClass, bool Always)> Auths { get; } = new List<(string User, string AuthClass, bool Always)>();


            /// <summary>Records the call.</summary>
            /// <param name="user">The user.</param>
            /// <param name="authClass">The auth class.</param>
            /// <param name="always">The always flag.</param>
            public void OnAuth(string user, string authClass, bool always) {
                Auths.Add((user, authClass, always));
            }


            /// <summary>Not used by these tests.</summary>
            /// <param name="code">The disconnect code.</param>
            /// <param name="terminal">The terminal flag.</param>
            public void OnDisconnect(int code, bool terminal) {
            }


            /// <summary>Not used by these tests.</summary>
            /// <param name="error">The error.</param>
            /// <param name="explicitCall">The explicit flag.</param>
            public void OnRefreshFailed(CentrifugeException error, bool explicitCall) {
            }
        }


        /// <summary>A connect reply records the identity and the token in effect, and raises the auth event once.</summary>
        [Fact]
        public void OnConnectIdentity_WithIdentity_RecordsIdentityAndRaisesOnce() {
            FakeClientHost host = new FakeClientHost();
            host.ConnectionToken = "token-1";
            ConnectionIdentity identity = new ConnectionIdentity(host);
            Protocol.ConnectResult result = ConnectReplies.Connect(user: "u1", authClass: "authenticated");

            Exception? failure = identity.OnConnectIdentity(result);

            Assert.Null(failure);
            Assert.Equal("u1", identity.CurrentUser);
            Assert.Equal("authenticated", identity.CurrentAuthClass);
            Assert.Equal("token-1", identity.ConfirmedToken);
            Assert.Single(host.AuthRaised);
            Assert.Equal("u1", host.AuthRaised[0].User);
            Assert.Equal("authenticated", host.AuthRaised[0].AuthClass);
        }


        /// <summary>A connect raises the auth event even when the identity is the same as before.</summary>
        [Fact]
        public void OnConnectIdentity_WithSameIdentityTwice_RaisesTwice() {
            FakeClientHost host = new FakeClientHost();
            ConnectionIdentity identity = new ConnectionIdentity(host);
            Protocol.ConnectResult result = ConnectReplies.Connect(user: "u1", authClass: "authenticated");

            identity.OnConnectIdentity(result);
            identity.OnConnectIdentity(result);

            Assert.Equal(2, host.AuthRaised.Count);
        }


        /// <summary>A server that sends no identity gives empty values, and that is not an error.</summary>
        [Fact]
        public void OnConnectIdentity_WithLegacyReply_RecordsEmptyIdentity() {
            FakeClientHost host = new FakeClientHost();
            ConnectionIdentity identity = new ConnectionIdentity(host);

            identity.OnConnectIdentity(ConnectReplies.Connect());

            Assert.Equal(string.Empty, identity.CurrentUser);
            Assert.Equal(string.Empty, identity.CurrentAuthClass);
            Assert.Single(host.AuthRaised);
            Assert.Equal(string.Empty, host.AuthRaised[0].User);
            Assert.Equal(string.Empty, host.AuthRaised[0].AuthClass);
        }


        /// <summary>A refresh with the same identity returns no event.</summary>
        [Fact]
        public void ApplyAuth_RefreshWithSameIdentity_ReturnsNull() {
            FakeClientHost host = new FakeClientHost();
            ConnectionIdentity identity = new ConnectionIdentity(host);
            identity.ApplyAuth("u1", "authenticated", true);

            CentrifugeAuthEventArgs? args = identity.ApplyAuth("u1", "authenticated", false);

            Assert.Null(args);
        }


        /// <summary>A refresh that changes only the auth class returns an event with the new class.</summary>
        [Fact]
        public void ApplyAuth_RefreshWithNewAuthClass_ReturnsTheNewIdentity() {
            FakeClientHost host = new FakeClientHost();
            ConnectionIdentity identity = new ConnectionIdentity(host);
            identity.ApplyAuth("u1", "authenticated", true);

            CentrifugeAuthEventArgs? args = identity.ApplyAuth("u1", "bot", false);

            Assert.NotNull(args);
            Assert.Equal("u1", args!.User);
            Assert.Equal("bot", args.AuthClass);
        }


        /// <summary>The confirmed token is the connection token at the last apply, not the current one.</summary>
        [Fact]
        public void ConfirmedToken_IsTheTokenAtTheLastApply() {
            FakeClientHost host = new FakeClientHost();
            ConnectionIdentity identity = new ConnectionIdentity(host);
            Assert.Equal(string.Empty, identity.ConfirmedToken);
            host.ConnectionToken = "token-1";
            identity.ApplyAuth("u1", "authenticated", true);
            Assert.Equal("token-1", identity.ConfirmedToken);

            host.ConnectionToken = "token-2";
            Assert.Equal("token-1", identity.ConfirmedToken);

            identity.ApplyAuth("u1", "authenticated", false);
            Assert.Equal("token-2", identity.ConfirmedToken);
        }


        /// <summary>When the auth handler throws on connect, the failure is returned and the token sink is not notified.</summary>
        [Fact]
        public void OnConnectIdentity_WhenHandlerFails_ReturnsFailureAndSkipsTheSink() {
            FakeClientHost host = new FakeClientHost();
            InvalidOperationException handlerError = new InvalidOperationException("handler");
            host.RaiseResult = handlerError;
            ConnectionIdentity identity = new ConnectionIdentity(host);
            RecordingSink sink = new RecordingSink();
            identity.SetTokenSink(sink);

            Exception? failure = identity.OnConnectIdentity(ConnectReplies.Connect(user: "u1"));

            Assert.Same(handlerError, failure);
            Assert.Empty(sink.Auths);
        }


        /// <summary>A connect whose handler succeeds notifies the token sink with always set.</summary>
        [Fact]
        public void OnConnectIdentity_WhenHandlerSucceeds_NotifiesTheSinkWithAlways() {
            FakeClientHost host = new FakeClientHost();
            ConnectionIdentity identity = new ConnectionIdentity(host);
            RecordingSink sink = new RecordingSink();
            identity.SetTokenSink(sink);

            Exception? failure = identity.OnConnectIdentity(ConnectReplies.Connect(user: "u1", authClass: "a"));

            Assert.Null(failure);
            Assert.Single(sink.Auths);
            Assert.Equal(("u1", "a", true), sink.Auths[0]);
        }


        /// <summary>A refresh with no identity change raises no event but still notifies the sink with always false.</summary>
        [Fact]
        public void OnRefreshIdentity_WithUnchangedIdentity_NotifiesTheSinkWithoutAnEvent() {
            FakeClientHost host = new FakeClientHost();
            ConnectionIdentity identity = new ConnectionIdentity(host);
            RecordingSink sink = new RecordingSink();
            identity.SetTokenSink(sink);
            identity.OnConnectIdentity(ConnectReplies.Connect(user: "u1", authClass: "a"));

            identity.OnRefreshIdentity("u1", "a");

            Assert.Single(host.AuthRaised);
            Assert.Equal(2, sink.Auths.Count);
            Assert.Equal(("u1", "a", false), sink.Auths[1]);
        }


        /// <summary>A refresh that changes the identity raises the event with the new identity.</summary>
        [Fact]
        public void OnRefreshIdentity_WithChangedIdentity_RaisesTheNewIdentity() {
            FakeClientHost host = new FakeClientHost();
            ConnectionIdentity identity = new ConnectionIdentity(host);
            identity.OnConnectIdentity(ConnectReplies.Connect(user: "u1", authClass: "a"));

            identity.OnRefreshIdentity("u2", "a");

            Assert.Equal(2, host.AuthRaised.Count);
            Assert.Equal("u2", host.AuthRaised[1].User);
            Assert.Equal("u2", identity.CurrentUser);
        }


        /// <summary>
        /// A refresh whose auth handler throws is reported by the raise itself: the client is not disconnected and the
        /// token sink is not notified.
        /// </summary>
        [Fact]
        public void OnRefreshIdentity_WhenHandlerFails_SkipsTheSinkAndDoesNotDisconnect() {
            FakeClientHost host = new FakeClientHost();
            ConnectionIdentity identity = new ConnectionIdentity(host);
            RecordingSink sink = new RecordingSink();
            identity.SetTokenSink(sink);
            host.RaiseResult = new InvalidOperationException("handler");

            identity.OnRefreshIdentity("u2", "a");

            Assert.Single(host.AuthRaised);
            Assert.Empty(sink.Auths);
            Assert.Empty(host.Disconnects);
            Assert.Empty(host.HandlerFailures);
        }


        /// <summary>The auth event carries only the user and the auth class; the connection token is not part of it.</summary>
        [Fact]
        public void AuthEventArgs_ExposeOnlyUserAndAuthClass() {
            string[] names = typeof(CentrifugeAuthEventArgs)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select((PropertyInfo property) => property.Name)
                .OrderBy((string name) => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { "AuthClass", "User" }, names);
        }
    }
}
