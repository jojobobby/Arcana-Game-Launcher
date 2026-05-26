using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameLauncher
{
    enum LauncherState
    {
        checking,
        updatingLauncher,
        ready,
        failed,
        downloadingGame,
        downloadingUpdate,
        extractingGame
    }

    public partial class MainWindow : Window
    {
        private const string ServerUrl = "https://app.tidansrealm.com";
        private const string LauncherDownloadUrl = "https://github.com/jojobobby/Arcana-Game-Launcher/releases/download/latest/YamanoRealmsLauncher.exe";
        private const string GameServerHost = "app.tidansrealm.com";
        private const int GameServerPort = 8887;
        private const string VersionPrefix = "YamanoRealms-no-wipe-betatesting";
        private const int HashDisplayChars = 6;

        private const string SwfFileName = "WebMain.swf";
        private const string HashFileName = "Hash.txt";
        private const string BundleDirName = "YamanoRealms";
        private const string BundleExeName = "YamanoRealms.exe";
        private const string BundleZipName = "YamanoRealms-AIR.zip";

        private readonly string rootPath;
        private readonly string swfPath;
        private readonly string hashFile;
        private readonly string bundleDir;
        private readonly string bundleExe;
        private readonly string bundleZip;

        private LauncherState _status;
        private bool _isBusy;
        private bool _isMeasuringLatency;
        private readonly DispatcherTimer latencyTimer;
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        private static string RemoteMetadataUrl => ServerUrl + "/client/download?metadata=true";
        private static string RemoteSwfUrl => ServerUrl + "/client/download";
        private static string RemoteBundleUrl => ServerUrl + "/air/download";

        internal LauncherState Status
        {
            get => _status;
            set
            {
                _status = value;
                UpdateStatusUi();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            rootPath = Directory.GetCurrentDirectory();
            swfPath = Path.Combine(rootPath, SwfFileName);
            hashFile = Path.Combine(rootPath, HashFileName);
            bundleDir = Path.Combine(rootPath, BundleDirName);
            bundleExe = Path.Combine(bundleDir, BundleExeName);
            bundleZip = Path.Combine(rootPath, BundleZipName);
            EndpointText.Text = new Uri(ServerUrl).Host;

            latencyTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            latencyTimer.Tick += LatencyTimer_Tick;
        }

        private void UpdateStatusUi()
        {
            switch (_status)
            {
                case LauncherState.checking:
                    PlayButton.Content = "Checking For Updates";
                    StatusText.Text = "Checking for updates...";
                    break;
                case LauncherState.updatingLauncher:
                    PlayButton.Content = "Updating Launcher";
                    StatusText.Text = "Updating launcher...";
                    break;
                case LauncherState.ready:
                    PlayButton.Content = "Play";
                    StatusText.Text = "Ready to play";
                    break;
                case LauncherState.failed:
                    PlayButton.Content = "Update Failed - Retry";
                    StatusText.Text = "Update check failed";
                    break;
                case LauncherState.downloadingGame:
                    PlayButton.Content = "Downloading Game";
                    StatusText.Text = "Downloading game files...";
                    break;
                case LauncherState.downloadingUpdate:
                    PlayButton.Content = "Updating Game";
                    StatusText.Text = "Updating game files...";
                    break;
                case LauncherState.extractingGame:
                    PlayButton.Content = "Installing Game";
                    StatusText.Text = "Installing game files...";
                    break;
            }

            PlayButton.IsEnabled = !_isBusy || _status == LauncherState.failed || _status == LauncherState.ready;
        }

        private static string FormatVersionLabel(string md5Hex)
        {
            if (string.IsNullOrEmpty(md5Hex))
                return $"{VersionPrefix}-unknown";

            var slice = md5Hex.Length >= HashDisplayChars
                ? md5Hex.Substring(0, HashDisplayChars)
                : md5Hex;
            return $"{VersionPrefix}-{slice}";
        }

        private static string ComputeFileMd5(string path)
        {
            if (!File.Exists(path))
                return "";

            using (var stream = File.OpenRead(path))
            using (var md5 = MD5.Create())
            {
                var hashBytes = md5.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        private string ReadLocalHash()
        {
            if (!File.Exists(swfPath) || !File.Exists(bundleExe))
                return "";

            return ComputeFileMd5(swfPath);
        }

        private sealed class RemoteMetadata
        {
            public string SwfHash { get; set; }
            public long LatencyMs { get; set; }
        }

        private static async Task<RemoteMetadata> FetchRemoteMetadataAsync()
        {
            string json;
            var timer = Stopwatch.StartNew();
            json = await Http.GetStringAsync(RemoteMetadataUrl);
            timer.Stop();

            var trimmed = json?.TrimStart() ?? "";
            if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
            {
                var preview = trimmed.Length > 120 ? trimmed.Substring(0, 120) + "..." : trimmed;
                throw new InvalidServerResponseException(
                    "The game server didn't return version info. It may be restarting or behind on its deploy.\n\n" +
                    "Server said:\n" + preview);
            }

            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("swf", out var swf) &&
                        swf.TryGetProperty("md5", out var md5) &&
                        md5.ValueKind == JsonValueKind.String)
                    {
                        return new RemoteMetadata
                        {
                            SwfHash = md5.GetString().Trim().ToLowerInvariant(),
                            LatencyMs = timer.ElapsedMilliseconds
                        };
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidServerResponseException(
                    "Couldn't read the version info from the server.\n\n" +
                    "Server said:\n" + (json.Length > 200 ? json.Substring(0, 200) + "..." : json) +
                    "\n\nParse error: " + ex.Message);
            }

            return new RemoteMetadata
            {
                SwfHash = "",
                LatencyMs = timer.ElapsedMilliseconds
            };
        }

        private sealed class InvalidServerResponseException : Exception
        {
            public InvalidServerResponseException(string message) : base(message) { }
        }

        private async Task<bool> CheckForLauncherUpdateAsync()
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
                return false;

            var updateDir = Path.Combine(Path.GetTempPath(), "YamanoRealmsLauncherUpdate");
            var updateExe = Path.Combine(updateDir, "YamanoRealmsLauncher.exe");
            var updateScript = Path.Combine(updateDir, "apply-launcher-update.cmd");

            try
            {
                Directory.CreateDirectory(updateDir);
                TryDelete(updateExe);
                TryDelete(updateScript);

                Status = LauncherState.updatingLauncher;
                UpdateProgress.Value = 0;

                await DownloadFileAsync(LauncherDownloadUrl, updateExe, 100);

                var currentHash = ComputeFileMd5(currentExe);
                var updateHash = ComputeFileMd5(updateExe);
                if (string.IsNullOrEmpty(updateHash) ||
                    string.Equals(currentHash, updateHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(updateExe);
                    UpdateProgress.Value = 0;
                    return false;
                }

                WriteLauncherUpdateScript(updateScript, currentExe, updateExe);
                Process.Start(new ProcessStartInfo(updateScript)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                Close();
                return true;
            }
            catch (Exception ex)
            {
                TryDelete(updateExe);
                TryDelete(updateScript);
                Status = LauncherState.failed;
                MessageBox.Show($"Could not update the launcher: {ex.Message}",
                    "Launcher update failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        private static void WriteLauncherUpdateScript(string scriptPath, string currentExe, string updateExe)
        {
            var currentPid = Process.GetCurrentProcess().Id;
            var script =
                "@echo off\r\n" +
                "setlocal\r\n" +
                $"set \"TARGET={currentExe}\"\r\n" +
                $"set \"UPDATE={updateExe}\"\r\n" +
                $"set \"PID={currentPid}\"\r\n" +
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
            File.WriteAllText(scriptPath, script);
        }

        private async Task CheckForUpdatesAsync()
        {
            if (_isBusy)
                return;

            _isBusy = true;
            UpdateProgress.Value = 0;
            Status = LauncherState.checking;
            SetServerStatus(isOnline: null, latencyMs: null);

            var localHash = ReadLocalHash();
            VersionText.Text = FormatVersionLabel(localHash);

            try
            {
                var remote = await FetchRemoteMetadataAsync();
                await UpdateGameLatencyAsync();
                var onlineHash = remote.SwfHash;

                if (string.IsNullOrEmpty(onlineHash))
                {
                    Status = LauncherState.failed;
                    MessageBox.Show("Remote metadata had no SWF hash.");
                    return;
                }

                if (string.IsNullOrEmpty(localHash))
                {
                    await InstallUpdateAsync(isUpdate: false, expectedHash: onlineHash);
                }
                else if (!string.Equals(localHash, onlineHash, StringComparison.OrdinalIgnoreCase))
                {
                    await InstallUpdateAsync(isUpdate: true, expectedHash: onlineHash);
                }
                else
                {
                    UpdateProgress.Value = 100;
                    Status = LauncherState.ready;
                }
            }
            catch (InvalidServerResponseException ex)
            {
                Status = LauncherState.failed;
                SetServerStatus(isOnline: false, latencyMs: null);
                MessageBox.Show(ex.Message, "Server unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (HttpRequestException ex)
            {
                Status = LauncherState.failed;
                SetServerStatus(isOnline: false, latencyMs: null);
                MessageBox.Show("Couldn't reach the game server.\n\n" + ex.Message,
                    "Connection error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (TaskCanceledException)
            {
                Status = LauncherState.failed;
                SetServerStatus(isOnline: false, latencyMs: null);
                MessageBox.Show("Couldn't reach the game server.\n\nThe game server took too long to respond.",
                    "Connection error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                Status = LauncherState.failed;
                SetServerStatus(isOnline: false, latencyMs: null);
                MessageBox.Show($"Error checking for game updates: {ex.Message}");
            }
            finally
            {
                _isBusy = false;
                UpdateStatusUi();
            }
        }

        private void SetServerStatus(bool? isOnline, long? latencyMs)
        {
            if (isOnline == true)
            {
                ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(87, 214, 132));
                ServerStatusText.Text = "Server: online";
                LatencyText.Text = latencyMs.HasValue ? $"Game: {latencyMs.Value} ms" : "Game: -- ms";
                return;
            }

            if (isOnline == false)
            {
                ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(230, 84, 84));
                ServerStatusText.Text = "Server: offline";
                LatencyText.Text = "Game: -- ms";
                return;
            }

            ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(123, 135, 144));
            ServerStatusText.Text = "Server: checking";
            LatencyText.Text = "Game: -- ms";
        }

        private static async Task<long?> MeasureGameServerLatencyAsync()
        {
            var timer = Stopwatch.StartNew();
            using (var client = new TcpClient())
            {
                var connectTask = client.ConnectAsync(GameServerHost, GameServerPort);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
                var completed = await Task.WhenAny(connectTask, timeoutTask);
                if (completed != connectTask)
                    return null;

                await connectTask;
            }

            timer.Stop();
            return timer.ElapsedMilliseconds;
        }

        private async Task UpdateGameLatencyAsync()
        {
            if (_isMeasuringLatency)
                return;

            _isMeasuringLatency = true;
            try
            {
                var latency = await MeasureGameServerLatencyAsync();
                SetServerStatus(isOnline: latency.HasValue, latencyMs: latency);
            }
            catch
            {
                SetServerStatus(isOnline: false, latencyMs: null);
            }
            finally
            {
                _isMeasuringLatency = false;
            }
        }

        private async Task InstallUpdateAsync(bool isUpdate, string expectedHash)
        {
            try
            {
                Status = isUpdate ? LauncherState.downloadingUpdate : LauncherState.downloadingGame;

                TryDelete(swfPath);
                TryDelete(hashFile);
                TryDelete(bundleZip);
                TryDeleteDir(bundleDir);

                await DownloadFileAsync(RemoteSwfUrl, swfPath, 45);

                var actualHash = ComputeFileMd5(swfPath);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(swfPath);
                    Status = LauncherState.failed;
                    MessageBox.Show($"SWF integrity check failed.\nExpected {expectedHash}, got {actualHash}.");
                    return;
                }

                await DownloadFileAsync(RemoteBundleUrl, bundleZip, 90);

                Status = LauncherState.extractingGame;
                UpdateProgress.Value = 94;

                await Task.Run(() => ZipFile.ExtractToDirectory(bundleZip, bundleDir));

                if (!File.Exists(bundleExe))
                {
                    Status = LauncherState.failed;
                    MessageBox.Show(
                        $"AIR bundle extracted but {BundleExeName} was not found. " +
                        "The bundle layout may have changed.");
                    return;
                }

                var bundledSwfHash = FindBundledSwfHash();
                if (!string.IsNullOrEmpty(bundledSwfHash) &&
                    !string.Equals(bundledSwfHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(swfPath);
                    TryDeleteDir(bundleDir);
                    TryDelete(bundleZip);

                    Status = LauncherState.failed;
                    MessageBox.Show(
                        "AIR bundle SWF doesn't match standalone SWF. Refusing to install.\n" +
                        $"Expected {expectedHash}, bundle had {bundledSwfHash}.");
                    return;
                }

                File.WriteAllText(hashFile, expectedHash);
                VersionText.Text = FormatVersionLabel(expectedHash);
                TryDelete(bundleZip);

                UpdateProgress.Value = 100;
                Status = LauncherState.ready;
            }
            catch (Exception ex)
            {
                Status = LauncherState.failed;
                MessageBox.Show($"Error installing game files: {ex.Message}");
            }
        }

        private async Task DownloadFileAsync(string url, string outputPath, double completedAt)
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = File.Create(outputPath))
                {
                    var buffer = new byte[81920];
                    long readBytes = 0;

                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, 0, buffer.Length);
                        if (read == 0)
                            break;

                        await output.WriteAsync(buffer, 0, read);
                        readBytes += read;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            var progress = completedAt * readBytes / totalBytes.Value;
                            UpdateProgress.Value = Math.Min(completedAt, Math.Max(UpdateProgress.Value, progress));
                        }
                    }
                }
            }
        }

        private string FindBundledSwfHash()
        {
            var swfInBundleCandidates = new[]
            {
                Path.Combine(bundleDir, "WebMain.swf"),
                Path.Combine(bundleDir, "META-INF", "AIR", "WebMain.swf"),
            };

            foreach (var candidate in swfInBundleCandidates)
                if (File.Exists(candidate))
                    return ComputeFileMd5(candidate);

            return "";
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }

        private async void Window_ContentRendered(object sender, EventArgs e)
        {
            latencyTimer.Start();
            if (await CheckForLauncherUpdateAsync())
                return;

            await CheckForUpdatesAsync();
        }

        private async void LatencyTimer_Tick(object sender, EventArgs e)
        {
            await UpdateGameLatencyAsync();
        }

        private async void Play_Click(object sender, RoutedEventArgs e)
        {
            if (Status == LauncherState.failed)
            {
                await CheckForUpdatesAsync();
                return;
            }

            if (Status != LauncherState.ready || !File.Exists(bundleExe))
                return;

            try
            {
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

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folderToOpen = Directory.Exists(bundleDir) ? bundleDir : rootPath;
                Directory.CreateDirectory(folderToOpen);
                Process.Start(new ProcessStartInfo(folderToOpen)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open the launcher folder: {ex.Message}");
            }
        }

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
                return;

            var result = MessageBox.Show(
                "Remove the installed game files from this launcher folder?",
                "Uninstall Yamano Realms",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            TryDelete(hashFile);
            TryDeleteDir(bundleDir);

            VersionText.Text = FormatVersionLabel("");
            UpdateProgress.Value = 0;
            Status = LauncherState.failed;
            StatusText.Text = "Game files removed";
        }
    }
}
