using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher
{
    /// <summary>Where a download stands: bytes on disk of the expected total, and a line for the player while it waits.</summary>
    internal readonly struct DownloadProgress
    {
        public DownloadProgress(long done, long total, string note)
        {
            Done = done;
            Total = total;
            Note = note;
        }

        public long Done { get; }
        public long Total { get; }
        /// <summary>"Server busy. Trying again in 5 s." while a retry waits; null while bytes flow.</summary>
        public string Note { get; }
        public double Fraction => Total > 0 ? Math.Min(1.0, (double)Done / Total) : 0;
    }

    /// <summary>How long to wait between tries. Tests pass no-wait delays.</summary>
    internal sealed class RetryPolicy
    {
        public static readonly RetryPolicy Default = new RetryPolicy(
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) }, Task.Delay);
        public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);
        /// <summary>For a small request (metadata, a poll): the shared HttpClient has no timeout of its own.</summary>
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        /// <summary>A token that also fires after <paramref name="after"/>; dispose it when the request is done.</summary>
        public static CancellationTokenSource WithTimeout(CancellationToken cancel, TimeSpan after)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            source.CancelAfter(after);
            return source;
        }

        public RetryPolicy(IReadOnlyList<TimeSpan> delays, Func<TimeSpan, CancellationToken, Task> delay)
        {
            Delays = delays;
            Delay = delay;
        }

        /// <summary>One entry per retry: three retries by default, so four tries in all.</summary>
        public IReadOnlyList<TimeSpan> Delays { get; }
        public Func<TimeSpan, CancellationToken, Task> Delay { get; }

        /// <summary>The wait before retry <paramref name="retry"/> (0-based): the server's Retry-After when it sent one, capped.</summary>
        public TimeSpan WaitFor(int retry, TimeSpan? retryAfter)
        {
            var wait = retryAfter ?? Delays[Math.Min(retry, Delays.Count - 1)];
            return wait > MaxRetryAfter ? MaxRetryAfter : wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        public static TimeSpan? RetryAfterOf(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header == null) return null;
            if (header.Delta.HasValue) return header.Delta.Value;
            if (header.Date.HasValue) return header.Date.Value - DateTimeOffset.UtcNow;
            return null;
        }
    }

    /// <summary>A failure worth another try (network, stall, busy server); the message says what happened.</summary>
    internal sealed class RetryableException : Exception
    {
        public RetryableException(string message, TimeSpan? retryAfter = null, bool busy = false) : base(message)
        {
            RetryAfter = retryAfter;
            Busy = busy;
        }

        public TimeSpan? RetryAfter { get; }
        public bool Busy { get; }
    }

    /// <summary>
    /// Fetches one file of known size and SHA-256, the way a launcher on a bad connection needs it:
    ///   - into "&lt;target&gt;.part", renamed into place only when the size and the hash match;
    ///   - resumed with Range (and If-Range with the server's ETag, when known) after a broken connection, a stall, a
    ///     closed launcher or Cancel; a server that ignores Range sends the whole file, and that is fine too;
    ///   - retried on network errors, stalls and 408/429/5xx, honouring Retry-After;
    ///   - a stall is 30 s without a byte: HttpClient's own timeout only covers the response headers.
    /// A target that already holds the right bytes is not fetched again. Errors a player can act on are LauncherException.
    /// </summary>
    internal sealed class Downloader
    {
        public static readonly TimeSpan DefaultStall = TimeSpan.FromSeconds(30);
        private const int BufferSize = 81920;

        private readonly HttpClient _http;
        private readonly TimeSpan _stall;
        private readonly RetryPolicy _retry;

        public Downloader(HttpClient http, TimeSpan? stall = null, RetryPolicy retry = null)
        {
            _http = http;
            _stall = stall ?? DefaultStall;
            _retry = retry ?? RetryPolicy.Default;
        }

        public async Task FetchAsync(string url, string target, long size, string sha256, string etag,
            IProgress<DownloadProgress> progress, CancellationToken cancel)
        {
            if (IsComplete(target, size, sha256))
            {
                progress?.Report(new DownloadProgress(size, size, null));
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var part = target + ".part";
            for (var retry = 0; ; retry++)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    await FetchOnceAsync(url, part, size, sha256, etag, progress, cancel).ConfigureAwait(false);
                    File.Move(part, target, overwrite: true);
                    Log.Info($"Downloaded {Path.GetFileName(target)} ({size} bytes) from {url}");
                    return;
                }
                catch (RetryableException ex)
                {
                    if (retry >= _retry.Delays.Count)
                    {
                        Log.Warn($"Giving up on {url} after {retry + 1} tries: {ex.Message}");
                        throw new LauncherException("Download failed",
                            "The download kept failing (" + ex.Message + "). Check your connection and try again: it picks up where it stopped.", ex);
                    }

                    var wait = _retry.WaitFor(retry, ex.RetryAfter);
                    var done = File.Exists(part) ? new FileInfo(part).Length : 0;
                    Log.Warn($"Try {retry + 1} of {url} failed: {ex.Message}. Next try in {wait.TotalSeconds:0} s from byte {done}.");
                    var note = (ex.Busy ? "Server busy." : "Connection problem.") + $" Trying again in {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} s.";
                    progress?.Report(new DownloadProgress(done, size, note));
                    await _retry.Delay(wait, cancel).ConfigureAwait(false);
                }
            }
        }

        private async Task FetchOnceAsync(string url, string part, long size, string sha256, string etag,
            IProgress<DownloadProgress> progress, CancellationToken cancel)
        {
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > size)
            {
                File.Delete(part);
                have = 0;
            }
            if (have == size)
            {
                if (string.Equals(Sha256OfFile(part), sha256, StringComparison.OrdinalIgnoreCase)) return;
                File.Delete(part);
                have = 0;
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (have > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(have, null);
                    if (!string.IsNullOrEmpty(etag)) request.Headers.TryAddWithoutValidation("If-Range", etag);
                }

                HttpResponseMessage response;
                // The shared HttpClient has no overall timeout (a slow download must not be cut off at a fixed time):
                // the headers get one here, disarmed once they arrive, and the body has the stall timer.
                var headers = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                headers.CancelAfter(_stall);
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
                    headers.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                catch (HttpRequestException ex)
                {
                    headers.Dispose();
                    throw new RetryableException("couldn't reach the server: " + ex.Message);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    headers.Dispose();
                    throw new RetryableException("the server took too long to answer");
                }

                using (headers)
                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (status == 416)
                    {
                        File.Delete(part);
                        throw new RetryableException("the server would not resume the download", TimeSpan.Zero);
                    }
                    if (status == 408 || status == 429 || status >= 500)
                    {
                        var busy = status == 429 || status == 503;
                        throw new RetryableException(busy ? "the server is busy" : "the server answered HTTP " + status,
                            RetryPolicy.RetryAfterOf(response), busy);
                    }
                    if (!response.IsSuccessStatusCode)
                        throw new LauncherException("Download failed", $"The server answered HTTP {status} for {url}.");

                    var resuming = status == 206;
                    if (resuming)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range?.From != have || range.Length.HasValue && range.Length != size)
                        {
                            File.Delete(part);
                            throw new RetryableException("the server resumed from the wrong place", TimeSpan.Zero);
                        }
                    }
                    else
                    {
                        have = 0;
                    }

                    await CopyBodyAsync(response, part, have, size, sha256, progress, cancel).ConfigureAwait(false);
                }
            }
        }

        private async Task CopyBodyAsync(HttpResponseMessage response, string part, long have, long size, string sha256,
            IProgress<DownloadProgress> progress, CancellationToken cancel)
        {
            var oversized = false;
            string actual;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                if (have > 0) AppendFile(hash, part);
                using (var output = OpenPart(part, append: have > 0))
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    Stream input;
                    try
                    {
                        input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException || ex is HttpRequestException)
                    {
                        throw new RetryableException("the connection dropped: " + ex.Message);
                    }

                    using (input)
                    {
                        var buffer = new byte[BufferSize];
                        var done = have;
                        progress?.Report(new DownloadProgress(done, size, null));
                        while (true)
                        {
                            int read;
                            stall.CancelAfter(_stall);
                            try
                            {
                                read = await input.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                            {
                                throw new RetryableException($"no data for {_stall.TotalSeconds:0} s");
                            }
                            catch (Exception ex) when (ex is IOException || ex is HttpRequestException)
                            {
                                throw new RetryableException("the connection dropped: " + ex.Message);
                            }

                            if (read == 0) break;
                            if (done + read > size)
                            {
                                oversized = true;
                                break;
                            }
                            Write(output, buffer, read, part);
                            hash.AppendData(buffer, 0, read);
                            done += read;
                            progress?.Report(new DownloadProgress(done, size, null));
                        }

                        if (!oversized && done < size)
                            throw new RetryableException($"the connection closed after {done} of {size} bytes");
                    }
                }
                actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (oversized)
            {
                File.Delete(part);
                throw new LauncherException("Download failed", "The download is larger than the server said it would be, so it was thrown away.");
            }
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new LauncherException("Download failed",
                    "The downloaded files failed their integrity check, so nothing was installed.\n\n" +
                    $"Expected {sha256}\nGot {actual}");
            }
        }

        private static FileStream OpenPart(string part, bool append)
        {
            try
            {
                return new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: false);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw Disk.CannotWrite(Path.GetDirectoryName(part), ex);
            }
            catch (IOException ex) when (Disk.IsFull(ex))
            {
                throw Disk.Full(ex);
            }
            catch (IOException ex)
            {
                // Most often another launcher (or an antivirus scan) holding the same file.
                throw new RetryableException("the download file is in use: " + ex.Message);
            }
        }

        private static void Write(FileStream output, byte[] buffer, int count, string part)
        {
            try
            {
                output.Write(buffer, 0, count);
            }
            catch (IOException ex) when (Disk.IsFull(ex))
            {
                throw Disk.Full(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw Disk.CannotWrite(Path.GetDirectoryName(part), ex);
            }
        }

        private static void AppendFile(IncrementalHash hash, string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
            }
        }

        /// <summary>True when <paramref name="path"/> already holds exactly these bytes; a file that doesn't is deleted.</summary>
        public static bool IsComplete(string path, long size, string sha256)
        {
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).Length == size && string.Equals(Sha256OfFile(path), sha256, StringComparison.OrdinalIgnoreCase))
                return true;
            TryDelete(path);
            return false;
        }

        public static string Sha256OfFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize))
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }

    /// <summary>Disk errors in words a player can act on.</summary>
    internal static class Disk
    {
        private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
        private const int ErrorDiskFull = unchecked((int)0x80070070);

        public static bool IsFull(IOException ex) => ex.HResult == ErrorDiskFull || ex.HResult == ErrorHandleDiskFull;

        public static LauncherException Full(Exception inner) => new LauncherException("Not enough disk space",
            "Your drive ran out of space. Free some space, then try again: the download picks up where it stopped.", inner);

        public static LauncherException CannotWrite(string folder, Exception inner) => new LauncherException("Can't write files",
            $"Windows did not let Arcana Launcher write to {folder}.\n\nIf your antivirus blocks it, allow Arcana Launcher, then try again.", inner);

        /// <summary>Free bytes on the drive holding <paramref name="path"/>, or null when Windows won't say.</summary>
        public static long? FreeBytes(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                return string.IsNullOrEmpty(root) ? (long?)null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
    }
}
