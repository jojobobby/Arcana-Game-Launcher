using System;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Net;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace GameLauncher
{
    enum LauncherState
    {
        ready,
        failed,
        downloadingGame,
        downloadingUpdate,
        extractingGame
    }

    public partial class MainWindow : Window
    {
        // ── Endpoints ────────────────────────────────────────────────
        // The game's CI publishes per-environment floating releases on
        // every successful build. The launcher consumes the dev channel
        // (`client-latest-dev`) — switch to `client-latest` to follow
        // the production channel instead.
        //
        // Each release contains four assets we care about:
        //   WebMain.swf            — the standalone SWF (browser channel)
        //   WebMain.swf.md5        — 32-char hex MD5 of WebMain.swf
        //   YamanoRealms-AIR.zip   — Adobe AIR captive runtime bundle
        //   client-metadata.json   — build metadata (unused here)
        //
        // The MD5 is the source of truth for "do we need to update?" The
        // AIR zip is what we actually install when the answer is yes.
        private const string CLIENT_RELEASE_BASE =
            "https://github.com/jojobobby/Arcana/releases/download/client-latest-dev";
        private const string REMOTE_HASH_URL   = CLIENT_RELEASE_BASE + "/WebMain.swf.md5";
        private const string REMOTE_BUNDLE_URL = CLIENT_RELEASE_BASE + "/YamanoRealms-AIR.zip";

        private const string VERSION_PREFIX = "YamanoRealms-no-wipe-betatesting";
        private const int HASH_DISPLAY_CHARS = 6;

        // ── Local layout ─────────────────────────────────────────────
        // After install:
        //   <rootPath>/Hash.txt                  — SWF MD5 of what's installed
        //   <rootPath>/YamanoRealms/             — extracted AIR captive bundle
        //   <rootPath>/YamanoRealms/YamanoRealms.exe — entry point
        //   <rootPath>/YamanoRealms-AIR.zip      — staging file during install
        private const string BUNDLE_DIR_NAME = "YamanoRealms";
        private const string BUNDLE_EXE_NAME = "YamanoRealms.exe";
        private const string BUNDLE_ZIP_NAME = "YamanoRealms-AIR.zip";
        private const string HASH_FILE_NAME  = "Hash.txt";

        private readonly string rootPath;
        private readonly string hashFile;
        private readonly string bundleDir;
        private readonly string bundleExe;
        private readonly string bundleZip;

        private LauncherState _status;
        internal LauncherState Status
        {
            get => _status;
            set
            {
                _status = value;
                switch (_status)
                {
                    case LauncherState.ready:
                        PlayButton.Content = "Play";
                        break;
                    case LauncherState.failed:
                        PlayButton.Content = "Update Failed - Retry";
                        break;
                    case LauncherState.downloadingGame:
                        PlayButton.Content = "Downloading Game";
                        break;
                    case LauncherState.downloadingUpdate:
                        PlayButton.Content = "Updating Game";
                        break;
                    case LauncherState.extractingGame:
                        PlayButton.Content = "Installing Game";
                        break;
                }
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            rootPath  = Directory.GetCurrentDirectory();
            hashFile  = Path.Combine(rootPath, HASH_FILE_NAME);
            bundleDir = Path.Combine(rootPath, BUNDLE_DIR_NAME);
            bundleExe = Path.Combine(bundleDir, BUNDLE_EXE_NAME);
            bundleZip = Path.Combine(rootPath, BUNDLE_ZIP_NAME);
        }

        // ── Display formatting ───────────────────────────────────────
        private static string FormatVersionLabel(string md5Hex)
        {
            if (string.IsNullOrEmpty(md5Hex))
                return $"{VERSION_PREFIX}-unknown";

            var slice = md5Hex.Length >= HASH_DISPLAY_CHARS
                ? md5Hex.Substring(0, HASH_DISPLAY_CHARS)
                : md5Hex;
            return $"{VERSION_PREFIX}-{slice}";
        }

        // ── Hash helpers ─────────────────────────────────────────────
        private static string ComputeFileMd5(string path)
        {
            if (!File.Exists(path)) return "";

            using (var stream = File.OpenRead(path))
            using (var md5 = MD5.Create())
            {
                var hashBytes = md5.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        // Hash.txt is the cached "what's installed". If the bundle itself is
        // gone (player deleted it), invalidate the cache so we reinstall on
        // the next check.
        private string ReadLocalHash()
        {
            if (!File.Exists(bundleExe))
                return "";

            if (File.Exists(hashFile))
            {
                var stored = File.ReadAllText(hashFile).Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(stored)) return stored;
            }
            return "";
        }

        // ── Update flow ──────────────────────────────────────────────
        private void CheckForUpdates()
        {
            var localHash = ReadLocalHash();
            VersionText.Text = FormatVersionLabel(localHash);

            try
            {
                WebClient webClient = new WebClient();
                var onlineHash = webClient.DownloadString(REMOTE_HASH_URL)
                    .Trim()
                    .ToLowerInvariant();

                if (string.IsNullOrEmpty(onlineHash))
                {
                    Status = LauncherState.failed;
                    MessageBox.Show("Remote hash file was empty.");
                    return;
                }

                if (string.IsNullOrEmpty(localHash))
                {
                    InstallBundle(isUpdate: false, expectedSwfHash: onlineHash);
                }
                else if (!string.Equals(localHash, onlineHash, StringComparison.OrdinalIgnoreCase))
                {
                    InstallBundle(isUpdate: true, expectedSwfHash: onlineHash);
                }
                else
                {
                    Status = LauncherState.ready;
                }
            }
            catch (Exception ex)
            {
                Status = LauncherState.failed;
                MessageBox.Show($"Error checking for game updates: {ex.Message}");
            }
        }

        private void InstallBundle(bool isUpdate, string expectedSwfHash)
        {
            try
            {
                Status = isUpdate ? LauncherState.downloadingUpdate : LauncherState.downloadingGame;

                // Clean up any zip left over from a previous failed install
                // so the new download doesn't append-or-confuse on disk.
                if (File.Exists(bundleZip))
                    File.Delete(bundleZip);

                WebClient webClient = new WebClient();
                webClient.DownloadFileCompleted += DownloadBundleCompleted;
                webClient.DownloadFileAsync(new Uri(REMOTE_BUNDLE_URL), bundleZip, expectedSwfHash);
            }
            catch (Exception ex)
            {
                Status = LauncherState.failed;
                MessageBox.Show($"Error starting download: {ex.Message}");
            }
        }

        private void DownloadBundleCompleted(object sender, AsyncCompletedEventArgs e)
        {
            try
            {
                if (e.Error != null)
                {
                    Status = LauncherState.failed;
                    MessageBox.Show($"Download error: {e.Error.Message}");
                    return;
                }

                Status = LauncherState.extractingGame;

                var expectedSwfHash = ((string)e.UserState ?? "").ToLowerInvariant();

                // Wipe the prior install directory before extracting the new
                // bundle. Without this, stale runtime DLLs from a previous
                // build could be loaded by the new AIR app and mismatch the
                // packaged SWF.
                if (Directory.Exists(bundleDir))
                {
                    try { Directory.Delete(bundleDir, recursive: true); }
                    catch (Exception ex)
                    {
                        Status = LauncherState.failed;
                        MessageBox.Show($"Could not remove old install: {ex.Message}");
                        return;
                    }
                }

                ZipFile.ExtractToDirectory(bundleZip, bundleDir);

                if (!File.Exists(bundleExe))
                {
                    Status = LauncherState.failed;
                    MessageBox.Show(
                        $"Bundle extracted but {BUNDLE_EXE_NAME} was not found. " +
                        $"The AIR package may have been built with a different output name.");
                    return;
                }

                // Verify the SWF inside the bundle matches the hash the
                // server advertised. AIR puts the SWF at one of two known
                // paths depending on packaging mode; check both.
                var swfCandidates = new[]
                {
                    Path.Combine(bundleDir, "WebMain.swf"),
                    Path.Combine(bundleDir, "META-INF", "AIR", "WebMain.swf"),
                };
                string actualSwfHash = "";
                foreach (var candidate in swfCandidates)
                {
                    if (File.Exists(candidate))
                    {
                        actualSwfHash = ComputeFileMd5(candidate);
                        break;
                    }
                }

                if (string.IsNullOrEmpty(actualSwfHash))
                {
                    Status = LauncherState.failed;
                    MessageBox.Show("Could not locate WebMain.swf inside the AIR bundle to verify integrity.");
                    return;
                }

                if (!string.Equals(actualSwfHash, expectedSwfHash, StringComparison.OrdinalIgnoreCase))
                {
                    // Don't commit a bundle whose SWF doesn't match the
                    // advertised hash — could be a partial download or an
                    // upload race. Tear it down and require a retry.
                    try { Directory.Delete(bundleDir, recursive: true); } catch { }
                    try { File.Delete(hashFile); } catch { }

                    Status = LauncherState.failed;
                    MessageBox.Show(
                        $"Hash mismatch after install. Expected {expectedSwfHash}, got {actualSwfHash}.");
                    return;
                }

                File.WriteAllText(hashFile, actualSwfHash);
                VersionText.Text = FormatVersionLabel(actualSwfHash);

                // Zip is only kept as staging; remove it now that the
                // bundle is installed and verified.
                try { File.Delete(bundleZip); } catch { }

                Status = LauncherState.ready;
            }
            catch (Exception ex)
            {
                Status = LauncherState.failed;
                MessageBox.Show($"Error finishing game install: {ex.Message}");
            }
        }

        // ── UI hooks ─────────────────────────────────────────────────
        private void Window_ContentRendered(object sender, EventArgs e)
        {
            CheckForUpdates();
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (Status == LauncherState.failed)
            {
                CheckForUpdates();
                return;
            }

            if (Status != LauncherState.ready || !File.Exists(bundleExe))
                return;

            try
            {
                // The AIR captive bundle is fully self-contained — its own
                // exe holds the runtime + the SWF, no external Flash Player
                // needed. WorkingDirectory must be the bundle root so the
                // runtime finds its sibling DLLs.
                var startInfo = new ProcessStartInfo(bundleExe)
                {
                    WorkingDirectory = bundleDir,
                    UseShellExecute = true,
                };
                Process.Start(startInfo);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not launch the game: {ex.Message}");
            }
        }
    }
}
