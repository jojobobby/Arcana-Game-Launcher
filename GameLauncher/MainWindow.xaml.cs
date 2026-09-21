using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
        private readonly string rootPath;
        private readonly LauncherChannel channel;
        private readonly ClientInstaller installer;
        private readonly LauncherSelfUpdate selfUpdate;

        private LauncherState _status;
        private bool _isBusy;
        private bool _isMeasuringLatency;
        private bool _launcherUpdateAvailable;
        private bool _gameUpdateAvailable;
        private bool _isBackgroundAActive = true;
        private int _backgroundIndex;
        private readonly DispatcherTimer latencyTimer;
        private readonly DispatcherTimer backgroundTimer;
        private readonly string[] backgroundSources =
        {
            "/Assets/Title_Screen_0.png",
            "/Assets/Title_Screen_1.png",
            "/Assets/Title_Screen_2.png",
            "/Assets/Title_Screen_3.png"
        };
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

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
            // Beside the launcher exe, not the working directory: a shortcut's "Start in" must not move the install.
            rootPath = AppContext.BaseDirectory;
            channel = LauncherChannel.FromArgs(Environment.GetCommandLineArgs().Skip(1).ToArray());
            installer = new ClientInstaller(Http, channel, rootPath);
            selfUpdate = new LauncherSelfUpdate(Http, Environment.ProcessPath);
            EndpointText.Text = new Uri(channel.ServerUrl).Host;
            ChannelText.Text = channel.Label;

            latencyTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            latencyTimer.Tick += LatencyTimer_Tick;

            backgroundTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            backgroundTimer.Tick += BackgroundTimer_Tick;
        }

        private void UpdateStatusUi()
        {
            switch (_status)
            {
                case LauncherState.checking:
                    UpdateButton.Content = "Checking";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 248, 232));
                    StatusText.Text = "Checking for updates...";
                    break;
                case LauncherState.updatingLauncher:
                    UpdateButton.Content = "Updating";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 248, 232));
                    StatusText.Text = "Updating launcher...";
                    break;
                case LauncherState.ready:
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(87, 214, 132));
                    StatusText.Text = "Ready to play";
                    break;
                case LauncherState.failed:
                    UpdateButton.Content = "Retry Update";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 248, 232));
                    StatusText.Text = "Update check failed";
                    break;
                case LauncherState.downloadingGame:
                    UpdateButton.Content = "Downloading";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 248, 232));
                    StatusText.Text = "Downloading game files...";
                    break;
                case LauncherState.downloadingUpdate:
                    UpdateButton.Content = "Updating";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 248, 232));
                    StatusText.Text = "Updating game files...";
                    break;
                case LauncherState.extractingGame:
                    UpdateButton.Content = "Installing";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 248, 232));
                    StatusText.Text = "Installing game files...";
                    break;
            }

            var isGameInstalled = installer.ReadInstalled() != null;
            PlayButton.IsEnabled = !_isBusy;
            PlayButton.Content = isGameInstalled ? "Play" : "Download";
            PlayButton.Style = (Style)FindResource(isGameInstalled ? "SmallPlayButton" : "DownloadButton");
            OpenFolderButton.IsEnabled = !_isBusy;
            UninstallButton.IsEnabled = !_isBusy && isGameInstalled;
            UninstallButton.Visibility = isGameInstalled ? Visibility.Visible : Visibility.Collapsed;

            if (!_isBusy)
                ApplyUpdateButtonAvailability();
            else
                UpdateButton.IsEnabled = false;
        }

        private void ApplyUpdateButtonAvailability()
        {
            if (_launcherUpdateAvailable || _gameUpdateAvailable)
            {
                UpdateButton.Content = "Update Available";
                UpdateButton.IsEnabled = true;
                return;
            }

            UpdateButton.Content = "Update";
            UpdateButton.IsEnabled = false;
        }

        // Two players who read the same build id here are running the same client.
        private static string FormatVersionLabel(InstalledClient installed)
        {
            return installed == null
                ? "Arcana (not installed)"
                : $"Arcana v{installed.Version} ({installed.Build})";
        }

        private async Task<bool> UpdateLauncherAsync()
        {
            if (!await selfUpdate.IsUpdateAvailableAsync())
                return false;

            try
            {
                Status = LauncherState.updatingLauncher;
                UpdateProgress.Value = 0;

                var script = await selfUpdate.StageAsync(new Progress<double>(ReportProgress));
                Process.Start(new ProcessStartInfo(script)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                Close();
                return true;
            }
            catch (Exception ex)
            {
                Status = LauncherState.failed;
                MessageBox.Show($"Could not update the launcher: {ex.Message}",
                    "Launcher update failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        private async Task CheckForUpdatesAsync(bool includeLauncherUpdate)
        {
            if (_isBusy)
                return;

            _isBusy = true;
            UpdateProgress.Value = 0;
            Status = LauncherState.checking;
            SetServerStatus(isOnline: null, latencyMs: null);

            var installed = installer.ReadInstalled();
            VersionText.Text = FormatVersionLabel(installed);

            try
            {
                if (includeLauncherUpdate && await UpdateLauncherAsync())
                    return;

                var remote = await installer.FetchMetadataAsync();
                await UpdateGameLatencyAsync();

                if (installed != null && string.Equals(installed.Build, remote.Build, StringComparison.Ordinal))
                {
                    _gameUpdateAvailable = false;
                    UpdateProgress.Value = 100;
                    Status = LauncherState.ready;
                    return;
                }

                Status = installed == null ? LauncherState.downloadingGame : LauncherState.downloadingUpdate;
                await installer.InstallAsync(remote, new Progress<double>(ReportProgress));

                VersionText.Text = FormatVersionLabel(installer.ReadInstalled());
                _gameUpdateAvailable = false;
                UpdateProgress.Value = 100;
                Status = LauncherState.ready;
            }
            catch (LauncherException ex)
            {
                Status = LauncherState.failed;
                MessageBox.Show(ex.Message, ex.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show($"Error updating the game: {ex.Message}");
            }
            finally
            {
                _isBusy = false;
                UpdateStatusUi();
            }
        }

        // The installer reports 0..100; past the download the rest is unpacking.
        private void ReportProgress(double percent)
        {
            UpdateProgress.Value = Math.Max(UpdateProgress.Value, percent);
            if (percent > 90 && (_status == LauncherState.downloadingGame || _status == LauncherState.downloadingUpdate))
                Status = LauncherState.extractingGame;
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

        private async Task<long?> MeasureGameServerLatencyAsync()
        {
            var timer = Stopwatch.StartNew();
            using (var client = new TcpClient())
            {
                var connectTask = client.ConnectAsync(channel.GameHost, channel.GamePort);
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

        private async void Window_ContentRendered(object sender, EventArgs e)
        {
            latencyTimer.Start();
            backgroundTimer.Start();

            var installed = installer.ReadInstalled();
            VersionText.Text = FormatVersionLabel(installed);
            UpdateProgress.Value = installed == null ? 0 : 100;
            Status = installed != null ? LauncherState.ready : LauncherState.failed;
            if (installed == null)
                StatusText.Text = "Install required";
            await RefreshUpdateAvailabilityAsync();
        }

        private async Task RefreshUpdateAvailabilityAsync()
        {
            if (_isBusy)
                return;

            try
            {
                UpdateButton.Content = "Checking";
                UpdateButton.IsEnabled = false;

                var launcherCheck = selfUpdate.IsUpdateAvailableAsync();
                var remoteCheck = installer.FetchMetadataAsync();

                _launcherUpdateAvailable = await launcherCheck;
                var remote = await remoteCheck;

                var installed = installer.ReadInstalled();
                _gameUpdateAvailable = installed == null ||
                                       !string.Equals(installed.Build, remote.Build, StringComparison.Ordinal);

                ApplyUpdateButtonAvailability();
            }
            catch
            {
                _gameUpdateAvailable = false;
                ApplyUpdateButtonAvailability();
            }
        }

        private void BackgroundTimer_Tick(object sender, EventArgs e)
        {
            FadeToNextBackground();
        }

        private void FadeToNextBackground()
        {
            _backgroundIndex = (_backgroundIndex + 1) % backgroundSources.Length;

            var active = _isBackgroundAActive ? BackgroundImageA : BackgroundImageB;
            var incoming = _isBackgroundAActive ? BackgroundImageB : BackgroundImageA;

            incoming.Source = LoadImage(backgroundSources[_backgroundIndex]);
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

        private static ImageSource LoadImage(string source)
        {
            return new BitmapImage(new Uri(source, UriKind.Relative));
        }

        private async void LatencyTimer_Tick(object sender, EventArgs e)
        {
            await UpdateGameLatencyAsync();
        }

        private async void Play_Click(object sender, RoutedEventArgs e)
        {
            var installed = installer.ReadInstalled();
            if (installed == null)
            {
                await CheckForUpdatesAsync(includeLauncherUpdate: false);
                return;
            }

            if (Status != LauncherState.ready)
                return;

            try
            {
                var startInfo = new ProcessStartInfo(installed.ExePath)
                {
                    WorkingDirectory = installer.InstallDir,
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

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(includeLauncherUpdate: true);
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folderToOpen = Directory.Exists(installer.InstallDir) ? installer.InstallDir : rootPath;
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
                "Uninstall Arcana",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                installer.Uninstall();
            }
            catch (LauncherException ex)
            {
                MessageBox.Show(ex.Message, ex.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            VersionText.Text = FormatVersionLabel(null);
            UpdateProgress.Value = 0;
            _gameUpdateAvailable = true;
            Status = LauncherState.failed;
            StatusText.Text = "Game files removed";
            ApplyUpdateButtonAvailability();
        }
    }
}
