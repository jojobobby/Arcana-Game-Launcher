using System;
using NUnit.Framework;

namespace GameLauncher.Tests
{
    public class LauncherChannelTests
    {
        [Test]
        public void PlayersGetProduction()
        {
            Assert.That(LauncherChannel.FromArgs(new string[0]), Is.SameAs(LauncherChannel.Production));
            Assert.That(LauncherChannel.Production.ServerUrl, Is.EqualTo("https://app.tidansrealm.com"));
            Assert.That(LauncherChannel.Production.MetadataUrl, Is.EqualTo("https://app.tidansrealm.com/client/metadata"));
            Assert.That(LauncherChannel.Production.DownloadUrl, Is.EqualTo("https://app.tidansrealm.com/client/download"));
            Assert.That(LauncherChannel.Production.InstallDirName, Is.EqualTo("Arcana"));
            Assert.That(LauncherChannel.Production.Arguments(), Is.Empty);
        }

        [TestCase("--channel", "development")]
        [TestCase("--channel", "DEVELOPMENT")]
        [TestCase("--channel=development", null)]
        [TestCase("--dev", null)]
        public void TestersOptIntoDevelopment(string first, string second)
        {
            var args = second == null ? new[] { first } : new[] { first, second };
            var channel = LauncherChannel.FromArgs(args);
            Assert.That(channel, Is.SameAs(LauncherChannel.Development));
            Assert.That(channel.ServerUrl, Is.EqualTo("https://devapp.tidansrealm.com"));
            Assert.That(channel.InstallDirName, Is.EqualTo("Arcana-development"));
            Assert.That(channel.Arguments(), Is.EqualTo(new[] { "--channel", "development" }), "survives a restart");
        }

        [TestCase("--channel", "staging")]
        [TestCase("--channel", null)]
        [TestCase("--fullscreen", null)]
        public void AnythingElseIsProduction(string first, string second)
        {
            var args = second == null ? new[] { first } : new[] { first, second };
            Assert.That(LauncherChannel.FromArgs(args), Is.SameAs(LauncherChannel.Production));
        }

        [Test]
        public void ATestServerKeepsTheChannelAndItsFolder()
        {
            var local = LauncherChannel.Development.WithServer("http://127.0.0.1:8123/");
            Assert.That(local.Name, Is.EqualTo("development"));
            Assert.That(local.InstallDirName, Is.EqualTo("Arcana-development"));
            Assert.That(local.MetadataUrl, Is.EqualTo("http://127.0.0.1:8123/client/metadata"));
            Assert.That(local.Label, Does.Contain("test server"));
        }
    }

    public class LaunchOptionsTests
    {
        private static string Env(string name) => name switch
        {
            "ARCANA_LAUNCHER_SERVER_URL" => "http://127.0.0.1:8123",
            "ARCANA_LAUNCHER_RELEASES_URL" => "http://127.0.0.1:8123/releases/",
            _ => null,
        };

        [Test]
        public void AReleaseBuildAlwaysTalksToTheRealHosts()
        {
            var options = LaunchOptions.Parse(new string[0], Env, releaseBuild: true);
            Assert.That(options.Channel.ServerUrl, Is.EqualTo("https://app.tidansrealm.com"));
            Assert.That(options.ReleasesUrl, Is.EqualTo(LauncherSelfUpdate.GitHubReleases));
        }

        [Test]
        public void ALocalBuildTakesTestServersAndOtherwiseNeverUpdatesItself()
        {
            var overridden = LaunchOptions.Parse(new string[0], Env, releaseBuild: false);
            Assert.That(overridden.Channel.ServerUrl, Is.EqualTo("http://127.0.0.1:8123"));
            Assert.That(overridden.ReleasesUrl, Is.EqualTo("http://127.0.0.1:8123/releases"));

            var plain = LaunchOptions.Parse(new string[0], name => null, releaseBuild: false);
            Assert.That(plain.ReleasesUrl, Is.Null);
            Assert.That(plain.Channel, Is.SameAs(LauncherChannel.Production));
        }

        [Test]
        public void ReadsTheInstallerAndUpdateFlags()
        {
            var options = LaunchOptions.Parse(new[] { "--adopt", "C:\\Users\\me\\Downloads", "--updated-from-pid", "4242", "--dev" },
                name => null, releaseBuild: true);
            Assert.That(options.AdoptFrom, Is.EqualTo("C:\\Users\\me\\Downloads"));
            Assert.That(options.UpdatedFromPid, Is.EqualTo(4242));
            Assert.That(options.Channel, Is.SameAs(LauncherChannel.Development));
            Assert.That(options.RestartArguments(), Is.EqualTo(new[] { "--channel", "development" }), "one-shot flags are not passed on");
        }

        [TestCase("--adopt", "")]
        [TestCase("--updated-from-pid", "x")]
        public void BadFlagValuesAreIgnored(string flag, string value)
        {
            var options = LaunchOptions.Parse(new[] { flag, value }, name => null, releaseBuild: true);
            Assert.That(options.AdoptFrom, Is.Null);
            Assert.That(options.UpdatedFromPid, Is.EqualTo(0));
        }
    }
}
