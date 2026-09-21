using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GameLauncher.Tests
{
    public class LauncherSelfUpdateTests
    {
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
            _update = new LauncherSelfUpdate(_server.Client(), _exe, Path.Combine(_root, "staging"));
        }

        [TearDown]
        public void Cleanup() => Directory.Delete(_root, true);

        private void Release(string body) =>
            _server.Text(LauncherSelfUpdate.MetadataUrl,
                "{ \"version\": \"1.0.9.0\", \"sha256\": \"" + FakeServer.Sha256(Encoding.UTF8.GetBytes(body)) + "\" }");

        [Test]
        public async Task TheReleasedLauncherIsNotAnUpdateToItself()
        {
            Release("this launcher");
            Assert.That(await _update.IsUpdateAvailableAsync(), Is.False);
        }

        [Test]
        public async Task ADifferentReleaseIsAnUpdate()
        {
            Release("newer launcher");
            Assert.That(await _update.IsUpdateAvailableAsync(), Is.True);
        }

        // The check used to download the whole ~60 MB launcher on every start.
        [Test]
        public async Task CheckingReadsOnlyTheSmallMetadataFile()
        {
            Release("newer launcher");
            await _update.IsUpdateAvailableAsync();
            Assert.That(_server.Requests, Is.EqualTo(new[] { LauncherSelfUpdate.MetadataUrl }));
        }

        [Test]
        public async Task AnUnreachableReleaseIsNotAnUpdate()
        {
            _server.Status(LauncherSelfUpdate.MetadataUrl, HttpStatusCode.ServiceUnavailable);
            Assert.That(await _update.IsUpdateAvailableAsync(), Is.False);
        }

        [Test]
        public async Task StagesTheVerifiedLauncherAndAScriptThatSwapsItIn()
        {
            Release("newer launcher");
            _server.Text(LauncherSelfUpdate.DownloadUrl, "newer launcher");

            var script = await _update.StageAsync(null);

            Assert.That(File.ReadAllText(Path.Combine(_root, "staging", "ArcanaLauncher.exe")), Is.EqualTo("newer launcher"));
            var text = File.ReadAllText(script);
            Assert.That(text, Does.Contain("set \"TARGET=" + _exe + "\""));
            Assert.That(text, Does.Contain("copy /Y"));
            Assert.That(File.ReadAllText(_exe), Is.EqualTo("this launcher"), "nothing is replaced until the script runs");
        }

        [Test]
        public void ADownloadThatDoesNotMatchTheReleaseIsNeverStaged()
        {
            Release("newer launcher");
            _server.Text(LauncherSelfUpdate.DownloadUrl, "something else entirely");

            Assert.ThrowsAsync<LauncherException>(() => _update.StageAsync(null));

            Assert.That(File.Exists(Path.Combine(_root, "staging", "ArcanaLauncher.exe")), Is.False);
        }
    }
}
