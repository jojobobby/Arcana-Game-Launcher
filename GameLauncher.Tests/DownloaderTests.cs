using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GameLauncher.Tests
{
    public class DownloaderTests
    {
        private const string Url = "https://app.tidansrealm.com/client/download";
        private static readonly byte[] Body = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31)).ToArray();
        private static readonly string Sha = FakeServer.Sha256(Body);
        private static readonly string ETag = "\"" + Sha + "\"";
        private string _root;
        private string _target;
        private FakeServer _server;
        private List<TimeSpan> _waits;
        private Downloader _downloader;
        private List<DownloadProgress> _reports;

        [SetUp]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "arcana-download-" + Guid.NewGuid());
            _target = Path.Combine(_root, "downloads", Sha + ".zip");
            _server = new FakeServer();
            _waits = new List<TimeSpan>();
            _reports = new List<DownloadProgress>();
            _downloader = new Downloader(_server.Client(), TimeSpan.FromMilliseconds(300), FakeServer.NoWait(_waits));
        }

        [TearDown]
        public void Cleanup()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private Task Fetch(CancellationToken cancel = default) =>
            _downloader.FetchAsync(Url, _target, Body.Length, Sha, ETag, new SyncProgress<DownloadProgress>(_reports.Add), cancel);

        [Test]
        public async Task DownloadsVerifiesAndRenamesIntoPlace()
        {
            _server.Bytes(Url, Body, ETag);
            await Fetch();
            Assert.That(File.ReadAllBytes(_target), Is.EqualTo(Body));
            Assert.That(File.Exists(_target + ".part"), Is.False);
            Assert.That(_reports.Last().Done, Is.EqualTo(Body.Length));
        }

        [Test]
        public async Task ABrokenConnectionResumesFromTheLastByte()
        {
            _server.Sequence(Url, FakeServer.Cut(Body, 120_000), FakeServer.Answer(Body, ETag));
            await Fetch();

            Assert.That(File.ReadAllBytes(_target), Is.EqualTo(Body));
            Assert.That(_server.Seen, Has.Count.EqualTo(2));
            Assert.That(_server.Seen[1].Range, Is.EqualTo("bytes=120000-"));
            Assert.That(_server.Seen[1].IfRange, Is.EqualTo(ETag), "a resume only continues the same bytes");
            Assert.That(_waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(2) }));
            Assert.That(_reports.Any(r => r.Note != null && r.Note.StartsWith("Connection problem.")), Is.True);
        }

        [Test]
        public async Task AServerThatIgnoresRangesSendsItAllAgainAndThatWorks()
        {
            _server.Sequence(Url, FakeServer.Cut(Body, 120_000), FakeServer.Answer(Body, ETag, ranges: false));
            await Fetch();
            Assert.That(File.ReadAllBytes(_target), Is.EqualTo(Body));
        }

        [Test]
        public async Task ADownloadClosedHalfWayResumesOnTheNextRun()
        {
            _server.Sequence(Url, FakeServer.Cut(Body, 50_000), FakeServer.Cut(Body, 50_000), FakeServer.Cut(Body, 50_000), FakeServer.Cut(Body, 50_000));
            Assert.ThrowsAsync<LauncherException>(() => Fetch());
            Assert.That(new FileInfo(_target + ".part").Length, Is.EqualTo(50_000), "the part survives for the next run");

            _server.Bytes(Url, Body, ETag);
            await Fetch();
            Assert.That(File.ReadAllBytes(_target), Is.EqualTo(Body));
            Assert.That(_server.Seen.Last().Range, Is.EqualTo("bytes=50000-"));
        }

        [Test]
        public async Task ABusyServerIsRetriedAfterItsRetryAfter()
        {
            _server.Sequence(Url, FakeServer.Busy(TimeSpan.FromSeconds(7)), FakeServer.Busy(TimeSpan.FromSeconds(90)), FakeServer.Answer(Body, ETag));
            await Fetch();
            Assert.That(File.ReadAllBytes(_target), Is.EqualTo(Body));
            Assert.That(_waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(7), RetryPolicy.MaxRetryAfter }), "Retry-After honoured, capped at 30 s");
            Assert.That(_reports.First(r => r.Note != null).Note, Is.EqualTo("Server busy. Trying again in 7 s."));
        }

        [Test]
        public void AServerThatStaysBusyEndsInAPlainError()
        {
            _server.Route(Url, FakeServer.Busy());
            var error = Assert.ThrowsAsync<LauncherException>(() => Fetch());
            Assert.That(error.Message, Does.Contain("the server is busy"));
            Assert.That(_server.Seen, Has.Count.EqualTo(4), "one try and three retries");
        }

        [Test]
        public async Task AStalledDownloadIsRetriedNotLeftHanging()
        {
            _server.Sequence(Url, FakeServer.Stall(Body, 100_000), FakeServer.Answer(Body, ETag));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Fetch();
            Assert.That(File.ReadAllBytes(_target), Is.EqualTo(Body));
            Assert.That(_server.Seen[1].Range, Is.EqualTo("bytes=100000-"));
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void BytesThatFailTheHashAreThrownAway()
        {
            var tampered = (byte[])Body.Clone();
            tampered[1234] ^= 0xFF;
            _server.Bytes(Url, tampered, ETag);
            var error = Assert.ThrowsAsync<LauncherException>(() => Fetch());
            Assert.That(error.Message, Does.Contain("integrity"));
            Assert.That(File.Exists(_target), Is.False);
            Assert.That(File.Exists(_target + ".part"), Is.False);
        }

        [Test]
        public void ALongerBodyThanPromisedIsRefused()
        {
            _server.Bytes(Url, Body.Concat(new byte[] { 1, 2, 3 }).ToArray(), ETag);
            var error = Assert.ThrowsAsync<LauncherException>(() => Fetch());
            Assert.That(error.Message, Does.Contain("larger"));
            Assert.That(File.Exists(_target + ".part"), Is.False);
        }

        [Test]
        public async Task AFileAlreadyThereIsNotDownloadedAgain()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_target));
            File.WriteAllBytes(_target, Body);
            await Fetch();
            Assert.That(_server.Seen, Is.Empty);
        }

        [Test]
        public async Task ADamagedCachedFileIsDownloadedAgain()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_target));
            File.WriteAllBytes(_target, Body.Take(1000).ToArray());
            _server.Bytes(Url, Body, ETag);
            await Fetch();
            Assert.That(File.ReadAllBytes(_target), Is.EqualTo(Body));
        }

        [Test]
        public async Task ARangeTheServerRefusesStartsOver()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_target));
            File.WriteAllBytes(_target + ".part", Body.Take(1000).ToArray());
            _server.Sequence(Url,
                (request, cancel) => Task.FromResult(new System.Net.Http.HttpResponseMessage((HttpStatusCode)416)),
                FakeServer.Answer(Body, ETag));
            await Fetch();
            Assert.That(File.ReadAllBytes(_target), Is.EqualTo(Body));
            Assert.That(_server.Seen[1].Range, Is.Null, "after a 416 it starts from byte 0");
        }

        [Test]
        public async Task APartFromAnotherBuildIsNotResumed()
        {
            // An If-Range for other bytes gets the whole new file: the server ignores the range.
            Directory.CreateDirectory(Path.GetDirectoryName(_target));
            File.WriteAllBytes(_target + ".part", new byte[] { 9, 9, 9 });
            _server.Bytes(Url, Body, "\"something-else\"");
            await Fetch();
            Assert.That(File.ReadAllBytes(_target), Is.EqualTo(Body));
        }

        [Test]
        public async Task CancelKeepsWhatWasDownloadedForNextTime()
        {
            using (var cancel = new CancellationTokenSource())
            {
                _server.Route(Url, FakeServer.Stall(Body, 80_000));
                var fetch = _downloader.FetchAsync(Url, _target, Body.Length, Sha, ETag,
                    new SyncProgress<DownloadProgress>(p => { if (p.Done >= 80_000) cancel.Cancel(); }), cancel.Token);
                Assert.That(async () => await fetch, Throws.InstanceOf<OperationCanceledException>());
            }
            Assert.That(new FileInfo(_target + ".part").Length, Is.EqualTo(80_000));

            _server.Bytes(Url, Body, ETag);
            await Fetch();
            Assert.That(File.ReadAllBytes(_target), Is.EqualTo(Body));
        }

        [Test]
        public void ANotFoundIsAPlainErrorWithoutRetries()
        {
            var error = Assert.ThrowsAsync<LauncherException>(() => Fetch());
            Assert.That(error.Message, Does.Contain("404"));
            Assert.That(_server.Seen, Has.Count.EqualTo(1));
        }
    }
}
