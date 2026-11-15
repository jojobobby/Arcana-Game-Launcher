using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher
{
    /// <summary>What is on disk: read back from client-version.json and install-manifest.json in the install folder.</summary>
    internal sealed class InstalledClient
    {
        public string Build { get; set; }
        public string Version { get; set; }
        public string Channel { get; set; }
        public string ExePath { get; set; }
        /// <summary>Every file of the install, or null for an install made by a launcher from before the manifest.</summary>
        public IReadOnlyList<ManifestEntry> Manifest { get; set; }
    }

    internal enum InstallHealth
    {
        NotInstalled,
        Ready,
        /// <summary>The install is there but Arcana.exe is gone: almost always an antivirus quarantine.</summary>
        ExeMissing,
        /// <summary>A file is missing or the wrong size, or the version file is unreadable.</summary>
        Damaged,
    }

    internal sealed class InstallInspection
    {
        public InstallInspection(InstallHealth health, InstalledClient client = null, string detail = null)
        {
            Health = health;
            Client = client;
            Detail = detail;
        }

        public InstallHealth Health { get; }
        public InstalledClient Client { get; }
        /// <summary>The first problem found, for the log.</summary>
        public string Detail { get; }
    }

    internal enum InstallStage { Downloading, Unpacking, Checking, Finishing }

    internal readonly struct InstallProgress
    {
        public InstallProgress(InstallStage stage, double percent, DownloadProgress download = default)
        {
            Stage = stage;
            Percent = percent;
            Download = download;
        }

        public InstallStage Stage { get; }
        /// <summary>0..100 over the whole install.</summary>
        public double Percent { get; }
        public DownloadProgress Download { get; }
    }

    /// <summary>The server's answer to a poll: changed (with the new metadata), unchanged, or unreachable.</summary>
    internal sealed class MetadataPoll
    {
        public bool Reached { get; set; }
        /// <summary>The server is up but wants an access code it was not given (401).</summary>
        public bool CodeNeeded { get; set; }
        public ClientMetadata Metadata { get; set; }
        public string ETag { get; set; }
        public long Milliseconds { get; set; }
    }

    /// <summary>
    /// Installs the native client the game server serves. The server bakes in the client built from its own commit
    /// (Arcana repo: .github/workflows client job -> realm-server-master/client-files -> ClientReleaseStore), so asking
    /// the server we are about to play on is what keeps the two in step; the server also refuses any other build
    /// (ClientBuildGate).
    ///   GET {server}/client/metadata  -> build id, sha256, size, executable, manifest
    ///   GET {server}/client/download  -> Arcana-client.zip (resumable)
    /// An install is current when its build id equals the server's. The id is the git tree of the client source, not a
    /// hash of the exe, so an unchanged client is never downloaded twice.
    ///
    /// The install is replaced as a whole: the zip is verified, unpacked beside the install, every file checked against
    /// the manifest, and only then swapped in by renaming folders, so a failed, corrupt or cancelled update never touches
    /// a working install. The last two zips stay in the downloads folder, so going back a build needs no download.
    /// </summary>
    internal sealed class ClientInstaller
    {
        public const string VersionFileName = "client-version.json";
        public const string ManifestFileName = "install-manifest.json";
        /// <summary>What launcher 1.0.9 and older left beside the install while downloading.</summary>
        private const string LegacyDownloadFileName = "Arcana-client.zip.download";
        private const long DiskMargin = 50L * 1024 * 1024;
        private const int ZipsKept = 2;

        private readonly HttpClient _http;
        private readonly LauncherChannel _channel;
        private readonly string _root;
        private readonly string _downloads;
        private readonly RetryPolicy _retry;
        private readonly Downloader _downloader;
        private readonly Func<string, bool> _gameRunningIn;
        private readonly Func<string, long?> _freeBytes;

        public string InstallDir { get; }
        public LauncherChannel Channel => _channel;

        private volatile bool _swapping;

        /// <summary>True for the moment the install folders are being renamed: closing then could lose the game folder.</summary>
        public bool IsSwapping => _swapping;

        /// <param name="root">The folder holding the channel folders (the launcher's own folder).</param>
        /// <param name="downloadsDir">Where zips are downloaded and kept; a subfolder per channel is used.</param>
        public ClientInstaller(HttpClient http, LauncherChannel channel, string root, string downloadsDir,
            RetryPolicy retry = null, Func<string, bool> gameRunningIn = null, Func<string, long?> freeBytes = null,
            TimeSpan? stall = null)
        {
            _http = http;
            _channel = channel;
            _root = root;
            _downloads = Path.Combine(downloadsDir, channel.Name);
            _retry = retry ?? RetryPolicy.Default;
            _downloader = new Downloader(http, stall, _retry);
            _gameRunningIn = gameRunningIn ?? (folder => GameProcesses.AnyRunningFrom(folder, "Arcana.exe"));
            _freeBytes = freeBytes ?? Disk.FreeBytes;
            InstallDir = Path.Combine(root, channel.InstallDirName);
        }

        public bool IsGameRunning() => Directory.Exists(InstallDir) && _gameRunningIn(InstallDir);

        /// <summary>The install if it can be started (version file read and the exe there), else null.</summary>
        public InstalledClient ReadInstalled()
        {
            var inspection = Inspect();
            return inspection.Health == InstallHealth.Ready || inspection.Health == InstallHealth.Damaged && inspection.Client != null
                ? inspection.Client
                : null;
        }

        /// <summary>What is on disk, with a quick check of every file's presence and size (no hashing).</summary>
        public InstallInspection Inspect()
        {
            var versionFile = Path.Combine(InstallDir, VersionFileName);
            if (!File.Exists(versionFile))
                return new InstallInspection(InstallHealth.NotInstalled);

            InstalledClient client;
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(versionFile)))
                {
                    var root = doc.RootElement;
                    var executable = Path.GetFileName(root.GetProperty("executable").GetString() ?? "");
                    if (executable.Length == 0)
                        return new InstallInspection(InstallHealth.Damaged, null, "the version file names no executable");
                    client = new InstalledClient
                    {
                        Build = root.GetProperty("build").GetString(),
                        Version = root.GetProperty("version").GetString(),
                        Channel = root.TryGetProperty("channel", out var channel) ? channel.GetString() : _channel.Name,
                        ExePath = Path.Combine(InstallDir, executable),
                    };
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is InvalidOperationException ||
                                       ex is KeyNotFoundException || ex is UnauthorizedAccessException)
            {
                // A damaged version file means "reinstall", never "crash on start".
                return new InstallInspection(InstallHealth.Damaged, null, "the version file is unreadable: " + ex.Message);
            }

            if (!File.Exists(client.ExePath))
                return new InstallInspection(InstallHealth.ExeMissing, client, Path.GetFileName(client.ExePath) + " is missing");

            client.Manifest = ReadManifest(Path.Combine(InstallDir, ManifestFileName));
            if (client.Manifest != null)
            {
                foreach (var entry in client.Manifest)
                {
                    var file = new FileInfo(entry.Under(InstallDir));
                    if (!file.Exists)
                        return new InstallInspection(InstallHealth.Damaged, client, entry.Path + " is missing");
                    if (file.Length != entry.Size)
                        return new InstallInspection(InstallHealth.Damaged, client, $"{entry.Path} is {file.Length} bytes, not {entry.Size}");
                }
            }
            return new InstallInspection(InstallHealth.Ready, client);
        }

        /// <summary>
        /// Hashes every file of the install against its manifest (the install's own, or the server's when it is the same
        /// build). Returns the paths that are missing or differ, or null when there is no manifest to check against.
        /// </summary>
        public async Task<IReadOnlyList<string>> VerifyAsync(InstalledClient installed, ClientMetadata served,
            IProgress<double> progress, CancellationToken cancel)
        {
            var manifest = installed.Manifest ??
                           (served != null && served.Build == installed.Build ? served.Manifest : null);
            if (manifest == null) return null;

            return await Task.Run(() =>
            {
                var bad = new List<string>();
                var total = Math.Max(1, manifest.Sum(e => e.Size));
                long done = 0;
                foreach (var entry in manifest)
                {
                    cancel.ThrowIfCancellationRequested();
                    var path = entry.Under(InstallDir);
                    try
                    {
                        if (!File.Exists(path) || new FileInfo(path).Length != entry.Size ||
                            !string.Equals(Downloader.Sha256OfFile(path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                            bad.Add(entry.Path);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        bad.Add(entry.Path);
                    }
                    done += entry.Size;
                    progress?.Report(100.0 * done / total);
                }
                return (IReadOnlyList<string>)bad;
            }, cancel).ConfigureAwait(false);
        }

        public async Task<ClientMetadata> FetchMetadataAsync(CancellationToken cancel = default, IProgress<string> notes = null)
        {
            for (var retry = 0; ; retry++)
            {
                try
                {
                    using (var timeout = RetryPolicy.WithTimeout(cancel, RetryPolicy.RequestTimeout))
                    using (var response = await _http.GetAsync(_channel.MetadataUrl, timeout.Token).ConfigureAwait(false))
                    {
                        var status = (int)response.StatusCode;
                        if (status == 404)
                            throw new LauncherException("Server unavailable",
                                "The game server is running but has no client to offer yet. Try again in a few minutes.");
                        if (status == 401)
                            throw new LauncherException("Access code needed",
                                $"The {_channel.Label} server asks for an access code before it gives out its client.");
                        if (status == 408 || status == 429 || status >= 500)
                            throw new RetryableException(status == 503 || status == 429 ? "the server is busy" : "the server answered HTTP " + status,
                                RetryPolicy.RetryAfterOf(response), status == 503 || status == 429);

                        // An error page is not JSON; Parse turns it into "server said: ..." for the player.
                        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            body = "HTTP " + status + " " + body;
                        return ClientMetadata.Parse(body, _channel);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is RetryableException ||
                                           ex is OperationCanceledException && !cancel.IsCancellationRequested)
                {
                    var reason = ex is RetryableException ? ex.Message
                        : ex is HttpRequestException ? "couldn't reach the server: " + ex.Message
                        : "the server took too long to answer";
                    if (retry >= _retry.Delays.Count)
                    {
                        Log.Warn($"Giving up on {_channel.MetadataUrl}: {reason}");
                        throw new LauncherException("Can't reach the game server",
                            $"Arcana Launcher couldn't get the game's version from {new Uri(_channel.ServerUrl).Host} ({reason}).\n\n" +
                            "Check your connection. If it is fine, the server may be restarting: try again in a minute.", ex);
                    }
                    var wait = _retry.WaitFor(retry, (ex as RetryableException)?.RetryAfter);
                    Log.Warn($"Version check failed ({reason}); trying again in {wait.TotalSeconds:0} s.");
                    notes?.Report(((ex as RetryableException)?.Busy == true ? "Server busy." : "Connection problem.") +
                                  $" Trying again in {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} s.");
                    await _retry.Delay(wait, cancel).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// A conditional GET of the metadata for the idle launcher: whether the server answers, how fast, and whether
        /// its build changed. Never throws for the network; a 304 is "unchanged".
        /// </summary>
        public async Task<MetadataPoll> PollAsync(string etag, CancellationToken cancel)
        {
            var poll = new MetadataPoll { ETag = etag };
            var clock = Stopwatch.StartNew();
            try
            {
                using (var timeout = RetryPolicy.WithTimeout(cancel, TimeSpan.FromSeconds(10)))
                using (var request = new HttpRequestMessage(HttpMethod.Get, _channel.MetadataUrl))
                {
                    if (!string.IsNullOrEmpty(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                    using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        poll.Milliseconds = clock.ElapsedMilliseconds;
                        var status = (int)response.StatusCode;
                        // Busy (503) still means the server is up.
                        poll.CodeNeeded = status == 401;
                        poll.Reached = status == 304 || status == 503 || status == 401 || response.IsSuccessStatusCode;
                        if (response.IsSuccessStatusCode)
                        {
                            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                            poll.Metadata = ClientMetadata.Parse(body, _channel);
                            poll.ETag = response.Headers.ETag?.ToString();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is LauncherException ||
                                       ex is OperationCanceledException && !cancel.IsCancellationRequested)
            {
                poll.Reached = ex is LauncherException;
            }
            return poll;
        }

        /// <summary>
        /// Asks the server for its client's metadata with <paramref name="code"/> (null: with whatever code is already
        /// remembered for it) and says how the server took it. Never throws for the network. Only the server knows the
        /// code, so this is the one way to tell a right code from a wrong one.
        /// </summary>
        public async Task<AccessAnswer> ProbeAccessAsync(string code, CancellationToken cancel = default)
        {
            try
            {
                using (var timeout = RetryPolicy.WithTimeout(cancel, TimeSpan.FromSeconds(10)))
                using (var request = new HttpRequestMessage(HttpMethod.Get, _channel.MetadataUrl))
                {
                    if (code != null) request.Headers.TryAddWithoutValidation(AccessCodes.Header, code);
                    using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        var status = (int)response.StatusCode;
                        if (status == 401) return AccessAnswer.Refused;
                        if (status == 429) return AccessAnswer.TooManyTries;
                        // 404 is a server with no client to give: nothing there for a code to open.
                        return response.IsSuccessStatusCode || status == 404 ? AccessAnswer.Allowed : AccessAnswer.Unreachable;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException ||
                                       ex is OperationCanceledException && !cancel.IsCancellationRequested)
            {
                return AccessAnswer.Unreachable;
            }
        }

        /// <summary>
        /// Throws when the drive can't hold the rest of the download plus the unpacked install beside the current one
        /// (the swap keeps both for a moment), with 50 MB to spare.
        /// </summary>
        public void EnsureDiskSpace(ClientMetadata metadata)
        {
            var zip = ZipPathFor(metadata);
            var have = File.Exists(zip) ? metadata.FileSize : File.Exists(zip + ".part") ? new FileInfo(zip + ".part").Length : 0;
            var needs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            void Need(string folder, long bytes)
            {
                var drive = Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder;
                needs[drive] = (needs.TryGetValue(drive, out var sum) ? sum : 0) + bytes;
            }
            Need(_downloads, Math.Max(0, metadata.FileSize - have) + DiskMargin);
            Need(_root, metadata.UnpackedSize + DiskMargin);

            foreach (var need in needs)
            {
                var free = _freeBytes(need.Key);
                if (free.HasValue && free.Value < need.Value)
                {
                    Log.Warn($"Not enough space on {need.Key}: need {need.Value}, free {free.Value}.");
                    throw new LauncherException("Not enough disk space",
                        $"Arcana needs {Disk.Megabytes(need.Value)} free on {need.Key.TrimEnd('\\')}. You have {Disk.Megabytes(free.Value)}.\n\n" +
                        "Free some space, then try again.");
                }
            }
        }

        public string ZipPathFor(ClientMetadata metadata) => Path.Combine(_downloads, metadata.Sha256 + ".zip");

        /// <summary>Progress runs 0..100. The current install is untouched unless the new one is complete and checked.</summary>
        public async Task InstallAsync(ClientMetadata metadata, IProgress<InstallProgress> progress, CancellationToken cancel = default)
        {
            CleanLeftovers();
            if (IsGameRunning())
                throw new LauncherException("Game is running", "Close Arcana, then update again.");
            EnsureDiskSpace(metadata);

            var zip = ZipPathFor(metadata);
            var staged = InstallDir + ".new";
            var retired = InstallDir + ".old";
            var clock = Stopwatch.StartNew();
            Log.Info($"Installing {_channel.Name} build {metadata.Build} ({metadata.Version}) into {InstallDir}");
            try
            {
                var download = progress == null ? null
                    : new SyncProgress<DownloadProgress>(p => progress.Report(new InstallProgress(InstallStage.Downloading, 85 * p.Fraction, p)));
                await _downloader.FetchAsync(_channel.DownloadUrl, zip, metadata.FileSize, metadata.Sha256, metadata.DownloadETag,
                    download, cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();

                progress?.Report(new InstallProgress(InstallStage.Unpacking, 86));
                await Task.Run(() => Unpack(zip, staged), cancel).ConfigureAwait(false);

                progress?.Report(new InstallProgress(InstallStage.Checking, 93));
                var manifest = await Task.Run(() => CheckStaged(staged, metadata), cancel).ConfigureAwait(false);
                WriteInstallFiles(staged, metadata, manifest);
                cancel.ThrowIfCancellationRequested();

                // Past this point the swap is a few renames: it is not cancelled half way, and the window won't close
                // during it (IsSwapping). If the process dies between the renames anyway, CleanLeftovers puts it back.
                progress?.Report(new InstallProgress(InstallStage.Finishing, 98));
                _swapping = true;
                try
                {
                    Swap(staged, retired);
                }
                finally
                {
                    _swapping = false;
                }
                TryDeleteDir(retired);
                PruneDownloads(metadata.Sha256);
                progress?.Report(new InstallProgress(InstallStage.Finishing, 100));
                Log.Info($"Installed {_channel.Name} build {metadata.Build} in {clock.Elapsed.TotalSeconds:0.0} s");
            }
            finally
            {
                TryDeleteDir(staged);
                // If a failed swap could not be rolled back, the retired folder is the only copy left.
                if (Directory.Exists(InstallDir))
                    TryDeleteDir(retired);
            }
        }

        private static void Unpack(string zip, string staged)
        {
            TryDeleteDir(staged);
            try
            {
                // ExtractToDirectory refuses entries that would land outside the target folder.
                ZipFile.ExtractToDirectory(zip, staged);
            }
            catch (InvalidDataException ex)
            {
                // A zip that passed its hash but can't be read means a bad release; a fresh download won't help.
                throw new LauncherException("Install failed", "The game files from the server could not be unpacked: " + ex.Message, ex);
            }
            catch (IOException ex) when (Disk.IsFull(ex))
            {
                throw Disk.Full(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw Disk.CannotWrite(Path.GetDirectoryName(staged), ex);
            }
        }

        // Every file the server listed is there with its size and hash; without a manifest, the one we write records
        // what was unpacked, so later checks still work.
        private static IReadOnlyList<ManifestEntry> CheckStaged(string staged, ClientMetadata metadata)
        {
            if (!File.Exists(Path.Combine(staged, metadata.Executable)))
                throw new LauncherException("Install failed",
                    $"The download did not contain {metadata.Executable}. The release layout may have changed.");

            if (metadata.Manifest == null)
            {
                return Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories)
                    .Select(file => new ManifestEntry(Path.GetRelativePath(staged, file).Replace('\\', '/'),
                        new FileInfo(file).Length, Downloader.Sha256OfFile(file)))
                    .OrderBy(e => e.Path, StringComparer.Ordinal)
                    .ToList();
            }

            foreach (var entry in metadata.Manifest)
            {
                var path = entry.Under(staged);
                if (!File.Exists(path) || new FileInfo(path).Length != entry.Size ||
                    !string.Equals(Downloader.Sha256OfFile(path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException("Install failed",
                        $"{entry.Path} in the download does not match the server's file list, so nothing was installed.");
            }
            return metadata.Manifest;
        }

        private void WriteInstallFiles(string staged, ClientMetadata metadata, IReadOnlyList<ManifestEntry> manifest)
        {
            File.WriteAllText(Path.Combine(staged, VersionFileName), JsonSerializer.Serialize(new
            {
                build = metadata.Build,
                version = metadata.Version,
                executable = metadata.Executable,
                channel = _channel.Name,
            }));
            File.WriteAllText(Path.Combine(staged, ManifestFileName), JsonSerializer.Serialize(new
            {
                build = metadata.Build,
                files = manifest.Select(e => new { path = e.Path, size = e.Size, sha256 = e.Sha256 }),
            }));
        }

        private static IReadOnlyList<ManifestEntry> ReadManifest(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    var entries = new List<ManifestEntry>();
                    foreach (var item in doc.RootElement.GetProperty("files").EnumerateArray())
                    {
                        var safe = ManifestEntry.SafePath(item.GetProperty("path").GetString());
                        var sha = item.GetProperty("sha256").GetString();
                        if (safe == null || !ClientMetadata.IsSha256(sha)) return null;
                        entries.Add(new ManifestEntry(safe, item.GetProperty("size").GetInt64(), sha));
                    }
                    return entries;
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is InvalidOperationException ||
                                       ex is KeyNotFoundException || ex is FormatException || ex is UnauthorizedAccessException)
            {
                return null;
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

        /// <summary>
        /// Removes the game files. The folder is renamed first, which fails as a whole while anything inside is open, so
        /// a running game never leaves a half-deleted install behind.
        /// </summary>
        public void Uninstall()
        {
            if (!Directory.Exists(InstallDir)) return;
            if (IsGameRunning())
                throw new LauncherException("Game is running", "Close Arcana, then uninstall again.");

            var doomed = InstallDir + ".delete";
            TryDeleteDir(doomed);
            try
            {
                Directory.Move(InstallDir, doomed);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new LauncherException("Game files in use",
                    "Something is using the game files. Close Arcana (and any folder window showing them), then uninstall again.", ex);
            }
            TryDeleteDir(doomed);
            Log.Info($"Uninstalled {_channel.Name} from {InstallDir}");
        }

        /// <summary>
        /// Moves a game folder a launcher left elsewhere (a copy run from Downloads) into this install, when this one has
        /// none yet. Returns true when it moved. Never moves a folder whose game is running.
        /// </summary>
        public bool Adopt(string oldRoot)
        {
            if (string.IsNullOrWhiteSpace(oldRoot)) return false;
            var source = Path.Combine(oldRoot, _channel.InstallDirName);
            if (LauncherInfo.SameFolder(source, InstallDir) || !Directory.Exists(source) || Directory.Exists(InstallDir))
                return false;
            if (!File.Exists(Path.Combine(source, VersionFileName)) || _gameRunningIn(source))
                return false;

            try
            {
                Directory.CreateDirectory(_root);
                if (string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(InstallDir)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(source, InstallDir);
                }
                else
                {
                    var staged = InstallDir + ".new";
                    TryDeleteDir(staged);
                    CopyDirectory(source, staged);
                    Directory.Move(staged, InstallDir);
                    TryDeleteDir(source);
                }
                Log.Info($"Adopted the {_channel.Name} game folder from {source}");
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn($"Could not adopt {source}; the game will download fresh.", ex);
                TryDeleteDir(InstallDir + ".new");
                return false;
            }
        }

        /// <summary>
        /// Folders and files an interrupted install, update or uninstall left behind. An update stopped between its two
        /// renames leaves the working install as ".old" and no install: that one is put back, not deleted.
        /// </summary>
        public void CleanLeftovers()
        {
            TryDeleteDir(InstallDir + ".new");
            var retired = InstallDir + ".old";
            if (!Directory.Exists(InstallDir) && Directory.Exists(retired))
            {
                try
                {
                    Directory.Move(retired, InstallDir);
                    Log.Warn($"Put back {InstallDir}: an update was stopped half way.");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Warn($"Could not put back {retired}.", ex);
                }
            }
            if (Directory.Exists(InstallDir)) TryDeleteDir(retired);
            TryDeleteDir(InstallDir + ".delete");
            TryDelete(Path.Combine(_root, LegacyDownloadFileName));
        }

        // Keeps the zip just installed and the newest other one (a rollback then needs no download); drops the rest,
        // and any half-downloaded file of another build.
        private void PruneDownloads(string currentSha)
        {
            try
            {
                if (!Directory.Exists(_downloads)) return;
                var zips = new DirectoryInfo(_downloads).GetFiles("*.zip")
                    .OrderByDescending(f => string.Equals(Path.GetFileNameWithoutExtension(f.Name), currentSha, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                foreach (var stale in zips.Skip(ZipsKept)) TryDelete(stale.FullName);
                foreach (var part in new DirectoryInfo(_downloads).GetFiles("*.part"))
                    if (!part.Name.StartsWith(currentSha, StringComparison.OrdinalIgnoreCase))
                        TryDelete(part.FullName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn("Could not prune old downloads.", ex);
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(file, to);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }

    /// <summary>An IProgress that reports on the caller's thread (Progress&lt;T&gt; posts to the UI thread's context).</summary>
    internal sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public SyncProgress(Action<T> report) { _report = report; }
        public void Report(T value) => _report(value);
    }
}
