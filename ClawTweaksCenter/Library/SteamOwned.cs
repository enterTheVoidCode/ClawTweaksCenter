using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Every Steam game this account owns, installed or not — read out of Steam's own caches.
    ///
    /// NO WEB API, NO KEY, NO LOGIN, NO NETWORK. Two files under <c>Steam\appcache\</c> answer it
    /// between them:
    ///
    ///   packageinfo.vdf   the LICENCES this account holds. Each package lists the appids it grants.
    ///   appinfo.vdf       appid to name and type. Without it the answer is a list of numbers.
    ///
    /// Measured on this machine: 976 packages to 2065 appids, of which 879 are of type "game";
    /// 840 of those are not installed, and 832 of THOSE already have a cover in Steam's library
    /// cache. Total 294 ms, 17 of them for the licences and the rest for the 6 MB app cache.
    ///
    /// Coverage was checked the only way that means anything: all 43 apps installed on this machine
    /// appear in the licence list. Four of them are typed "tool" rather than "game" by Valve —
    /// Half-Life 2's two episodes and the Steamworks redistributables — which is a quirk with no
    /// consequence here, because installed games come from the manifests either way.
    ///
    /// ⚠️ BOTH FORMATS ARE UNDOCUMENTED BINARY KEYVALUES AND VALVE HAS CHANGED THEM. The version is
    /// in the first four bytes and this reader accepts only the ones it has been checked against;
    /// anything else yields NOTHING rather than a plausible-looking list of wrong numbers. A tab that
    /// is empty after a Steam update is a bug report. A tab full of garbage is a trap.
    ///
    /// ⚠️ NOT VERIFIED, and worth knowing before trusting the list: a second Steam account on the
    /// same machine, Family Sharing, and a purchase made since the last Steam login. All three are
    /// properties of a CACHE, and this is a cache.
    /// </summary>
    public static class SteamOwned
    {
        // The two file generations this reader has actually been run against. The number is a
        // magic + format version in one; a new one means Valve moved something.
        private const uint PackageInfo28 = 0x06565528;   // packageinfo, carries a PICS token
        private const uint AppInfo28 = 0x07564428;       // appinfo, no string table
        private const uint AppInfo29 = 0x07564429;       // appinfo, string table at the end

        public sealed class OwnedGame
        {
            public int AppId;
            public string Name;
        }

        /// <summary>
        /// The owned games, by appid. Empty when Steam is not installed, when a cache is missing, or
        /// when either file is a version this reader does not know.
        /// </summary>
        public static IReadOnlyDictionary<int, OwnedGame> Read()
        {
            var result = new Dictionary<int, OwnedGame>();
            try
            {
                string steam = SteamSource.SteamPath();
                if (steam == null) return result;

                var owned = ReadLicensedAppIds(Path.Combine(steam, "appcache", "packageinfo.vdf"));
                if (owned.Count == 0) return result;

                ReadNames(Path.Combine(steam, "appcache", "appinfo.vdf"), owned, result);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("Steam owned-games read failed: " + ex.Message);
            }
            return result;
        }

        // ── packageinfo.vdf ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Layout, per package: id (u32), sha1 (20), change number (u32), PICS token (u64), then one
        /// binary-KV blob. A package id of 0xFFFFFFFF ends the file.
        ///
        /// The blob is wrapped one level deep in the package id as a KEY - so the appids sit at
        /// <c>&lt;id&gt; ▸ appids ▸ 0,1,2…</c>, not at the top. Reading the top level finds nothing and
        /// looks exactly like "this account owns no games", which is what it did on the first try.
        /// </summary>
        private static HashSet<int> ReadLicensedAppIds(string path)
        {
            var owned = new HashSet<int>();
            if (!File.Exists(path)) return owned;

            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 8) return owned;

            uint magic = BitConverter.ToUInt32(b, 0);
            if (magic != PackageInfo28)
            {
                Core.InstallLog.Write("Steam packageinfo.vdf is version 0x" + magic.ToString("x8") +
                                      ", which this build has not been checked against - owned games not read.");
                return owned;
            }

            int i = 8;
            while (i < b.Length - 4)
            {
                uint packageId = BitConverter.ToUInt32(b, i);
                if (packageId == 0xFFFFFFFF) break;

                i += 4 + 20 + 4 + 8;
                var node = ReadBinaryKv(b, ref i, null);

                foreach (var outer in node.Values)
                {
                    if (!(outer is Dictionary<string, object> pkg)) continue;
                    if (!pkg.TryGetValue("appids", out object appids)) continue;
                    if (!(appids is Dictionary<string, object> list)) continue;
                    foreach (var v in list.Values)
                        if (v is int id) owned.Add(id);
                }
            }
            return owned;
        }

        // ── appinfo.vdf ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Layout: header, then per app - appid (u32), size (u32), then a fixed 60-byte block
        /// (info state, timestamp, PICS token, two SHA-1s and a change number) and the KV blob.
        /// An appid of 0 ends the file.
        ///
        /// Version 29 moved every KEY NAME into one string table at the end of the file and left an
        /// index in its place, which is why the table has to be read first and why a v28 reader
        /// produces nonsense rather than an error on a v29 file.
        /// </summary>
        private static void ReadNames(string path, HashSet<int> wanted, Dictionary<int, OwnedGame> into)
        {
            if (!File.Exists(path)) return;

            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 16) return;

            uint magic = BitConverter.ToUInt32(b, 0);
            if (magic != AppInfo28 && magic != AppInfo29)
            {
                Core.InstallLog.Write("Steam appinfo.vdf is version 0x" + magic.ToString("x8") +
                                      ", which this build has not been checked against - owned games not read.");
                return;
            }

            int i = 8;
            string[] strings = null;

            if (magic == AppInfo29)
            {
                long tableOffset = BitConverter.ToInt64(b, i);
                i += 8;
                if (tableOffset <= 0 || tableOffset >= b.Length) return;

                int j = (int)tableOffset;
                int count = BitConverter.ToInt32(b, j);
                j += 4;
                if (count < 0 || count > 5_000_000) return;

                strings = new string[count];
                for (int k = 0; k < count; k++) strings[k] = ReadCString(b, ref j);
            }

            while (i < b.Length - 8)
            {
                int appId = BitConverter.ToInt32(b, i);
                if (appId == 0) break;

                int size = BitConverter.ToInt32(b, i + 4);
                int body = i + 8;
                if (size < 0 || body + size > b.Length) break;
                i = body + size;

                if (!wanted.Contains(appId)) continue;

                try
                {
                    int at = body + 60;
                    var node = ReadBinaryKv(b, ref at, strings);

                    // Wrapped one level deep again, under "appinfo".
                    if (!node.TryGetValue("appinfo", out object infoObj) ||
                        !(infoObj is Dictionary<string, object> info)) continue;
                    if (!info.TryGetValue("common", out object commonObj) ||
                        !(commonObj is Dictionary<string, object> common)) continue;

                    string name = common.TryGetValue("name", out object n) ? n as string : null;
                    string type = common.TryGetValue("type", out object t) ? t as string : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // GAMES ONLY. The same list carries demos, soundtracks, dedicated servers, tools
                    // and every redistributable Valve ships - roughly 1200 of the 2065 entries here.
                    if (!string.Equals(type, "game", StringComparison.OrdinalIgnoreCase)) continue;

                    into[appId] = new OwnedGame { AppId = appId, Name = name };
                }
                catch
                {
                    // One unreadable app is not a reason to lose the other two thousand.
                }
            }
        }

        // ── binary KeyValues ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One node set. Types: 0x00 opens a child set, 0x01 string, 0x02 int32, 0x07 uint64,
        /// 0x08 ends the set. <paramref name="strings"/> non-null means key names are table indices
        /// rather than inline text.
        /// </summary>
        private static Dictionary<string, object> ReadBinaryKv(byte[] b, ref int i, string[] strings)
        {
            var node = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                if (i >= b.Length) return node;

                byte type = b[i++];
                if (type == 0x08) return node;

                string name;
                if (strings == null) name = ReadCString(b, ref i);
                else
                {
                    int index = BitConverter.ToInt32(b, i);
                    i += 4;
                    if (index < 0 || index >= strings.Length) throw new InvalidDataException("string index out of range");
                    name = strings[index];
                }

                switch (type)
                {
                    case 0x00: node[name] = ReadBinaryKv(b, ref i, strings); break;
                    case 0x01: node[name] = ReadCString(b, ref i); break;
                    case 0x02: node[name] = BitConverter.ToInt32(b, i); i += 4; break;
                    case 0x07: node[name] = BitConverter.ToUInt64(b, i); i += 8; break;
                    default: throw new InvalidDataException("unknown node type 0x" + type.ToString("x2"));
                }
            }
        }

        private static string ReadCString(byte[] b, ref int i)
        {
            int start = i;
            while (i < b.Length && b[i] != 0) i++;
            string s = Encoding.UTF8.GetString(b, start, i - start);
            i++;                       // the terminator
            return s;
        }
    }
}
