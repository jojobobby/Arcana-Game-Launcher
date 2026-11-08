using System.Linq;
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

        private static readonly (string, string)[] Files = { ("Arcana.exe", "game"), ("licenses/sdl2.txt", "zlib") };

        [Test]
        public void ReadsTheFileListAndInstallSize()
        {
            var metadata = ClientMetadata.Parse(FakeServer.Metadata(Zip, files: Files), LauncherChannel.Production);
            Assert.That(metadata.Manifest.Select(e => e.Path), Is.EqualTo(new[] { "Arcana.exe", "licenses/sdl2.txt" }));
            Assert.That(metadata.Manifest[1].Sha256, Is.EqualTo(FakeServer.Sha256("zlib")));
            Assert.That(metadata.UnpackedSize, Is.EqualTo(8));
            Assert.That(metadata.DownloadETag, Is.EqualTo("\"" + FakeServer.Sha256(Zip) + "\""));
        }

        [Test]
        public void AServerWithoutAFileListIsBudgetedAtThreeTimesTheZip()
        {
            var metadata = ClientMetadata.Parse(FakeServer.Metadata(Zip), LauncherChannel.Production);
            Assert.That(metadata.Manifest, Is.Null);
            Assert.That(metadata.UnpackedSize, Is.EqualTo(Zip.Length * 3));
        }

        // The launcher writes and deletes by these paths: none may leave the install folder.
        [TestCase("../evil.dll")]
        [TestCase("..\\evil.dll")]
        [TestCase("C:/Windows/evil.dll")]
        [TestCase("/rooted.dll")]
        [TestCase("a//b.dll")]
        [TestCase("dir./x.dll")]
        [TestCase("bad|name.dll")]
        public void AFileListEntryOutsideTheInstallIsRefused(string path)
        {
            var files = new[] { ("Arcana.exe", "game"), (path.Replace("\\", "\\\\"), "x") };
            Assert.Throws<LauncherException>(() =>
                ClientMetadata.Parse(FakeServer.Metadata(Zip, files: files), LauncherChannel.Production));
        }

        [Test]
        public void AFileListWithoutTheExecutableIsRefused()
        {
            Assert.Throws<LauncherException>(() =>
                ClientMetadata.Parse(FakeServer.Metadata(Zip, files: new[] { ("lime.ndll", "x") }), LauncherChannel.Production));
        }

        [Test]
        public void AnInstallSizeThatDoesNotAddUpIsRefused()
        {
            var json = FakeServer.Metadata(Zip, files: Files).Replace("\"unpacked_size\": 8", "\"unpacked_size\": 9");
            Assert.Throws<LauncherException>(() => ClientMetadata.Parse(json, LauncherChannel.Production));
        }

        [Test]
        public void ADuplicatedFileIsRefused()
        {
            var files = new[] { ("Arcana.exe", "game"), ("arcana.EXE", "game") };
            Assert.Throws<LauncherException>(() =>
                ClientMetadata.Parse(FakeServer.Metadata(Zip, files: files), LauncherChannel.Production));
        }

        [Test]
        public void ReadsTheRealServersMetadata()
        {
            // The layout tools/build_client_release.ps1 writes (Windows PowerShell's ConvertTo-Json spacing), cut to two files.
            const string json = "{\n    \"build\":  \"b46063466648\",\n    \"version\":  \"9.1.3\",\n    \"channel\":  \"production\",\n" +
                "    \"commit\":  \"21fea4e\",\n    \"file_name\":  \"Arcana-client.zip\",\n    \"file_size\":  17621141,\n" +
                "    \"sha256\":  \"ab855271027e47b496dedadb3fb64bf6497692ad741be1c712eb62c980539a85\",\n    \"executable\":  \"Arcana.exe\",\n" +
                "    \"unpacked_size\":  53722112,\n    \"files\":  [ \"Arcana.exe\", \"lime.ndll\" ],\n    \"manifest\":  [\n" +
                "        { \"path\":  \"Arcana.exe\", \"size\":  45493760, \"sha256\":  \"97f5c3802040918a3a2563fb658606c45a8985aef455bbde73cb211d209bedae\" },\n" +
                "        { \"path\":  \"lime.ndll\", \"size\":  8228352, \"sha256\":  \"ffa28f9436a91a39859ec87568fb910135cfec3dd65d0c973f3e90f210d81610\" }\n    ]\n}";
            var metadata = ClientMetadata.Parse(json, LauncherChannel.Production);
            Assert.That(metadata.Build, Is.EqualTo("b46063466648"));
            Assert.That(metadata.Manifest, Has.Count.EqualTo(2));
            Assert.That(metadata.UnpackedSize, Is.EqualTo(53722112));
        }
    }
}
