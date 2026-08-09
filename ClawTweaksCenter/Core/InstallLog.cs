using System;
using System.IO;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Durable copy of the install flow's log, next to the other Center logs.
    ///
    /// The install log used to exist only as rows in the UI panel, which the flow then scrolls past and
    /// finally replaces when onboarding takes over — so the moment an install needed explaining, the
    /// evidence was already gone. That is exactly the situation this file removes: a user can hand over
    /// %LOCALAPPDATA%\ClawTweaks\center_install.log after the fact, and it lines up by wall-clock time
    /// with the helper's own log.
    ///
    /// Best-effort throughout: logging must never be able to fail an install.
    /// </summary>
    internal static class InstallLog
    {
        private static readonly string Path_ = BuildPath();

        private static string BuildPath()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawTweaks");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "center_install.log");
            }
            catch { return null; }
        }

        /// <summary>Marks the start of a run so consecutive attempts stay distinguishable in one file.</summary>
        public static void StartSession(string what)
        {
            string version;
            try
            {
                version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            }
            catch { version = "?"; }

            Write(string.Empty);
            Write($"=== {what} — Center {version} ===");
        }

        /// <summary>One log line. <paramref name="indent"/> mirrors the UI's sub-line style.</summary>
        public static void Write(string message, bool indent = false)
        {
            if (Path_ == null) return;
            try
            {
                string line = message.Length == 0
                    ? Environment.NewLine
                    : $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {(indent ? "    " : "")}{message}{Environment.NewLine}";
                File.AppendAllText(Path_, line);
            }
            catch { /* never let logging break an install */ }
        }
    }
}
