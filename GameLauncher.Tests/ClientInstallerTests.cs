using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GameLauncher.Tests
{
    public class ClientInstallerTests
    {
        private const string MetadataUrl = "https://app.tidansrealm.com/client/metadata";
        private const string DownloadUrl = "https://app.tidansrealm.com/client/download";
        private string _root;
        private FakeServer _server;
        private ClientInstaller _installer;

        [SetUp]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "arcana-launcher-" + Guid.NewGuid());
            Directory.CreateDirectory(_root);
            _server = new FakeServer();
            _installer = new ClientInstaller(_server.Client(), LauncherChannel.Production, _root);
        }

        [TearDown]
        public void Cleanup() => Directory.Delete(_root, true);

        private byte[] Publish(string build, params (string Name, string Body)[] files)
        {
            var zip = FakeServer.Zip(files);
            _server.Text(MetadataUrl, FakeServer.Metadata(zip, build));
            _server.Bytes(DownloadUrl, zip);
            return zip;
        }

        [Test]
        public void NothingIsInstalledInAFreshFolder() => Assert.That(_installer.ReadInstalled(), Is.Null);

        [Test]
        public async Task InstallsTheClientTheServerDescribes()
        {
            Publish("build-one", ("Arcana.exe", "game"), ("lime.ndll", "native"));
            double last = 0;
            var metadata = await _installer.FetchMetadataAsync();
            await _installer.InstallAsync(metadata, new InlineProgress(p => last = p));

            Assert.That(File.ReadAllText(Path.Combine(_root, "Arcana", "Arcana.exe")), Is.EqualTo("game"));
            Assert.That(File.Exists(Path.Combine(_root, "Arcana", "lime.ndll")), Is.True);
            var installed = _installer.ReadInstalled();
            Assert.That(installed.Build, Is.EqualTo("build-one"));
            Assert.That(installed.Version, Is.EqualTo("5.2.9"));
            Assert.That(installed.ExePath, Is.EqualTo(Path.Combine(_root, "Arcana", "Arcana.exe")));
            Assert.That(last, Is.EqualTo(100));
            Assert.That(Directory.GetFiles(_root), Is.Empty, "the downloaded zip is cleaned up");
        }

        [Test]
        public async Task AnUpdateReplacesEveryOldFile()
        {
            Publish("build-one", ("Arcana.exe", "old"), ("removed-in-two.dll", "stale"));
            await _installer.InstallAsync(await _installer.FetchMetadataAsync(), null);
            Publish("build-two", ("Arcana.exe", "new"));
            var metadata = await _installer.FetchMetadataAsync();
            Assert.That(_installer.ReadInstalled().Build, Is.Not.EqualTo(metadata.Build));

            await _installer.InstallAsync(metadata, null);

            Assert.That(File.ReadAllText(Path.Combine(_root, "Arcana", "Arcana.exe")), Is.EqualTo("new"));
            Assert.That(File.Exists(Path.Combine(_root, "Arcana", "removed-in-two.dll")), Is.False);
            Assert.That(_installer.ReadInstalled().Build, Is.EqualTo("build-two"));
            Assert.That(Directory.GetDirectories(_root), Has.Length.EqualTo(1), "no .new/.old folders left behind");
        }

        [Test]
        public async Task ACorruptDownloadLeavesTheWorkingInstallAlone()
        {
            Publish("build-one", ("Arcana.exe", "works"));
            await _installer.InstallAsync(await _installer.FetchMetadataAsync(), null);
            var zip = FakeServer.Zip(("Arcana.exe", "tampered"));
            _server.Text(MetadataUrl, FakeServer.Metadata(zip, "build-two", sha256: new string('a', 64)));
            _server.Bytes(DownloadUrl, zip);

            var metadata = await _installer.FetchMetadataAsync();
            var error = Assert.ThrowsAsync<LauncherException>(() => _installer.InstallAsync(metadata, null));

            Assert.That(error.Message, Does.Contain("integrity"));
            Assert.That(File.ReadAllText(Path.Combine(_root, "Arcana", "Arcana.exe")), Is.EqualTo("works"));
            Assert.That(_installer.ReadInstalled().Build, Is.EqualTo("build-one"));
            Assert.That(Directory.GetFiles(_root), Is.Empty);
        }

        [Test]
        public async Task AZipWithoutTheExecutableIsRefused()
        {
            Publish("build-one", ("readme.txt", "no game here"));
            var metadata = await _installer.FetchMetadataAsync();
            Assert.ThrowsAsync<LauncherException>(() => _installer.InstallAsync(metadata, null));
            Assert.That(_installer.ReadInstalled(), Is.Null);
            Assert.That(Directory.GetDirectories(_root), Is.Empty);
        }

        [Test]
        public async Task AnInstallWhoseExeWasDeletedCountsAsNotInstalled()
        {
            Publish("build-one", ("Arcana.exe", "game"));
            await _installer.InstallAsync(await _installer.FetchMetadataAsync(), null);
            File.Delete(Path.Combine(_root, "Arcana", "Arcana.exe"));
            Assert.That(_installer.ReadInstalled(), Is.Null);
        }

        [Test]
        public async Task UninstallRemovesTheGameFolder()
        {
            Publish("build-one", ("Arcana.exe", "game"));
            await _installer.InstallAsync(await _installer.FetchMetadataAsync(), null);
            _installer.Uninstall();
            Assert.That(Directory.Exists(Path.Combine(_root, "Arcana")), Is.False);
            Assert.That(_installer.ReadInstalled(), Is.Null);
        }

        [Test]
        public async Task DevelopmentInstallsBesideProductionFromItsOwnServer()
        {
            var zip = FakeServer.Zip(("Arcana.exe", "dev game"));
            _server.Text("https://devapp.tidansrealm.com/client/metadata", FakeServer.Metadata(zip, "tree-dev", "development"));
            _server.Bytes("https://devapp.tidansrealm.com/client/download", zip);
            var dev = new ClientInstaller(_server.Client(), LauncherChannel.Development, _root);

            await dev.InstallAsync(await dev.FetchMetadataAsync(), null);

            Assert.That(File.Exists(Path.Combine(_root, "Arcana-development", "Arcana.exe")), Is.True);
            Assert.That(_installer.ReadInstalled(), Is.Null, "production is a separate install");
        }

        [Test]
        public void AServerErrorPageIsReportedAsTheServerNotAsJson()
        {
            _server.Text(MetadataUrl, "<html><body>502 Bad Gateway</body></html>");
            var error = Assert.ThrowsAsync<LauncherException>(() => _installer.FetchMetadataAsync());
            Assert.That(error.Message, Does.Contain("502 Bad Gateway"));
        }

        [Test]
        public void AServerWithoutAClientSaysSo()
        {
            _server.Status(MetadataUrl, HttpStatusCode.NotFound);
            var error = Assert.ThrowsAsync<LauncherException>(() => _installer.FetchMetadataAsync());
            Assert.That(error.Message, Does.Contain("no client"));
        }

        private sealed class InlineProgress : IProgress<double>
        {
            private readonly Action<double> _report;
            public InlineProgress(Action<double> report) { _report = report; }
            public void Report(double value) => _report(value);
        }
    }
}
