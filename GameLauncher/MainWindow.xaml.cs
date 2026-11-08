using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GameLauncher
{
    /// <summary>
    /// The launcher window. One job runs at a time (<see cref="_busy"/>):
    ///   Play       check the server's build; install, update or repair when needed; start the game; close
    ///   Update     the same without starting the game
    ///   Verify     hash every file against the manifest; repair what differs
    ///   Uninstall  remove the game files (the launcher stays; Windows' Apps list removes the launcher)
    /// On open: a release build updates itself first (LauncherSelfUpdate), then the window shows what is installed, and
    /// polls the server every 15 s for its state and build. A copy run from outside its install offers the installer.
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
        private static readonly SolidColorBrush Cream = Frozen(255, 248, 232);
        private static readonly SolidColorBrush Green = Frozen(87, 214, 132);
        private static readonly SolidColorBrush Red = Frozen(230, 84, 84);
        private static readonly SolidColorBrush Grey = Frozen(123, 135, 144);
        private static readonly SolidColorBrush Amber = Frozen(240, 190, 96);

        private readonly LaunchOptions _options;
        // The server this window talks to and the install that goes with it. Both change together, only in
        // SwitchChannel, and only while no job runs: Live and Development never share a folder or a download.
        private LauncherChannel _channel;
        private ClientInstaller _installer;
        private readonly LauncherSelfUpdate _selfUpdate;
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _backgroundTimer;
        private readonly string[] _backgrounds =
        {
            "/Assets/Title_Screen_0.png",
            "/Assets/Title_Screen_1.png",
            "/Assets/Title_Screen_2.png",
            "/Assets/Title_Screen_3.png"
        };
        private readonly List<long> _pings = new List<long>();

        private ClientMetadata _served;
        private string _servedETag;
        private bool _busy;
        private bool _polling;
        private bool _pollAgain;
        private bool _launcherUpdateFailed;
        private CancellationTokenSource _work;
        private BannerKind _banner;
        private string _adoptedFrom;
        private bool _isBackgroundAActive = true;
        private int _backgroundIndex;
        private readonly Stopwatch _speedClock = new Stopwatch();
        private long _speedBytes;
        private double _speed;

        private enum BannerKind { None, Install, OpenInstalled, Adopted }

        internal MainWindow(LaunchOptions options)
        {
            InitializeComponent();
            _options = options;
            _channel = options.Channel;
            _installer = InstallerFor(_channel);
            _selfUpdate = options.ReleasesUrl == null ? null
                : new LauncherSelfUpdate(App.Http, options.ReleasesUrl, LauncherInfo.ExePath);

            ShowChannel();
            LauncherVersionText.Text = "Launcher " + LauncherInfo.Version + (LauncherInfo.IsReleaseBuild ? "" : " (local build)");
            _pollTimer = new DispatcherTimer { Interval = PollInterval };
            _pollTimer.Tick += async (sender, args) => await PollAsync();
            _backgroundTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _backgroundTimer.Tick += (sender, args) => FadeToNextBackground();
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private string Host => new Uri(_channel.ServerUrl).Host;

        // Beside the launcher exe, not the working directory: a shortcut's "Start in" must not move the install.
        private static ClientInstaller InstallerFor(LauncherChannel channel) =>
            new ClientInstaller(App.Http, channel, LauncherInfo.ExeDir, LauncherInfo.DownloadsDir);

        // ---- Live / Development -------------------------------------------------------------------------------

        // The window sets the two toggles itself; their Checked events mean a switch only when the player made them.
        private bool _showingChannel;

        private void ShowChannel()
        {
            _showingChannel = true;
            try
            {
                var live = _channel.Name == LauncherChannel.Production.Name;
                LiveButton.IsChecked = live;
                DevelopmentButton.IsChecked = !live;
                var test = _options.ServerOverride != null ? " (test server)" : "";
                LiveButton.ToolTip = "Play on the live servers (" + new Uri(LauncherChannel.Production.ServerUrl).Host + ")" + test;
                DevelopmentButton.ToolTip = "Play on the development servers (" + new Uri(LauncherChannel.Development.ServerUrl).Host + ")" + test;
            }
            finally
            {
                _showingChannel = false;
            }
        }

        // Checked and Unchecked, not Click: a keyboard, a screen reader (UI Automation's Toggle) and the mouse all
        // land here. Clicking the selected one only unchecks it, which ShowChannel puts back.
        private void Live_Checked(object sender, RoutedEventArgs e)
        {
            if (!_showingChannel) SwitchChannel(LauncherChannel.Production);
        }

        private void Development_Checked(object sender, RoutedEventArgs e)
        {
            if (!_showingChannel) SwitchChannel(LauncherChannel.Development);
        }

        private void Segment_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!_showingChannel) ShowChannel();
        }

        /// <summary>
        /// Moves the window to the other server: its own install folder (Arcana\ or Arcana-development\), its own
        /// build check, and the choice saved for the next run. Downloads share one folder but are named by checksum,
        /// so the two builds never mix. Refused while a job runs, so an install of one never finishes into the other.
        /// A game already running keeps running: it is the other folder.
        /// </summary>
        private async void SwitchChannel(LauncherChannel target)
        {
            if (_busy || _channel.Name == target.Name)
            {
                ShowChannel();
                return;
            }
            var previous = _channel.Name;
            _channel = _options.Resolve(target);
            _installer = InstallerFor(_channel);
            _served = null;
            _servedETag = null;
            _pings.Clear();
            new LauncherSettings { Channel = _channel.Name }.Save(LauncherSettings.DefaultPath);
            Log.Info($"Switched from {previous} to {_channel.Name} ({_channel.ServerUrl}); saved for the next run.");

            ShowChannel();
            _installer.CleanLeftovers();
            SetServerStatus(null, null);
            ShowIdle();
            await PollAsync();
        }

        // ---- Start-up -------------------------------------------------------------------------------------------

        private async void Window_ContentRendered(object sender, EventArgs e)
        {
#if DEBUG
            if (_options.Preview != null)
            {
                Preview(_options.Preview);
                return;
            }
#endif
            _backgroundTimer.Start();
            if (_options.UpdatedFromPid > 0)
                SignalUpdatedLauncherStarted();
            _installer.CleanLeftovers();
            ShowIdle();
            SetServerStatus(null, null);

            if (await UpdateLauncherAsync())
                return;

            if (_options.AdoptFrom != null)
                AdoptOldInstall(_options.AdoptFrom);
            RefreshBanner();

            await PollAsync();
            _pollTimer.Start();
        }

        // The launcher that updated us waits for this before it exits (LauncherSelfUpdate).
        private void SignalUpdatedLauncherStarted()
        {
            if (EventWaitHandle.TryOpenExisting(LauncherSelfUpdate.StartedEventName(_options.UpdatedFromPid), out var started))
                using (started)
                    started.Set();
            Log.Info($"Updated to launcher {LauncherInfo.Version}.");
        }

        private void AdoptOldInstall(string folder)
        {
            var moved = false;
            foreach (var channel in new[] { LauncherChannel.Production, LauncherChannel.Development })
            {
                var installer = channel.Name == _channel.Name ? _installer
                    : new ClientInstaller(App.Http, channel, LauncherInfo.ExeDir, LauncherInfo.DownloadsDir);
                moved |= installer.Adopt(folder);
            }
            _adoptedFrom = folder;
            Log.Info(moved ? $"Moved the game in from {folder}." : $"Nothing to move from {folder}.");
        }

        // ---- Idle state -----------------------------------------------------------------------------------------

        /// <summary>The window at rest: what is installed, and whether the server has something newer.</summary>
        private void ShowIdle(string status = null, string detail = null)
        {
            var inspection = _installer.Inspect();
            var installed = inspection.Client;
            VersionText.Text = installed == null ? "Arcana (not installed)" : $"Arcana v{installed.Version} ({installed.Build})";
            UpdateProgress.Value = inspection.Health == InstallHealth.Ready ? 100 : 0;

            var updateAvailable = false;
            switch (inspection.Health)
            {
                case InstallHealth.NotInstalled:
                    SetStatus(status ?? "Install required", detail ?? Host);
                    SetPlay("Install", "DownloadButton");
                    break;
                case InstallHealth.ExeMissing:
                    SetStatus(status ?? "Arcana.exe is missing", detail ?? "Windows Security may have removed it. Click Repair.", Amber);
                    SetPlay("Repair", "DownloadButton");
                    break;
                case InstallHealth.Damaged:
                    SetStatus(status ?? "Game files need repair", detail ?? "Some files are missing or changed. Click Repair.", Amber);
                    SetPlay("Repair", "DownloadButton");
                    break;
                default:
                    updateAvailable = _served != null && !string.Equals(_served.Build, installed.Build, StringComparison.Ordinal);
                    if (updateAvailable)
                        SetStatus(status ?? "Update available", detail ?? $"Arcana v{_served.Version} is out. Play updates it first.");
                    else
                        SetStatus(status ?? "Ready to play", detail ?? Host, status == null ? Green : null);
                    SetPlay("Play", "SmallPlayButton");
                    break;
            }

            var hasFiles = inspection.Health != InstallHealth.NotInstalled;
            PlayButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = true;
            VerifyButton.Visibility = inspection.Health == InstallHealth.Ready ? Visibility.Visible : Visibility.Collapsed;
            VerifyButton.IsEnabled = true;
            UninstallButton.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
            UninstallButton.IsEnabled = true;
            UpdateButton.Content = updateAvailable ? "Update Available" : "Update";
            UpdateButton.IsEnabled = updateAvailable;
            BannerButton.IsEnabled = true;
            LiveButton.IsEnabled = true;
            DevelopmentButton.IsEnabled = true;
        }

        private void SetStatus(string status, string detail, Brush statusBrush = null)
        {
            StatusText.Text = status;
            StatusText.Foreground = statusBrush ?? Cream;
            DetailText.Text = detail ?? "";
        }

        private void SetPlay(string label, string style)
        {
            PlayButton.Content = label;
            PlayButton.Style = (Style)FindResource(style);
        }

        // ---- Server poll ----------------------------------------------------------------------------------------

        /// <summary>
        /// Every 15 s while idle, and right after a switch: is the server up, how fast, and did its build change. An
        /// answer that comes back after the player switched servers belongs to the other server: it is dropped, and
        /// the new server is asked instead.
        /// </summary>
        private async Task PollAsync()
        {
            if (_busy) return;
            if (_polling)
            {
                _pollAgain = true;
                return;
            }
            _polling = true;
            try
            {
                do
                {
                    _pollAgain = false;
                    var installer = _installer;
                    var poll = await installer.PollAsync(_servedETag, CancellationToken.None);
                    if (!ReferenceEquals(installer, _installer))
                    {
                        _pollAgain = true;
                        continue;
                    }
                    if (poll.Metadata != null)
                    {
                        if (_served == null || _served.Build != poll.Metadata.Build)
                            Log.Info($"Server {Host} serves build {poll.Metadata.Build} ({poll.Metadata.Version}).");
                        _served = poll.Metadata;
                        _servedETag = poll.ETag;
                    }
                    if (poll.Reached)
                    {
                        _pings.Add(poll.Milliseconds);
                        if (_pings.Count > 3) _pings.RemoveAt(0);
                    }
                    SetServerStatus(poll.Reached, poll.Reached ? Ping() : null);
                    if (!_busy) ShowIdle();
                }
                while (_pollAgain && !_busy);
            }
            finally
            {
                _polling = false;
            }
        }

        /// <summary>The median of the last three polls (the first also pays for the TLS handshake), or null before one.</summary>
        private long? Ping() => _pings.Count == 0 ? (long?)null : _pings.OrderBy(p => p).ElementAt(_pings.Count / 2);

        private void SetServerStatus(bool? online, long? ping)
        {
            ServerStatusDot.Fill = online == true ? Green : online == false ? Red : Grey;
            ServerStatusText.Text = online == true ? "Server: online" : online == false ? "Server: offline" : "Server: checking";
            LatencyText.Text = ping.HasValue ? $"Ping: {ping.Value} ms" : "Ping: -- ms";
        }

        // ---- Jobs -----------------------------------------------------------------------------------------------

        private void BeginJob(bool cancellable)
        {
            _busy = true;
            _work = new CancellationTokenSource();
            PlayButton.IsEnabled = false;
            OpenFolderButton.IsEnabled = false;
            VerifyButton.IsEnabled = false;
            UninstallButton.IsEnabled = false;
            BannerButton.IsEnabled = false;
            // No switching servers mid-job: an install of one must never finish into the other.
            LiveButton.IsEnabled = false;
            DevelopmentButton.IsEnabled = false;
            UpdateButton.Content = cancellable ? "Cancel" : "Update";
            UpdateButton.IsEnabled = cancellable;
            UpdateProgress.Value = 0;
        }

        private void EndJob()
        {
            _busy = false;
            _work?.Dispose();
            _work = null;
        }

        private async void Play_Click(object sender, RoutedEventArgs e) =>
            await RunAsync(launch: _installer.Inspect().Health == InstallHealth.Ready);

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _work?.Cancel();
                UpdateButton.IsEnabled = false;
                return;
            }
            await RunAsync(launch: false);
        }

        /// <summary>Check the server, install or update or repair if needed, then start the game when asked.</summary>
        private async Task RunAsync(bool launch)
        {
            if (_busy) return;
            BeginJob(cancellable: true);
            var cancel = _work.Token;
            var finalStatus = (string)null;
            var finalDetail = (string)null;
            try
            {
                SetStatus("Checking for updates...", Host);
                var notes = new Progress<string>(note => DetailText.Text = note);
                _served = await _installer.FetchMetadataAsync(cancel, notes);
                SetServerStatus(true, Ping());

                var inspection = _installer.Inspect();
                var current = inspection.Health == InstallHealth.Ready &&
                              string.Equals(inspection.Client.Build, _served.Build, StringComparison.Ordinal);
                if (!current)
                {
                    if (inspection.Health == InstallHealth.ExeMissing && !ConfirmRepairAfterQuarantine())
                        return;
                    await WaitForGameToCloseAsync(cancel);
                    var verb = inspection.Health == InstallHealth.NotInstalled ? "Downloading" :
                        inspection.Health == InstallHealth.Ready ? "Updating" : "Repairing";
                    Log.Info($"{verb} Arcana: installed {inspection.Client?.Build ?? "nothing"} ({inspection.Health}" +
                             (inspection.Detail == null ? "" : ", " + inspection.Detail) + $"), server {_served.Build}.");
                    await InstallAsync(verb, cancel);
                }

                if (launch)
                {
                    StartGame();
                    return;
                }
                // null: ShowIdle's own green "Ready to play".
                finalStatus = current ? "Up to date" : null;
                finalDetail = current ? $"Arcana v{_served.Version} is the latest." : null;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                Log.Info("Cancelled by the player.");
                finalStatus = "Update cancelled";
                finalDetail = "It picks up where it stopped next time.";
            }
            catch (LauncherException ex)
            {
                Log.Warn($"{ex.Title}: {ex.Message}", ex.InnerException);
                if (ex.Title.StartsWith("Can't reach", StringComparison.Ordinal)) SetServerStatus(false, null);
                finalStatus = ex.Title;
                ShowError(ex.Title, ex.Message);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Error("Install failed.", ex);
                finalStatus = "Install failed";
                ShowError("Install failed", "Arcana Launcher couldn't write the game files: " + ex.Message);
            }
            finally
            {
                if (_busy)
                {
                    EndJob();
                    if (IsLoaded) ShowIdle(finalStatus, finalDetail);
                }
            }
        }

        private async Task InstallAsync(string verb, CancellationToken cancel)
        {
            _speedClock.Restart();
            _speedBytes = 0;
            _speed = 0;
            SetStatus(verb + " Arcana", "");
            var progress = new Progress<InstallProgress>(p => ShowInstallProgress(verb, p));
            await _installer.InstallAsync(_served, progress, cancel);
            VersionText.Text = $"Arcana v{_served.Version} ({_served.Build})";
        }

        private void ShowInstallProgress(string verb, InstallProgress p)
        {
            UpdateProgress.Value = Math.Max(UpdateProgress.Value, p.Percent);
            switch (p.Stage)
            {
                case InstallStage.Downloading:
                    StatusText.Text = verb + " Arcana";
                    DetailText.Text = p.Download.Note ?? DownloadDetail(p.Download);
                    break;
                case InstallStage.Unpacking:
                    StatusText.Text = "Unpacking";
                    DetailText.Text = "";
                    break;
                case InstallStage.Checking:
                    StatusText.Text = "Checking files";
                    DetailText.Text = "";
                    break;
                case InstallStage.Finishing:
                    UpdateButton.IsEnabled = false;
                    StatusText.Text = "Finishing up";
                    DetailText.Text = "";
                    break;
            }
        }

        private string DownloadDetail(DownloadProgress d)
        {
            var seconds = _speedClock.Elapsed.TotalSeconds;
            if (seconds >= 1)
            {
                _speed = (d.Done - _speedBytes) / seconds;
                _speedBytes = d.Done;
                _speedClock.Restart();
            }
            var text = $"{Disk.Megabytes(d.Done).Replace(" MB", "")} of {Disk.Megabytes(d.Total)}";
            return _speed > 0 ? text + $", {_speed / (1024 * 1024):0.0} MB/s" : text;
        }

        private async Task WaitForGameToCloseAsync(CancellationToken cancel)
        {
            var logged = false;
            while (_installer.IsGameRunning())
            {
                if (!logged)
                {
                    Log.Info("Arcana is running from the install folder; the update waits for it to close.");
                    logged = true;
                }
                SetStatus("Waiting for Arcana to close", "The update starts when you close the game.");
                await Task.Delay(2000, cancel);
            }
        }

        private bool ConfirmRepairAfterQuarantine()
        {
            Log.Warn("Arcana.exe is missing from the install; asking to repair.");
            return MessageBox.Show(this,
                "Arcana.exe is missing from the game folder. Windows Security or another antivirus may have removed it.\n\n" +
                "Arcana Launcher will download it again. If it disappears again, allow Arcana in your antivirus, or ask for help at " +
                LauncherInfo.SupportUrl + ".",
                "Repair Arcana", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        }

        private void StartGame()
        {
            var installed = _installer.ReadInstalled();
            SetStatus("Starting Arcana...", "");
            try
            {
                using (var game = Process.Start(new ProcessStartInfo(installed.ExePath)
                {
                    WorkingDirectory = _installer.InstallDir,
                    UseShellExecute = true,
                }))
                    Log.Info($"Started {installed.ExePath} build {installed.Build} (pid {game?.Id}).");
                Close();
            }
            catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is InvalidOperationException)
            {
                Log.Error("Could not start the game.", ex);
                ShowError("Could not start Arcana", "Windows couldn't start Arcana: " + ex.Message +
                    "\n\nIf your antivirus blocked it, allow Arcana and try again.");
            }
        }

        private async void Verify_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            BeginJob(cancellable: true);
            var cancel = _work.Token;
            string finalStatus = null, finalDetail = null;
            try
            {
                var inspection = _installer.Inspect();
                if (inspection.Client == null) return;
                SetStatus("Checking files", "");
                var bad = await _installer.VerifyAsync(inspection.Client, _served,
                    new Progress<double>(percent => UpdateProgress.Value = percent), cancel);
                if (bad != null && bad.Count == 0)
                {
                    Log.Info($"Verify: all {inspection.Client.Manifest?.Count ?? _served?.Manifest?.Count ?? 0} files match.");
                    finalStatus = "All files are OK";
                    finalDetail = "Every game file matches the release.";
                    return;
                }

                Log.Warn(bad == null
                    ? "Verify: this install has no file list (made by an older launcher); reinstalling to get one."
                    : $"Verify: {bad.Count} damaged: {string.Join(", ", bad.Take(10))}");
                _served = await _installer.FetchMetadataAsync(cancel, new Progress<string>(note => DetailText.Text = note));
                await WaitForGameToCloseAsync(cancel);
                await InstallAsync("Repairing", cancel);
                finalStatus = "Repaired";
                finalDetail = bad == null ? "The game files were replaced with a checked copy."
                    : $"{bad.Count} damaged file{(bad.Count == 1 ? " was" : "s were")} replaced.";
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                finalStatus = "Check cancelled";
            }
            catch (LauncherException ex)
            {
                Log.Warn($"{ex.Title}: {ex.Message}", ex.InnerException);
                finalStatus = ex.Title;
                ShowError(ex.Title, ex.Message);
            }
            finally
            {
                EndJob();
                ShowIdle(finalStatus, finalDetail);
            }
        }

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var answer = MessageBox.Show(this,
                "Remove the Arcana game files from this PC?\n\n" +
                "Arcana Launcher stays, so you can install the game again. To remove the launcher too, use Apps in Windows Settings.",
                "Uninstall Arcana", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                _installer.Uninstall();
                ShowIdle("Game files removed", "Click Install to get Arcana again.");
            }
            catch (LauncherException ex)
            {
                Log.Warn($"{ex.Title}: {ex.Message}");
                ShowError(ex.Title, ex.Message);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
            OpenInExplorer(Directory.Exists(_installer.InstallDir) ? _installer.InstallDir : LauncherInfo.ExeDir);

        private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenInExplorer(LauncherInfo.LogsDir);

        private void OpenInExplorer(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn($"Could not open {folder}.", ex);
                ShowError("Could not open the folder", ex.Message);
            }
        }

        private void ShowError(string title, string message) =>
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        // ---- Launcher self-update -------------------------------------------------------------------------------

        /// <summary>True when the launcher is restarting into a new version (this window then closes).</summary>
        private async Task<bool> UpdateLauncherAsync()
        {
            if (_selfUpdate == null || _launcherUpdateFailed) return false;
            BeginJob(cancellable: false);
            try
            {
                var release = await _selfUpdate.CheckAsync(CancellationToken.None);
                if (release == null) return false;

                SetStatus("Updating Arcana Launcher", $"Version {release.Version}");
                _speedClock.Restart();
                _speedBytes = 0;
                _speed = 0;
                var staged = await _selfUpdate.DownloadAsync(release, new Progress<DownloadProgress>(p =>
                {
                    UpdateProgress.Value = 100 * p.Fraction;
                    DetailText.Text = p.Note ?? DownloadDetail(p);
                }), CancellationToken.None);
                return await RestartIntoAsync(staged, release);
            }
            catch (LauncherException ex)
            {
                // Not fatal: this version still plays. Next start tries again.
                Log.Warn($"Launcher update skipped: {ex.Message}", ex.InnerException);
                _launcherUpdateFailed = true;
                return false;
            }
            finally
            {
                if (_busy) EndJob();
                if (IsLoaded) ShowIdle();
            }
        }

        /// <summary>
        /// Swaps the new exe in, starts it, and waits for it to say its window is up. If it doesn't, the old exe goes back
        /// and this launcher carries on, so a broken release can't leave players without a launcher.
        /// </summary>
        private async Task<bool> RestartIntoAsync(string staged, LauncherRelease release)
        {
            var exe = LauncherInfo.ExePath;
            LauncherSelfUpdate.Swap(exe, staged);
            var app = (App)Application.Current;
            using (var started = new EventWaitHandle(false, EventResetMode.ManualReset,
                       LauncherSelfUpdate.StartedEventName(Environment.ProcessId)))
            {
                app.ReleaseInstance();
                Process process = null;
                try
                {
                    var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = LauncherInfo.ExeDir };
                    foreach (var argument in _channel.Arguments()) start.ArgumentList.Add(argument);
                    start.ArgumentList.Add("--updated-from-pid");
                    start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    process = Process.Start(start);
                    SetStatus("Updating Arcana Launcher", "Starting the new version...");
                    Log.Info($"Started launcher {release.Version} (pid {process?.Id}); waiting for its window.");

                    var signalled = Task.Run(() => started.WaitOne(LauncherSelfUpdate.StartTimeout));
                    var exited = process?.WaitForExitAsync() ?? Task.CompletedTask;
                    await Task.WhenAny(signalled, exited);
                    if (signalled.IsCompleted && signalled.Result)
                    {
                        Log.Info("The new launcher is up; closing this one.");
                        Close();
                        return true;
                    }

                    Log.Error(process != null && process.HasExited
                        ? $"The new launcher exited with code {process.ExitCode} before opening its window."
                        : "The new launcher did not open its window in time.");
                    if (process != null && !process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(10000);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is InvalidOperationException)
                {
                    Log.Error("Could not start the new launcher.", ex);
                }
                finally
                {
                    process?.Dispose();
                }

                LauncherSelfUpdate.Revert(exe);
                app.AcquireInstance(TimeSpan.FromSeconds(5));
                _launcherUpdateFailed = true;
                ShowError("Launcher update failed",
                    "Arcana Launcher couldn't start its new version, so it kept the one you have. You can still play.\n\n" +
                    "The details are in the launcher log (Open logs).");
                return false;
            }
        }

        // ---- Install banner -------------------------------------------------------------------------------------

        private void RefreshBanner()
        {
            if (_adoptedFrom != null && LauncherInfo.RunsFromInstall())
            {
                ShowBanner(BannerKind.Adopted, "Arcana Launcher is installed",
                    "It runs from its own folder now. You can delete the old copy.", "Show Old Copy");
                return;
            }
            if (_options.ReleasesUrl == null || LauncherInfo.RunsFromInstall())
            {
                ShowBanner(BannerKind.None, null, null, null);
                return;
            }
            var installed = LauncherInfo.InstalledLocation();
            if (installed != null && File.Exists(Path.Combine(installed, LauncherInfo.ExeName)))
                ShowBanner(BannerKind.OpenInstalled, "Arcana Launcher is installed on this PC",
                    "This is another copy. Use the one in the Start menu.", "Open Installed");
            else
                ShowBanner(BannerKind.Install, "Install Arcana Launcher",
                    "Its own folder and a Start menu shortcut. Your game comes along.", "Install");
        }

        private void ShowBanner(BannerKind kind, string title, string text, string button)
        {
            _banner = kind;
            Banner.Visibility = kind == BannerKind.None ? Visibility.Collapsed : Visibility.Visible;
            BannerTitle.Text = title ?? "";
            BannerText.Text = text ?? "";
            BannerButton.Content = button ?? "";
        }

        private async void Banner_Click(object sender, RoutedEventArgs e)
        {
            switch (_banner)
            {
                case BannerKind.Adopted:
                    OpenInExplorer(_adoptedFrom);
                    ShowBanner(BannerKind.None, null, null, null);
                    break;
                case BannerKind.OpenInstalled:
                    StartOtherLauncher(Path.Combine(LauncherInfo.InstalledLocation(), LauncherInfo.ExeName), null);
                    break;
                case BannerKind.Install:
                    await InstallLauncherAsync();
                    break;
            }
        }

        /// <summary>Downloads the installer, runs it quietly with this folder to adopt, and closes.</summary>
        private async Task InstallLauncherAsync()
        {
            if (_busy || _selfUpdate == null) return;
            BeginJob(cancellable: false);
            try
            {
                SetStatus("Downloading the installer", "");
                var release = await _selfUpdate.FetchLatestAsync(CancellationToken.None);
                if (release == null || !release.HasSetup)
                {
                    ShowError("Installer not available", "The installer isn't published yet. Try again later; this copy still plays.");
                    return;
                }
                var setup = await _selfUpdate.DownloadSetupAsync(release, LauncherInfo.DownloadsDir,
                    new Progress<DownloadProgress>(p => UpdateProgress.Value = 100 * p.Fraction), CancellationToken.None);
                var folder = Path.TrimEndingDirectorySeparator(LauncherInfo.ExeDir);
                Log.Info($"Running {setup} to install the launcher and adopt {folder}.");
                StartOtherLauncher(setup, new[] { "/SILENT", "/NORESTART", "/ADOPT=" + folder });
            }
            catch (LauncherException ex)
            {
                Log.Warn($"Installer download failed: {ex.Message}", ex.InnerException);
                ShowError(ex.Title, ex.Message);
            }
            finally
            {
                if (_busy) EndJob();
                if (IsLoaded) ShowIdle();
            }
        }

        /// <summary>Starts another launcher or the installer after letting go of the mutex, then closes this one.</summary>
        private void StartOtherLauncher(string exe, string[] arguments)
        {
            var app = (App)Application.Current;
            app.ReleaseInstance();
            try
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
                foreach (var argument in arguments ?? _channel.Arguments()) start.ArgumentList.Add(argument);
                Process.Start(start)?.Dispose();
                Close();
            }
            catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is InvalidOperationException)
            {
                Log.Error($"Could not start {exe}.", ex);
                app.AcquireInstance(TimeSpan.FromSeconds(5));
                ShowError("Could not start it", ex.Message);
            }
        }

        // ---- Window ---------------------------------------------------------------------------------------------

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        /// <summary>
        /// A dark title bar to match the dark window (DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 2004 and later,
        /// 19 before). Windows still paints the accent colour instead when the player chose "accent colour on title bars".
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var on = 1;
            if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
        }

        /// <summary>A second launcher start lands here: show this window instead.</summary>
        internal void BringToFront()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // The swap is a few renames; closing in the middle could leave the game folder half moved.
            if (_installer.IsSwapping)
            {
                e.Cancel = true;
                return;
            }
            _work?.Cancel();
            _pollTimer.Stop();
            _backgroundTimer.Stop();
        }

        private void FadeToNextBackground()
        {
            _backgroundIndex = (_backgroundIndex + 1) % _backgrounds.Length;
            var active = _isBackgroundAActive ? BackgroundImageA : BackgroundImageB;
            var incoming = _isBackgroundAActive ? BackgroundImageB : BackgroundImageA;
            incoming.Source = new BitmapImage(new Uri(_backgrounds[_backgroundIndex], UriKind.Relative));
            incoming.Opacity = 0;

            var fadeDuration = TimeSpan.FromSeconds(2.5);
            var fadeIn = new DoubleAnimation(0, 1, fadeDuration);
            var fadeOut = new DoubleAnimation(1, 0, fadeDuration);
            fadeIn.Completed += (_, _) =>
            {
                active.Opacity = 0;
                incoming.Opacity = 1;
                _isBackgroundAActive = !_isBackgroundAActive;
            };
            incoming.BeginAnimation(OpacityProperty, fadeIn);
            active.BeginAnimation(OpacityProperty, fadeOut);
        }

#if DEBUG
        // ---- Preview states (debug builds only): --preview <state> ---------------------------------------------

        private void Preview(string state)
        {
            VersionText.Text = "Arcana v9.1.3 (b46063466648)";
            SetServerStatus(true, 41);
            switch (state)
            {
                case "install":
                    VersionText.Text = "Arcana (not installed)";
                    SetStatus("Install required", "app.tidansrealm.com");
                    SetPlay("Install", "DownloadButton");
                    VerifyButton.Visibility = UninstallButton.Visibility = Visibility.Collapsed;
                    UpdateButton.IsEnabled = false;
                    break;
                case "ready":
                    SetStatus("Ready to play", "app.tidansrealm.com", Green);
                    UpdateProgress.Value = 100;
                    SetPlay("Play", "SmallPlayButton");
                    UpdateButton.IsEnabled = false;
                    break;
                case "update":
                    SetStatus("Update available", "Arcana v9.1.4 is out. Play updates it first.");
                    UpdateProgress.Value = 100;
                    SetPlay("Play", "SmallPlayButton");
                    UpdateButton.Content = "Update Available";
                    break;
                case "downloading":
                    BeginJob(true);
                    SetStatus("Updating Arcana", "9.4 of 17.6 MB, 3.2 MB/s");
                    UpdateProgress.Value = 45;
                    SetPlay("Play", "SmallPlayButton");
                    break;
                case "retrying":
                    BeginJob(true);
                    SetStatus("Updating Arcana", "Server busy. Trying again in 5 s.");
                    UpdateProgress.Value = 30;
                    SetPlay("Play", "SmallPlayButton");
                    break;
                case "waiting":
                    BeginJob(true);
                    SetStatus("Waiting for Arcana to close", "The update starts when you close the game.");
                    SetPlay("Play", "SmallPlayButton");
                    break;
                case "missing":
                    SetStatus("Arcana.exe is missing", "Windows Security may have removed it. Click Repair.", Amber);
                    SetPlay("Repair", "DownloadButton");
                    VerifyButton.Visibility = Visibility.Collapsed;
                    UpdateButton.IsEnabled = false;
                    break;
                case "launcher-update":
                    BeginJob(false);
                    SetStatus("Updating Arcana Launcher", "31.2 of 63.4 MB, 6.0 MB/s");
                    UpdateProgress.Value = 49;
                    SetPlay("Play", "SmallPlayButton");
                    break;
                case "banner-install":
                    SetStatus("Ready to play", "app.tidansrealm.com", Green);
                    UpdateProgress.Value = 100;
                    SetPlay("Play", "SmallPlayButton");
                    UpdateButton.IsEnabled = false;
                    ShowBanner(BannerKind.Install, "Install Arcana Launcher",
                        "Its own folder and a Start menu shortcut. Your game comes along.", "Install");
                    break;
                case "banner-adopted":
                    SetStatus("Ready to play", "app.tidansrealm.com", Green);
                    UpdateProgress.Value = 100;
                    SetPlay("Play", "SmallPlayButton");
                    UpdateButton.IsEnabled = false;
                    ShowBanner(BannerKind.Adopted, "Arcana Launcher is installed",
                        "It runs from its own folder now. You can delete the old copy.", "Show Old Copy");
                    break;
                case "verified":
                    SetStatus("All files are OK", "Every game file matches the release.");
                    UpdateProgress.Value = 100;
                    SetPlay("Play", "SmallPlayButton");
                    UpdateButton.IsEnabled = false;
                    break;
            }
        }
#endif
    }
}
