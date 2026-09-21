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
            Assert.That(LauncherChannel.Production.GamePort, Is.EqualTo(8887));
            Assert.That(LauncherChannel.Production.InstallDirName, Is.EqualTo("Arcana"));
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
            Assert.That(channel.GamePort, Is.EqualTo(9998));
            Assert.That(channel.InstallDirName, Is.EqualTo("Arcana-development"));
        }

        [TestCase("--channel", "staging")]
        [TestCase("--channel", null)]
        [TestCase("--fullscreen", null)]
        public void AnythingElseIsProduction(string first, string second)
        {
            var args = second == null ? new[] { first } : new[] { first, second };
            Assert.That(LauncherChannel.FromArgs(args), Is.SameAs(LauncherChannel.Production));
        }
    }
}
