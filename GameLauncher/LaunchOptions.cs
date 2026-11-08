using System;
using System.Globalization;

namespace GameLauncher
{
    /// <summary>
    /// The command line, the saved choice and the test overrides.
    ///   --channel development | --dev     the development server and its own game folder, for this run
    ///   --channel production              the live server, for this run
    ///   --adopt "&lt;folder&gt;"            move the game folder a launcher left in &lt;folder&gt; (the installer passes it)
    ///   --updated-from-pid &lt;pid&gt;        started by a launcher that just updated itself (LauncherSelfUpdate)
    /// Without a channel flag the launcher opens on the server picked last time (LauncherSettings), else Live.
    /// A non-release build also reads ARCANA_LAUNCHER_SERVER_URL (another game server) and ARCANA_LAUNCHER_RELEASES_URL
    /// (another place to update from); without the second it never updates itself. A release build ignores both.
    /// </summary>
    internal sealed class LaunchOptions
    {
        /// <summary>The channel this run opens on, with any test server applied.</summary>
        public LauncherChannel Channel { get; private set; }
        /// <summary>What picked the channel, for the log: "command line", "saved choice" or "default" (nothing saved yet).</summary>
        public string ChannelSource { get; private set; }
        /// <summary>ARCANA_LAUNCHER_SERVER_URL in a non-release build, else null.</summary>
        public string ServerOverride { get; private set; }
        public string AdoptFrom { get; private set; }
        public int UpdatedFromPid { get; private set; }
        /// <summary>Where launcher releases come from, or null when this build does not update itself.</summary>
        public string ReleasesUrl { get; private set; }
#if DEBUG
        /// <summary>--preview &lt;state&gt;: shows the window in one state, no network (screenshots of every state).</summary>
        public string Preview { get; private set; }
#endif

        public static LaunchOptions Parse(string[] args, Func<string, string> environment, bool releaseBuild, string savedChannel = null)
        {
            var options = new LaunchOptions();
            var fromFlag = LauncherChannel.TryFromArgs(args, out var flagged);
            var channel = fromFlag ? flagged : LauncherChannel.FromName(savedChannel);
            options.ChannelSource = fromFlag ? "command line" : string.IsNullOrWhiteSpace(savedChannel) ? "default" : "saved choice";
            for (var i = 0; i < args.Length; i++)
            {
                var next = i + 1 < args.Length ? args[i + 1] : null;
                if (string.Equals(args[i], "--adopt", StringComparison.OrdinalIgnoreCase) && next != null)
                    options.AdoptFrom = next.Trim().Trim('"');
                else if (string.Equals(args[i], "--updated-from-pid", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(next, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                    options.UpdatedFromPid = pid;
#if DEBUG
                else if (string.Equals(args[i], "--preview", StringComparison.OrdinalIgnoreCase) && next != null)
                    options.Preview = next;
#endif
            }

            if (releaseBuild)
            {
                options.ReleasesUrl = LauncherSelfUpdate.GitHubReleases;
            }
            else
            {
                var server = environment("ARCANA_LAUNCHER_SERVER_URL");
                if (!string.IsNullOrWhiteSpace(server) && Uri.TryCreate(server, UriKind.Absolute, out _))
                    options.ServerOverride = server;
                var releases = environment("ARCANA_LAUNCHER_RELEASES_URL");
                if (!string.IsNullOrWhiteSpace(releases) && Uri.TryCreate(releases, UriKind.Absolute, out _))
                    options.ReleasesUrl = releases.TrimEnd('/');
            }
            options.Channel = options.Resolve(channel);
            if (string.IsNullOrWhiteSpace(options.AdoptFrom)) options.AdoptFrom = null;
            return options;
        }

        /// <summary>The channel as this run talks to it: the test server instead of the real host when one is set.</summary>
        public LauncherChannel Resolve(LauncherChannel channel) => ServerOverride == null ? channel : channel.WithServer(ServerOverride);
    }
}
