using System;
using System.Diagnostics;
using System.IO;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Uninstalls a non-Steam game through the entry it registered with Windows (Settings > Apps).
    ///
    /// THE GAME'S OWN UNINSTALLER DOES THE WORK AND ASKS. The same shape as steam://uninstall: Center
    /// finds the command and starts it, and never deletes a file itself. The interactive
    /// UninstallString is used, never the quiet one - the vendor's "are you sure" is the confirmation.
    ///
    /// CENTER STAYS UNELEVATED. The command is started through the shell, so an uninstaller that
    /// needs admin raises its own UAC prompt from its manifest - the same hand-off as a driver
    /// installer. Center never adds a runas.
    ///
    /// Matched by FOLDER, never by title: a title is ambiguous across editions, a folder is not.
    /// </summary>
    public static class GameUninstaller
    {
        public enum Kind
        {
            /// <summary>The uninstaller the game registered with Windows. It asks, not us.</summary>
            Registered,
            /// <summary>Epic has no uninstall link (tested 2026-09-18: <c>?action=uninstall</c> is
            /// ignored in both URI forms). The library is opened, the user uninstalls there.</summary>
            EpicLibrary,
            /// <summary>An Xbox/Store package, removed for this user. Nobody else asks, so the menu
            /// takes a second press first.</summary>
            XboxPackage,
        }

        public sealed class Command
        {
            public Kind Kind;
            /// <summary>Package family name, XboxPackage only.</summary>
            public string PackageFamily;
            public string DisplayName;
            public string FileName;
            public string Arguments;
        }

        /// <summary>
        /// The registered uninstaller for this game, or null. Steam, ROMs and hand-added apps never
        /// get one here: Steam has its own route, the other two are not installations.
        /// </summary>
        public static Command Find(GameEntry game)
        {
            if (game == null || !game.Installed) return null;
            if (game.Store == GameStore.Steam || game.Store == GameStore.Misc || game.Store == GameStore.Playnite)
                return null;

            if (game.Store == GameStore.Epic)
                return new Command { Kind = Kind.EpicLibrary, FileName = "com.epicgames.launcher://store/library", Arguments = string.Empty };

            if (game.Store == GameStore.Xbox)
            {
                // The Id is the AUMID ("Family_publisher!App"); the family is everything before '!'.
                string id = game.Id ?? string.Empty;
                int bang = id.IndexOf('!');
                string family = bang > 0 ? id.Substring(0, bang) : null;
                if (family == null || family.IndexOf('_') < 0) return null;
                return new Command { Kind = Kind.XboxPackage, PackageFamily = family, DisplayName = game.Title };
            }

            string dir = Normalize(game.InstallDir);
            // A drive root or a first-level folder ("C:\Games") is a library folder, not a game:
            // everything installed below it would match.
            if (dir == null || dir.Split('\\').Length < 3) return null;

            try
            {
                Command exact = null, inside = null;
                int insideCount = 0;
                foreach (var u in StoreRegistry.Uninstalls())
                {
                    if (string.IsNullOrWhiteSpace(u.UninstallString)) continue;
                    if (!Matches(u, dir)) continue;

                    var cmd = Split(u.UninstallString);
                    if (cmd == null) continue;
                    cmd.DisplayName = u.DisplayName;

                    if (string.Equals(Normalize(u.InstallLocation), dir, StringComparison.OrdinalIgnoreCase))
                    {
                        if (exact == null) exact = cmd;
                    }
                    else
                    {
                        inside = cmd;
                        insideCount++;
                    }
                }
                // Exact folder wins. Otherwise only an unambiguous single entry: two different
                // programs registered inside one game folder means we cannot tell which is the game.
                if (exact != null) return exact;
                if (insideCount == 1) return inside;
                if (insideCount > 1)
                    Core.InstallLog.Write("[Uninstall] " + insideCount + " entries inside '" + dir + "' - none offered");
            }
            catch { }
            return null;
        }

        /// <summary>
        /// An entry belongs to the game when its InstallLocation IS the game folder or sits inside
        /// it, or - for entries that leave InstallLocation empty, which Inno and many others do -
        /// when its uninstaller or icon lives inside the game folder.
        ///
        /// NEVER the other way round. An InstallLocation that is a PARENT of the game folder
        /// ("C:\Games") would match every game under it and uninstall the wrong one.
        /// </summary>
        private static bool Matches(StoreRegistry.UninstallEntry u, string dir)
        {
            if (IsAtOrInside(Normalize(u.InstallLocation), dir)) return true;
            if (IsAtOrInside(Normalize(ExePart(u.UninstallString)), dir)) return true;
            if (IsAtOrInside(Normalize(ExePart(u.DisplayIcon)), dir)) return true;
            return false;
        }

        private static bool IsAtOrInside(string path, string dir)
        {
            if (path == null) return false;
            return string.Equals(path, dir, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The executable path at the front of a command line or icon value
        /// ("C:\x\unins000.exe" /SILENT, C:\x\game.exe,0), or null.</summary>
        private static string ExePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim();
            if (v.StartsWith("\""))
            {
                int end = v.IndexOf('"', 1);
                return end > 1 ? v.Substring(1, end - 1) : null;
            }
            int exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? v.Substring(0, exe + 4) : null;
        }

        /// <summary>
        /// Splits a registered UninstallString into file and arguments. Three shapes are in use:
        /// a quoted path plus arguments, an unquoted path (MsiExec.exe /X{...}), and a URI
        /// (uplay://uninstall/N). The URI goes to the shell whole.
        /// </summary>
        private static Command Split(string raw)
        {
            string v = raw.Trim();
            if (v.StartsWith("\""))
            {
                int end = v.IndexOf('"', 1);
                if (end <= 1) return null;
                return new Command { FileName = v.Substring(1, end - 1), Arguments = v.Substring(end + 1).Trim() };
            }
            int exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0)
                return new Command { FileName = v.Substring(0, exe + 4), Arguments = v.Substring(exe + 4).Trim() };
            if (v.Contains("://"))
                return new Command { FileName = v, Arguments = string.Empty };
            return null;
        }

        /// <summary>Starts the uninstaller. The result says whether it STARTED, not whether the game
        /// is gone - the entry stays on the shelf until the next scan finds the folder empty.</summary>
        public static bool Run(Command cmd)
        {
            if (cmd == null) return false;
            if (cmd.Kind == Kind.XboxPackage) return false;   // see RemoveXboxPackageAsync
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = cmd.FileName,
                    Arguments = cmd.Arguments ?? string.Empty,
                    UseShellExecute = true,
                });
                return true;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Uninstall] '" + cmd.FileName + "' failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Removes an Xbox/Store package for the current user - the same per-user deployment
        /// Add-AppxPackage uses, so no admin. Takes minutes for a large game; returns the error text,
        /// or null on success.
        /// </summary>
        public static System.Threading.Tasks.Task<string> RemoveXboxPackageAsync(Command cmd)
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                if (cmd?.PackageFamily == null) return "no package";
                // The family name is validated to [A-Za-z0-9._-] so it cannot break out of the quotes.
                foreach (char c in cmd.PackageFamily)
                    if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')) return "bad package name";

                string script = "$p = Get-AppxPackage | Where-Object { $_.PackageFamilyName -eq '" + cmd.PackageFamily + "' }; " +
                                "if (-not $p) { Write-Error 'package not installed'; exit 2 }; " +
                                "$p | Remove-AppxPackage -ErrorAction Stop";
                string winPs = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                try
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = File.Exists(winPs) ? winPs : "powershell.exe",
                        Arguments = "-NoProfile -NonInteractive -Command \"" + script + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    });
                    if (proc == null) return "powershell did not start";
                    string err = proc.StandardError.ReadToEnd();
                    proc.StandardOutput.ReadToEnd();
                    if (!proc.WaitForExit(30 * 60 * 1000)) { try { proc.Kill(); } catch { } return "timed out"; }
                    return proc.ExitCode == 0 && string.IsNullOrWhiteSpace(err) ? null
                         : (string.IsNullOrWhiteSpace(err) ? "exit " + proc.ExitCode : err.Trim());
                }
                catch (Exception ex) { return ex.Message; }
            });
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string p = path.Trim().Trim('"').Replace('/', '\\');
                if (p.Length < 3 || p[1] != ':') return null;   // relative or not a path at all
                return Path.GetFullPath(p).TrimEnd('\\');
            }
            catch { return null; }
        }
    }
}
