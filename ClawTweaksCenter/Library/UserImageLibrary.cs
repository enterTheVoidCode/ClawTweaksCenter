using System;
using System.Collections.Generic;
using System.IO;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// The user's own pictures, as a source of cover art and of the Center background.
    ///
    /// -- Why a FOLDER and not a file dialog ----------------------------------------------------
    /// Center is driven with a gamepad, usually as the full screen experience. The Windows file
    /// dialog is a mouse surface: it is reachable here only because Misc's "add a program" has no
    /// alternative, and it is the wrong shape for something people do repeatedly. A folder the user
    /// names ONCE turns "find that file again" into a grid of pictures the D-pad already knows how
    /// to walk - the same grid the SteamGridDB picker uses.
    ///
    /// Sub-folders are included: the point is that someone can drop pictures anywhere under their
    /// downloads folder and have them show up, without sorting anything first.
    /// </summary>
    public static class UserImageLibrary
    {
        /// <summary>What WPF can decode without a codec pack. WebP is deliberately absent - WIC
        /// carries no WebP decoder out of the box, so listing one would put tiles on screen that
        /// stay grey with nothing saying why.</summary>
        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp",
        };

        /// <summary>
        /// A ceiling on how many pictures are listed at all.
        ///
        /// A downloads folder that has been collecting for two years is a plausible target for this,
        /// and every tile costs a decode. The list is newest first (see <see cref="Scan"/>), so the
        /// cap cuts the oldest - which is the half nobody is scrolling to.
        /// </summary>
        public const int MaxImages = 400;

        public static string Folder => Core.CenterSettings.UserImageFolder;

        /// <summary>
        /// True only when the stored folder is still THERE. A path that has been renamed, or lived on
        /// a stick that is now unplugged, must read as "not set up" and offer the suggestions again
        /// rather than opening an empty grid that looks like the folder had no pictures in it.
        /// </summary>
        public static bool HasFolder
        {
            get
            {
                string folder = Folder;
                if (string.IsNullOrWhiteSpace(folder)) return false;
                try { return Directory.Exists(folder); }
                catch { return false; }
            }
        }

        public readonly struct Suggestion
        {
            public Suggestion(string label, string path) { Label = label; Path = path; }
            public string Label { get; }
            public string Path { get; }
        }

        /// <summary>
        /// The folders worth offering without asking anyone to type a path: where a downloaded cover
        /// lands, where a screenshot lands, and where Windows itself puts pictures. Only the ones
        /// that exist - an offer that opens an empty grid is worse than one fewer choice.
        /// </summary>
        public static List<Suggestion> Suggestions()
        {
            var list = new List<Suggestion>();
            AddIfPresent(list, "Downloads", DownloadsPath());
            AddIfPresent(list, "Desktop", SafeFolder(Environment.SpecialFolder.DesktopDirectory));
            AddIfPresent(list, "Pictures", SafeFolder(Environment.SpecialFolder.MyPictures));
            return list;
        }

        private static void AddIfPresent(List<Suggestion> list, string label, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (!Directory.Exists(path)) return; }
            catch { return; }
            list.Add(new Suggestion(label, path));
        }

        private static string SafeFolder(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch { return null; }
        }

        /// <summary>
        /// Downloads has NO SpecialFolder value - it is a Known Folder and nothing else. Composing it
        /// from the profile path is the pragmatic answer, and it is wrong for anyone who has moved
        /// theirs; that costs one suggestion, and typing the path in Library settings still works.
        /// </summary>
        private static string DownloadsPath()
        {
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
            }
            catch { return null; }
        }

        /// <summary>
        /// Every usable picture under the stored folder, newest first.
        ///
        /// CALL IT OFF THE UI THREAD. Walking a large downloads tree takes long enough to be a
        /// visible stall, and the folder the user picks is not something this code gets to bound.
        ///
        /// EnumerationOptions rather than SearchOption.AllDirectories, and the difference is not
        /// cosmetic: the old overload throws on the first unreadable sub-folder and abandons the
        /// whole walk, so ONE protected directory anywhere underneath would return nothing at all.
        /// IgnoreInaccessible skips it and carries on.
        /// </summary>
        public static List<string> Scan()
        {
            var results = new List<string>();
            string folder = Folder;
            if (string.IsNullOrWhiteSpace(folder)) return results;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // Junctions and symlinks are how a tree turns into a cycle, and a picture reached
                // twice through two paths is the same picture listed twice.
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
                // Deep enough for "Downloads\some archive\extracted\covers", short of walking a whole
                // drive that somebody pointed at the root of.
                MaxRecursionDepth = 6,
            };

            var found = new List<KeyValuePair<string, DateTime>>();
            try
            {
                foreach (string path in Directory.EnumerateFiles(folder, "*", options))
                {
                    if (!Extensions.Contains(Path.GetExtension(path))) continue;
                    DateTime stamp;
                    try { stamp = File.GetLastWriteTimeUtc(path); }
                    catch { stamp = DateTime.MinValue; }
                    found.Add(new KeyValuePair<string, DateTime>(path, stamp));

                    // A hard stop on the WALK, not just on the result list. Without it, pointing this
                    // at a drive root would enumerate the whole disk before throwing most of it away.
                    if (found.Count >= MaxImages * 4) break;
                }
            }
            catch (Exception ex) { Core.InstallLog.Write("[UserArt] scan of " + folder + " failed: " + ex.Message); }

            // Newest first: the reason someone opens this right after saving a cover from a browser
            // is the file they just saved.
            found.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < found.Count && results.Count < MaxImages; i++) results.Add(found[i].Key);
            return results;
        }

        /// <summary>
        /// Copies a chosen picture into Center's own art cache and returns the new path.
        ///
        /// COPIED, NOT REFERENCED, for the same reason the SteamGridDB picker downloads into that
        /// folder rather than hot-linking: what the user picked out of Downloads is a file they will
        /// delete, move, or lose with the next clean-up, and a cover that vanishes months later reads
        /// as Center losing it. The copy is a few hundred kilobytes and it is ours.
        /// </summary>
        public static string CopyIntoCache(string sourcePath, string prefix)
        {
            if (string.IsNullOrEmpty(sourcePath)) return null;
            try
            {
                if (!File.Exists(sourcePath)) return null;
                string ext = Path.GetExtension(sourcePath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                Directory.CreateDirectory(SteamGridDb.CacheDir);
                string target = Path.Combine(SteamGridDb.CacheDir, prefix + Guid.NewGuid().ToString("N") + ext);
                File.Copy(sourcePath, target, overwrite: false);
                return target;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[UserArt] copy of " + sourcePath + " failed: " + ex.Message);
                return null;
            }
        }
    }
}
