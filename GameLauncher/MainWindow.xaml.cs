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
        // ── Endpoints ─────────────────────────────────────────────────
        // Game CI publishes a floating `client-latest-dev` release on every
        // push to develop. Releases live on the public Arcana-Game-Launcher
        // repo (not the private Arcana source repo) so anonymous downloads
        // work without auth. The game CI cross-posts using a PAT secret.
        // We pull three files from there:
        //   WebMain.swf          → the standalone SWF (held next to us)
        //   WebMain.swf.md5      → 32-char hex MD5 of WebMain.swf
        //   YamanoRealms-AIR.zip → the AIR captive runtime bundle to run
        private const string CLIENT_RELEASE_BASE =
            "https://github.com/jojobobby/Arcana-Game-Launcher/releases/download/client-latest-dev";
        private const string REMOTE_HASH_URL   = CLIENT_RELEASE_BASE + "/WebMain.swf.md5";
        private const string REMOTE_SWF_URL    = CLIENT_RELEASE_BASE + "/WebMain.swf";
        private const string REMOTE_BUNDLE_URL = CLIENT_RELEASE_BASE + "/YamanoRealms-AIR.zip";

        private const string VERSION_PREFIX = "YamanoRealms-no-wipe-betatesting";
        private const int HASH_DISPLAY_CHARS = 6;

        // ── Local layout ──────────────────────────────────────────────
        // Everything lives in the launcher's working directory:
        //   <rootPath>/YamanoRealmsLauncher.exe
        //   <rootPath>/WebMain.swf              — held alongside, hash source of truth
        //   <rootPath>/Hash.txt                 — cached MD5 of WebMain.swf
        //   <rootPath>/YamanoRealms/            — extracted AIR captive bundle
        //   <rootPath>/YamanoRealms/YamanoRealms.exe — entry point we spawn
        //   <rootPath>/YamanoRealms-AIR.zip     — staging file, deleted after extract
        private const string SWF_FILE_NAME    = "WebMain.swf";
        private const string HASH_FILE_NAME   = "Hash.txt";
        private const string BUNDLE_DIR_NAME  = "YamanoRealms";
        private const string BUNDLE_EXE_NAME  = "YamanoRealms.exe";
        private const string BUNDLE_ZIP_NAME  = "YamanoRealms-AIR.zip";

        private readonly string rootPath;
        private readonly string swfPath;
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
            swfPath   = Path.Combine(rootPath, SWF_FILE_NAME);
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

        // "What's installed?" = hash of the local WebMain.swf, but only if
        // the AIR bundle the launcher actually runs is also present.
        // Missing either → treat as nothing installed → reinstall.
        private string ReadLocalHash()
        {
            if (!File.Exists(swfPath) || !File.Exists(bundleExe))
                return "";

            return ComputeFileMd5(swfPath);
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
                    InstallUpdate(isUpdate: false, expectedHash: onlineHash);
                }
                else if (!string.Equals(localHash, onlineHash, StringComparison.OrdinalIgnoreCase))
                {
                    InstallUpdate(isUpdate: true, expectedHash: onlineHash);
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

        // Delete-and-redownload, per spec. We can't safely "patch in place"
        // when the AIR captive runtime DLLs may have changed between builds,
        // so tearing the old install down before extracting the new one
        // avoids stale-DLL hybrids that would crash at runtime.
        private void InstallUpdate(bool isUpdate, string expectedHash)
        {
            try
            {
                Status = isUpdate ? LauncherState.downloadingUpdate : LauncherState.downloadingGame;

                // Stage 1 — wipe whatever's there.
                TryDelete(swfPath);
                TryDelete(hashFile);
                TryDelete(bundleZip);
                TryDeleteDir(bundleDir);

                // Stage 2 — pull the standalone SWF (cheap, gives us the
                // verification target we'll check the AIR bundle against).
                WebClient webClient = new WebClient();
                webClient.DownloadFileCompleted += DownloadSwfCompleted;
                webClient.DownloadFileAsync(new Uri(REMOTE_SWF_URL), swfPath, expectedHash);
            }
            catch (Exception ex)
            {
                Status = LauncherState.failed;
                MessageBox.Show($"Error starting download: {ex.Message}");
            }
        }

        // Step 2 done — verify the SWF, then chain into the bundle download.
        private void DownloadSwfCompleted(object sender, AsyncCompletedEventArgs e)
        {
            try
            {
                if (e.Error != null)
                {
                    Status = LauncherState.failed;
                    MessageBox.Show($"SWF download failed: {e.Error.Message}");
                    return;
                }

                var expectedHash = ((string)e.UserState ?? "").ToLowerInvariant();
                var actualHash = ComputeFileMd5(swfPath);

                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(swfPath);
                    Status = LauncherState.failed;
                    MessageBox.Show(
                        $"SWF integrity check failed.\nExpected {expectedHash}, got {actualHash}.");
                    return;
                }

                // Stage 3 — pull the AIR captive runtime bundle.
                WebClient webClient = new WebClient();
                webClient.DownloadFileCompleted += DownloadBundleCompleted;
                webClient.DownloadFileAsync(new Uri(REMOTE_BUNDLE_URL), bundleZip, expectedHash);
            }
            catch (Exception ex)
            {
                Status = LauncherState.failed;
                MessageBox.Show($"Error processing SWF download: {ex.Message}");
            }
        }

        // Step 3 done — extract, sanity-check the bundled SWF against the
        // standalone (both should match), commit Hash.txt, ready.
        private void DownloadBundleCompleted(object sender, AsyncCompletedEventArgs e)
        {
            try
            {
                if (e.Error != null)
                {
                    Status = LauncherState.failed;
                    MessageBox.Show($"AIR bundle download failed: {e.Error.Message}");
                    return;
                }

                Status = LauncherState.extractingGame;

                var expectedHash = ((string)e.UserState ?? "").ToLowerInvariant();

                ZipFile.ExtractToDirectory(bundleZip, bundleDir);

                if (!File.Exists(bundleExe))
                {
                    Status = LauncherState.failed;
                    MessageBox.Show(
                        $"AIR bundle extracted but {BUNDLE_EXE_NAME} was not found. " +
                        "The bundle layout may have changed.");
                    return;
                }

                // Belt-and-suspenders: confirm the SWF inside the bundle
                // matches what the server published. Either of two paths
                // depending on AIR packaging mode.
                var swfInBundleCandidates = new[]
                {
                    Path.Combine(bundleDir, "WebMain.swf"),
                    Path.Combine(bundleDir, "META-INF", "AIR", "WebMain.swf"),
                };
                string bundledSwfHash = "";
                foreach (var candidate in swfInBundleCandidates)
                {
                    if (File.Exists(candidate))
                    {
                        bundledSwfHash = ComputeFileMd5(candidate);
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(bundledSwfHash) &&
                    !string.Equals(bundledSwfHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    // Standalone and bundled SWFs disagree — refuse to commit.
                    TryDelete(swfPath);
                    TryDeleteDir(bundleDir);
                    TryDelete(bundleZip);

                    Status = LauncherState.failed;
                    MessageBox.Show(
                        $"AIR bundle SWF doesn't match standalone SWF — refusing to install.\n" +
                        $"Expected {expectedHash}, bundle had {bundledSwfHash}.");
                    return;
                }

                File.WriteAllText(hashFile, expectedHash);
                VersionText.Text = FormatVersionLabel(expectedHash);
                TryDelete(bundleZip);

                Status = LauncherState.ready;
            }
            catch (Exception ex)
            {
                Status = LauncherState.failed;
                MessageBox.Show($"Error installing AIR bundle: {ex.Message}");
            }
        }

        // ── Cleanup helpers ──────────────────────────────────────────
        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
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
                // WorkingDirectory must be the bundle root so the AIR
                // runtime finds its sibling DLLs and bundled assets.
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
