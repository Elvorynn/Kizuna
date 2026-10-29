using System.Diagnostics;

namespace Kizuna
{
    /// <summary>Opens a file with the OS-associated application (e.g. VLC for videos).</summary>
    internal static class MediaLauncher
    {
        /// <summary>Launches <paramref name="path"/> via the shell. Throws on failure so
        /// callers can surface the reason.</summary>
        public static void Open(string path)
        {
            // UseShellExecute=true is required to resolve the file's default program.
            // It is the default on .NET Framework, but we set it explicitly to be safe.
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            Process.Start(psi);
        }
    }
}
