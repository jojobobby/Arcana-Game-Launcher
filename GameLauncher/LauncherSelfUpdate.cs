using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameLauncher
{
    /// <summary>
    /// Keeps the launcher itself current from the floating 'latest' GitHub release that
    /// .github/workflows/build-launcher.yml publishes. The check reads launcher-metadata.json
    /// (a few hundred bytes) and compares its sha256 with this exe; only a real update
    /// downloads the launcher.
    /// </summary>
    internal sealed class LauncherSelfUpdate
    {
        private const string ReleaseBase = "https://github.com/jojobobby/Arcana-Game-Launcher/releases/download/latest/";
        public const string ExeName = "ArcanaLauncher.exe";
        public const string MetadataUrl = ReleaseBase + "launcher-metadata.json";
        public const string DownloadUrl = ReleaseBase + ExeName;
        private const long MaxLauncherBytes = 512L * 1024 * 1024;

        private readonly HttpClient _http;
        private readonly string _currentExe;
        private readonly string _stagingDir;

        public LauncherSelfUpdate(HttpClient http, string currentExe, string stagingDir = null)
        {
            _http = http;
            _currentExe = currentExe;
            _stagingDir = stagingDir ?? Path.Combine(Path.GetTempPath(), "ArcanaLauncherUpdate");
        }

        /// <summary>False on any failure: a launcher that cannot reach GitHub must still start the game.</summary>
        public async Task<bool> IsUpdateAvailableAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentExe) || !File.Exists(_currentExe))
                    return false;

                var released = await FetchReleasedSha256Async();
                return released.Length > 0 &&
                       !string.Equals(released, Download.Sha256OfFile(_currentExe), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Downloads and verifies the released launcher, then writes the script that swaps it in once this
        /// process has exited. Returns the script path; the caller starts it and closes. Progress runs 0..100.
        /// </summary>
        public async Task<string> StageAsync(IProgress<double> progress)
        {
            Directory.CreateDirectory(_stagingDir);
            var stagedExe = Path.Combine(_stagingDir, ExeName);
            var script = Path.Combine(_stagingDir, "apply-launcher-update.cmd");
            TryDelete(stagedExe);
            TryDelete(script);

            var released = await FetchReleasedSha256Async();
            var scaled = progress == null ? null : new PercentProgress(progress);
            var downloaded = await Download.ToFileAsync(_http, DownloadUrl, stagedExe, 0, MaxLauncherBytes, scaled);
            if (released.Length == 0 || !string.Equals(released, downloaded, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(stagedExe);
                throw new LauncherException("Launcher update failed",
                    "The downloaded launcher did not match the published release, so it was discarded.");
            }

            File.WriteAllText(script, UpdateScript(_currentExe, stagedExe, Process.GetCurrentProcess().Id));
            return script;
        }

        private async Task<string> FetchReleasedSha256Async()
        {
            var json = await _http.GetStringAsync(MetadataUrl);
            using (var doc = JsonDocument.Parse(json))
                return doc.RootElement.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String
                    ? sha.GetString().Trim()
                    : "";
        }

        // Waits for this launcher to exit, copies the new exe over it and starts it again.
        private static string UpdateScript(string currentExe, string stagedExe, int pid) =>
            "@echo off\r\n" +
            "setlocal\r\n" +
            $"set \"TARGET={currentExe}\"\r\n" +
            $"set \"UPDATE={stagedExe}\"\r\n" +
            $"set \"PID={pid}\"\r\n" +
            ":wait\r\n" +
            "tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  timeout /t 1 /nobreak >nul\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            "copy /Y \"%UPDATE%\" \"%TARGET%\" >nul\r\n" +
            "start \"\" \"%TARGET%\"\r\n" +
            "del \"%UPDATE%\" >nul 2>nul\r\n" +
            "del \"%~f0\" >nul 2>nul\r\n";

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private sealed class PercentProgress : IProgress<double>
        {
            private readonly IProgress<double> _inner;
            public PercentProgress(IProgress<double> inner) { _inner = inner; }
            public void Report(double value) => _inner.Report(value * 100);
        }
    }
}
