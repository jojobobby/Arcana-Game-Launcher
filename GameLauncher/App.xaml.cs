using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GameLauncher
{
    /// <summary>
    /// Start-up: the log, the crash handlers, one launcher per session, then the window. Every failure that reaches here
    /// is written to the log before the player sees anything.
    /// </summary>
    public partial class App : Application
    {
        private SingleInstance _instance;
        private bool _showingError;

        /// <summary>
        /// One HttpClient for the whole launcher. No overall timeout: each request carries its own (RetryPolicy), and a
        /// download has a stall timer instead, so a slow connection is never cut off at a fixed time.
        /// </summary>
        internal static HttpClient Http { get; } = CreateHttp();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Log.Init(LauncherInfo.LogsDir);
            DispatcherUnhandledException += OnUnhandledUiError;
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                Log.Error("Unhandled error; the launcher is closing.", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Log.Warn("A background task failed and nothing was waiting for it.", args.Exception);
                args.SetObserved();
            };

            var options = LaunchOptions.Parse(e.Args, Environment.GetEnvironmentVariable, LauncherInfo.IsReleaseBuild);
            Log.Info($"{LauncherInfo.Name} {LauncherInfo.Version}{(LauncherInfo.IsReleaseBuild ? "" : " (local build)")} " +
                     $"from {LauncherInfo.ExeDir} on {RuntimeInformation.OSDescription}; channel {options.Channel.Name} " +
                     $"({options.Channel.ServerUrl}); installed copy: {LauncherInfo.RunsFromInstall()}; " +
                     $"updates from: {options.ReleasesUrl ?? "nowhere (local build)"}");

#if DEBUG
            if (options.Preview != null)
            {
                ShowWindow(new MainWindow(options));
                return;
            }
#endif
            // A launcher that just updated itself waits for the old one to let go of the mutex.
            if (!AcquireInstance(options.UpdatedFromPid > 0 ? TimeSpan.FromSeconds(15) : TimeSpan.Zero))
            {
                Log.Info("Another Arcana Launcher is already open; showing that one.");
                SingleInstance.ShowFirst();
                Shutdown();
                return;
            }

            if (options.UpdatedFromPid > 0)
                LauncherSelfUpdate.CleanUpAfterUpdate(LauncherInfo.ExePath, options.UpdatedFromPid);
            else
                LauncherSelfUpdate.DeleteLeftovers(LauncherInfo.ExePath);
            LauncherSelfUpdate.RecordInstalledVersion();

            ShowWindow(new MainWindow(options));
        }

        private void ShowWindow(MainWindow window)
        {
            MainWindow = window;
            window.Closed += (sender, args) => Shutdown();
            window.Show();
        }

        /// <summary>Takes the one-launcher mutex (again, after a failed self-update). False when another launcher has it.</summary>
        internal bool AcquireInstance(TimeSpan wait)
        {
            if (_instance != null) return true;
            _instance = SingleInstance.TryAcquire(wait);
            if (_instance == null) return false;
            _instance.ListenForSecondStart(() => Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.BringToFront())));
            return true;
        }

        /// <summary>
        /// Lets go of the mutex so the process about to start (a new launcher, or the installer, whose AppMutex check
        /// looks for it) does not see this one as running.
        /// </summary>
        internal void ReleaseInstance()
        {
            _instance?.Dispose();
            _instance = null;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Info("Closing.");
            ReleaseInstance();
            base.OnExit(e);
        }

        // A bug in a click handler must not take the launcher down: log it, say so once, carry on.
        private void OnUnhandledUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("Unexpected error in the launcher window.", e.Exception);
            e.Handled = true;
            if (_showingError) return;
            _showingError = true;
            try
            {
                MessageBox.Show(
                    "Something went wrong in Arcana Launcher: " + e.Exception.Message + "\n\n" +
                    "The details are in the launcher log (Open logs). If it keeps happening, send it to " + LauncherInfo.SupportUrl + ".",
                    LauncherInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _showingError = false;
            }
        }

        private static HttpClient CreateHttp()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                // Byte ranges and hashes are of the bytes as stored: never ask for a compressed transfer.
                AutomaticDecompression = System.Net.DecompressionMethods.None,
            };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherInfo.UserAgent);
            return http;
        }
    }
}
