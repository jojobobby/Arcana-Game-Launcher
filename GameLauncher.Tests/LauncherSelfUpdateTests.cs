using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GameLauncher.Tests
{
    public class LauncherSelfUpdateTests
    {
        private const string Releases = "https://github.com/jojobobby/Arcana-Game-Launcher/releases";
        private const string MetadataUrl = Releases + "/latest/download/launcher-metadata.json";
        private string _root;
        private string _exe;
        private FakeServer _server;
        private LauncherSelfUpdate _update;

        [SetUp]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "arcana-selfupdate-" + Guid.NewGuid());
            Directory.CreateDirectory(_root);
            _exe = Path.Combine(_root, "ArcanaLauncher.exe");
            File.WriteAllText(_exe, "this launcher");
            _server = new FakeServer();
            _update = new LauncherSelfUpdate(_server.Client(), Releases, _exe, FakeServer.NoWait());
        }

        [TearDown]
        public void Cleanup()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private static string Metadata(string exeBody, string tag = "v1.0.12.0", string setupBody = null) =>
            "{ \"version\": \"1.0.12.0\", \"release_tag\": " + (tag == null ? "null" : "\"" + tag + "\"") +
            ", \"file_name\": \"ArcanaLauncher.exe\", \"file_size\": " + Encoding.UTF8.GetByteCount(exeBody) +
            ", \"sha256\": \"" + FakeServer.Sha256(exeBody) + "\"" +
            (setupBody == null ? "" : ", \"setup_file_name\": \"ArcanaLauncherSetup.exe\", \"setup_size\": " +
                Encoding.UTF8.GetByteCount(setupBody) + ", \"setup_sha256\": \"" + FakeServer.Sha256(setupBody) + "\"") + " }";

        [Test]
        public async Task TheReleasedLauncherIsNotAnUpdateToItself()
        {
            _server.Text(MetadataUrl, Metadata("this launcher"));
            Assert.That(await _update.CheckAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ADifferentReleaseIsAnUpdate()
        {
            _server.Text(MetadataUrl, Metadata("the next launcher"));
            var release = await _update.CheckAsync(CancellationToken.None);
            Assert.That(release.Version, Is.EqualTo("1.0.12.0"));
            Assert.That(_server.Requests, Is.EqualTo(new[] { MetadataUrl }), "checking reads only the small metadata file");
        }

        [Test]
        public async Task AnOlderReleaseIsAnUpdateTooSoARollbackReachesEveryone()
        {
            _server.Text(MetadataUrl, Metadata("the previous launcher"));
            Assert.That(await _update.CheckAsync(CancellationToken.None), Is.Not.Null);
        }

        [Test]
        public async Task AnUnreachableOrBrokenReleaseIsNotAnUpdate()
        {
            Assert.That(await _update.CheckAsync(CancellationToken.None), Is.Null, "404");
            _server.Text(MetadataUrl, "<html>rate limited</html>");
            Assert.That(await _update.CheckAsync(CancellationToken.None), Is.Null, "not JSON");
            _server.Text(MetadataUrl, "{ \"sha256\": \"short\", \"file_size\": 10 }");
            Assert.That(await _update.CheckAsync(CancellationToken.None), Is.Null, "no valid hash");
        }

        [Test]
        public async Task DownloadsTheExeFromTheReleasesOwnTag()
        {
            _server.Text(MetadataUrl, Metadata("the next launcher"));
            _server.Text(Releases + "/download/v1.0.12.0/ArcanaLauncher.exe", "the next launcher");
            var release = await _update.CheckAsync(CancellationToken.None);

            var staged = await _update.DownloadAsync(release, null, CancellationToken.None);

            Assert.That(staged, Is.EqualTo(_exe + ".new"));
            Assert.That(File.ReadAllText(staged), Is.EqualTo("the next launcher"));
            Assert.That(File.ReadAllText(_exe), Is.EqualTo("this launcher"), "nothing replaced yet");
        }

        [Test]
        public async Task AReleaseWithoutATagDownloadsFromLatest()
        {
            _server.Text(MetadataUrl, Metadata("the next launcher", tag: null));
            _server.Text(Releases + "/latest/download/ArcanaLauncher.exe", "the next launcher");
            var release = await _update.CheckAsync(CancellationToken.None);
            Assert.That(await _update.DownloadAsync(release, null, CancellationToken.None), Is.EqualTo(_exe + ".new"));
        }

        [Test]
        public async Task AnExeThatDoesNotMatchTheReleaseIsNeverStaged()
        {
            _server.Text(MetadataUrl, Metadata("the next launcher"));
            _server.Text(Releases + "/download/v1.0.12.0/ArcanaLauncher.exe", "the next launcheR");
            var release = await _update.CheckAsync(CancellationToken.None);
            Assert.ThrowsAsync<LauncherException>(() => _update.DownloadAsync(release, null, CancellationToken.None));
            Assert.That(File.Exists(_exe + ".new"), Is.False);
        }

        [Test]
        public async Task TheInstallerIsDownloadedAndCheckedLikeTheLauncher()
        {
            _server.Text(MetadataUrl, Metadata("the next launcher", setupBody: "setup bytes"));
            _server.Text(Releases + "/download/v1.0.12.0/ArcanaLauncherSetup.exe", "setup bytes");
            var release = await _update.FetchLatestAsync(CancellationToken.None);
            Assert.That(release.HasSetup, Is.True);

            var setup = await _update.DownloadSetupAsync(release, Path.Combine(_root, "downloads"), null, CancellationToken.None);
            Assert.That(Path.GetFileName(setup), Is.EqualTo("ArcanaLauncherSetup-1.0.12.0.exe"), "never named setup.exe");
            Assert.That(File.ReadAllText(setup), Is.EqualTo("setup bytes"));
        }

        [TestCase("../../evil")]
        [TestCase("v1.0/../x")]
        [TestCase("v1 0")]
        public void ATagThatIsNotAPlainNameIsIgnored(string tag)
        {
            var release = LauncherRelease.Parse(Metadata("x", tag));
            Assert.That(release.ReleaseTag, Is.Null);
            Assert.That(release.ExeUrl(Releases), Is.EqualTo(Releases + "/latest/download/ArcanaLauncher.exe"));
        }

        [Test]
        public void ASetupEntryWithoutAValidHashIsNotOffered()
        {
            var json = Metadata("x").Replace(" }", ", \"setup_file_name\": \"ArcanaLauncherSetup.exe\", \"setup_size\": 5, \"setup_sha256\": \"nope\" }");
            Assert.That(LauncherRelease.Parse(json).HasSetup, Is.False);
        }

        [Test]
        public void SwapPutsTheNewExeInPlaceAndKeepsTheOldUntilCleanedUp()
        {
            File.WriteAllText(_exe + ".new", "the next launcher");
            LauncherSelfUpdate.Swap(_exe, _exe + ".new");
            Assert.That(File.ReadAllText(_exe), Is.EqualTo("the next launcher"));
            Assert.That(File.ReadAllText(_exe + ".old"), Is.EqualTo("this launcher"));
            Assert.That(File.Exists(_exe + ".new"), Is.False);

            LauncherSelfUpdate.DeleteLeftovers(_exe);
            Assert.That(File.Exists(_exe + ".old"), Is.False);
        }

        [Test]
        public void RevertPutsTheOldExeBack()
        {
            File.WriteAllText(_exe + ".new", "a broken launcher");
            LauncherSelfUpdate.Swap(_exe, _exe + ".new");
            LauncherSelfUpdate.Revert(_exe);
            Assert.That(File.ReadAllText(_exe), Is.EqualTo("this launcher"));
            Assert.That(Directory.GetFiles(_root).Select(Path.GetFileName), Is.EqualTo(new[] { "ArcanaLauncher.exe" }));
        }

        [Test]
        public void AFailedSwapChangesNothing()
        {
            Assert.Throws<LauncherException>(() => LauncherSelfUpdate.Swap(_exe, _exe + ".missing"));
            Assert.That(File.ReadAllText(_exe), Is.EqualTo("this launcher"));
            Assert.That(File.Exists(_exe + ".old"), Is.False);
        }

        // The whole self-update rests on this: Windows lets a running exe be renamed (not deleted).
        [Test]
        public void ARunningExeCanBeSwappedOut()
        {
            var running = Path.Combine(_root, "Running.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), running);
            using (var process = Process.Start(new ProcessStartInfo(running, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true }))
            {
                try
                {
                    Thread.Sleep(300);
                    Assert.That(process.HasExited, Is.False);
                    File.WriteAllText(running + ".new", "the next launcher");

                    LauncherSelfUpdate.Swap(running, running + ".new");

                    Assert.That(File.ReadAllText(running), Is.EqualTo("the next launcher"));
                    Assert.That(File.Exists(running + ".old"), Is.True);
                    Assert.That(() => File.Delete(running + ".old"), Throws.InstanceOf<UnauthorizedAccessException>(),
                        "the old exe is still running, so it waits for CleanUpAfterUpdate");
                }
                finally
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
        }
    }
}
