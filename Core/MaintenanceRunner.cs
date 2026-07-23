using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shared.Enums;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// Drives the Center "Reset · Backup · Restore" column. Center is a pure trigger + UI: the actual
    /// file/process/ZIP work happens in the elevated helper (the profile stores live in the PBC package
    /// whose ACLs block unelevated writes, and freeing the widget hive needs the widget stopped). Talks
    /// to the helper over the same ClawTweaksCenter pipe the onboarding uses — no UAC prompt, because the
    /// helper is already elevated. See Doku/PLAN_Backup_Restore.md + Doku/RESET_StoreMap_and_FactoryReset_Gaps.md.
    /// </summary>
    public sealed class MaintenanceRunner
    {
        public HelperPipeClient PipeClient { get; } = new HelperPipeClient();

        public bool IsConnected => PipeClient.IsConnected;

        /// <summary>Default folder for user backups + the automatic pre-restore backups (mirrors the
        /// helper's BackupService.GetBackupsFolder so both sides look in the same place).</summary>
        public static string BackupsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClawTweaks", "Backups");

        public static string SuggestedBackupFileName()
            => $"ctw-backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip";

        /// <summary>Connects to the helper if not already connected. Generous window: right after an
        /// update the pipe-serving helper is being swapped.</summary>
        public async Task<bool> EnsureConnectedAsync(Action<string> log = null)
        {
            if (PipeClient.IsConnected) return true;
            return await PipeClient.ConnectAsync(TimeSpan.FromSeconds(45), log).ConfigureAwait(false);
        }

        // ── Result type ──────────────────────────────────────────────────────────────────────
        public struct OpResult
        {
            public bool Ok;
            public string Error;
            public int Count;   // backup: stores written; restore: files restored
            public string Path;  // backup: created zip; restore: the auto pre-restore zip
            public bool TimedOut;
        }

        // ── Reset ────────────────────────────────────────────────────────────────────────────
        public async Task<OpResult> ResetAsync()
        {
            if (!await EnsureConnectedAsync().ConfigureAwait(false))
                return new OpResult { Ok = false, Error = "Could not connect to the helper.", TimedOut = true };
            var content = await PipeClient.RequestWithResultAsync("FactoryReset", true, Function.CenterResetResult, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return ParseResult(content);
        }

        // ── Backup ───────────────────────────────────────────────────────────────────────────
        public async Task<OpResult> BackupAsync(string zipPath)
        {
            if (!await EnsureConnectedAsync().ConfigureAwait(false))
                return new OpResult { Ok = false, Error = "Could not connect to the helper.", TimedOut = true };
            var content = await PipeClient.RequestWithResultAsync("BackupCreate", zipPath, Function.CenterBackupResult, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            return ParseResult(content);
        }

        // ── Restore ──────────────────────────────────────────────────────────────────────────
        public async Task<OpResult> RestoreAsync(string zipPath)
        {
            if (!await EnsureConnectedAsync().ConfigureAwait(false))
                return new OpResult { Ok = false, Error = "Could not connect to the helper.", TimedOut = true };
            // The helper restarts itself after a successful restore, so the reply can arrive just before
            // the pipe drops — a longer window is fine; ParseResult(null) below reports a clean timeout.
            var content = await PipeClient.RequestWithResultAsync("BackupRestore", zipPath, Function.CenterRestoreResult, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            return ParseResult(content);
        }

        private static OpResult ParseResult(string content)
        {
            if (string.IsNullOrEmpty(content))
                return new OpResult { Ok = false, Error = "The helper did not respond in time.", TimedOut = true };

            var kv = ParseCompact(content);
            bool ok = kv.TryGetValue("ok", out var okv) && okv == "1";
            var r = new OpResult { Ok = ok };
            if (!ok) { r.Error = kv.TryGetValue("error", out var e) ? e : "Unknown error."; return r; }
            if (kv.TryGetValue("stores", out var s) && int.TryParse(s, out var sc)) r.Count = sc;
            if (kv.TryGetValue("restored", out var rs) && int.TryParse(rs, out var rc)) r.Count = rc;
            if (kv.TryGetValue("path", out var p)) r.Path = p;
            if (kv.TryGetValue("pre", out var pre) && !string.IsNullOrEmpty(pre)) r.Path = pre;
            return r;
        }

        private static Dictionary<string, string> ParseCompact(string content)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in content.Split(';'))
            {
                int i = part.IndexOf('=');
                if (i <= 0) continue;
                d[part.Substring(0, i).Trim()] = part.Substring(i + 1).Trim();
            }
            return d;
        }

        // ── Local backup listing (read by Center directly — the zips live in the user's Documents,
        //    readable unelevated; only the WRITE-back needs the helper) ────────────────────────
        public sealed class BackupInfo
        {
            public string FilePath;
            public string FileName;
            public DateTime FileTime;     // file last-write time (fallback ordering)
            public long SizeBytes;
            public string AppVersion;     // from manifest (may be null on a corrupt/foreign zip)
            public string DeviceModel;    // from manifest
            public DateTime? CreatedUtc;  // from manifest
            public int StoreCount;        // from manifest.stores[]
            public bool ManifestValid;    // false = not a ClawTweaks backup / unreadable
            public bool IsPreRestore;     // auto pre-restore snapshot (named ctw-prerestore_*)
        }

        /// <summary>Lists local backup ZIPs newest-first, reading each one's manifest.json (best-effort —
        /// a zip with no/invalid manifest still lists, flagged ManifestValid=false so the UI can warn).</summary>
        public static List<BackupInfo> ListLocalBackups()
        {
            var list = new List<BackupInfo>();
            try
            {
                var dir = BackupsFolder;
                if (!Directory.Exists(dir)) return list;
                foreach (var f in Directory.GetFiles(dir, "*.zip"))
                {
                    var info = ReadBackupInfo(f);
                    if (info != null) list.Add(info);
                }
            }
            catch { /* listing is best-effort */ }

            return list
                .OrderByDescending(b => b.CreatedUtc ?? b.FileTime.ToUniversalTime())
                .ToList();
        }

        /// <summary>Reads a single backup ZIP's metadata (manifest.json if present). Returns a BackupInfo
        /// even for a zip without a valid manifest (ManifestValid=false) so the restore picker can still
        /// show it with a warning. Null only if the file can't be opened at all.</summary>
        public static BackupInfo ReadBackupInfo(string zipPath)
        {
            try
            {
                var fi = new FileInfo(zipPath);
                if (!fi.Exists) return null;
                var info = new BackupInfo
                {
                    FilePath = zipPath,
                    FileName = fi.Name,
                    FileTime = fi.LastWriteTime,
                    SizeBytes = fi.Length,
                    IsPreRestore = fi.Name.StartsWith("ctw-prerestore_", StringComparison.OrdinalIgnoreCase),
                };

                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var entry = zip.GetEntry("manifest.json");
                    if (entry == null) return info; // valid zip, just not (or no longer) a CTW backup manifest
                    using (var s = entry.Open())
                    using (var reader = new StreamReader(s))
                    {
                        var json = reader.ReadToEnd();
                        try
                        {
                            using (var doc = JsonDocument.Parse(json))
                            {
                                var root = doc.RootElement;
                                if (root.TryGetProperty("appVersion", out var av)) info.AppVersion = av.GetString();
                                if (root.TryGetProperty("deviceModel", out var dm)) info.DeviceModel = dm.GetString();
                                if (root.TryGetProperty("createdUtc", out var cu) && DateTime.TryParse(cu.GetString(), null,
                                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                                    info.CreatedUtc = parsed;
                                if (root.TryGetProperty("stores", out var st) && st.ValueKind == JsonValueKind.Array)
                                    info.StoreCount = st.GetArrayLength();
                                info.ManifestValid = true;
                            }
                        }
                        catch { info.ManifestValid = false; }
                    }
                }
                return info;
            }
            catch
            {
                return null;
            }
        }
    }
}
