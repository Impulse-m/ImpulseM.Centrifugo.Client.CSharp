using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge.Tests {


    /// <summary>
    /// A small HTTP server for the redirect tests of the rpc pool, on the loopback interface, with or without TLS. It
    /// serves one request per connection, answers each with the Responder, and records the request paths. TLS uses the
    /// self-signed test certificate of the Java fork (Resources\rpc-pool-test.p12, password changeit): the client
    /// trusts exactly that certificate through CreateTrustingHandler. Replaces the MockWebServer of RpcPoolRedirectTest.java.
    /// </summary>
    public sealed class RedirectTestServer : IAsyncDisposable {
        private const string CertificatePassword = "changeit";
        private const string CertificateFile = "rpc-pool-test.p12";


        private readonly bool useTls;
        private readonly X509Certificate2? certificate;
        private readonly List<string> paths = new List<string>();
        private readonly object gate = new object();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private TcpListener? listener;
        private Uri? uri;


        /// <summary>
        /// Creates the server. It listens after StartAsync.
        /// </summary>
        /// <param name="useTls">True to serve HTTPS with the test certificate.</param>
        public RedirectTestServer(bool useTls) {
            this.useTls = useTls;
            certificate = useTls ? LoadCertificate() : null;
            Responder = (string path) => new RedirectTestResponse(404, new Dictionary<string, string>(), "not found");
        }

        /// <summary>The base address of the server, such as https://127.0.0.1:port/. Available after StartAsync.</summary>
        public Uri Uri {
            get {
                return uri ?? throw new InvalidOperationException("the server is not started");
            }
        }

        /// <summary>Answers a request for the path. Set it before the requests are sent.</summary>
        public Func<string, RedirectTestResponse> Responder { get; set; }


        /// <summary>The paths of the received requests, in order.</summary>
        public IReadOnlyList<string> Paths {
            get {
                lock (gate) {
                    return paths.ToArray();
                }
            }
        }

        /// <summary>Starts listening on a random port of the loopback interface.</summary>
        public Task StartAsync() {
            TcpListener started = new TcpListener(IPAddress.Loopback, 0);
            started.Start();
            listener = started;
            int port = ((IPEndPoint)started.LocalEndpoint).Port;
            string scheme = useTls ? "https" : "http";
            uri = new Uri(scheme + "://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
            _ = AcceptLoopAsync(started, stop.Token);
            return Task.CompletedTask;
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

        /// <summary>Stops listening. Connections being served finish on their own.</summary>
        public ValueTask DisposeAsync() {
            stop.Cancel();
            listener?.Stop();
            return ValueTask.CompletedTask;
        }

        private static X509Certificate2 LoadCertificate() {
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", CertificateFile);
            return X509CertificateLoader.LoadPkcs12FromFile(path, CertificatePassword);
        }

        private async Task AcceptLoopAsync(TcpListener tcpListener, CancellationToken token) {
            while (!token.IsCancellationRequested) {
                TcpClient client;
                try {
                    client = await tcpListener.AcceptTcpClientAsync(token);
                } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) {
                    return;
                }
                _ = HandleConnectionAsync(client);
            }
        }

        private async Task HandleConnectionAsync(TcpClient client) {
            using (client) {
                try {
                    Stream stream = client.GetStream();
                    if (useTls) {
                        SslStream secure = new SslStream(stream, false);
                        await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate });
                        stream = secure;
                    }
                    string head = await ReadHeadAsync(stream);
                    await ReadBodyAsync(stream, ContentLength(head));
                    string path = PathOf(head);
                    lock (gate) {
                        paths.Add(path);
                    }
                    await WriteResponseAsync(stream, Responder(path));
                } catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or ObjectDisposedException) {
                    // A client that gave up on the connection: nothing to answer.
                }
            }
        }

        private static async Task WriteResponseAsync(Stream stream, RedirectTestResponse response) {
            byte[] payload = Encoding.UTF8.GetBytes(response.Body);
            StringBuilder builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(" Test\r\n");
            foreach (KeyValuePair<string, string> header in response.Headers) {
                builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            builder.Append("Content-Length: ").Append(payload.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append("\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }

        // Reads the request head, up to and including the blank line.
        private static async Task<string> ReadHeadAsync(Stream stream) {
            List<byte> bytes = new List<byte>();
            byte[] one = new byte[1];
            while (true) {
                int read = await stream.ReadAsync(one.AsMemory(0, 1));
                if (read == 0) {
                    break;
                }
                bytes.Add(one[0]);
                int count = bytes.Count;
                if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' && bytes[count - 2] == '\r' && bytes[count - 1] == '\n') {
                    break;
                }
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static async Task ReadBodyAsync(Stream stream, int length) {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length) {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset));
                if (read == 0) {
                    break;
                }
                offset += read;
            }
        }

        private static int ContentLength(string head) {
            foreach (string line in head.Split("\r\n")) {
                if (line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase)) {
                    string value = line.Substring("content-length:".Length).Trim();
                    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) ? length : 0;
                }
            }
            return 0;
        }

        private static string PathOf(string head) {
            string[] parts = head.Split(' ');
            return parts.Length > 1 ? parts[1] : string.Empty;
        }
    }
}
