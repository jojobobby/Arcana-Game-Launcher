using System;

namespace GameLauncher
{
    /// <summary>A failure whose message is already written for the player; the window shows it as is.</summary>
    internal sealed class LauncherException : Exception
    {
        public string Title { get; }

        public LauncherException(string title, string message, Exception inner = null) : base(message, inner)
        {
            Title = title;
        }
    }
}
