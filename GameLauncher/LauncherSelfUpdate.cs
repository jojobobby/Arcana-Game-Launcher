using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GameLauncher
{
    /// <summary>One published launcher: launcher-metadata.json on a GitHub release (.github/workflows/build-launcher.yml).</summary>
    internal sealed class LauncherRelease
    {
        public const long MaxBytes = 256L * 1024 * 1024;

        public string Version { get; private set; }
        /// <summary>The release tag ("v1.0.12.0"); its files never change. Null on releases from before tags.</summary>
        public string ReleaseTag { get; private set; }
        public string Sha256 { get; private set; }
        public long Size { get; private set; }
        public string SetupFileName { get; private set; }
        public string SetupSha256 { get; private set; }
        public long SetupSize { get; private set; }
        public bool HasSetup => SetupFileName != null;

        public string ExeUrl(string releases) => FileUrl(releases, LauncherInfo.ExeName);
        public string SetupUrl(string releases) => FileUrl(releases, SetupFileName);

        private string FileUrl(string releases, string file) =>
            ReleaseTag != null ? $"{releases}/download/{ReleaseTag}/{file}" : $"{releases}/latest/download/{file}";

        public static LauncherRelease Parse(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var release = new LauncherRelease
                {
                    Version = Text(root, "version"),
                    ReleaseTag = Text(root, "release_tag"),
                    Sha256 = Text(root, "sha256")?.ToLowerInvariant(),
                    Size = Number(root, "file_size"),
                    SetupFileName = Text(root, "setup_file_name"),
                    SetupSha256 = Text(root, "setup_sha256")?.ToLowerInvariant(),
                    SetupSize = Number(root, "setup_size"),
                };
                if (!ClientMetadata.IsSha256(release.Sha256) || release.Size <= 0 || release.Size > MaxBytes)
                    throw new InvalidDataException("launcher-metadata.json has no valid sha256 and file_size.");
                // Both end up in a URL path: plain names only.
                if (release.ReleaseTag != null && !release.ReleaseTag.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                    release.ReleaseTag = null;
                if (release.SetupFileName != null && (release.SetupFileName != Path.GetFileName(release.SetupFileName) ||
                        !release.SetupFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        !ClientMetadata.IsSha256(release.SetupSha256) || release.SetupSize <= 0 || release.SetupSize > MaxBytes))
                    release.SetupFileName = null;
                return release;
            }
        }

        private static string Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString().Trim().Length > 0
                ? value.GetString().Trim()
                : null;

        private static long Number(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    }

    /// <summary>
    /// Keeps the launcher itself current from the launcher repo's GitHub releases (build-launcher.yml). A release build
    /// checks on every start and updates without asking:
    ///   1. read launcher-metadata.json from the release GitHub marks Latest; any SHA-256 that differs from this exe is an
    ///      update (so rolling the release back rolls launchers back);
    ///   2. download ArcanaLauncher.exe from that release's own tag beside this exe ("ArcanaLauncher.exe.new"), resumable,
    ///      checked against size and SHA-256;
    ///   3. rename this running exe to "ArcanaLauncher.exe.old" (Windows allows renaming a running exe) and the new one
    ///      into its place; start it with the same arguments plus --updated-from-pid, and wait until it signals that its
    ///      window is up (<see cref="StartedEventName"/>). If it doesn't, the old exe goes back and this one carries on.
    /// The new process deletes the .old file once this one has exited, and writes its version into the Apps list entry.
    /// Every launcher version from 1.0.10 on keeps this protocol; a launcher that cannot reach GitHub still starts the game.
    /// </summary>
    internal sealed class LauncherSelfUpdate
    {
        public const string GitHubReleases = "https://github.com/jojobobby/Arcana-Game-Launcher/releases";
        public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);

        private readonly HttpClient _http;
        private readonly string _releases;
        private readonly string _currentExe;
        private readonly Downloader _downloader;
        private string _currentSha;

        public LauncherSelfUpdate(HttpClient http, string releasesUrl, string currentExe, RetryPolicy retry = null)
        {
            _http = http;
            _releases = releasesUrl.TrimEnd('/');
            _currentExe = currentExe;
            _downloader = new Downloader(http, null, retry ?? RetryPolicy.Default);
        }

        public string MetadataUrl => _releases + "/latest/download/launcher-metadata.json";
        public string StagedPath => _currentExe + ".new";
        public static string OldPath(string exe) => exe + ".old";

        public static string StartedEventName(int oldProcessId) => LauncherInfo.MutexName + ".Started." + oldProcessId;

        /// <summary>The release to update to, or null when this exe is current or the check failed (logged, never thrown).</summary>
        public async Task<LauncherRelease> CheckAsync(CancellationToken cancel)
        {
            try
            {
                if (string.IsNullOrEmpty(_currentExe) || !File.Exists(_currentExe))
                    return null;
                string json;
                using (var timeout = RetryPolicy.WithTimeout(cancel, RetryPolicy.RequestTimeout))
                    json = await _http.GetStringAsync(MetadataUrl, timeout.Token).ConfigureAwait(false);
                var release = LauncherRelease.Parse(json);
                _currentSha ??= await Task.Run(() => Downloader.Sha256OfFile(_currentExe), cancel).ConfigureAwait(false);
                if (string.Equals(release.Sha256, _currentSha, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"Launcher {LauncherInfo.Version} is current (release {release.Version}).");
                    return null;
                }
                Log.Info($"Launcher update available: {LauncherInfo.Version} -> {release.Version} ({release.ReleaseTag ?? "latest"}).");
                return release;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException || ex is InvalidDataException ||
                                       ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is OperationCanceledException && !cancel.IsCancellationRequested)
            {
                Log.Warn("Launcher update check failed; carrying on with this version.", ex);
                return null;
            }
        }

        /// <summary>The latest release's metadata, or null (for the installer download when there is no update).</summary>
        public async Task<LauncherRelease> FetchLatestAsync(CancellationToken cancel)
        {
            try
            {
                using (var timeout = RetryPolicy.WithTimeout(cancel, RetryPolicy.RequestTimeout))
                    return LauncherRelease.Parse(await _http.GetStringAsync(MetadataUrl, timeout.Token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException || ex is InvalidDataException ||
                                       ex is OperationCanceledException && !cancel.IsCancellationRequested)
            {
                Log.Warn("Could not read the latest launcher release.", ex);
                return null;
            }
        }

        /// <summary>Downloads and verifies the new exe beside this one. Returns its path.</summary>
        public async Task<string> DownloadAsync(LauncherRelease release, IProgress<DownloadProgress> progress, CancellationToken cancel)
        {
            await _downloader.FetchAsync(release.ExeUrl(_releases), StagedPath, release.Size, release.Sha256, null, progress, cancel)
                .ConfigureAwait(false);
            return StagedPath;
        }

        /// <summary>Downloads and verifies ArcanaLauncherSetup.exe into <paramref name="folder"/>. Returns its path.</summary>
        public async Task<string> DownloadSetupAsync(LauncherRelease release, string folder, IProgress<DownloadProgress> progress,
            CancellationToken cancel)
        {
            var target = Path.Combine(folder, Path.GetFileNameWithoutExtension(release.SetupFileName) + "-" + release.Version + ".exe");
            await _downloader.FetchAsync(release.SetupUrl(_releases), target, release.SetupSize, release.SetupSha256, null, progress, cancel)
                .ConfigureAwait(false);
            return target;
        }

        /// <summary>
        /// Puts <paramref name="staged"/> in place of the running <paramref name="exe"/>. Afterwards the old bytes are at
        /// <see cref="OldPath"/> until the new process deletes them. Nothing changes when it throws.
        /// </summary>
        public static void Swap(string exe, string staged)
        {
            var old = OldPath(exe);
            TryDelete(old);
            if (File.Exists(old))
                throw new LauncherException("Launcher update failed", "An earlier launcher is still closing. Try again in a moment.");
            try
            {
                File.Move(exe, old);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new LauncherException("Launcher update failed", "Windows did not let the launcher replace itself: " + ex.Message, ex);
            }
            try
            {
                File.Move(staged, exe);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                File.Move(old, exe);
                throw new LauncherException("Launcher update failed", "Couldn't move the new launcher into place: " + ex.Message, ex);
            }
        }

        /// <summary>Undoes <see cref="Swap"/> after the new exe failed to start: the old bytes go back.</summary>
        public static void Revert(string exe)
        {
            var old = OldPath(exe);
            if (!File.Exists(old)) return;
            // The new process was just killed: its file can stay locked for a moment.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(exe, exe + ".failed", overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 10)
                {
                    Thread.Sleep(500);
                }
            }
            File.Move(old, exe);
            TryDelete(exe + ".failed");
            Log.Info("Put the previous launcher back.");
        }

        /// <summary>
        /// The new process's half: once the process that updated it has exited, delete its old exe. Waits in the
        /// background; a file still locked is left for the next start.
        /// </summary>
        public static void CleanUpAfterUpdate(string exe, int oldProcessId)
        {
            Task.Run(() =>
            {
                try
                {
                    if (oldProcessId > 0)
                        using (var old = Process.GetProcessById(oldProcessId))
                            old.WaitForExit(60000);
                }
                catch (ArgumentException)
                {
                    // already gone
                }
                catch (InvalidOperationException)
                {
                }
                DeleteLeftovers(exe);
            });
        }

        /// <summary>Old exes an update left behind (ArcanaLauncher.exe.old, .failed).</summary>
        public static void DeleteLeftovers(string exe)
        {
            TryDelete(OldPath(exe));
            TryDelete(exe + ".failed");
        }

        /// <summary>The Apps list shows the installer's version until told otherwise: tell it this one.</summary>
        public static void RecordInstalledVersion()
        {
            if (!LauncherInfo.RunsFromInstall()) return;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(LauncherInfo.UninstallKeyPath, writable: true))
                {
                    if (key == null) return;
                    if (!string.Equals(key.GetValue("DisplayVersion") as string, LauncherInfo.FileVersion, StringComparison.Ordinal))
                    {
                        key.SetValue("DisplayVersion", LauncherInfo.FileVersion);
                        Log.Info($"Apps list now shows version {LauncherInfo.FileVersion}.");
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                Log.Warn("Could not update the Apps list version.", ex);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
