using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ValveKeyValue;

namespace ClawTweaksCenter.Library
{
    /// <summary>One friend as the library shows them.</summary>
    public sealed class SteamFriend
    {
        public ulong SteamId;
        /// <summary>The nickname the user gave them in Steam, else their persona name.</summary>
        public string Name;
        public SteamPersonaState State;
        /// <summary>Non-zero while they are in a game. Zero with <see cref="InGame"/> set means a
        /// non-Steam game or a mod - Steam reports those without an appid.</summary>
        public int AppId;
        public bool InGame;
        public string GameName;
        public string AvatarUrl;
        /// <summary>Their newest entry in Steam's own activity feed, or null.</summary>
        public FriendActivity LatestActivity;
        /// <summary>What Center itself last saw of them, or null.</summary>
        public FriendSeen Seen;

        public bool IsOnline => State != SteamPersonaState.Offline;
    }

    /// <summary>Steam's EPersonaState, in Steam's own numbering.</summary>
    public enum SteamPersonaState
    {
        Offline = 0,
        Online = 1,
        Busy = 2,
        Away = 3,
        Snooze = 4,
        LookingToTrade = 5,
        LookingToPlay = 6,
        Invisible = 7,
    }

    public sealed class SteamFriendsSnapshot
    {
        /// <summary>steam.exe was running when this was read.</summary>
        public bool SteamRunning;
        /// <summary>The friend list could actually be read. False with Steam running means a signed-out
        /// client or a read that failed.</summary>
        public bool Available;
        public List<SteamFriend> Friends = new List<SteamFriend>();
        /// <summary>Steam's activity feed, newest first, friends in this list only.</summary>
        public List<FriendActivity> Activity = new List<FriendActivity>();

        public int OnlineCount => Friends.Count(f => f.IsOnline);
    }

    /// <summary>
    /// Steam friends, their status and what they are playing - read from the RUNNING Steam client.
    ///
    /// -- Where it comes from --------------------------------------------------------------------
    /// Presence is not on disk (Doku/STEAM_Achievements.md §7). It comes from ISteamFriends in
    /// steamclient64.dll - the DLL that ships WITH STEAM, loaded from Steam's own folder. Center ships
    /// no Steam binary: no steam_api64.dll, no steam_appid.txt.
    ///
    /// -- Why no app id, measured 2026-09-11 ---------------------------------------------------------
    /// steam_api's SteamAPI_Init registers the process as a running game, so every friend would see
    /// the user "playing" whatever app id was used. Connecting to the client directly with NO app id
    /// does not: a probe held the connection for three minutes and the user's status stayed "Online",
    /// checked in the desktop client and on the phone app. ⚠️ Do not add a SteamAppId environment
    /// variable or a steam_appid.txt to "fix" anything here - that is exactly what flips it.
    ///
    /// -- Why a child process ----------------------------------------------------------------------
    /// The read runs in a short-lived copy of Center (<see cref="ChildArg"/>), never in Center itself.
    /// Program.Main checks the argument before the splash screen - otherwise every refresh flashes it.
    /// Three reasons, each enough on its own:
    ///   1. steamclient64.dll stays loaded for the life of the process that loaded it. Held by Center
    ///      - which runs for days - it would sit locked on disk when Steam tries to update it.
    ///   2. The vtable layout is Valve's, not ours. If a Steam update ever moves a method, a wrong
    ///      call is an access violation, and that should cost one friends refresh, not Center.
    ///   3. Nothing of Steam's threads or state lingers between reads.
    /// The child prints one JSON line and exits; the parent parses it.
    /// </summary>
    public static class SteamFriends
    {
        /// <summary>The argument that turns a Center process into the one-shot friends reader.</summary>
        public const string ChildArg = "--steam-friends";

        private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(8);

        // Interface versions this was measured against (2026-09-11). Steam keeps old versions
        // exported, and the first methods of both interfaces have not moved in a decade.
        private const string ClientVersion = "SteamClient020";
        private const string FriendsVersion = "SteamFriends017";

        private const int FriendFlagImmediate = 0x04;

        #region Parent side
        private static readonly object NameCacheLock = new object();
        private static readonly Dictionary<int, string> NameCache = new Dictionary<int, string>();

        private static Dictionary<ulong, string> _avatars = new Dictionary<ulong, string>();
        private static DateTime _avatarsReadFrom = DateTime.MinValue;

        private static bool _loggedFailure;

        public static bool SteamIsRunning()
        {
            try { return Process.GetProcessesByName("steam").Length > 0; }
            catch { return false; }
        }

        /// <summary>
        /// Reads the friend list. Never throws; a failure is a snapshot with Available false.
        /// Blocking parts run off the calling thread.
        /// </summary>
        public static Task<SteamFriendsSnapshot> ReadAsync()
        {
            return Task.Run(() =>
            {
                var snapshot = new SteamFriendsSnapshot { SteamRunning = SteamIsRunning() };
                if (!snapshot.SteamRunning) return snapshot;

                string json = RunChildProcess();
                if (json == null) return snapshot;

                try
                {
                    ParseInto(json, snapshot);
                }
                catch (Exception ex)
                {
                    LogOnce("[SteamFriends] could not parse the reader's answer: " + ex.Message);
                    snapshot.Available = false;
                    snapshot.Friends.Clear();
                }

                if (snapshot.Available)
                {
                    _loggedFailure = false;
                    ResolveAvatars(snapshot.Friends);
                    Sort(snapshot.Friends);

                    // Steam's feed, trimmed to the people in this list - a removed friend's events stay
                    // in the cache and would otherwise show up under a name nobody can chat with.
                    var ids = new HashSet<ulong>(snapshot.Friends.Select(f => f.SteamId));
                    snapshot.Activity = SteamFriendActivity.Read().Where(a => ids.Contains(a.SteamId)).ToList();

                    ResolveNames(snapshot.Friends.Where(f => f.AppId > 0).Select(f => f.AppId)
                                         .Concat(snapshot.Activity.Select(a => a.AppId)));
                    foreach (var f in snapshot.Friends)
                        if (f.AppId > 0) f.GameName = NameFor(f.AppId);
                    foreach (var a in snapshot.Activity)
                        a.GameName = NameFor(a.AppId);

                    // After the names: what is noted as "last played" should carry the name, not a number.
                    SteamFriendSeen.Note(snapshot.Friends);

                    foreach (var f in snapshot.Friends)
                    {
                        f.Seen = SteamFriendSeen.Get(f.SteamId);
                        f.LatestActivity = snapshot.Activity.FirstOrDefault(a => a.SteamId == f.SteamId);
                    }
                }
                return snapshot;
            });
        }

        private static string RunChildProcess()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = ChildArg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return null;
                    var output = proc.StandardOutput.ReadToEndAsync();
                    var error = proc.StandardError.ReadToEndAsync();

                    if (!proc.WaitForExit((int)ChildTimeout.TotalMilliseconds))
                    {
                        try { proc.Kill(); } catch { }
                        LogOnce("[SteamFriends] the reader did not finish within " + ChildTimeout.TotalSeconds + " s and was stopped.");
                        return null;
                    }

                    string text = output.Wait(1000) ? output.Result : null;
                    if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(text))
                    {
                        string err = error.Wait(500) ? error.Result : null;
                        LogOnce("[SteamFriends] the reader exited with " + proc.ExitCode +
                                (string.IsNullOrWhiteSpace(err) ? "" : ": " + err.Trim()));
                        return null;
                    }
                    return text;
                }
            }
            catch (Exception ex)
            {
                LogOnce("[SteamFriends] could not start the reader: " + ex.Message);
                return null;
            }
        }

        /// <summary>One line per failure mode, not one per refresh - this runs every half minute.</summary>
        private static void LogOnce(string line)
        {
            if (_loggedFailure) return;
            _loggedFailure = true;
            Core.InstallLog.Write(line);
        }

        private static void ParseInto(string json, SteamFriendsSnapshot snapshot)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                snapshot.Available = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                if (!snapshot.Available)
                {
                    if (root.TryGetProperty("error", out var err))
                        LogOnce("[SteamFriends] reader: " + err.GetString());
                    return;
                }

                if (!root.TryGetProperty("friends", out var list) || list.ValueKind != JsonValueKind.Array) return;
                foreach (var f in list.EnumerateArray())
                {
                    if (!ulong.TryParse(f.GetProperty("id").GetString(), out ulong id) || id == 0) continue;

                    string nick = f.TryGetProperty("nick", out var n) ? n.GetString() : null;
                    string name = f.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    ulong gameId = 0;
                    if (f.TryGetProperty("gameId", out var g)) ulong.TryParse(g.GetString(), out gameId);
                    bool playing = f.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;

                    snapshot.Friends.Add(new SteamFriend
                    {
                        SteamId = id,
                        Name = !string.IsNullOrWhiteSpace(nick) ? nick : (name ?? string.Empty),
                        State = (SteamPersonaState)f.GetProperty("state").GetInt32(),
                        InGame = playing,
                        // CGameID: the low 24 bits are the appid for a plain Steam app. A shortcut or
                        // a mod sets type bits above them, and its "appid" is not a Steam app at all.
                        AppId = playing && (gameId >> 24) == 0 ? (int)(gameId & 0xFFFFFF) : 0,
                    });
                }
            }
        }

        /// <summary>
        /// Appid to name, from Steam's appinfo cache. Parsed only when an appid turns up that has not
        /// been seen yet this session - appinfo.vdf is several megabytes and friends change games
        /// far less often than this refreshes.
        /// </summary>
        private static void ResolveNames(IEnumerable<int> appIds)
        {
            var missing = new HashSet<int>();
            lock (NameCacheLock)
            {
                foreach (int id in appIds)
                    if (id > 0 && !NameCache.ContainsKey(id)) missing.Add(id);
            }

            if (missing.Count > 0)
            {
                var found = SteamOwned.NamesFor(missing);
                lock (NameCacheLock)
                {
                    // A miss is cached as null, so an app Steam has never described is not looked
                    // up again on every refresh.
                    foreach (int id in missing)
                        NameCache[id] = found.TryGetValue(id, out string name) ? name : null;
                }
            }

        }

        private static string NameFor(int appId)
        {
            lock (NameCacheLock)
                return NameCache.TryGetValue(appId, out string name) ? name : null;
        }

        /// <summary>
        /// Avatars, by hash, from localconfig.vdf's friends block - the same hash Steam's CDN serves
        /// the picture under. Re-read only when the file has been written since.
        /// </summary>
        private static void ResolveAvatars(List<SteamFriend> friends)
        {
            try
            {
                string file = SteamPlaytime.LocalConfigPath();
                if (file != null)
                {
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    if (written != _avatarsReadFrom)
                    {
                        _avatars = ReadAvatarHashes(file);
                        _avatarsReadFrom = written;
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce("[SteamFriends] avatars unavailable: " + ex.Message);
            }

            foreach (var f in friends)
                if (_avatars.TryGetValue(f.SteamId, out string hash))
                    f.AvatarUrl = "https://avatars.steamstatic.com/" + hash + "_medium.jpg";
        }

        private static Dictionary<ulong, string> ReadAvatarHashes(string file)
        {
            var result = new Dictionary<ulong, string>();
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // HasEscapeSequences: without it this file does not parse at all - see SteamPlaytime.
                var options = new KVSerializerOptions { HasEscapeSequences = true };
                var root = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(fs, options)?.Root;
                if (root == null) return result;

                KVObject friendsBlock = null;
                foreach (var c in root)
                    if (string.Equals(c.Key, "friends", StringComparison.OrdinalIgnoreCase)) { friendsBlock = c.Value; break; }
                if (friendsBlock == null) return result;

                foreach (var entry in friendsBlock)
                {
                    // Keyed by the 32-bit account id; the 64-bit id is that plus the individual-account
                    // base. Other keys in this block (settings, groups) are not numbers and fall out.
                    if (!uint.TryParse(entry.Key, out uint accountId)) continue;
                    string hash = null;
                    try
                    {
                        foreach (var field in entry.Value)
                            if (string.Equals(field.Key, "avatar", StringComparison.OrdinalIgnoreCase))
                                hash = field.Value?.ToString();
                    }
                    catch { continue; }

                    // All zeros is Steam's "no avatar set".
                    if (string.IsNullOrEmpty(hash) || hash.Trim('0').Length == 0) continue;
                    result[76561197960265728UL + accountId] = hash;
                }
            }
            return result;
        }

        /// <summary>In a game first, then online, then away and busy, then offline - each by name.</summary>
        private static void Sort(List<SteamFriend> friends)
        {
            friends.Sort((a, b) =>
            {
                int c = Rank(a).CompareTo(Rank(b));
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        private static int Rank(SteamFriend f)
        {
            if (!f.IsOnline) return 3;
            if (f.InGame) return 0;
            return f.State == SteamPersonaState.Away || f.State == SteamPersonaState.Snooze || f.State == SteamPersonaState.Busy ? 2 : 1;
        }

        /// <summary>Opens Steam's chat window with this friend. Works for offline friends too - Steam
        /// delivers the message when they come back.</summary>
        public static bool OpenChat(SteamFriend friend)
        {
            if (friend == null) return false;
            return GameLibrary.OpenSteamUri("steam://friends/message/" + friend.SteamId);
        }
        #endregion

        #region Child side
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectoryW(string path);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CreateInterfaceFn([MarshalAs(UnmanagedType.LPStr)] string name, IntPtr returnCode);

        // x64 has one calling convention; `this` is simply the first argument.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSteamPipeFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte ReleaseSteamPipeFn(IntPtr self, int pipe);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ConnectToGlobalUserFn(IntPtr self, int pipe);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseUserFn(IntPtr self, int pipe, int user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetInterfaceFn(IntPtr self, int user, int pipe, [MarshalAs(UnmanagedType.LPStr)] string version);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetFriendCountFn(IntPtr self, int flags);
        // CSteamID is a class, and MSVC returns a class from a member function through a hidden
        // pointer that comes right after `this`. Verified against the running client 2026-09-11.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetFriendByIndexFn(IntPtr self, out ulong result, int index, int flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetFriendPersonaStateFn(IntPtr self, ulong id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetFriendPersonaNameFn(IntPtr self, ulong id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte GetFriendGamePlayedFn(IntPtr self, ulong id, [Out] byte[] info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetPlayerNicknameFn(IntPtr self, ulong id);

        private static T Method<T>(IntPtr instance, int slot) where T : Delegate
        {
            IntPtr vtable = Marshal.ReadIntPtr(instance);
            IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }

        /// <summary>
        /// The child process's whole life: read, print one JSON object to stdout, return the exit code.
        /// Called from App.OnStartup before anything else runs.
        /// </summary>
        public static int RunChild()
        {
            string json;
            try
            {
                json = ReadFromClient();
            }
            catch (Exception ex)
            {
                json = ErrorJson(ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                using (var stdout = Console.OpenStandardOutput())
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    stdout.Write(bytes, 0, bytes.Length);
                    stdout.Flush();
                }
                return 0;
            }
            catch
            {
                return 2;
            }
        }

        private static string ErrorJson(string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteBoolean("ok", false);
                w.WriteString("error", message);
                w.WriteEndObject();
                w.Flush();
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static string ReadFromClient()
        {
            string steam = SteamSource.SteamPath();
            if (steam == null) return ErrorJson("Steam is not installed");
            string dll = Path.Combine(steam, "steamclient64.dll");
            if (!File.Exists(dll)) return ErrorJson("steamclient64.dll not found");

            // ⚠️ NO APP ID. See the class summary - an app id is what makes Steam report a game.
            Environment.SetEnvironmentVariable("SteamAppId", null);
            Environment.SetEnvironmentVariable("SteamGameId", null);

            // tier0_s64.dll and vstdlib_s64.dll sit beside it and are found through this.
            SetDllDirectoryW(steam);
            IntPtr lib = NativeLibrary.Load(dll);
            var create = Marshal.GetDelegateForFunctionPointer<CreateInterfaceFn>(NativeLibrary.GetExport(lib, "CreateInterface"));

            IntPtr client = create(ClientVersion, IntPtr.Zero);
            if (client == IntPtr.Zero) return ErrorJson(ClientVersion + " is not exported");

            int pipe = Method<CreateSteamPipeFn>(client, 0)(client);
            if (pipe == 0) return ErrorJson("no pipe to the Steam client");

            int user = 0;
            try
            {
                user = Method<ConnectToGlobalUserFn>(client, 2)(client, pipe);
                if (user == 0) return ErrorJson("no signed-in Steam user");

                IntPtr friends = Method<GetInterfaceFn>(client, 8)(client, user, pipe, FriendsVersion);
                if (friends == IntPtr.Zero) return ErrorJson(FriendsVersion + " is not available");

                var count = Method<GetFriendCountFn>(friends, 3);
                var byIndex = Method<GetFriendByIndexFn>(friends, 4);
                var state = Method<GetFriendPersonaStateFn>(friends, 6);
                var name = Method<GetFriendPersonaNameFn>(friends, 7);
                var game = Method<GetFriendGamePlayedFn>(friends, 8);
                var nick = Method<GetPlayerNicknameFn>(friends, 11);

                using (var ms = new MemoryStream())
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteBoolean("ok", true);
                    w.WriteStartArray("friends");

                    int n = count(friends, FriendFlagImmediate);
                    // FriendGameInfo_t: CGameID (8), IP (4), game port (2), query port (2), lobby id (8).
                    byte[] info = new byte[32];
                    for (int i = 0; i < n; i++)
                    {
                        byIndex(friends, out ulong id, i, FriendFlagImmediate);
                        if (id == 0) continue;

                        Array.Clear(info, 0, info.Length);
                        bool playing = game(friends, id, info) != 0;

                        w.WriteStartObject();
                        w.WriteString("id", id.ToString());
                        w.WriteString("name", Marshal.PtrToStringUTF8(name(friends, id)));
                        w.WriteString("nick", Marshal.PtrToStringUTF8(nick(friends, id)));
                        w.WriteNumber("state", state(friends, id));
                        w.WriteBoolean("playing", playing);
                        w.WriteString("gameId", BitConverter.ToUInt64(info, 0).ToString());
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                    w.Flush();
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            finally
            {
                try
                {
                    if (user != 0) Method<ReleaseUserFn>(client, 4)(client, pipe, user);
                    Method<ReleaseSteamPipeFn>(client, 1)(client, pipe);
                }
                catch { }
            }
        }
        #endregion
    }
}
