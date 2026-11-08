using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GameLauncher.Tests
{
    public class ClientInstallerTests
    {
        private const string MetadataUrl = "https://app.tidansrealm.com/client/metadata";
        private const string DownloadUrl = "https://app.tidansrealm.com/client/download";
        private string _temp;
        private string _root;
        private string _downloads;
        private FakeServer _server;
        private List<TimeSpan> _waits;
        private bool _gameRunning;
        private long? _freeBytes;
        private ClientInstaller _installer;

        [SetUp]
        public void Setup()
        {
            _temp = Path.Combine(Path.GetTempPath(), "arcana-launcher-" + Guid.NewGuid());
            _root = Path.Combine(_temp, "launcher");
            _downloads = Path.Combine(_temp, "downloads");
            Directory.CreateDirectory(_root);
            _server = new FakeServer();
            _waits = new List<TimeSpan>();
            _gameRunning = false;
            _freeBytes = null;
            _installer = Make(LauncherChannel.Production, _root);
        }

        [TearDown]
        public void Cleanup()
        {
            if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
        }

        private ClientInstaller Make(LauncherChannel channel, string root) =>
            new ClientInstaller(_server.Client(), channel, root, _downloads, FakeServer.NoWait(_waits),
                folder => _gameRunning, drive => _freeBytes, TimeSpan.FromMilliseconds(300));

        private string Game(string file) => Path.Combine(_root, "Arcana", file);
        private int Downloads => _server.Seen.Count(r => r.Url == DownloadUrl);

        /// <summary>Publishes a build the way the server serves it: metadata with a manifest, and the zip.</summary>
        private byte[] Publish(string build, params (string Name, string Body)[] files) => Publish(build, true, files);

        private byte[] Publish(string build, bool manifest, params (string Name, string Body)[] files)
        {
            var zip = FakeServer.Zip(files);
            _server.Text(MetadataUrl, FakeServer.Metadata(zip, build, files: manifest ? files : null));
            _server.Bytes(DownloadUrl, zip, "\"" + FakeServer.Sha256(zip) + "\"");
            return zip;
        }

        private async Task InstallServed() => await _installer.InstallAsync(await _installer.FetchMetadataAsync(), null);

        [Test]
        public void NothingIsInstalledInAFreshFolder()
        {
            Assert.That(_installer.Inspect().Health, Is.EqualTo(InstallHealth.NotInstalled));
            Assert.That(_installer.ReadInstalled(), Is.Null);
        }

        [Test]
        public async Task InstallsTheClientTheServerDescribes()
        {
            Publish("build-one", ("Arcana.exe", "game"), ("lime.ndll", "native"), ("licenses/sdl2.txt", "zlib"));
            var reports = new List<InstallProgress>();
            await _installer.InstallAsync(await _installer.FetchMetadataAsync(), new SyncProgress<InstallProgress>(reports.Add));

            Assert.That(File.ReadAllText(Game("Arcana.exe")), Is.EqualTo("game"));
            Assert.That(File.ReadAllText(Game("licenses/sdl2.txt")), Is.EqualTo("zlib"));
            var installed = _installer.ReadInstalled();
            Assert.That(installed.Build, Is.EqualTo("build-one"));
            Assert.That(installed.Version, Is.EqualTo("5.2.9"));
            Assert.That(installed.ExePath, Is.EqualTo(Game("Arcana.exe")));
            Assert.That(installed.Manifest.Select(e => e.Path), Is.EquivalentTo(new[] { "Arcana.exe", "lime.ndll", "licenses/sdl2.txt" }));
            Assert.That(_installer.Inspect().Health, Is.EqualTo(InstallHealth.Ready));
            Assert.That(reports.Last().Percent, Is.EqualTo(100));
            Assert.That(reports.Select(r => r.Stage).Distinct(), Is.EqualTo(new[]
                { InstallStage.Downloading, InstallStage.Unpacking, InstallStage.Checking, InstallStage.Finishing }));
            Assert.That(Directory.GetDirectories(_root).Select(Path.GetFileName), Is.EqualTo(new[] { "Arcana" }), "no .new/.old left");
            Assert.That(Directory.GetFiles(_root), Is.Empty);
        }

        [Test]
        public async Task AnUpdateReplacesEveryOldFile()
        {
            Publish("build-one", ("Arcana.exe", "old"), ("removed-in-two.dll", "stale"));
            await InstallServed();
            Publish("build-two", ("Arcana.exe", "new"));
            var metadata = await _installer.FetchMetadataAsync();
            Assert.That(_installer.ReadInstalled().Build, Is.Not.EqualTo(metadata.Build));

            await _installer.InstallAsync(metadata, null);

            Assert.That(File.ReadAllText(Game("Arcana.exe")), Is.EqualTo("new"));
            Assert.That(File.Exists(Game("removed-in-two.dll")), Is.False);
            Assert.That(_installer.ReadInstalled().Build, Is.EqualTo("build-two"));
            Assert.That(Directory.GetDirectories(_root), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task ACorruptDownloadLeavesTheWorkingInstallAlone()
        {
            Publish("build-one", ("Arcana.exe", "works"));
            await InstallServed();
            var zip = FakeServer.Zip(("Arcana.exe", "tampered"));
            _server.Text(MetadataUrl, FakeServer.Metadata(zip, "build-two", sha256: new string('a', 64)));
            _server.Bytes(DownloadUrl, zip);

            var metadata = await _installer.FetchMetadataAsync();
            var error = Assert.ThrowsAsync<LauncherException>(() => _installer.InstallAsync(metadata, null));

            Assert.That(error.Message, Does.Contain("integrity"));
            Assert.That(File.ReadAllText(Game("Arcana.exe")), Is.EqualTo("works"));
            Assert.That(_installer.ReadInstalled().Build, Is.EqualTo("build-one"));
        }

        [Test]
        public async Task AZipThatDoesNotMatchItsFileListIsRefused()
        {
            Publish("build-one", ("Arcana.exe", "works"));
            await InstallServed();
            // The zip hashes as the metadata says, but holds a file the manifest describes differently.
            var zip = FakeServer.Zip(("Arcana.exe", "something else"));
            _server.Text(MetadataUrl, FakeServer.Metadata(zip, "build-two", files: new[] { ("Arcana.exe", "the real game") }));
            _server.Bytes(DownloadUrl, zip);

            var error = Assert.ThrowsAsync<LauncherException>(async () => await InstallServed());
            Assert.That(error.Message, Does.Contain("does not match"));
            Assert.That(File.ReadAllText(Game("Arcana.exe")), Is.EqualTo("works"));
        }

        [Test]
        public async Task AZipWithoutTheExecutableIsRefused()
        {
            Publish("build-one", false, ("readme.txt", "no game here"));
            var metadata = await _installer.FetchMetadataAsync();
            Assert.ThrowsAsync<LauncherException>(() => _installer.InstallAsync(metadata, null));
            Assert.That(_installer.ReadInstalled(), Is.Null);
            Assert.That(Directory.GetDirectories(_root), Is.Empty);
        }

        [Test]
        public async Task AnInstallFromAServerWithoutAManifestStillGetsOne()
        {
            Publish("build-one", false, ("Arcana.exe", "game"), ("lime.ndll", "native"));
            await InstallServed();
            var installed = _installer.ReadInstalled();
            Assert.That(installed.Manifest.Select(e => e.Path), Is.EquivalentTo(new[] { "Arcana.exe", "lime.ndll" }));
            Assert.That(installed.Manifest.Single(e => e.Path == "lime.ndll").Sha256, Is.EqualTo(FakeServer.Sha256("native")));
        }

        [Test]
        public async Task AMissingExeIsTheAntivirusCaseNotAFreshInstall()
        {
            Publish("build-one", ("Arcana.exe", "game"));
            await InstallServed();
            File.Delete(Game("Arcana.exe"));
            var inspection = _installer.Inspect();
            Assert.That(inspection.Health, Is.EqualTo(InstallHealth.ExeMissing));
            Assert.That(inspection.Client.Build, Is.EqualTo("build-one"));
            Assert.That(_installer.ReadInstalled(), Is.Null);
        }

        [Test]
        public async Task AFileOfTheWrongSizeIsFoundBeforePlay()
        {
            Publish("build-one", ("Arcana.exe", "game"), ("lime.ndll", "native"));
            await InstallServed();
            File.WriteAllText(Game("lime.ndll"), "truncated? no, longer");
            var inspection = _installer.Inspect();
            Assert.That(inspection.Health, Is.EqualTo(InstallHealth.Damaged));
            Assert.That(inspection.Detail, Does.Contain("lime.ndll"));
        }

        [Test]
        public async Task VerifyFindsAChangedFileAndRepairNeedsNoDownload()
        {
            Publish("build-one", ("Arcana.exe", "game"), ("lime.ndll", "native"));
            await InstallServed();
            File.WriteAllText(Game("lime.ndll"), "NATIVE");   // same size: only the hash can tell
            Assert.That(_installer.Inspect().Health, Is.EqualTo(InstallHealth.Ready), "the quick check only looks at sizes");

            var served = await _installer.FetchMetadataAsync();
            var bad = await _installer.VerifyAsync(_installer.ReadInstalled(), served, null, CancellationToken.None);
            Assert.That(bad, Is.EqualTo(new[] { "lime.ndll" }));

            await _installer.InstallAsync(served, null);
            Assert.That(File.ReadAllText(Game("lime.ndll")), Is.EqualTo("native"));
            Assert.That(Downloads, Is.EqualTo(1), "the repair used the zip already downloaded");
            Assert.That(await _installer.VerifyAsync(_installer.ReadInstalled(), served, null, CancellationToken.None), Is.Empty);
        }

        [Test]
        public async Task AnOldInstallWithoutAFileListIsVerifiedAgainstTheServers()
        {
            Publish("build-one", ("Arcana.exe", "game"));
            await InstallServed();
            File.Delete(Game(ClientInstaller.ManifestFileName));
            var installed = _installer.ReadInstalled();
            Assert.That(installed.Manifest, Is.Null);

            Assert.That(await _installer.VerifyAsync(installed, await _installer.FetchMetadataAsync(), null, CancellationToken.None), Is.Empty);
            Assert.That(await _installer.VerifyAsync(installed, null, null, CancellationToken.None), Is.Null, "nothing to check against");
        }

        [Test]
        public async Task GoingBackABuildNeedsNoDownload()
        {
            var one = ("Arcana.exe", "one");
            Publish("build-one", one);
            await InstallServed();
            Publish("build-two", ("Arcana.exe", "two"));
            await InstallServed();
            Publish("build-one", one);
            await InstallServed();

            Assert.That(File.ReadAllText(Game("Arcana.exe")), Is.EqualTo("one"));
            Assert.That(Downloads, Is.EqualTo(2));
        }

        [Test]
        public async Task OnlyTheLastTwoZipsAreKept()
        {
            foreach (var build in new[] { "one", "two", "three" })
            {
                Publish(build, ("Arcana.exe", build));
                await InstallServed();
            }
            var zips = Directory.GetFiles(Path.Combine(_downloads, "production"), "*.zip");
            Assert.That(zips, Has.Length.EqualTo(2));
            Assert.That(zips.Select(Path.GetFileNameWithoutExtension), Does.Contain(FakeServer.Sha256(FakeServer.Zip(("Arcana.exe", "three")))));
        }

        [Test]
        public async Task ABrokenDownloadResumesInsideAnInstall()
        {
            var zip = Publish("build-one", ("Arcana.exe", new string('x', 200_000)));
            _server.Sequence(DownloadUrl, FakeServer.Cut(zip, 300), FakeServer.Answer(zip, "\"" + FakeServer.Sha256(zip) + "\""));
            await InstallServed();
            Assert.That(_installer.Inspect().Health, Is.EqualTo(InstallHealth.Ready));
            Assert.That(_server.Seen.Last().Range, Is.EqualTo("bytes=300-"));
        }

        [Test]
        public async Task NotEnoughDiskSpaceStopsBeforeAnythingIsDownloaded()
        {
            Publish("build-one", ("Arcana.exe", "works"));
            await InstallServed();
            Publish("build-two", ("Arcana.exe", "newer"));
            _freeBytes = 10 * 1024 * 1024;

            var error = Assert.ThrowsAsync<LauncherException>(async () => await InstallServed());
            Assert.That(error.Title, Is.EqualTo("Not enough disk space"));
            Assert.That(error.Message, Does.Contain("You have 10.0 MB"));
            Assert.That(Downloads, Is.EqualTo(1));
            Assert.That(File.ReadAllText(Game("Arcana.exe")), Is.EqualTo("works"));
        }

        [Test]
        public async Task ARunningGameIsNeverUpdatedUnderneathIt()
        {
            Publish("build-one", ("Arcana.exe", "works"));
            await InstallServed();
            Publish("build-two", ("Arcana.exe", "newer"));
            _gameRunning = true;

            var error = Assert.ThrowsAsync<LauncherException>(async () => await InstallServed());
            Assert.That(error.Title, Is.EqualTo("Game is running"));
            Assert.That(Downloads, Is.EqualTo(1), "nothing is downloaded while the game runs");
            Assert.That(File.ReadAllText(Game("Arcana.exe")), Is.EqualTo("works"));
        }

        [Test]
        public async Task UninstallRemovesTheGameFolder()
        {
            Publish("build-one", ("Arcana.exe", "game"));
            await InstallServed();
            _installer.Uninstall();
            Assert.That(Directory.Exists(Path.Combine(_root, "Arcana")), Is.False);
            Assert.That(Directory.GetDirectories(_root), Is.Empty, "no .delete folder left");
        }

        [Test]
        public async Task UninstallWaitsForTheGameToClose()
        {
            Publish("build-one", ("Arcana.exe", "game"));
            await InstallServed();
            _gameRunning = true;
            Assert.Throws<LauncherException>(() => _installer.Uninstall());
            Assert.That(File.Exists(Game("Arcana.exe")), Is.True);
        }

        [Test]
        public async Task UninstallWithAFileInUseLeavesEveryFileInPlace()
        {
            Publish("build-one", ("Arcana.exe", "game"), ("lime.ndll", "native"), ("zz-last.txt", "x"));
            await InstallServed();
            using (new FileStream(Game("zz-last.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Assert.Throws<LauncherException>(() => _installer.Uninstall());
                Assert.That(error.Title, Is.EqualTo("Game files in use"));
            }
            Assert.That(File.Exists(Game("Arcana.exe")), Is.True, "not half deleted");
            Assert.That(File.Exists(Game("lime.ndll")), Is.True);
            Assert.That(_installer.Inspect().Health, Is.EqualTo(InstallHealth.Ready));
        }

        [Test]
        public async Task AdoptMovesAGameLeftBesideAnOldLauncher()
        {
            var oldRoot = Path.Combine(_temp, "Downloads");
            Directory.CreateDirectory(oldRoot);
            Publish("build-one", ("Arcana.exe", "game"));
            var old = Make(LauncherChannel.Production, oldRoot);
            await old.InstallAsync(await old.FetchMetadataAsync(), null);

            Assert.That(_installer.Adopt(oldRoot), Is.True);
            Assert.That(_installer.ReadInstalled().Build, Is.EqualTo("build-one"));
            Assert.That(Directory.Exists(Path.Combine(oldRoot, "Arcana")), Is.False);
        }

        [Test]
        public async Task AdoptNeverOverwritesAnInstall()
        {
            var oldRoot = Path.Combine(_temp, "Downloads");
            Directory.CreateDirectory(oldRoot);
            Publish("build-one", ("Arcana.exe", "mine"));
            await InstallServed();
            var old = Make(LauncherChannel.Production, oldRoot);
            Publish("build-old", ("Arcana.exe", "theirs"));
            await old.InstallAsync(await old.FetchMetadataAsync(), null);

            Assert.That(_installer.Adopt(oldRoot), Is.False);
            Assert.That(File.ReadAllText(Game("Arcana.exe")), Is.EqualTo("mine"));
        }

        [Test]
        public void AdoptIgnoresAFolderThatIsNotAnInstall()
        {
            var oldRoot = Path.Combine(_temp, "Downloads");
            Directory.CreateDirectory(Path.Combine(oldRoot, "Arcana"));
            File.WriteAllText(Path.Combine(oldRoot, "Arcana", "notes.txt"), "not a game");
            Assert.That(_installer.Adopt(oldRoot), Is.False);
            Assert.That(_installer.Adopt(null), Is.False);
        }

        [Test]
        public async Task DevelopmentInstallsBesideProductionFromItsOwnServer()
        {
            var zip = FakeServer.Zip(("Arcana.exe", "dev game"));
            _server.Text("https://devapp.tidansrealm.com/client/metadata", FakeServer.Metadata(zip, "tree-dev", "development"));
            _server.Bytes("https://devapp.tidansrealm.com/client/download", zip);
            var dev = Make(LauncherChannel.Development, _root);

            await dev.InstallAsync(await dev.FetchMetadataAsync(), null);

            Assert.That(File.Exists(Path.Combine(_root, "Arcana-development", "Arcana.exe")), Is.True);
            Assert.That(_installer.ReadInstalled(), Is.Null, "production is a separate install");
        }

        [Test]
        public void AServerErrorPageIsReportedAsTheServerNotAsJson()
        {
            _server.Text(MetadataUrl, "<html><body>Maintenance</body></html>");
            var error = Assert.ThrowsAsync<LauncherException>(() => _installer.FetchMetadataAsync());
            Assert.That(error.Message, Does.Contain("Maintenance"));
        }

        [Test]
        public void AServerWithoutAClientSaysSo()
        {
            _server.Status(MetadataUrl, HttpStatusCode.NotFound);
            var error = Assert.ThrowsAsync<LauncherException>(() => _installer.FetchMetadataAsync());
            Assert.That(error.Message, Does.Contain("no client"));
        }

        [Test]
        public async Task ABusyServerIsAskedAgain()
        {
            var zip = FakeServer.Zip(("Arcana.exe", "game"));
            _server.Sequence(MetadataUrl, FakeServer.Busy(TimeSpan.FromSeconds(4)), FakeServer.Busy(),
                FakeServer.Answer(System.Text.Encoding.UTF8.GetBytes(FakeServer.Metadata(zip, "build-one"))));
            var notes = new List<string>();
            var metadata = await _installer.FetchMetadataAsync(CancellationToken.None, new SyncProgress<string>(notes.Add));
            Assert.That(metadata.Build, Is.EqualTo("build-one"));
            Assert.That(_waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5) }));
            Assert.That(notes.First(), Is.EqualTo("Server busy. Trying again in 4 s."));
        }

        [Test]
        public void AServerThatNeverAnswersEndsInAPlainError()
        {
            _server.Route(MetadataUrl, FakeServer.Busy());
            var error = Assert.ThrowsAsync<LauncherException>(() => _installer.FetchMetadataAsync());
            Assert.That(error.Title, Is.EqualTo("Can't reach the game server"));
            Assert.That(error.Message, Does.Contain("app.tidansrealm.com"));
        }

        [Test]
        public async Task ThePollSendsTheETagAndTreatsA304AsUnchanged()
        {
            const string tag = "\"meta-1\"";
            var zip = FakeServer.Zip(("Arcana.exe", "game"));
            var body = System.Text.Encoding.UTF8.GetBytes(FakeServer.Metadata(zip, "build-one"));
            _server.Route(MetadataUrl, (request, cancel) =>
                Task.FromResult(request.Headers.IfNoneMatch.Any(t => t.Tag == tag)
                    ? new HttpResponseMessage(HttpStatusCode.NotModified)
                    : FakeServer.Serve(request, body, tag)));

            var first = await _installer.PollAsync(null, CancellationToken.None);
            Assert.That(first.Reached, Is.True);
            Assert.That(first.Metadata.Build, Is.EqualTo("build-one"));
            Assert.That(first.ETag, Is.EqualTo(tag));

            var second = await _installer.PollAsync(first.ETag, CancellationToken.None);
            Assert.That(second.Reached, Is.True);
            Assert.That(second.Metadata, Is.Null, "unchanged");
            Assert.That(_server.Seen.Last().IfNoneMatch, Is.EqualTo(tag));
        }

        [Test]
        public async Task AnUnreachableServerPollsAsOffline()
        {
            _server.Route(MetadataUrl, (request, cancel) => throw new HttpRequestException("no route to host"));
            var poll = await _installer.PollAsync(null, CancellationToken.None);
            Assert.That(poll.Reached, Is.False);
        }

        [Test]
        public async Task AnUpdateStoppedBetweenItsRenamesGetsTheGameBack()
        {
            Publish("build-one", ("Arcana.exe", "works"));
            await InstallServed();
            // What a kill between "Arcana -> Arcana.old" and "Arcana.new -> Arcana" leaves behind.
            Directory.Move(Path.Combine(_root, "Arcana"), Path.Combine(_root, "Arcana.old"));
            Directory.CreateDirectory(Path.Combine(_root, "Arcana.new"));
            Assert.That(_installer.Inspect().Health, Is.EqualTo(InstallHealth.NotInstalled));

            _installer.CleanLeftovers();

            Assert.That(_installer.Inspect().Health, Is.EqualTo(InstallHealth.Ready));
            Assert.That(File.ReadAllText(Game("Arcana.exe")), Is.EqualTo("works"));
            Assert.That(Directory.GetDirectories(_root).Select(Path.GetFileName), Is.EqualTo(new[] { "Arcana" }));
        }

        [Test]
        public void LeftoversOfAnInterruptedUpdateAreCleanedUp()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Arcana.new"));
            Directory.CreateDirectory(Path.Combine(_root, "Arcana.delete"));
            File.WriteAllText(Path.Combine(_root, "Arcana-client.zip.download"), "old launcher temp");
            _installer.CleanLeftovers();
            Assert.That(Directory.GetDirectories(_root), Is.Empty);
            Assert.That(Directory.GetFiles(_root), Is.Empty);
        }
    }
}
