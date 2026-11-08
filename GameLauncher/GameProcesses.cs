using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLauncher
{
    /// <summary>
    /// Is the game running from a folder? A running Arcana.exe locks its folder, so an update or an uninstall of that
    /// folder must wait for it. The path comes from QueryFullProcessImageName, which needs only limited query rights,
    /// so it also works for a game started elevated.
    /// </summary>
    internal static class GameProcesses
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>True when a process named like <paramref name="exeName"/> runs an exe inside <paramref name="folder"/>.</summary>
        public static bool AnyRunningFrom(string folder, string exeName)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)))
            {
                using (process)
                {
                    var path = ImagePath(process.Id);
                    if (path != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static string ImagePath(int processId)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var size = 1024;
                var name = new StringBuilder(size);
                return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
