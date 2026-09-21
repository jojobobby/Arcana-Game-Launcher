using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Tests
{
    /// <summary>Stands in for the game server's /client/* routes and the launcher's GitHub release.</summary>
    internal sealed class FakeServer : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new Dictionary<string, Func<HttpResponseMessage>>();
        public List<string> Requests { get; } = new List<string>();

        public HttpClient Client() => new HttpClient(this, disposeHandler: false);

        public void Bytes(string url, byte[] body) =>
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        public void Text(string url, string body) => Bytes(url, Encoding.UTF8.GetBytes(body));

        public void Status(string url, HttpStatusCode code) => _routes[url] = () => new HttpResponseMessage(code);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri.ToString();
            Requests.Add(url);
            return Task.FromResult(_routes.TryGetValue(url, out var route)
                ? route()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public static byte[] Zip(params (string Name, string Body)[] files)
        {
            using (var memory = new MemoryStream())
            {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                    foreach (var file in files)
                        using (var writer = new StreamWriter(archive.CreateEntry(file.Name).Open()))
                            writer.Write(file.Body);
                return memory.ToArray();
            }
        }

        public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>The shape tools/build_client_release.ps1 writes in the Arcana repo.</summary>
        public static string Metadata(byte[] zip, string build = "8a991dfa1c24", string channel = "production",
            string executable = "Arcana.exe", string sha256 = null, long? size = null) =>
            "{\r\n  \"build\": \"" + build + "\",\r\n  \"version\": \"5.2.9\",\r\n  \"channel\": \"" + channel +
            "\",\r\n  \"commit\": \"1f7b2ee1\",\r\n  \"file_name\": \"Arcana-client.zip\",\r\n  \"file_size\": " +
            (size ?? zip.Length) + ",\r\n  \"sha256\": \"" + (sha256 ?? Sha256(zip)) +
            "\",\r\n  \"executable\": \"" + executable.Replace("\\", "\\\\") + "\"\r\n}";
    }
}
