using System;
using System.IO;
using System.Text.Json;

namespace GameLauncher
{
    /// <summary>
    /// What the launcher remembers between runs: %LocalAppData%\Arcana Launcher\settings.json. Today that is the server
    /// the player picked (Live or Development). A missing or damaged file means the defaults, never a crash, and a save
    /// is written beside the file and renamed over it, so a crash half way never leaves a broken file.
    /// </summary>
    internal sealed class LauncherSettings
    {
        public const string FileName = "settings.json";

        /// <summary>A channel name ("production" or "development"), or null for the default.</summary>
        public string Channel { get; set; }

        public static string DefaultPath => Path.Combine(LauncherInfo.DataDir, FileName);

        public static LauncherSettings Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new LauncherSettings();
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    var settings = new LauncherSettings();
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("channel", out var channel) && channel.ValueKind == JsonValueKind.String)
                        settings.Channel = channel.GetString();
                    return settings;
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn("Launcher settings unreadable; using the defaults.", ex);
                return new LauncherSettings();
            }
        }

        public void Save(string path)
        {
            var temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temporary, JsonSerializer.Serialize(new { channel = Channel }));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Not fatal: the choice still applies to this run.
                Log.Warn("Could not save the launcher settings.", ex);
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException) { }
            }
        }
    }
}
