using System;

namespace GameLauncher
{
    /// <summary>
    /// Which server this launcher belongs to. A client is compiled for exactly one
    /// environment, so a channel is a server to ask, a folder to install into and a
    /// name the downloaded client must carry. Players always get production;
    /// testers start the launcher with <c>--channel development</c> (or <c>--dev</c>).
    /// Hosts and ports mirror the port map in the Arcana repo's CLAUDE.md.
    /// </summary>
    internal sealed class LauncherChannel
    {
        public static readonly LauncherChannel Production = new LauncherChannel(
            "production", "Production", "https://app.tidansrealm.com", "app.tidansrealm.com", 8887, "Arcana");

        public static readonly LauncherChannel Development = new LauncherChannel(
            "development", "Development", "https://devapp.tidansrealm.com", "app.tidansrealm.com", 9998, "Arcana-development");

        /// <summary>Must equal <c>channel</c> in the server's client-metadata.json.</summary>
        public string Name { get; }
        public string Label { get; }
        public string ServerUrl { get; }
        public string GameHost { get; }
        public int GamePort { get; }
        public string InstallDirName { get; }

        private LauncherChannel(string name, string label, string serverUrl, string gameHost, int gamePort, string installDirName)
        {
            Name = name;
            Label = label;
            ServerUrl = serverUrl;
            GameHost = gameHost;
            GamePort = gamePort;
            InstallDirName = installDirName;
        }

        public static LauncherChannel FromArgs(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--dev", StringComparison.OrdinalIgnoreCase))
                    return Development;

                string value = null;
                if (string.Equals(arg, "--channel", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    value = args[i + 1];
                else if (arg.StartsWith("--channel=", StringComparison.OrdinalIgnoreCase))
                    value = arg.Substring("--channel=".Length);

                if (string.Equals(value, Development.Name, StringComparison.OrdinalIgnoreCase))
                    return Development;
            }

            return Production;
        }
    }
}
