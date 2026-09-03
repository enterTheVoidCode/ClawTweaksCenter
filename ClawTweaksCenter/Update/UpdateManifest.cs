using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Update
{
    /// <summary>
    /// The small file that says which Center versions may install themselves without asking.
    ///
    /// ── Shape ───────────────────────────────────────────────────────────────────────────────────
    /// <code>
    /// {
    ///   "schemaVersion": 1,
    ///   "silentUpdatesEnabled": true,
    ///   "essentialVersions": ["0.3.0.0", "0.3.1.4"]
    /// }
    /// </code>
    ///
    /// <b>essentialVersions</b> lists TARGET versions, exactly as written in the release. A version
    /// that is not in the list is a normal update: offered, never forced.
    ///
    /// <b>silentUpdatesEnabled</b> is a remote off switch. It exists because the local switch is no
    /// use in the situation it is for: if a silent update turns out to be harmful, the machines that
    /// need stopping are the ones nobody is sitting at. Setting this to false stops every client at
    /// its next check without shipping anything.
    ///
    /// ── ⚠️ Not to be confused with setup-manifest.json ──────────────────────────────────────────
    /// That one lives on the ClawTweaks repo's public master and governs which APP builds Center
    /// offers (minimumClawTweaksVersion) and when Center calls itself outdated (minimumSetupVersion).
    /// This file lives in the CENTER repo and governs Center updating ITSELF. Two files, two repos,
    /// two meanings - and the surest way to mix them up is to call both of them "the manifest".
    ///
    /// ── ⚠️ It fails CLOSED, and that is the opposite of the other one ───────────────────────────
    /// setup-manifest.json fails open: unreachable, missing field or unparsable version means
    /// "installable", because shutting somebody out for having no network is worse than letting an
    /// old build through. The asymmetry here runs the other way. The cost of wrongly saying "not
    /// essential" is that an update waits for a click; the cost of wrongly saying "essential" is
    /// restarting an app underneath somebody who did not ask. Anything unclear is therefore a no.
    /// </summary>
    internal sealed class UpdateManifest
    {
        private const int SupportedSchema = 1;

        private bool _silentEnabled;
        private HashSet<string> _essential = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Exact match against the release's version string. Deliberately not a range or a
        /// "this and everything above" rule: a range is a promise about versions that do not exist
        /// yet, and this list decides whether to restart somebody's app.</summary>
        public bool IsEssential(string version)
        {
            if (!_silentEnabled) return false;
            return !string.IsNullOrWhiteSpace(version) && _essential.Contains(version.Trim());
        }

        /// <summary>
        /// Reads the manifest from an http(s) URL or a local path. Returns null on any problem - see
        /// the fail-closed note on the class.
        ///
        /// The local-path branch is not a convenience: it is what lets the whole silent-update
        /// decision be rehearsed against a folder feed, with no network and no public artifact.
        /// </summary>
        public static async Task<UpdateManifest> FetchAsync(string urlOrPath)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath)) return null;

            try
            {
                string json;
                if (urlOrPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                        json = await http.GetStringAsync(urlOrPath).ConfigureAwait(false);
                }
                else
                {
                    if (!File.Exists(urlOrPath)) return null;
                    json = File.ReadAllText(urlOrPath);
                }

                return Parse(json);
            }
            catch
            {
                // No logging of the exception text here: this runs on every check, and an offline
                // handheld would otherwise write the same line forever. VelopackUpdates logs the
                // DECISION ("no manifest - not updating silently"), which is the part that matters.
                return null;
            }
        }

        private static UpdateManifest Parse(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                // An unknown schema is refused rather than read optimistically. The whole point of
                // the field is to be able to change the meaning of the others later, and a client
                // that reads a version it does not understand would act on a guess.
                if (!root.TryGetProperty("schemaVersion", out var schema)
                    || schema.ValueKind != JsonValueKind.Number
                    || schema.GetInt32() != SupportedSchema)
                    return null;

                var manifest = new UpdateManifest();

                manifest._silentEnabled =
                    root.TryGetProperty("silentUpdatesEnabled", out var enabled)
                    && enabled.ValueKind == JsonValueKind.True;

                if (root.TryGetProperty("essentialVersions", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in list.EnumerateArray())
                        if (entry.ValueKind == JsonValueKind.String)
                            manifest._essential.Add(entry.GetString()?.Trim() ?? string.Empty);
                }

                return manifest;
            }
        }
    }
}
