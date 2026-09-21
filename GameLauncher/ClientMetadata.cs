using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GameLauncher
{
    /// <summary>
    /// The server's <c>/client/metadata</c>: which client it was built with. Written by
    /// tools/build_client_release.ps1 in the Arcana repo. Everything here comes off the
    /// network, so it is validated before the launcher downloads or runs anything.
    /// </summary>
    internal sealed class ClientMetadata
    {
        public const long MaxZipBytes = 256L * 1024 * 1024;

        /// <summary>Git tree of the client source; two installs with the same build run the same client.</summary>
        public string Build { get; private set; }
        public string Version { get; private set; }
        public string Sha256 { get; private set; }
        public long FileSize { get; private set; }
        public string Executable { get; private set; }

        public static ClientMetadata Parse(string json, LauncherChannel channel)
        {
            var trimmed = json?.TrimStart() ?? "";
            if (!trimmed.StartsWith("{"))
                throw Invalid("The game server didn't return version info. It may be restarting or behind on its deploy.", trimmed);

            try
            {
                using (var doc = JsonDocument.Parse(trimmed))
                {
                    var root = doc.RootElement;
                    var metadata = new ClientMetadata
                    {
                        Build = Text(root, "build"),
                        Version = Text(root, "version"),
                        Sha256 = Text(root, "sha256").ToLowerInvariant(),
                        Executable = Text(root, "executable"),
                        FileSize = root.TryGetProperty("file_size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                    };

                    var served = Text(root, "channel");
                    if (!string.Equals(served, channel.Name, StringComparison.OrdinalIgnoreCase))
                        throw Invalid($"This is a {channel.Name} launcher but the server offered a '{served}' client.", "");
                    if (metadata.Build.Length == 0)
                        throw Invalid("The server's version info has no build id.", trimmed);
                    if (metadata.Sha256.Length != 64 || !metadata.Sha256.All(Uri.IsHexDigit))
                        throw Invalid("The server's version info has no valid SHA-256.", trimmed);
                    if (metadata.FileSize <= 0 || metadata.FileSize > MaxZipBytes)
                        throw Invalid("The server's version info has an impossible download size.", trimmed);
                    // The launcher starts <install folder>/<executable>; it must not be able to point anywhere else.
                    if (metadata.Executable != Path.GetFileName(metadata.Executable) ||
                        metadata.Executable.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                        !metadata.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        throw Invalid("The server's version info names an executable the launcher will not run.", trimmed);

                    return metadata;
                }
            }
            catch (JsonException ex)
            {
                throw Invalid("Couldn't read the version info from the server (" + ex.Message + ").", trimmed);
            }
        }

        private static string Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString().Trim()
                : "";

        private static LauncherException Invalid(string message, string serverSaid)
        {
            if (serverSaid.Length > 200)
                serverSaid = serverSaid.Substring(0, 200) + "...";
            return new LauncherException("Server unavailable",
                serverSaid.Length == 0 ? message : message + "\n\nServer said:\n" + serverSaid);
        }
    }
}
