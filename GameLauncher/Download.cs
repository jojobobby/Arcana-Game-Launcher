using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace GameLauncher
{
    internal static class Download
    {
        /// <summary>
        /// Streams <paramref name="url"/> to <paramref name="outputPath"/> and returns the SHA-256 of what
        /// was written. Progress runs 0..1; <paramref name="expectedBytes"/> covers servers that send no length.
        /// </summary>
        public static async Task<string> ToFileAsync(HttpClient http, string url, string outputPath,
            long expectedBytes, long maxBytes, IProgress<double> progress)
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? expectedBytes;

                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = File.Create(outputPath))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        written += read;
                        if (written > maxBytes)
                            throw new LauncherException("Download failed", "The download is larger than the server said it would be.");

                        await output.WriteAsync(buffer, 0, read);
                        sha.AppendData(buffer, 0, read);
                        if (total > 0)
                            progress?.Report(Math.Min(1.0, (double)written / total));
                    }

                    return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                }
            }
        }

        public static string Sha256OfFile(string path)
        {
            using (var stream = File.OpenRead(path))
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }
}
