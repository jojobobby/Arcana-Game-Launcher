using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace GameLauncher.Tests
{
    public class LogTests
    {
        [TestCase(@"C:\Users\Jordan\AppData\Local\Arcana Launcher\logs", @"%LOCALAPPDATA%\Arcana Launcher\logs")]
        [TestCase(@"c:/users/jordan/appdata/local/x", @"%LOCALAPPDATA%/x")]
        [TestCase(@"C:\Users\Jordan\Downloads\ArcanaLauncher.exe", @"%USERPROFILE%\Downloads\ArcanaLauncher.exe")]
        [TestCase("signed in as Jordan.", "signed in as %USERNAME%.")]
        [TestCase("Jordanna stays", "Jordanna stays")]
        public void TheWindowsUserNameNeverReachesTheLog(string text, string scrubbed) =>
            Assert.That(Log.Scrub(text, @"C:\Users\Jordan\AppData\Local", @"C:\Users\Jordan", "Jordan"), Is.EqualTo(scrubbed));

        [Test]
        public void AShortUserNameIsLeftAlone() =>
            Assert.That(Log.Scrub("an io error", null, null, "io"), Is.EqualTo("an io error"));

        [Test]
        public void TheLogRollsAtOneMegabyteAndKeepsThree()
        {
            var folder = Path.Combine(Path.GetTempPath(), "arcana-log-" + Guid.NewGuid());
            try
            {
                Log.Init(folder);
                var line = new string('x', 2000);
                for (var i = 0; i < 1600; i++) Log.Info(line);   // a bit over 3 MB
                var files = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n).ToArray();
                Assert.That(files, Is.EqualTo(new[] { "launcher.1.log", "launcher.2.log", "launcher.log" }));
                Assert.That(files.Select(f => new FileInfo(Path.Combine(folder, f)).Length), Has.All.LessThan(Log.MaxBytes + 4096));
            }
            finally
            {
                Log.Init(Path.GetTempPath());
                Directory.Delete(folder, true);
            }
        }
    }

    public class GameProcessesTests
    {
        [Test]
        public void FindsAGameRunningFromItsFolderAndNowhereElse()
        {
            var folder = Path.Combine(Path.GetTempPath(), "arcana-game-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            var exe = Path.Combine(folder, "PING.EXE");
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
            using (var process = Process.Start(new ProcessStartInfo(exe, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true }))
            {
                try
                {
                    Thread.Sleep(300);
                    Assert.That(GameProcesses.AnyRunningFrom(folder, "PING.EXE"), Is.True);
                    Assert.That(GameProcesses.AnyRunningFrom(folder + "-other", "PING.EXE"), Is.False);
                    Assert.That(GameProcesses.AnyRunningFrom(folder, "Arcana.exe"), Is.False);
                }
                finally
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            Assert.That(GameProcesses.AnyRunningFrom(folder, "PING.EXE"), Is.False);
            Directory.Delete(folder, true);
        }
    }

    [NonParallelizable]
    public class SingleInstanceTests
    {
        [Test]
        public void ASecondLauncherCannotTakeTheMutexUntilTheFirstLetsGo()
        {
            using (var first = SingleInstance.TryAcquire(TimeSpan.Zero))
            {
                Assert.That(first, Is.Not.Null, "close any running Arcana Launcher before running the tests");
                SingleInstance second = null;
                var other = new Thread(() =>
                {
                    second = SingleInstance.TryAcquire(TimeSpan.Zero);
                    second?.Dispose();
                });
                other.Start();
                other.Join();
                Assert.That(second, Is.Null);
            }

            SingleInstance after = null;
            var later = new Thread(() =>
            {
                after = SingleInstance.TryAcquire(TimeSpan.Zero);
                after?.Dispose();
            });
            later.Start();
            later.Join();
            Assert.That(after, Is.Not.Null, "disposed means the mutex is free");
        }

        [Test]
        public void ASecondStartWakesTheFirst()
        {
            using (var first = SingleInstance.TryAcquire(TimeSpan.Zero))
            using (var woken = new ManualResetEventSlim())
            {
                first.ListenForSecondStart(woken.Set);
                SingleInstance.ShowFirst();
                Assert.That(woken.Wait(TimeSpan.FromSeconds(5)), Is.True);
            }
        }
    }
}
