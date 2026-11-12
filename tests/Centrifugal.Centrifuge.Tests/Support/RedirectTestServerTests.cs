using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>Checks the redirect fixture over TLS and over plain HTTP.</summary>
    public sealed class RedirectTestServerTests {
        /// <summary>Over TLS, a client that trusts the test certificate gets the redirect and the path is recorded.</summary>
        [Fact]
        public async Task Tls_ServesTheRedirectToATrustingClient() {
            await using RedirectTestServer server = new RedirectTestServer(true);
            server.Responder = (string path) => new RedirectTestResponse(
                302,
                new Dictionary<string, string> { { "Location", "/next" } },
                "moved");
            await server.StartAsync();
            using SocketsHttpHandler handler = server.CreateTrustingHandler();
            handler.AllowAutoRedirect = false;
            using HttpClient http = new HttpClient(handler);

            HttpResponseMessage response = await http.GetAsync(new Uri(server.Uri, "/start"));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/next", response.Headers.Location?.ToString());
            Assert.Equal("moved", await response.Content.ReadAsStringAsync());
            Assert.Equal(new string[] { "/start" }, server.Paths);
        }

        /// <summary>Over TLS, a client that does not trust the test certificate cannot connect.</summary>
        [Fact]
        public async Task Tls_RefusesAClientThatDoesNotTrustTheCertificate() {
            await using RedirectTestServer server = new RedirectTestServer(true);
            server.Responder = (string path) => new RedirectTestResponse(200, new Dictionary<string, string>(), "ok");
            await server.StartAsync();
            using HttpClient http = new HttpClient();

            await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetStringAsync(new Uri(server.Uri, "/x")));
        }

        /// <summary>Over plain HTTP, the responder answers the request and the server records its path.</summary>
        [Fact]
        public async Task Plain_AnswersTheRequestAndRecordsThePath() {
            await using RedirectTestServer server = new RedirectTestServer(false);
            server.Responder = (string path) => new RedirectTestResponse(200, new Dictionary<string, string>(), "plain " + path);
            await server.StartAsync();
            using HttpClient http = new HttpClient();

            string body = await http.GetStringAsync(new Uri(server.Uri, "/x"));

            Assert.Equal("plain /x", body);
            Assert.Equal(new string[] { "/x" }, server.Paths);
        }

        /// <summary>A server without TLS has no certificate to trust, so the trusting handler is refused.</summary>
        [Fact]
        public void CreateTrustingHandler_WithoutTls_Throws() {
            RedirectTestServer server = new RedirectTestServer(false);

            Assert.Throws<InvalidOperationException>(() => server.CreateTrustingHandler());
        }
    }
}
