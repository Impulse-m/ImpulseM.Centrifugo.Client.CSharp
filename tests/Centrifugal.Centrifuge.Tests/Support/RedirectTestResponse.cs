using System.Collections.Generic;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>The answer of a RedirectTestServer to one request: the status, the headers and the body.</summary>
    /// <param name="Status">The HTTP status code.</param>
    /// <param name="Headers">The response headers, sent as given.</param>
    /// <param name="Body">The response body, sent as UTF-8.</param>
    public sealed record RedirectTestResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body);
}
