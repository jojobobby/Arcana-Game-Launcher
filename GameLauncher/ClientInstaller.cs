using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameLauncher
{
    /// <summary>What is on disk: read back from client-version.json inside the install folder.</summary>
    internal sealed class InstalledClient
    {
        public string Build { get; set; }
        public string Version { get; set; }
        public string ExePath { get; set; }
    }

    /// <summary>
    /// Installs the native client the game server serves. The server bakes in the client built from its
    /// own commit (Arcana repo: .github/workflows client job -> realm-server-master/client-files ->
    /// ClientReleaseStore), so asking the server we are about to play on is what keeps the two in step.
    ///   GET {server}/client/metadata  -> build id, sha256, size, executable
    ///   GET {server}/client/download  -> Arcana-client.zip
    /// An install is current when its build id equals the server's. The id is the git tree of the
    /// client source, not a hash of the exe, so an unchanged client is never downloaded twice.
    /// </summary>
    internal sealed class ClientInstaller
    {
        public const string VersionFileName = "client-version.json";
        private const string DownloadFileName = "Arcana-client.zip.download";

        private readonly HttpClient _http;
        private readonly LauncherChannel _channel;
        private readonly string _root;

        public string InstallDir { get; }

        public ClientInstaller(HttpClient http, LauncherChannel channel, string root)
        {
            _http = http;
            _channel = channel;
            _root = root;
            InstallDir = Path.Combine(root, channel.InstallDirName);
        }

        public InstalledClient ReadInstalled()
        {
            var versionFile = Path.Combine(InstallDir, VersionFileName);
            if (!File.Exists(versionFile))
                return null;

            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(versionFile)))
                {
                    var executable = Path.GetFileName(doc.RootElement.GetProperty("executable").GetString() ?? "");
                    var installed = new InstalledClient
                    {
                        Build = doc.RootElement.GetProperty("build").GetString(),
                        Version = doc.RootElement.GetProperty("version").GetString(),
                        ExePath = Path.Combine(InstallDir, executable),
                    };
                    return executable.Length > 0 && File.Exists(installed.ExePath) ? installed : null;
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is InvalidOperationException ||
                                       ex is System.Collections.Generic.KeyNotFoundException)
            {
                // A damaged version file means "reinstall", never "crash on start".
                return null;
            }
        }

        public async Task<ClientMetadata> FetchMetadataAsync()
        {
            using (var response = await _http.GetAsync(_channel.ServerUrl + "/client/metadata"))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new LauncherException("Server unavailable",
                        "The game server is running but has no client to offer yet. Try again in a few minutes.");

                // An error page is not JSON; Parse turns it into "server said: ..." for the player.
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    body = "HTTP " + (int)response.StatusCode + " " + body;
                return ClientMetadata.Parse(body, _channel);
            }
        }

        /// <summary>Progress runs 0..100. The current install is untouched unless the new one is complete.</summary>
        public async Task InstallAsync(ClientMetadata metadata, IProgress<double> progress)
        {
            var zip = Path.Combine(_root, DownloadFileName);
            var staged = InstallDir + ".new";
            var retired = InstallDir + ".old";
            try
            {
                TryDeleteDir(staged);
                TryDeleteDir(retired);

                var scaled = progress == null ? null : new ScaledProgress(progress, 90);
                var sha256 = await Download.ToFileAsync(_http, _channel.ServerUrl + "/client/download", zip,
                    metadata.FileSize, ClientMetadata.MaxZipBytes, scaled);
                if (!string.Equals(sha256, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException("Download failed",
                        "The downloaded game files failed their integrity check, so nothing was installed.\n\n" +
                        $"Expected {metadata.Sha256}\nGot {sha256}");

                progress?.Report(92);
                // ExtractToDirectory refuses entries that would land outside the target folder.
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, staged));
                if (!File.Exists(Path.Combine(staged, metadata.Executable)))
                    throw new LauncherException("Install failed",
                        $"The download did not contain {metadata.Executable}. The release layout may have changed.");

                File.WriteAllText(Path.Combine(staged, VersionFileName), JsonSerializer.Serialize(new
                {
                    build = metadata.Build,
                    version = metadata.Version,
                    executable = metadata.Executable,
                    channel = _channel.Name,
                }));

                progress?.Report(97);
                Swap(staged, retired);
                progress?.Report(100);
            }
            finally
            {
                TryDelete(zip);
                TryDeleteDir(staged);
                // If a failed swap could not be rolled back, the retired folder is the only copy left.
                if (Directory.Exists(InstallDir))
                    TryDeleteDir(retired);
            }
        }

        // A folder holding a running exe cannot be renamed, so a failed first move leaves the install intact.
        private void Swap(string staged, string retired)
        {
            var hadInstall = Directory.Exists(InstallDir);
            try
            {
                if (hadInstall)
                    Directory.Move(InstallDir, retired);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new LauncherException("Game is running",
                    "The game files are in use. Close Arcana, then update again.", ex);
            }

            try
            {
                Directory.Move(staged, InstallDir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (hadInstall && !Directory.Exists(InstallDir))
                    Directory.Move(retired, InstallDir);
                throw new LauncherException("Install failed", "Couldn't move the new game files into place: " + ex.Message, ex);
            }
        }

        public void Uninstall()
        {
            try
            {
                if (Directory.Exists(InstallDir))
                    Directory.Delete(InstallDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new LauncherException("Game is running",
                    "The game files are in use. Close Arcana, then uninstall again.", ex);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }

        private sealed class ScaledProgress : IProgress<double>
        {
            private readonly IProgress<double> _inner;
            private readonly double _scale;

            public ScaledProgress(IProgress<double> inner, double scale)
            {
                _inner = inner;
                _scale = scale;
            }

            public void Report(double value) => _inner.Report(value * _scale);
        }
    }
}
