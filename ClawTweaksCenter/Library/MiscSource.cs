using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// One tool the user put in the Misc tab by hand.
    ///
    /// Deliberately NOT a discovered thing. Every other source answers "what is installed"; this one
    /// answers "what did you ask for", and that difference is the whole point of the tab - an
    /// emulator front-end, a fan curve tool, a mod manager. Nothing lands here without being picked.
    /// </summary>
    public sealed class MiscEntry
    {
        /// <summary>Stable identity, made up when the entry is added. NOT the path: renaming or
        /// moving the target must not turn one entry into a second one, and the path is exactly what
        /// a user changes when they reinstall a tool somewhere else.</summary>
        public string Id { get; set; }

        /// <summary>What the tile says. Editable, and the name the cover lookup uses - which is why
        /// renaming has to invalidate the art (see CenterMenuWindow.Misc.cs).</summary>
        public string Title { get; set; }

        /// <summary>Absolute path to the executable, or null for an app that can only be activated
        /// through the shell (packaged apps, and the opaque ProgIDs the Start menu hands out).</summary>
        public string Exe { get; set; }

        /// <summary>Start parameters for an exe entry. Editable on the Rename screen. Ignored for a
        /// shortcut entry: the .lnk carries its own, and a second set here would be two answers to
        /// one question.</summary>
        public string Args { get; set; }

        /// <summary>For an entry whose <see cref="Exe"/> is a .lnk: the program the shortcut pointed at
        /// when it was added. Used for the icon, the Claw profile match and to find the running
        /// program afterwards - NEVER to start it. The shortcut itself is what gets started, so
        /// "Run as administrator", the working folder and the parameters in it all apply 1:1.</summary>
        public string ShortcutTarget { get; set; }

        /// <summary>The Start menu AppID, for entries with no resolvable path. Activated through
        /// <c>shell:appsFolder</c>, which is the documented route and needs no WinRT.</summary>
        public string Aumid { get; set; }
    }

    /// <summary>
    /// The Misc list on disk.
    ///
    /// A file rather than the registry, unlike <see cref="Core.CenterSettings"/>: this is a list that
    /// grows, not a handful of switches, and a JSON array is something the user can look at and
    /// repair if it ever goes wrong.
    /// </summary>
    public static class MiscStore
    {
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center");

        private static string FilePath => Path.Combine(Dir, "misc.json");

        public static List<MiscEntry> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<MiscEntry>();
                var list = JsonSerializer.Deserialize<List<MiscEntry>>(File.ReadAllText(FilePath));
                if (list == null) return new List<MiscEntry>();

                // An entry that can be neither started nor named is not repairable from here, and
                // showing it would give the user a tile that does nothing when pressed.
                list.RemoveAll(e => e == null
                                    || string.IsNullOrWhiteSpace(e.Title)
                                    || (string.IsNullOrWhiteSpace(e.Exe) && string.IsNullOrWhiteSpace(e.Aumid)));
                foreach (var e in list)
                    if (string.IsNullOrWhiteSpace(e.Id)) e.Id = Guid.NewGuid().ToString("N");
                return list;
            }
            catch { return new List<MiscEntry>(); }
        }

        /// <summary>Writes to a temporary file and moves it over the real one: a half-written list is
        /// indistinguishable from a corrupt one on the next start, and the read above would then quietly
        /// drop every tool the user had added.</summary>
        public static void Save(IReadOnlyList<MiscEntry> entries)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(entries,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }

        /// <summary>
        /// A stored entry as the library sees it.
        ///
        /// <see cref="GameEntry.InstallDir"/> stays EMPTY on purpose. The cross-source dedupe in
        /// GameLibrary keys on the install folder, and a tool sitting in the same folder as a game -
        /// a mod manager next to the game it manages is the normal case - would otherwise delete one
        /// of the two.
        /// </summary>
        public static GameEntry ToGameEntry(MiscEntry entry)
        {
            return new GameEntry
            {
                Id = entry.Id,
                Store = GameStore.Misc,
                Title = entry.Title,
                ExePath = MiscShortcut.IsShortcut(entry.Exe) ? entry.ShortcutTarget : entry.Exe,
                LaunchExe = entry.Exe,
                LaunchArgs = MiscShortcut.IsShortcut(entry.Exe) ? null : entry.Args,
                LaunchUri = string.IsNullOrWhiteSpace(entry.Aumid) ? null : "shell:appsFolder\\" + entry.Aumid,
            };
        }
    }

    /// <summary>
    /// Publishes the hand-picked Misc entries into the library.
    ///
    /// It scans nothing - it reads the list the user built. It is still a source rather than a
    /// special case in the UI so that art resolution, the cover download and the grid all treat these
    /// entries exactly like any other tile, with no second code path to keep in step.
    /// </summary>
    public sealed class MiscSource : IGameSource
    {
        public GameStore Store => GameStore.Misc;

        public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken ct)
        {
            IReadOnlyList<GameEntry> list = Build();
            return Task.FromResult(list);
        }

        public static List<GameEntry> Build()
        {
            var games = new List<GameEntry>();
            foreach (var entry in MiscStore.Load()) games.Add(MiscStore.ToGameEntry(entry));
            return games;
        }

        /// <summary>
        /// Starts one Misc entry.
        ///
        /// Three routes, decided by what the entry is, not by trying and seeing:
        ///   - a .lnk goes through the shell, exactly like a double-click on it;
        ///   - an exe is started directly with its own folder as the working directory - tools read
        ///     configuration next to themselves as routinely as emulators do;
        ///   - everything else goes through <c>shell:appsFolder</c>, which is what a packaged app and
        ///     an opaque Start menu ProgID both need.
        ///
        /// ⚠️ ERROR 740 IS RETRIED THROUGH THE SHELL. A bare CreateProcess on an exe that asks for
        /// admin rights fails with "the requested operation requires elevation" and shows no prompt.
        /// The shell start shows Windows' own UAC prompt FOR THAT PROGRAM - Center itself stays
        /// unelevated, so "Center never asks for admin" holds. It is the same as a double-click in
        /// Explorer on something the user picked.
        /// </summary>
        public static bool Launch(GameEntry game) => Launch(game, out _);

        public static bool Launch(GameEntry game, out Process startedProcess)
        {
            startedProcess = null;
            if (game == null) return false;

            if (!string.IsNullOrEmpty(game.LaunchExe) && File.Exists(game.LaunchExe))
            {
                if (MiscShortcut.IsShortcut(game.LaunchExe))
                    return ShellStart(game.LaunchExe, null, null, game.Title, out startedProcess);

                string args = game.LaunchArgs ?? string.Empty;
                string workDir = Path.GetDirectoryName(game.LaunchExe);
                try
                {
                    startedProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = game.LaunchExe,
                        Arguments = args,
                        WorkingDirectory = workDir,
                        UseShellExecute = false,
                    });
                    return true;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
                {
                    Core.InstallLog.Write("[Misc] '" + game.Title + "' needs admin rights (740) - starting it through the shell so Windows asks: " + game.LaunchExe);
                    return ShellStart(game.LaunchExe, args, workDir, game.Title, out startedProcess);
                }
                catch (Win32Exception ex)
                {
                    Core.InstallLog.Write("[Misc] start failed for '" + game.Title + "' (Win32 " + ex.NativeErrorCode + ": " + ex.Message + "): " + game.LaunchExe);
                }
                catch (Exception ex)
                {
                    Core.InstallLog.Write("[Misc] start failed for '" + game.Title + "' (" + ex.GetType().Name + ": " + ex.Message + "): " + game.LaunchExe);
                }
            }
            else if (!string.IsNullOrEmpty(game.LaunchExe))
            {
                Core.InstallLog.Write("[Misc] '" + game.Title + "' points at a file that is gone: " + game.LaunchExe);
            }

            if (string.IsNullOrEmpty(game.LaunchUri)) return false;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = game.LaunchUri, UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Misc] start failed for '" + game.Title + "' (" + ex.Message + "): " + game.LaunchUri);
                return false;
            }
        }

        private const int ErrorElevationRequired = 740;
        private const int ErrorCancelled = 1223;

        /// <summary>A start through the shell. Returns true when the shell accepted it; the process
        /// handle may still be null (a shortcut to a packaged app, or a program that hands off to an
        /// already running copy), and the tracker then watches the program's folder instead.</summary>
        private static bool ShellStart(string file, string args, string workDir, string title, out Process startedProcess)
        {
            startedProcess = null;
            try
            {
                var psi = new ProcessStartInfo { FileName = file, UseShellExecute = true };
                if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
                if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
                startedProcess = Process.Start(psi);
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                Core.InstallLog.Write("[Misc] '" + title + "': the UAC prompt was declined.");
                return false;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Misc] shell start failed for '" + title + "' (" + ex.Message + "): " + file);
                return false;
            }
        }
    }

    /// <summary>Shortcut (.lnk) entries in the Misc tab.</summary>
    public static class MiscShortcut
    {
        public static bool IsShortcut(string path) =>
            !string.IsNullOrEmpty(path) && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

        /// <summary>The program a shortcut points at, or null. Read once, when the entry is added -
        /// through WScript.Shell, which ships with Windows (the same object SelfInstaller uses to
        /// write Center's own shortcuts).</summary>
        public static string ResolveTarget(string lnkPath)
        {
            object shell = null, shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                shortcut = ((dynamic)shell).CreateShortcut(lnkPath);
                string target = ((dynamic)shortcut).TargetPath as string;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            catch { return null; }
            finally
            {
                if (shortcut != null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); } catch { }
                if (shell != null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }
    }
}
