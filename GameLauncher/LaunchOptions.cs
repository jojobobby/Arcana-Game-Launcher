using System;
using System.Collections.Generic;
using System.Globalization;

namespace GameLauncher
{
    /// <summary>
    /// The command line and the test overrides.
    ///   --channel development | --dev     the development server and its own game folder
    ///   --adopt "&lt;folder&gt;"            move the game folder a launcher left in &lt;folder&gt; (the installer passes it)
    ///   --updated-from-pid &lt;pid&gt;        started by a launcher that just updated itself (LauncherSelfUpdate)
    /// A non-release build also reads ARCANA_LAUNCHER_SERVER_URL (another game server) and ARCANA_LAUNCHER_RELEASES_URL
    /// (another place to update from); without the second it never updates itself. A release build ignores both.
    /// </summary>
    internal sealed class LaunchOptions
    {
        public LauncherChannel Channel { get; private set; }
        public string AdoptFrom { get; private set; }
        public int UpdatedFromPid { get; private set; }
        /// <summary>Where launcher releases come from, or null when this build does not update itself.</summary>
        public string ReleasesUrl { get; private set; }
#if DEBUG
        /// <summary>--preview &lt;state&gt;: shows the window in one state, no network (screenshots of every state).</summary>
        public string Preview { get; private set; }
#endif

        public static LaunchOptions Parse(string[] args, Func<string, string> environment, bool releaseBuild)
        {
            var options = new LaunchOptions { Channel = LauncherChannel.FromArgs(args) };
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
                    options.Channel = options.Channel.WithServer(server);
                var releases = environment("ARCANA_LAUNCHER_RELEASES_URL");
                if (!string.IsNullOrWhiteSpace(releases) && Uri.TryCreate(releases, UriKind.Absolute, out _))
                    options.ReleasesUrl = releases.TrimEnd('/');
            }
            if (string.IsNullOrWhiteSpace(options.AdoptFrom)) options.AdoptFrom = null;
            return options;
        }

        /// <summary>What a restarted launcher must be given to come back the same (the channel; never the one-shot flags).</summary>
        public IReadOnlyList<string> RestartArguments() => Channel.Arguments();
    }
}
