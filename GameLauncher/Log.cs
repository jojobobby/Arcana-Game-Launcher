using System;
using System.IO;
using System.Text.RegularExpressions;

namespace GameLauncher
{
    /// <summary>
    /// The launcher's log: %LocalAppData%\Arcana Launcher\logs\launcher.log, rolled at 1 MB with two older files kept
    /// (launcher.1.log, launcher.2.log). One line per step, so a player's "it won't update" comes with what happened.
    /// The Windows user name and profile folders are replaced the way the game's own log does it (ClientLog.scrubWith).
    /// Logging never throws: a log that can't be written must not stop the launcher.
    /// </summary>
    internal static class Log
    {
        public const string FileName = "launcher.log";
        public const long MaxBytes = 1024 * 1024;
        public const int Kept = 3;

        private static readonly object Gate = new object();
        private static string _path;

        public static string Directory { get; private set; }

        public static void Init(string directory)
        {
            lock (Gate)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(directory);
                    Directory = directory;
                    _path = Path.Combine(directory, FileName);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _path = null;
                }
            }
        }

        public static void Info(string message) => Write("INFO ", message, null);
        public static void Warn(string message, Exception ex = null) => Write("WARN ", message, ex);
        public static void Error(string message, Exception ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception ex)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + message;
            if (ex != null)
                line += level == "ERROR" ? Environment.NewLine + ex : " (" + ex.GetType().Name + ": " + ex.Message + ")";
            line = Scrub(line);
            lock (Gate)
            {
                if (_path == null) return;
                try
                {
                    Roll();
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch (Exception writeFailure) when (writeFailure is IOException || writeFailure is UnauthorizedAccessException)
                {
                }
            }
        }

        // launcher.log -> launcher.1.log -> launcher.2.log -> gone.
        private static void Roll()
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes) return;
            for (var i = Kept - 1; i >= 1; i--)
            {
                var older = Rolled(i);
                if (i == Kept - 1) File.Delete(older);
                var newer = i == 1 ? _path : Rolled(i - 1);
                if (File.Exists(newer)) File.Move(newer, older, overwrite: true);
            }
        }

        private static string Rolled(int index) =>
            Path.Combine(Directory, Path.GetFileNameWithoutExtension(FileName) + "." + index + Path.GetExtension(FileName));

        private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly string UserName = Environment.UserName;

        public static string Scrub(string text) => Scrub(text, LocalAppData, UserProfile, UserName);

        /// <summary>
        /// <paramref name="text"/> with the folders <paramref name="localAppData"/> and <paramref name="userProfile"/> (either
        /// slash, any case) and the word <paramref name="userName"/> replaced by %LOCALAPPDATA%, %USERPROFILE% and %USERNAME%.
        /// LocalAppData goes first because it sits inside the profile. A name under 3 letters would match ordinary words.
        /// </summary>
        public static string Scrub(string text, string localAppData, string userProfile, string userName)
        {
            if (string.IsNullOrEmpty(text)) return text;
            foreach (var (folder, token) in new[] { (localAppData, "%LOCALAPPDATA%"), (userProfile, "%USERPROFILE%") })
            {
                if (string.IsNullOrEmpty(folder) || folder.Length < 3) continue;
                foreach (var form in new[] { folder, folder.Replace('\\', '/') })
                    text = Regex.Replace(text, Regex.Escape(form), token.Replace("$", "$$"), RegexOptions.IgnoreCase);
            }
            if (!string.IsNullOrEmpty(userName) && userName.Length >= 3)
                text = Regex.Replace(text, "(?<![A-Za-z0-9])" + Regex.Escape(userName) + "(?![A-Za-z0-9])", "%USERNAME%",
                    RegexOptions.IgnoreCase);
            return text;
        }
    }
}
