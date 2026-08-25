using System;
using System.Diagnostics;
using System.IO;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// OptiClick, if this machine has it.
    ///
    /// Center looks for it ITSELF rather than asking the helper. The widget has to ask - it runs
    /// inside the Game Bar's AppContainer and cannot see %LOCALAPPDATA% - but Center is an ordinary
    /// per-user process and the folder is its own user's. Routing this through a pipe would mean the
    /// button only worked while the Game Bar happened to be open.
    ///
    /// OptiClick is somebody else's application. Center starts it and does nothing else with it: no
    /// arguments, no configuration, no injection. Injection is not something ClawTweaks does, and
    /// the catalog entry that made this button appear says so in its own words - see
    /// GamePresets.Info.AntiCheat.
    /// </summary>
    public static class OptiClick
    {
        /// <summary>
        /// The installed executable, or null.
        ///
        /// "current" is OptiClick's own updater layout - it keeps versioned folders beside it and
        /// points this one at whichever is live - so this path stays right across its updates.
        /// </summary>
        public static string InstalledPath
        {
            get
            {
                try
                {
                    string path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "OptiClickApp", "current", "OptiClick.exe");
                    return File.Exists(path) ? path : null;
                }
                catch { return null; }
            }
        }

        public static bool IsInstalled => InstalledPath != null;

        /// <summary>Starts it. Returns false when it is not there or refused to start - the caller
        /// says so on screen rather than leaving a button that appears to do nothing.</summary>
        public static bool Launch()
        {
            string path = InstalledPath;
            if (path == null) return false;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    // Its own folder: an application that keeps versioned data beside itself would
                    // otherwise inherit Center's working directory.
                    WorkingDirectory = Path.GetDirectoryName(path),
                    UseShellExecute = true,
                });
                return true;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("OptiClick did not start: " + ex.Message);
                return false;
            }
        }
    }
}
