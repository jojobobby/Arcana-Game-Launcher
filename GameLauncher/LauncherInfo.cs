using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace GameLauncher
{
    /// <summary>
    /// Who the launcher is and where its files live. The names here are what Windows shows (the installer in
    /// installer/ArcanaLauncher.iss uses the same AppId, mutex and AppUserModelID).
    ///
    ///   %LocalAppData%\Programs\Arcana Launcher\   the installed launcher and its game folders (Arcana\, Arcana-development\)
    ///   %LocalAppData%\Arcana Launcher\logs\       launcher.log
    ///   %LocalAppData%\Arcana Launcher\downloads\  game zips by sha256 (resumable .part files, the last two kept)
    ///
    /// The game keeps its own data under %LocalAppData%\Arcana and %AppData%\Arcana\Arcana; those names are the game's.
    /// </summary>
    internal static class LauncherInfo
    {
        public const string Name = "Arcana Launcher";
        public const string Publisher = "Tidan Games LLC";
        public const string ExeName = "ArcanaLauncher.exe";
        public const string AppUserModelId = "TidanGames.ArcanaLauncher";
        /// <summary>Session-local: one launcher per Windows session. installer/ArcanaLauncher.iss AppMutex is the same name.</summary>
        public const string MutexName = "TidanGames.ArcanaLauncher";
        public const string ShowEventName = "TidanGames.ArcanaLauncher.Show";
        /// <summary>The Inno Setup AppId. Never change it: Windows finds the existing install (and its uninstaller) by it.</summary>
        public const string InnoAppId = "{D5E69C41-93F1-499D-8514-ABD3329B6CC2}";
        public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoAppId + "_is1";
        public const string SupportUrl = "https://support.tidangames.com";

        /// <summary>"1.0.12" from the file version CI stamps (1.0.&lt;run&gt;.0); "1.0.0" for a local build.</summary>
        public static string Version { get; } = ReadVersion();

        /// <summary>The four-part file version ("1.0.12.0"): what the installer writes as the Apps list version.</summary>
        public static string FileVersion { get; } =
            typeof(LauncherInfo).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "1.0.0.0";

        /// <summary>A CI release build (-p:ArcanaReleaseBuild=true). Only these update themselves from GitHub.</summary>
        public static bool IsReleaseBuild { get; } = string.Equals(
            typeof(LauncherInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "ArcanaReleaseBuild")?.Value, "true", StringComparison.OrdinalIgnoreCase);

        public static string UserAgent => "ArcanaLauncher/" + Version + (IsReleaseBuild ? "" : "-dev");

        public static string ExePath => Environment.ProcessPath;
        public static string ExeDir => AppContext.BaseDirectory;

        public static string DataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
        public static string LogsDir => Path.Combine(DataDir, "logs");
        public static string DownloadsDir => Path.Combine(DataDir, "downloads");

        /// <summary>Where the installer put the launcher (its uninstall key's InstallLocation), or null when it isn't installed.</summary>
        public static string InstalledLocation()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath))
                {
                    var location = key?.GetValue("InstallLocation") as string;
                    return string.IsNullOrWhiteSpace(location) ? null : location;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return null;
            }
        }

        /// <summary>True when this exe is the installed copy (not one run from Downloads or elsewhere).</summary>
        public static bool RunsFromInstall() => SameFolder(InstalledLocation(), ExeDir);

        public static bool SameFolder(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        private static string ReadVersion()
        {
            var file = typeof(LauncherInfo).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "1.0.0.0";
            return System.Version.TryParse(file, out var parsed)
                ? parsed.Revision > 0 ? parsed.ToString(4) : parsed.ToString(3)
                : file;
        }
    }
}
