// Ported from the Java test ReplyErrorTest:
// - nullApplicationFieldsAreTreatedAsAbsent: ported (NullApplicationFields_AreTreatedAsAbsent)
// - replyErrorCarriesApplicationFields: ported (ReplyError_CarriesApplicationFields)
// - replyErrorApplicationFieldsAreEmptyWhenAbsent: ported (ReplyError_ApplicationFieldsEmpty_WhenAbsent)
// - replyErrorMetadataIsUnmodifiable: not ported yet. The metadata copy in Exceptions.cs (foundation file, the lead
//   assigns the fix) is a mutable Dictionary behind an IReadOnlyDictionary, so a cast to IDictionary can change it.
using System;
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
    }
}
