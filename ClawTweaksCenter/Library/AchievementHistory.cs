using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>One unlock in the history: the achievement, and the game it was earned in.</summary>
    public sealed class AchievementHistoryEntry
    {
        public AchievementEntry Achievement;
        public string GameTitle;
        public GameStore Store;
        /// <summary>Center's own entry for the game, when the library has one. Null for a game that
        /// is known only to the store (an Xbox title played elsewhere, say).</summary>
        public GameEntry Game;
    }

    /// <summary>
    /// The user's own achievements across every store, newest first, at most <see cref="Max"/>.
    ///
    /// ── DATED ONLY (user, 2026-10-03) ──────────────────────────────────────────────────────────
    /// A timeline needs a time. What each store gives:
    ///   Steam   the account says WHICH are unlocked but not WHEN; dates exist only for games whose
    ///           stats Steam wrote to this disk. Those are read; the rest cannot be placed and are
    ///           left out.
    ///   Xbox    one account-wide call, newest first, every one dated.
    ///   Epic    every unlock dated, for the installed games (the ones Center lists).
    /// </summary>
    public static class AchievementHistory
    {
        public const int Max = 100;

        /// <summary>Builds the list. Slow-ish (it reads Steam's stats files and asks Xbox once), so it
        /// runs off the UI thread; <paramref name="games"/> is Center's library, installed and not.</summary>
        public static Task<List<AchievementHistoryEntry>> BuildAsync(IReadOnlyList<GameEntry> games, CancellationToken ct) =>
            Task.Run(async () =>
            {
                var all = new List<AchievementHistoryEntry>();
                int steam = 0, xbox = 0, epic = 0;

                // Steam: the games with local stats - the only ones with dates.
                var steamGames = new Dictionary<string, GameEntry>(StringComparer.Ordinal);
                foreach (var g in games)
                    if (g.Store == GameStore.Steam && !string.IsNullOrEmpty(g.Id) && !steamGames.ContainsKey(g.Id)) steamGames[g.Id] = g;
                foreach (string appId in SteamAchievements.LocallyDatedAppIds())
                {
                    ct.ThrowIfCancellationRequested();
                    // A game with stats but no library entry has no title to show; left out.
                    if (!steamGames.TryGetValue(appId, out var g)) continue;
                    foreach (var a in SteamAchievements.UnlockedFor(g))
                    {
                        if (!a.UnlockedAt.HasValue) continue;
                        all.Add(new AchievementHistoryEntry { Achievement = a, GameTitle = g.Title, Store = GameStore.Steam, Game = g });
                        steam++;
                    }
                }

                // Xbox: one call for the whole account.
                if (Accounts.XboxAccount.IsSignedIn)
                {
                    var byPfn = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in games)
                    {
                        string pfn = g.Store == GameStore.Xbox ? Accounts.XboxAccountAchievements.PfnOf(g.Id) : null;
                        if (pfn != null && !byPfn.ContainsKey(pfn)) byPfn[pfn] = g;
                    }
                    try
                    {
                        foreach (var u in await Accounts.XboxAccountAchievements.RecentUnlocksAsync(Max, ct).ConfigureAwait(false))
                        {
                            if (!u.Entry.UnlockedAt.HasValue) continue;
                            GameEntry g = u.Pfn != null && byPfn.TryGetValue(u.Pfn, out var hit) ? hit : null;
                            all.Add(new AchievementHistoryEntry
                            {
                                Achievement = u.Entry,
                                GameTitle = g?.Title ?? u.TitleName ?? "Xbox",
                                Store = GameStore.Xbox,
                                Game = g,
                            });
                            xbox++;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Core.InstallLog.Write("[Achievements] history: Xbox failed: " + ex.GetType().Name + ": " + ex.Message); }
                }

                // Epic: the installed games' lists, as fetched by the account's refresh.
                foreach (var g in games)
                {
                    if (g.Store != GameStore.Epic) continue;
                    foreach (var a in SteamAchievements.UnlockedFor(g))
                    {
                        if (!a.UnlockedAt.HasValue) continue;
                        all.Add(new AchievementHistoryEntry { Achievement = a, GameTitle = g.Title, Store = GameStore.Epic, Game = g });
                        epic++;
                    }
                }

                var newest = all.OrderByDescending(e => e.Achievement.UnlockedAt.Value).Take(Max).ToList();
                Core.InstallLog.Write("[Achievements] history: " + newest.Count + " shown of " + all.Count
                    + " dated (Steam " + steam + ", Xbox " + xbox + ", Epic " + epic + ")");
                return newest;
            }, ct);
    }
}
