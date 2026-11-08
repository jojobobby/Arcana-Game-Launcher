using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GameLauncher
{
    /// <summary>One file an install must hold: its path inside the install folder ('/' separated), size and SHA-256.</summary>
    internal sealed class ManifestEntry
    {
        public ManifestEntry(string path, long size, string sha256)
        {
            Path = path;
            Size = size;
            Sha256 = sha256;
        }

        public string Path { get; }
        public long Size { get; }
        public string Sha256 { get; }

        /// <summary>Where this entry lives under <paramref name="folder"/>.</summary>
        public string Under(string folder) => System.IO.Path.Combine(folder, Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

        /// <summary>
        /// A safe relative path, or null: no rooted path, drive, '..', empty segment, or character Windows refuses.
        /// The manifest comes off the network and the launcher deletes and writes by it.
        /// </summary>
        public static string SafePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var segments = path.Replace('\\', '/').Split('/');
            foreach (var segment in segments)
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.Trim() != segment ||
                    segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                    return null;
            }
            return string.Join("/", segments);
        }
    }

    /// <summary>
    /// The server's <c>/client/metadata</c>: which client it was built with. Written by tools/build_client_release.ps1 in
    /// the Arcana repo. Everything here comes off the network, so it is validated before the launcher downloads or runs
    /// anything. <c>manifest</c> and <c>unpacked_size</c> are newer; a server without them still installs.
    /// </summary>
    internal sealed class ClientMetadata
    {
        /// <summary>The same cap as the server's (ClientReleaseStore.MaxZipBytes in the Arcana repo).</summary>
        public const long MaxZipBytes = 64L * 1024 * 1024;
        public const long MaxUnpackedBytes = 1024L * 1024 * 1024;
        public const int MaxManifestEntries = 10000;

        /// <summary>Git tree of the client source; two installs with the same build run the same client.</summary>
        public string Build { get; private set; }
        public string Version { get; private set; }
        public string Sha256 { get; private set; }
        public long FileSize { get; private set; }
        public string Executable { get; private set; }
        /// <summary>Every file of the install, or null for a server from before the manifest.</summary>
        public IReadOnlyList<ManifestEntry> Manifest { get; private set; }
        /// <summary>Bytes the install takes on disk; an estimate from the zip size when the server does not say.</summary>
        public long UnpackedSize { get; private set; }
        /// <summary>The ETag the server sends with the zip: an If-Range that resumes only the same bytes.</summary>
        public string DownloadETag => "\"" + Sha256 + "\"";

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
                    if (!IsSha256(metadata.Sha256))
                        throw Invalid("The server's version info has no valid SHA-256.", trimmed);
                    if (metadata.FileSize <= 0 || metadata.FileSize > MaxZipBytes)
                        throw Invalid("The server's version info has an impossible download size.", trimmed);
                    // The launcher starts <install folder>/<executable>; it must not be able to point anywhere else.
                    if (metadata.Executable != Path.GetFileName(metadata.Executable) ||
                        metadata.Executable.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                        !metadata.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        throw Invalid("The server's version info names an executable the launcher will not run.", trimmed);

                    metadata.Manifest = ReadManifest(root, metadata.Executable, trimmed);
                    var unpacked = root.TryGetProperty("unpacked_size", out var u) && u.TryGetInt64(out var ub) ? ub : 0;
                    if (unpacked < 0 || unpacked > MaxUnpackedBytes)
                        throw Invalid("The server's version info has an impossible install size.", trimmed);
                    if (metadata.Manifest != null && unpacked != metadata.Manifest.Sum(e => e.Size))
                        throw Invalid("The server's version info has a manifest that does not add up.", trimmed);
                    // Without a manifest, budget the install at three times the zip (today's client unpacks to about 3x).
                    metadata.UnpackedSize = unpacked > 0 ? unpacked : metadata.FileSize * 3;

                    return metadata;
                }
            }
            catch (JsonException ex)
            {
                throw Invalid("Couldn't read the version info from the server (" + ex.Message + ").", trimmed);
            }
        }

        private static IReadOnlyList<ManifestEntry> ReadManifest(JsonElement root, string executable, string raw)
        {
            if (!root.TryGetProperty("manifest", out var manifest) || manifest.ValueKind == JsonValueKind.Null)
                return null;
            if (manifest.ValueKind != JsonValueKind.Array || manifest.GetArrayLength() == 0 || manifest.GetArrayLength() > MaxManifestEntries)
                throw Invalid("The server's version info has an unreadable file list.", raw);

            var entries = new List<ManifestEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in manifest.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw Invalid("The server's version info has an unreadable file list.", raw);
                var path = ManifestEntry.SafePath(Text(item, "path"));
                var sha = Text(item, "sha256").ToLowerInvariant();
                var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sb) ? sb : -1;
                if (path == null || size < 0 || !IsSha256(sha) || !seen.Add(path))
                    throw Invalid("The server's version info lists a file the launcher will not install.", raw);
                entries.Add(new ManifestEntry(path, size, sha));
            }
            if (!seen.Contains(executable))
                throw Invalid($"The server's file list has no {executable}.", raw);
            return entries;
        }

        public static bool IsSha256(string value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);

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
