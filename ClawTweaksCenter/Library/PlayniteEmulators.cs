using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LiteDB;
using YamlDotNet.RepresentationModel;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Resolves a ROM to the emulator command line that starts it, so a ROM can be launched WITHOUT
    /// going through Playnite.
    ///
    /// WHY THIS EXISTS. Launching via <c>playnite://playnite/start/&lt;id&gt;</c> works, but it starts
    /// Playnite first - a full library application, several seconds, and then it stays on screen
    /// behind the emulator. On a handheld that is the difference between picking a game and waiting
    /// for a launcher to launch a launcher.
    ///
    /// WHY IT IS DERIVABLE, which was not obvious. The game's play action names an emulator and a
    /// profile id like <c>#builtin_53561e5e-…</c>, and the built-in profiles are not in the database
    /// as command lines. It looked like the mapping lived inside Playnite. It does not:
    /// <c>emulators.db</c> stores each emulator with its <c>InstallDir</c>, its <c>BuiltInConfigId</c>
    /// ("retroarch", "mgba", …) and a <c>BuiltinProfiles</c> array where every entry carries BOTH the
    /// <c>_id</c> the action refers to AND the <c>BuiltInProfileName</c>. The name is the key into
    /// Playnite's own definition files under
    /// <c>%LOCALAPPDATA%\Playnite\Emulation\Emulators\*\emulator.yaml</c>, which hold the real
    /// <c>StartupExecutable</c> and <c>StartupArguments</c>.
    ///
    /// So: action -> emulator -> profile name -> definition -> executable + arguments. Every step is
    /// a lookup by a value someone else wrote down. Nothing here is guessed, and where a step fails
    /// the caller keeps the <c>playnite://</c> URI - a slow launch is a worse experience, a wrong
    /// command line is a broken one.
    /// </summary>
    internal static class PlayniteEmulators
    {
        /// <summary>One emulator as the database describes it.</summary>
        internal sealed class EmulatorEntry
        {
            public string InstallDir;
            public string ConfigId;
            /// <summary>Profile id (<c>#builtin_…</c>) to the profile NAME used in the definition file.</summary>
            public readonly Dictionary<string, string> ProfileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>One profile out of a definition file.</summary>
        private sealed class DefinitionProfile
        {
            public string Name;
            public string StartupExecutable;   // a REGEX, not a file name
            public string StartupArguments;
        }

        /// <summary>Reads <c>emulators.db</c>. Takes the already-copied working folder, so it inherits
        /// the "never touch the live database" rule from <see cref="PlayniteSource"/>.</summary>
        internal static Dictionary<string, EmulatorEntry> LoadEmulators(string workDir)
        {
            var map = new Dictionary<string, EmulatorEntry>(StringComparer.OrdinalIgnoreCase);
            string file = Path.Combine(workDir, "emulators.db");
            if (!File.Exists(file)) return map;

            try
            {
                using (var db = new LiteDatabase("Filename=" + file + ";journal=false"))
                    foreach (string collection in db.GetCollectionNames())
                        foreach (var doc in db.GetCollection(collection).FindAll())
                        {
                            var entry = new EmulatorEntry
                            {
                                InstallDir = Str(doc, "InstallDir"),
                                ConfigId = Str(doc, "BuiltInConfigId"),
                            };

                            if (doc.ContainsKey("BuiltinProfiles") && doc["BuiltinProfiles"].IsArray)
                                foreach (var p in doc["BuiltinProfiles"].AsArray)
                                {
                                    var pd = p.AsDocument;
                                    if (pd == null) continue;
                                    string id = pd.ContainsKey("_id") ? pd["_id"].AsString : null;
                                    string name = Str(pd, "BuiltInProfileName") ?? Str(pd, "Name");
                                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) entry.ProfileNames[id] = name;
                                }

                            map[doc["_id"].AsGuid.ToString()] = entry;
                        }
            }
            catch { }
            return map;
        }

        private static string Str(BsonDocument doc, string key)
        {
            if (doc == null || !doc.ContainsKey(key) || doc[key].IsNull) return null;
            try { return doc[key].AsString; } catch { return null; }
        }

        private static string DefinitionsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Playnite", "Emulation", "Emulators");

        // configId -> profiles. Parsed once per process: 100+ definition files, and the ROM tab asks
        // for them once per game.
        private static Dictionary<string, List<DefinitionProfile>> _definitions;
        private static readonly object DefinitionLock = new object();

        private static Dictionary<string, List<DefinitionProfile>> Definitions
        {
            get
            {
                lock (DefinitionLock)
                {
                    if (_definitions != null) return _definitions;
                    _definitions = new Dictionary<string, List<DefinitionProfile>>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        if (!Directory.Exists(DefinitionsDir)) return _definitions;
                        foreach (string dir in Directory.GetDirectories(DefinitionsDir))
                        {
                            string yaml = Path.Combine(dir, "emulator.yaml");
                            if (!File.Exists(yaml)) continue;
                            ParseDefinition(yaml, _definitions);
                        }
                    }
                    catch { }
                    return _definitions;
                }
            }
        }

        /// <summary>
        /// Parses one definition file.
        ///
        /// A real YAML reader, not a line scanner: the arguments are single-quoted strings that
        /// themselves contain double quotes and backslashes (<c>'-L ".\cores\x.dll" "{ImagePath}"'</c>),
        /// and mangling one of those produces an emulator that starts and loads nothing.
        /// </summary>
        private static void ParseDefinition(string path, Dictionary<string, List<DefinitionProfile>> into)
        {
            try
            {
                var stream = new YamlStream();
                using (var reader = new StreamReader(path))
                    stream.Load(reader);
                if (stream.Documents.Count == 0) return;
                if (!(stream.Documents[0].RootNode is YamlMappingNode root)) return;

                string id = Scalar(root, "Id");
                if (string.IsNullOrEmpty(id)) return;

                var profiles = new List<DefinitionProfile>();
                if (root.Children.TryGetValue(new YamlScalarNode("Profiles"), out var profilesNode) &&
                    profilesNode is YamlSequenceNode sequence)
                {
                    foreach (var item in sequence.Children.OfType<YamlMappingNode>())
                        profiles.Add(new DefinitionProfile
                        {
                            Name = Scalar(item, "Name"),
                            StartupExecutable = Scalar(item, "StartupExecutable"),
                            StartupArguments = Scalar(item, "StartupArguments"),
                        });
                }

                if (profiles.Count > 0) into[id] = profiles;
            }
            catch { }
        }

        private static string Scalar(YamlMappingNode node, string key)
            => node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;

        /// <summary>The resolved command line for one ROM, or null when any step could not be made.</summary>
        internal sealed class Resolved
        {
            public string Executable;
            public string Arguments;
        }

        /// <summary>
        /// Turns a play action plus a ROM path into an executable and its arguments.
        /// </summary>
        /// <param name="emulatorId">GameAction.EmulatorId.</param>
        /// <param name="profileId">GameAction.EmulatorProfileId (<c>#builtin_…</c>).</param>
        /// <param name="romPath">The ROM, already resolved to a real path.</param>
        internal static Resolved Resolve(Dictionary<string, EmulatorEntry> emulators,
                                         string emulatorId, string profileId, string romPath)
        {
            if (emulators == null || string.IsNullOrEmpty(emulatorId) || string.IsNullOrEmpty(romPath)) return null;
            if (!emulators.TryGetValue(emulatorId, out var emulator)) return null;
            if (string.IsNullOrEmpty(emulator.InstallDir) || !Directory.Exists(emulator.InstallDir)) return null;
            if (string.IsNullOrEmpty(emulator.ConfigId)) return null;
            if (!Definitions.TryGetValue(emulator.ConfigId, out var profiles)) return null;

            string profileName = null;
            if (!string.IsNullOrEmpty(profileId)) emulator.ProfileNames.TryGetValue(profileId, out profileName);

            DefinitionProfile profile = null;
            if (profileName != null)
                profile = profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
            // A single-profile emulator needs no match at all - there is nothing to pick wrong.
            if (profile == null && profiles.Count == 1) profile = profiles[0];
            if (profile?.StartupExecutable == null) return null;

            string exe = FindExecutable(emulator.InstallDir, profile.StartupExecutable);
            if (exe == null) return null;

            // No arguments at all is legitimate: some emulators take the ROM as the bare first
            // argument, and the definition then leaves StartupArguments empty.
            string args = profile.StartupArguments ?? "\"{ImagePath}\"";
            args = args.Replace("{ImagePath}", romPath)
                       .Replace("{ImageName}", Path.GetFileName(romPath))
                       .Replace("{ImageNameNoExt}", Path.GetFileNameWithoutExtension(romPath))
                       .Replace("{StartupDir}", emulator.InstallDir)
                       .Replace("{EmulatorDir}", emulator.InstallDir);

            return new Resolved { Executable = exe, Arguments = args };
        }

        /// <summary>
        /// Finds the emulator binary. StartupExecutable is a REGEX (<c>^mGBA.exe$</c>,
        /// <c>^retroarch\.exe$</c>), matched against file names inside the install folder - which is
        /// how one definition covers builds that name their exe slightly differently. Searched one
        /// level deep as well, because several emulators ship a versioned subfolder.
        /// </summary>
        private static string FindExecutable(string installDir, string pattern)
        {
            Regex regex;
            try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch { return null; }

            try
            {
                foreach (string file in Directory.GetFiles(installDir, "*.exe"))
                    if (regex.IsMatch(Path.GetFileName(file))) return file;

                foreach (string dir in Directory.GetDirectories(installDir))
                    foreach (string file in Directory.GetFiles(dir, "*.exe"))
                        if (regex.IsMatch(Path.GetFileName(file))) return file;
            }
            catch { }
            return null;
        }
    }
}
