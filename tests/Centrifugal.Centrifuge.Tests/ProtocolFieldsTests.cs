using System;
using System.Reflection;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>
    /// Checks that the regenerated protocol code (Protocol/Client.g.cs) has every fork field the port uses.
    /// Reflection only: the wire behaviour is tested by the tests of the features that use the fields.
    /// </summary>
    public sealed class ProtocolFieldsTests {
        [Fact]
        public void ConnectResult_Flag_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.ConnectResult), "Flag");
        }


        [Fact]
        public void ConnectResult_User_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.ConnectResult), "User");
        }


        [Fact]
        public void ConnectResult_AuthClass_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.ConnectResult), "AuthClass");
        }


        [Fact]
        public void RefreshResult_User_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.RefreshResult), "User");
        }


        [Fact]
        public void RefreshResult_AuthClass_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.RefreshResult), "AuthClass");
        }


        [Fact]
        public void ConnectRequest_Instance_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.ConnectRequest), "Instance");
        }


        [Fact]
        public void Error_AppCode_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.Error), "AppCode");
        }


        [Fact]
        public void Error_AppType_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.Error), "AppType");
        }


        [Fact]
        public void Error_Metadata_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.Error), "Metadata");
        }


        [Fact]
        public void Reply_Time_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.Reply), "Time");
        }


        [Fact]
        public void RPCRequest_Headers_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.RPCRequest), "Headers");
        }


        [Fact]
        public void RPCRequest_MsgId_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.RPCRequest), "MsgId");
        }


        [Fact]
        public void RPCRequest_Retransmit_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.RPCRequest), "Retransmit");
        }


        [Fact]
        public void RPCRequest_Cancel_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.RPCRequest), "Cancel");
        }


        [Fact]
        public void RPCResult_Cached_IsPublicProperty() {
            AssertPublicProperty(typeof(Protocol.RPCResult), "Cached");
        }


        private static void AssertPublicProperty(Type type, string name) {
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

            Assert.NotNull(property);
        }
    }
}
