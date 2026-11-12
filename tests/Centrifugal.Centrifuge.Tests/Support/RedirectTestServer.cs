using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// A small HTTP server for the redirect tests of the rpc pool, on the loopback interface, with or without TLS. It
    /// answers each request with the Responder, and records the request paths. TLS uses the self-signed test
    /// certificate (Resources\rpc-pool-test.p12, password changeit): the client trusts exactly that certificate
    /// through CreateTrustingHandler.
    /// </summary>
    public sealed class RedirectTestServer : IAsyncDisposable {
        private const string CertificatePassword = "changeit";
        private const string CertificateFile = "rpc-pool-test.p12";


        private readonly X509Certificate2? certificate;
        private readonly List<string> paths = new List<string>();
        private readonly object gate = new object();
        private WebApplication? app;
        private Uri? uri;


        /// <summary>
        /// Creates the server. It listens after StartAsync.
        /// </summary>
        /// <param name="useTls">True to serve HTTPS with the test certificate.</param>
        public RedirectTestServer(bool useTls) {
            certificate = useTls ? LoadCertificate() : null;
            Responder = (string path) => new RedirectTestResponse(404, new Dictionary<string, string>(), "not found");
        }

        /// <summary>The base address of the server, such as https://127.0.0.1:port/. Available after StartAsync.</summary>
        public Uri Uri {
            get {
                return uri ?? throw new InvalidOperationException("the server is not started");
            }
        }

        /// <summary>
        /// Answers a request for the path. Set it before the requests are sent. Returning null aborts the connection
        /// without a response.
        /// </summary>
        public Func<string, RedirectTestResponse?> Responder { get; set; }


        /// <summary>The paths of the received requests, in order.</summary>
        public IReadOnlyList<string> Paths {
            get {
                lock (gate) {
                    return paths.ToArray();
                }
            }
        }

        /// <summary>Starts listening on a random port of the loopback interface.</summary>
        public async Task StartAsync() {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            X509Certificate2? serverCertificate = certificate;
            builder.WebHost.ConfigureKestrel(options => {
                options.Listen(IPAddress.Loopback, 0, listen => {
                    if (serverCertificate != null) {
                        listen.UseHttps(serverCertificate);
                    }
                });
            });
            app = builder.Build();
            app.Run(HandleAsync);
            await app.StartAsync();
            uri = new Uri(app.Urls.First());
        }

        /// <summary>
        /// Creates a client handler that trusts exactly the test certificate. Redirects are followed as the handler's
        /// default says; the test sets AllowAutoRedirect when it needs the redirect itself.
        /// </summary>
        public SocketsHttpHandler CreateTrustingHandler() {
            if (certificate == null) {
                throw new InvalidOperationException("the server runs without TLS");
            }
            string expected = certificate.GetCertHashString();
            SocketsHttpHandler handler = new SocketsHttpHandler();
            handler.SslOptions = new SslClientAuthenticationOptions {
                RemoteCertificateValidationCallback = (object sender, X509Certificate? presented, X509Chain? chain, SslPolicyErrors errors) => presented != null && presented.GetCertHashString() == expected,
            };
            return handler;
        }

        /// <summary>Stops the server. Requests being served finish on their own.</summary>
        public async ValueTask DisposeAsync() {
            if (app == null) {
                return;
            }
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private static X509Certificate2 LoadCertificate() {
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", CertificateFile);
            return X509CertificateLoader.LoadPkcs12FromFile(path, CertificatePassword);
        }

        private async Task HandleAsync(HttpContext context) {
            string path = context.Request.Path.ToString() + context.Request.QueryString.ToString();
            lock (gate) {
                paths.Add(path);
            }
            RedirectTestResponse? response = Responder(path);
            if (response == null) {
                context.Abort();
                return;
            }
            context.Response.StatusCode = response.Status;
            foreach (KeyValuePair<string, string> header in response.Headers) {
                context.Response.Headers[header.Key] = header.Value;
            }
            byte[] payload = Encoding.UTF8.GetBytes(response.Body);
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload);
        }
    }
}
