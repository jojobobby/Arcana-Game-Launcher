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
    /// <summary>
    /// The development server's access code: where the launcher sends it, how it is kept, and what the launcher makes
    /// of a server that wants one. The fake server here is locked the way ClientReleaseStore is: 401 without the
    /// code, 429 once it has had enough wrong ones.
    /// </summary>
    public class AccessCodeTests
    {
        private const string Code = "open-sesame";
        private const string DevMetadata = "https://devapp.tidansrealm.com/client/metadata";
        private const string DevDownload = "https://devapp.tidansrealm.com/client/download";
        private const string LiveMetadata = "https://app.tidansrealm.com/client/metadata";

        private string _temp;
        private FakeServer _server;
        private AccessCodes _codes;
        private HttpClient _http;
        private List<(string Url, string Code)> _sent;
        private int _wrong;

        [SetUp]
        public void Setup()
        {
            _temp = Path.Combine(Path.GetTempPath(), "arcana-launcher-" + Guid.NewGuid());
            Directory.CreateDirectory(_temp);
            _server = new FakeServer();
            _codes = new AccessCodes(_server);
            _http = new HttpClient(_codes, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            _sent = new List<(string, string)>();
            _wrong = 0;
        }

        [TearDown]
        public void Cleanup()
        {
            _http.Dispose();
            if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
        }

        private static string Offered(HttpRequestMessage request) =>
            request.Headers.TryGetValues(AccessCodes.Header, out var values) ? values.Single() : null;

        /// <summary>Notes the code each request to <paramref name="url"/> carried, and answers 200.</summary>
        private void Open(string url, byte[] body = null) => _server.Route(url, (request, cancel) =>
        {
            _sent.Add((url, Offered(request)));
            return Task.FromResult(FakeServer.Serve(request, body ?? new byte[] { 1 }));
        });

        /// <summary>Serves <paramref name="body"/> at <paramref name="url"/> only to the code; three wrong ones and it says 429.</summary>
        private void Locked(string url, byte[] body) => _server.Route(url, (request, cancel) =>
        {
            var offered = Offered(request);
            _sent.Add((url, offered));
            if (_wrong >= 3) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429));
            if (offered == Code) return Task.FromResult(FakeServer.Serve(request, body, "\"" + FakeServer.Sha256(body) + "\""));
            if (offered != null) _wrong++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        private ClientInstaller Installer(LauncherChannel channel) =>
            new ClientInstaller(_http, channel, Path.Combine(_temp, "launcher"), Path.Combine(_temp, "downloads"),
                FakeServer.NoWait(new List<TimeSpan>()), folder => false, drive => null, TimeSpan.FromMilliseconds(300));

        private byte[] PublishLocked(string build, params (string Name, string Body)[] files)
        {
            var zip = FakeServer.Zip(files);
            Locked(DevMetadata, System.Text.Encoding.UTF8.GetBytes(FakeServer.Metadata(zip, build, files: files, channel: "development")));
            Locked(DevDownload, zip);
            return zip;
        }

        // ---- where the code goes ----

        [Test]
        public async Task TheCodeGoesOnlyToItsOwnServersClientRoutes()
        {
            _codes.Set("https://devapp.tidansrealm.com", Code);
            var urls = new[]
            {
                DevMetadata, DevDownload,
                LiveMetadata,
                "https://devapp.tidansrealm.com/account/verify",
                "https://devapp.tidansrealm.com/",
                "http://devapp.tidansrealm.com/client/metadata",
                "https://devapp.tidansrealm.com:8443/client/metadata",
                "https://devapp.tidansrealm.com.evil.example/client/metadata",
                "https://github.com/jojobobby/Arcana-Game-Launcher/releases/latest/download/launcher-metadata.json",
            };
            foreach (var url in urls)
            {
                Open(url);
                (await _http.GetAsync(url)).Dispose();
            }
            Assert.That(_sent.Where(s => s.Code != null).Select(s => s.Url), Is.EquivalentTo(new[] { DevMetadata, DevDownload }));
            Assert.That(_sent.Where(s => s.Code != null).Select(s => s.Code), Is.All.EqualTo(Code));
        }

        [Test]
        public async Task AForgottenCodeIsNoLongerSent()
        {
            Open(DevMetadata);
            _codes.Set("https://devapp.tidansrealm.com", Code);
            _codes.Set("https://devapp.tidansrealm.com/", null);
            (await _http.GetAsync(DevMetadata)).Dispose();
            Assert.That(_sent.Single().Code, Is.Null);
        }

        [Test]
        public async Task ACodeBeingTriedWinsOverTheRememberedOne()
        {
            Open(DevMetadata);
            _codes.Set("https://devapp.tidansrealm.com", Code);
            using (var request = new HttpRequestMessage(HttpMethod.Get, DevMetadata))
            {
                request.Headers.TryAddWithoutValidation(AccessCodes.Header, "another");
                (await _http.SendAsync(request)).Dispose();
            }
            Assert.That(_sent.Single().Code, Is.EqualTo("another"));
        }

        [TestCase("open-sesame", "open-sesame")]
        [TestCase("  open-sesame \t", "open-sesame")]
        [TestCase("two words", "two words")]
        [TestCase("", null)]
        [TestCase("   ", null)]
        [TestCase(null, null)]
        [TestCase("line\nbreak", null)]
        [TestCase("café", null)]
        public void OnlyWhatAHeaderCanCarryIsACode(string typed, string sent)
        {
            Assert.That(AccessCodes.Clean(typed), Is.EqualTo(sent));
        }

        [Test]
        public void AnOverlongCodeIsNotACode()
        {
            Assert.That(AccessCodes.Clean(new string('a', AccessCodes.MaxLength)), Is.Not.Null);
            Assert.That(AccessCodes.Clean(new string('a', AccessCodes.MaxLength + 1)), Is.Null);
        }

        // ---- how it is kept ----

        [Test]
        public void TheCodeIsRememberedButNeverWrittenInTheClear()
        {
            var path = Path.Combine(_temp, LauncherSettings.FileName);
            new LauncherSettings { Channel = "development", DevelopmentCode = Code }.Save(path);
            Assert.That(File.ReadAllText(path), Does.Not.Contain(Code));
            var loaded = LauncherSettings.Load(path);
            Assert.That(loaded.DevelopmentCode, Is.EqualTo(Code));
            Assert.That(loaded.Channel, Is.EqualTo("development"));
        }

        [Test]
        public void SettingsWithoutACodeStillLoad()
        {
            var path = Path.Combine(_temp, LauncherSettings.FileName);
            File.WriteAllText(path, "{\"channel\":\"development\"}");
            Assert.That(LauncherSettings.Load(path).DevelopmentCode, Is.Null);
            Assert.That(LauncherSettings.Load(path).Channel, Is.EqualTo("development"));
            new LauncherSettings { Channel = "production" }.Save(path);
            Assert.That(LauncherSettings.Load(path).DevelopmentCode, Is.Null);
        }

        [TestCase("not base64 !!")]
        [TestCase("AAAA")]
        [TestCase("")]
        public void ADamagedSavedCodeIsNoCode(string stored)
        {
            var path = Path.Combine(_temp, LauncherSettings.FileName);
            File.WriteAllText(path, "{\"channel\":\"development\",\"development_code\":\"" + stored + "\"}");
            var loaded = LauncherSettings.Load(path);
            Assert.That(loaded.DevelopmentCode, Is.Null);
            Assert.That(loaded.Channel, Is.EqualTo("development"));
        }

        // ---- what the launcher makes of a locked server ----

        [Test]
        public async Task TheServerSaysWhetherACodeIsRight()
        {
            PublishLocked("dev1", ("Arcana.exe", "game"));
            var installer = Installer(LauncherChannel.Development);
            Assert.That(await installer.ProbeAccessAsync(null), Is.EqualTo(AccessAnswer.Refused));
            Assert.That(await installer.ProbeAccessAsync("nope"), Is.EqualTo(AccessAnswer.Refused));
            Assert.That(await installer.ProbeAccessAsync(Code), Is.EqualTo(AccessAnswer.Allowed));
        }

        [Test]
        public async Task TooManyWrongCodesIsItsOwnAnswer()
        {
            PublishLocked("dev1", ("Arcana.exe", "game"));
            var installer = Installer(LauncherChannel.Development);
            for (var i = 0; i < 3; i++)
                Assert.That(await installer.ProbeAccessAsync("guess" + i), Is.EqualTo(AccessAnswer.Refused));
            Assert.That(await installer.ProbeAccessAsync(Code), Is.EqualTo(AccessAnswer.TooManyTries));
        }

        [Test]
        public async Task AnOpenServerAllowsWithoutACode()
        {
            Open(LiveMetadata);
            Assert.That(await Installer(LauncherChannel.Production).ProbeAccessAsync(null), Is.EqualTo(AccessAnswer.Allowed));
            Assert.That(_sent.Single().Code, Is.Null);
        }

        [Test]
        public async Task AServerWithNoClientYetAsksForNoCode()
        {
            // nothing routed: the fake answers 404, as a server with no baked client does
            Assert.That(await Installer(LauncherChannel.Development).ProbeAccessAsync(null), Is.EqualTo(AccessAnswer.Allowed));
        }

        [Test]
        public async Task AServerThatIsDownSaysNothingAboutTheCode()
        {
            _server.Status(DevMetadata, HttpStatusCode.ServiceUnavailable);
            Assert.That(await Installer(LauncherChannel.Development).ProbeAccessAsync(Code), Is.EqualTo(AccessAnswer.Unreachable));
            _server.Route(DevMetadata, (request, cancel) => throw new HttpRequestException("no route to host"));
            Assert.That(await Installer(LauncherChannel.Development).ProbeAccessAsync(Code), Is.EqualTo(AccessAnswer.Unreachable));
        }

        [Test]
        public async Task ThePollSaysWhenTheServerWantsACode()
        {
            PublishLocked("dev1", ("Arcana.exe", "game"));
            var installer = Installer(LauncherChannel.Development);
            var locked = await installer.PollAsync(null, CancellationToken.None);
            Assert.That(locked.CodeNeeded, Is.True);
            Assert.That(locked.Reached, Is.True, "a server that asks for a code is up");
            Assert.That(locked.Metadata, Is.Null);

            _codes.Set(LauncherChannel.Development.ServerUrl, Code);
            var open = await installer.PollAsync(null, CancellationToken.None);
            Assert.That(open.CodeNeeded, Is.False);
            Assert.That(open.Metadata.Build, Is.EqualTo("dev1"));
        }

        [Test]
        public void InstallingWithoutTheCodeSaysSoInsteadOfRetrying()
        {
            PublishLocked("dev1", ("Arcana.exe", "game"));
            var failure = Assert.ThrowsAsync<LauncherException>(() => Installer(LauncherChannel.Development).FetchMetadataAsync());
            Assert.That(failure.Title, Is.EqualTo("Access code needed"));
            Assert.That(_sent.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task WithTheCodeTheWholeInstallGoesThrough()
        {
            PublishLocked("dev1", ("Arcana.exe", "game"), ("lime.ndll", "native"));
            _codes.Set(LauncherChannel.Development.ServerUrl, Code);
            var installer = Installer(LauncherChannel.Development);
            await installer.InstallAsync(await installer.FetchMetadataAsync(), null);

            Assert.That(installer.Inspect().Health, Is.EqualTo(InstallHealth.Ready));
            Assert.That(File.ReadAllText(Path.Combine(_temp, "launcher", "Arcana-development", "Arcana.exe")), Is.EqualTo("game"));
            Assert.That(_sent, Is.Not.Empty);
            Assert.That(_sent.Select(s => s.Code), Is.All.EqualTo(Code), "the metadata and the download both carry the code");
            Assert.That(_sent.Select(s => s.Url), Does.Contain(DevDownload));
        }
    }
}
