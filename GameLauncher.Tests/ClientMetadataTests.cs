using NUnit.Framework;

namespace GameLauncher.Tests
{
    public class ClientMetadataTests
    {
        private static readonly byte[] Zip = FakeServer.Zip(("Arcana.exe", "game"));

        [Test]
        public void ReadsWhatTheBuildScriptWrites()
        {
            var metadata = ClientMetadata.Parse(FakeServer.Metadata(Zip), LauncherChannel.Production);
            Assert.That(metadata.Build, Is.EqualTo("8a991dfa1c24"));
            Assert.That(metadata.Version, Is.EqualTo("5.2.9"));
            Assert.That(metadata.Sha256, Is.EqualTo(FakeServer.Sha256(Zip)));
            Assert.That(metadata.FileSize, Is.EqualTo(Zip.Length));
            Assert.That(metadata.Executable, Is.EqualTo("Arcana.exe"));
        }

        [Test]
        public void ADevelopmentClientIsNeverInstalledByAProductionLauncher()
        {
            var error = Assert.Throws<LauncherException>(() =>
                ClientMetadata.Parse(FakeServer.Metadata(Zip, channel: "development"), LauncherChannel.Production));
            Assert.That(error.Message, Does.Contain("development"));
        }

        // The launcher starts <install dir>/<executable>, so it must stay inside that folder.
        [TestCase("..\\..\\Windows\\System32\\cmd.exe")]
        [TestCase("../evil.exe")]
        [TestCase("C:\\Windows\\System32\\cmd.exe")]
        [TestCase("sub\\Arcana.exe")]
        [TestCase("Arcana.bat")]
        [TestCase("")]
        public void TheExecutableMustBeAPlainExeName(string executable)
        {
            Assert.Throws<LauncherException>(() =>
                ClientMetadata.Parse(FakeServer.Metadata(Zip, executable: executable), LauncherChannel.Production));
        }

        [TestCase("not-a-hash")]
        [TestCase("")]
        public void TheHashMustBeSha256(string sha256)
        {
            Assert.Throws<LauncherException>(() =>
                ClientMetadata.Parse(FakeServer.Metadata(Zip, sha256: sha256), LauncherChannel.Production));
        }

        [Test]
        public void AnAbsurdSizeIsRefusedBeforeDownloading()
        {
            Assert.Throws<LauncherException>(() =>
                ClientMetadata.Parse(FakeServer.Metadata(Zip, size: 10L * 1024 * 1024 * 1024), LauncherChannel.Production));
        }

        [Test]
        public void ABuildIdIsRequired()
        {
            Assert.Throws<LauncherException>(() =>
                ClientMetadata.Parse(FakeServer.Metadata(Zip, build: ""), LauncherChannel.Production));
        }
    }
}
