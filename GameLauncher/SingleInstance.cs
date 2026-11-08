using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GameLauncher
{
    /// <summary>
    /// One Arcana Launcher per Windows session: two would download into and swap the same folders. The second start
    /// brings the first window forward and exits. The mutex has the name installer/ArcanaLauncher.iss gives AppMutex,
    /// so setup and uninstall wait until the launcher is closed. Mutex ownership belongs to the thread that took it:
    /// take and dispose it on the UI thread.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        private const int AsfwAny = -1;

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        private readonly Mutex _mutex;
        private EventWaitHandle _show;
        private RegisteredWaitHandle _listening;
        private bool _disposed;

        private SingleInstance(Mutex mutex)
        {
            _mutex = mutex;
        }

        /// <summary>
        /// Takes the launcher mutex, waiting up to <paramref name="wait"/> (a launcher that just updated waits for the old
        /// one to let go). Null when another launcher holds it.
        /// </summary>
        public static SingleInstance TryAcquire(TimeSpan wait, string mutexName = null)
        {
            var mutex = new Mutex(false, mutexName ?? LauncherInfo.MutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                // A launcher that crashed still held it: it is ours now.
                owned = true;
            }
            if (owned) return new SingleInstance(mutex);
            mutex.Dispose();
            return null;
        }

        /// <summary>Calls <paramref name="show"/> (on a pool thread) whenever a second launcher starts.</summary>
        public void ListenForSecondStart(Action show, string eventName = null)
        {
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, eventName ?? LauncherInfo.ShowEventName);
            _listening = ThreadPool.RegisterWaitForSingleObject(_show, (state, timedOut) => show(), null, Timeout.Infinite, false);
        }

        /// <summary>The second launcher's half: let the first take the foreground, and wake it.</summary>
        public static void ShowFirst(string eventName = null)
        {
            AllowSetForegroundWindow(AsfwAny);
            if (EventWaitHandle.TryOpenExisting(eventName ?? LauncherInfo.ShowEventName, out var show))
                using (show)
                    show.Set();
        }

        /// <summary>Releases and closes the mutex so it no longer exists (setup's AppMutex check looks for existence).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _listening?.Unregister(null);
            _show?.Dispose();
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // not owned by this thread
            }
            _mutex.Dispose();
        }
    }
}
