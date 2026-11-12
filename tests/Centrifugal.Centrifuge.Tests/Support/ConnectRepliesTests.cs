using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks that the reply builders set the fields the tests name, and only those.</summary>
    public sealed class ConnectRepliesTests {
        /// <summary>A connect reply built with defaults carries the client id and no user, auth class, flag or expiry.</summary>
        [Fact]
        public void Connect_WithDefaults_SetsOnlyTheClientId() {
            Protocol.ConnectResult reply = ConnectReplies.Connect();

            Assert.Equal("c", reply.Client);
            Assert.False(reply.Expires);
            Assert.Equal(0u, reply.Ttl);
            Assert.Equal(string.Empty, reply.User);
            Assert.Equal(string.Empty, reply.AuthClass);
            Assert.Equal(0L, reply.Flag);
            Assert.Equal(0u, reply.Ping);
        }

        /// <summary>A connect reply built with every field carries each of them.</summary>
        [Fact]
        public void Connect_WithEveryField_CarriesEachField() {
            Protocol.ConnectResult reply = ConnectReplies.Connect(
                client: "client-1",
                expires: true,
                ttl: 60,
                user: "alice",
                authClass: "email",
                flag: 1L << 32,
                ping: 25);

            Assert.Equal("client-1", reply.Client);
            Assert.True(reply.Expires);
            Assert.Equal(60u, reply.Ttl);
            Assert.Equal("alice", reply.User);
            Assert.Equal("email", reply.AuthClass);
            Assert.Equal(1L << 32, reply.Flag);
            Assert.Equal(25u, reply.Ping);
        }

        /// <summary>A refresh reply carries the expiry, the time to live, the user and the auth class it is given.</summary>
        [Fact]
        public void Refresh_CarriesTheGivenFields() {
            Protocol.RefreshResult reply = ConnectReplies.Refresh(true, 120, "bob", "phone");

            Assert.True(reply.Expires);
            Assert.Equal(120u, reply.Ttl);
            Assert.Equal("bob", reply.User);
            Assert.Equal("phone", reply.AuthClass);
        }
    }
}
