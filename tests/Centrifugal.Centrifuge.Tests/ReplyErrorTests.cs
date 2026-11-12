using System;
using System.Collections.Generic;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {
    /// <summary>Reply errors and their application fields (app code, app type, metadata).</summary>
    public sealed class ReplyErrorTests {
        // Java: nullApplicationFieldsAreTreatedAsAbsent
        /// <summary>Null application type and metadata are read as empty, and the app code as 0.</summary>
        [Fact]
        public void NullApplicationFields_AreTreatedAsAbsent() {
            CentrifugeException error = new CentrifugeException(3, "message", true, null, 0, null!, null);

            Assert.Equal(string.Empty, error.AppType);
            Assert.Empty(error.Metadata);
            Assert.Equal(0, error.AppCode);
        }


        // Java: replyErrorCarriesApplicationFields
        /// <summary>A reply error copies the app code, the app type and the metadata of the server error.</summary>
        [Fact]
        public void ReplyError_CarriesApplicationFields() {
            Protocol.Error source = new Protocol.Error {
                Code = 1000,
                Message = "rejected",
                Temporary = true,
                AppCode = 7,
                AppType = "limit",
            };
            source.Metadata["x-centrifugo-retry"] = "later";

            CentrifugeException error = CentrifugeException.FromReply(source);

            Assert.Equal(1000, error.Code);
            Assert.Equal("rejected", error.Message);
            Assert.True(error.Temporary);
            Assert.Equal(7, error.AppCode);
            Assert.Equal("limit", error.AppType);
            Assert.Equal("later", error.Metadata["x-centrifugo-retry"]);
        }


        // Java: replyErrorApplicationFieldsAreEmptyWhenAbsent
        /// <summary>A reply error without application fields has an app code of 0, an empty app type and no metadata.</summary>
        [Fact]
        public void ReplyError_ApplicationFieldsEmpty_WhenAbsent() {
            Protocol.Error source = new Protocol.Error {
                Code = 1,
                Message = "plain",
            };

            CentrifugeException error = CentrifugeException.FromReply(source);

            Assert.Equal(0, error.AppCode);
            Assert.Equal(string.Empty, error.AppType);
            Assert.Empty(error.Metadata);
        }

        // The client-local codes are the values the client reports itself; the server never sends them.
        /// <summary>The client-local codes are 15 (unsupported), 16 (limit exceeded) and 17 (cancelled), which the server never sends.</summary>
        [Fact]
        public void ClientLocalCodes_Values() {
            Assert.Equal(15, CentrifugeErrorCodes.Unsupported);
            Assert.Equal(16, CentrifugeErrorCodes.LimitExceeded);
            Assert.Equal(17, CentrifugeErrorCodes.Cancelled);
        }

        // Java: replyErrorMetadataIsUnmodifiable
        /// <summary>The metadata of a reply error is a read-only copy: a write through the dictionary interface fails and the count stays.</summary>
        [Fact]
        public void ReplyError_Metadata_IsReadOnly() {
            Protocol.Error source = new Protocol.Error {
                Code = 1000,
                Message = "rejected",
            };
            source.Metadata["reason"] = "demo";

            CentrifugeException error = CentrifugeException.FromReply(source);
            IDictionary<string, string> writable = (IDictionary<string, string>)error.Metadata;

            Assert.Throws<NotSupportedException>(() => writable.Add("other", "value"));
            Assert.Single(error.Metadata);
            Assert.Equal("demo", error.Metadata["reason"]);
        }
    }
}
