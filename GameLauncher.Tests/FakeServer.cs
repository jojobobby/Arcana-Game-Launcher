using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Tests
{
    /// <summary>What a request asked for, as the fake server saw it.</summary>
    internal sealed class SeenRequest
    {
        public string Url { get; set; }
        public string Range { get; set; }
        public string IfRange { get; set; }
        public string IfNoneMatch { get; set; }
        public string UserAgent { get; set; }
    }

    /// <summary>
    /// Stands in for the game server's /client/* routes and the launcher's GitHub releases. A route can answer byte
    /// ranges (with If-Range), drop the connection part way, stall, or be busy, so the download code is tested against
    /// the failures players hit.
    /// </summary>
    internal sealed class FakeServer : HttpMessageHandler
    {
        public delegate Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancel);

        private readonly Dictionary<string, Queue<Handler>> _routes = new Dictionary<string, Queue<Handler>>();
        public List<SeenRequest> Seen { get; } = new List<SeenRequest>();
        public List<string> Requests => Seen.Select(r => r.Url).ToList();

        public HttpClient Client() => new HttpClient(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        public void Route(string url, Handler handler) => Sequence(url, handler);

        /// <summary>Answers requests to <paramref name="url"/> with each handler in turn; the last one repeats.</summary>
        public void Sequence(string url, params Handler[] handlers) => _routes[url] = new Queue<Handler>(handlers);

        public void Bytes(string url, byte[] body, string etag = null, bool ranges = true) =>
            Route(url, (request, cancel) => Task.FromResult(Serve(request, body, etag, ranges)));

        public void Text(string url, string body) => Bytes(url, Encoding.UTF8.GetBytes(body));

        public void Status(string url, HttpStatusCode code) =>
            Route(url, (request, cancel) => Task.FromResult(new HttpResponseMessage(code)));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = request.RequestUri.ToString();
            Seen.Add(new SeenRequest
            {
                Url = url,
                Range = request.Headers.Range?.ToString(),
                IfRange = request.Headers.TryGetValues("If-Range", out var ifRange) ? ifRange.Single() : null,
                IfNoneMatch = request.Headers.TryGetValues("If-None-Match", out var ifNoneMatch) ? ifNoneMatch.Single() : null,
                UserAgent = request.Headers.UserAgent.ToString(),
            });
            if (!_routes.TryGetValue(url, out var handlers))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var handler = handlers.Count > 1 ? handlers.Dequeue() : handlers.Peek();
            return handler(request, cancel);
        }

        /// <summary>The whole body, or the asked range (206) when ranges are on and any If-Range matches the ETag.</summary>
        public static HttpResponseMessage Serve(HttpRequestMessage request, byte[] body, string etag = null, bool ranges = true)
        {
            var from = 0L;
            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            var ifRange = request.Headers.TryGetValues("If-Range", out var values) ? values.Single() : null;
            var partial = ranges && range?.From != null && (ifRange == null || ifRange == etag);
            if (partial)
            {
                from = range.From.Value;
                if (from >= body.Length)
                {
                    var refused = new HttpResponseMessage((HttpStatusCode)416) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    refused.Content.Headers.ContentRange = new ContentRangeHeaderValue(body.Length);
                    return refused;
                }
            }

            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body, (int)from, body.Length - (int)from),
            };
            if (partial) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
            if (etag != null) response.Headers.ETag = new EntityTagHeaderValue(etag);
            if (ranges) response.Headers.AcceptRanges.Add("bytes");
            return response;
        }

        /// <summary>200 that sends the first <paramref name="after"/> bytes and then drops the connection.</summary>
        public static Handler Cut(byte[] body, int after) => (request, cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(body, after, false)) });

        /// <summary>200 that sends the first <paramref name="after"/> bytes and then goes silent.</summary>
        public static Handler Stall(byte[] body, int after) => (request, cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(body, after, true)) });

        public static Handler Busy(TimeSpan? retryAfter = null) => (request, cancel) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (retryAfter.HasValue) response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
            return Task.FromResult(response);
        };

        public static Handler Answer(byte[] body, string etag = null, bool ranges = true) => (request, cancel) =>
            Task.FromResult(Serve(request, body, etag, ranges));

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
        public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

        /// <summary>The shape tools/build_client_release.ps1 writes; with <paramref name="files"/>, manifest and unpacked_size too.</summary>
        public static string Metadata(byte[] zip, string build = "8a991dfa1c24", string channel = "production",
            string executable = "Arcana.exe", string sha256 = null, long? size = null, (string Name, string Body)[] files = null)
        {
            var json = "{\r\n  \"build\": \"" + build + "\",\r\n  \"version\": \"5.2.9\",\r\n  \"channel\": \"" + channel +
                       "\",\r\n  \"commit\": \"1f7b2ee1\",\r\n  \"file_name\": \"Arcana-client.zip\",\r\n  \"file_size\": " +
                       (size ?? zip.Length) + ",\r\n  \"sha256\": \"" + (sha256 ?? Sha256(zip)) +
                       "\",\r\n  \"executable\": \"" + executable.Replace("\\", "\\\\") + "\"";
            if (files != null)
            {
                var entries = files.Select(f => "{ \"path\": \"" + f.Name + "\", \"size\": " + Encoding.UTF8.GetByteCount(f.Body) +
                                                ", \"sha256\": \"" + Sha256(f.Body) + "\" }");
                json += ",\r\n  \"unpacked_size\": " + files.Sum(f => (long)Encoding.UTF8.GetByteCount(f.Body)) +
                        ",\r\n  \"manifest\": [ " + string.Join(", ", entries) + " ]";
            }
            return json + "\r\n}";
        }

        /// <summary>Retries that never wait; the waits asked for are recorded.</summary>
        public static RetryPolicy NoWait(List<TimeSpan> waits = null) =>
            new RetryPolicy(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) },
                (wait, cancel) =>
                {
                    waits?.Add(wait);
                    return Task.CompletedTask;
                });

        private sealed class BrokenStream : Stream
        {
            private readonly byte[] _body;
            private readonly int _after;
            private readonly bool _stall;
            private int _position;

            public BrokenStream(byte[] body, int after, bool stall)
            {
                _body = body;
                _after = after;
                _stall = stall;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
            {
                if (_position >= _after)
                {
                    if (!_stall) throw new IOException("The connection was reset (test).");
                    await Task.Delay(Timeout.Infinite, cancel);
                }
                var count = Math.Min(buffer.Length, _after - _position);
                _body.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _body.Length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
