using System;

namespace GameLauncher
{
    /// <summary>
    /// Which server this launcher belongs to. A client is compiled for exactly one environment, so a channel is a server
    /// to ask, a folder to install into and a name the downloaded client must carry. Players always get production;
    /// testers start the launcher with <c>--channel development</c> (or <c>--dev</c>). Hosts mirror the Arcana repo's
    /// k8s/values.&lt;env&gt;.yaml ingress.appserver.host.
    /// </summary>
    internal sealed class LauncherChannel
    {
        public static readonly LauncherChannel Production = new LauncherChannel(
            "production", "Production", "https://app.tidansrealm.com", "Arcana");

        public static readonly LauncherChannel Development = new LauncherChannel(
            "development", "Development", "https://devapp.tidansrealm.com", "Arcana-development");

        /// <summary>Must equal <c>channel</c> in the server's client-metadata.json.</summary>
        public string Name { get; }
        public string Label { get; }
        public string ServerUrl { get; }
        public string InstallDirName { get; }
        public string MetadataUrl => ServerUrl + "/client/metadata";
        public string DownloadUrl => ServerUrl + "/client/download";

        private LauncherChannel(string name, string label, string serverUrl, string installDirName)
        {
            Name = name;
            Label = label;
            ServerUrl = serverUrl;
            InstallDirName = installDirName;
        }

        /// <summary>
        /// The same channel against another server (a local test server). Only a non-release build honours this
        /// (App.xaml.cs, ARCANA_LAUNCHER_SERVER_URL): a player's launcher always talks to the real host.
        /// </summary>
        public LauncherChannel WithServer(string serverUrl) =>
            new LauncherChannel(Name, Label + " (test server)", serverUrl.TrimEnd('/'), InstallDirName);

        public static LauncherChannel FromArgs(string[] args) => TryFromArgs(args, out var channel) ? channel : Production;

        /// <summary>
        /// The channel the command line names (--dev, --channel development, --channel production), if it names one.
        /// An unknown name counts as production, so a typo never lands a player on the development server.
        /// </summary>
        public static bool TryFromArgs(string[] args, out LauncherChannel channel)
        {
            channel = null;
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--dev", StringComparison.OrdinalIgnoreCase))
                {
                    channel = Development;
                    return true;
                }

                string value = null;
                if (string.Equals(arg, "--channel", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    value = args[i + 1];
                else if (arg.StartsWith("--channel=", StringComparison.OrdinalIgnoreCase))
                    value = arg.Substring("--channel=".Length);
                if (value != null)
                {
                    channel = FromName(value);
                    return true;
                }
            }
            return false;
        }

        /// <summary>"development" is Development; anything else (null, a typo, "production") is Production.</summary>
        public static LauncherChannel FromName(string name) =>
            string.Equals(name?.Trim(), Development.Name, StringComparison.OrdinalIgnoreCase) ? Development : Production;

        /// <summary>The arguments that bring this channel back after the launcher restarts itself.</summary>
        public string[] Arguments() => Name == Development.Name ? new[] { "--channel", Name } : Array.Empty<string>();
    }
}
