using System;
using System.Threading.Tasks;
using Velopack;

namespace ClawTweaksCenter.Update
{
    /// <summary>
    /// The one routine that turns "is there an update" into "and what do we do about it".
    ///
    /// ── Why this is separate from VelopackUpdates ───────────────────────────────────────────────
    /// That class is the mechanism: check, decide-if-essential, apply. This one is the POLICY, and
    /// the policy is small enough to state in four lines:
    ///
    ///   no update                     nothing happens, nothing is drawn
    ///   update, not essential         the card on Home, and only the card
    ///   update, essential             it starts by itself and Center restarts
    ///   essential but it failed       the card appears anyway, saying so
    ///
    /// Keeping them apart matters for one concrete reason: <see cref="VelopackUpdates"/> must stay
    /// callable on its own by the self-test, which rehearses the SAME gate the product uses. A
    /// policy folded into the mechanism could not be exercised without the UI.
    ///
    /// ── ⚠️ The success path DOES NOT RETURN ─────────────────────────────────────────────────────
    /// <c>ApplyUpdatesAndRestart</c> ends the process. So a call that comes BACK is, by definition,
    /// a failure - and that return value is the entire signal that switches the fallback card on.
    /// There is no separate error channel to watch, and there does not need to be.
    ///
    /// This file is part of the removable folder - see REMOVAL.md next door.
    /// </summary>
    public static class UpdateFlow
    {
        /// <summary>What the UI needs to know, and nothing more. Deliberately not the UpdateInfo
        /// itself at the call site: the window should not be able to apply an update it did not get
        /// from here.</summary>
        public sealed class PendingUpdate
        {
            /// <summary>The version being offered, as the release spells it.</summary>
            public string Version;

            /// <summary>The manifest marked this one urgent. On the card it is the difference
            /// between "there is an update" and "an update tried to install itself".</summary>
            public bool Essential;

            /// <summary>Set only when <see cref="Essential"/> is true and applying it came back
            /// instead of restarting. This is the case the user asked for explicitly: the automatic
            /// path must not be able to fail silently.</summary>
            public bool AutoApplyFailed;

            internal UpdateInfo Info;
        }

        /// <summary>
        /// Checks, decides and - when the manifest says so and <paramref name="mayApplyNow"/>
        /// allows - applies.
        ///
        /// <para><paramref name="mayApplyNow"/> is not decoration. Center also starts straight into
        /// the guided leave screen, into onboarding and into the post-install screen. Restarting
        /// underneath the leave screen would abort an offboarding halfway through - it resets the
        /// charge limit, hands the fan curve back to the firmware and re-enables MSI Center M, and
        /// half of that done is worse than none of it. In those cases the update is still found and
        /// still offered on the card; it just does not start itself.</para>
        ///
        /// <para>Returns null when there is nothing to say: feature off, no newer release, or - the
        /// normal case on a classically installed Center - this is not a Velopack installation at
        /// all. Never throws; a failed update check must never be the reason a window does not
        /// come up.</para>
        /// </summary>
        public static async Task<PendingUpdate> RunAsync(bool mayApplyNow)
        {
            try
            {
                var info = await VelopackUpdates.CheckAsync().ConfigureAwait(false);
                if (info == null) return null;

                string version = info.TargetFullRelease?.Version?.ToString();
                if (string.IsNullOrWhiteSpace(version)) return null;

                bool essential = await VelopackUpdates.ShouldUpdateSilently(info).ConfigureAwait(false);
                var pending = new PendingUpdate { Version = version, Essential = essential, Info = info };

                if (!essential)
                {
                    // Logged as a decision, not as an absence. "Nothing in the log" cannot tell
                    // "we chose not to" from "the check never ran" - a confusion this project has
                    // paid for in several other subsystems.
                    VelopackUpdates.Note($"{version} is offered on the card only - not essential");
                    return pending;
                }

                if (!mayApplyNow)
                {
                    VelopackUpdates.Note($"{version} is essential but this start is busy " +
                                         "(leave/onboarding/install) - offering it on the card instead");
                    return pending;
                }

                // Applies and restarts. If this line is ever passed, it did not work.
                await VelopackUpdates.ApplyAsync(info).ConfigureAwait(false);

                VelopackUpdates.Note($"auto-apply of essential {version} returned instead of " +
                                     "restarting - falling back to the card");
                pending.AutoApplyFailed = true;
                return pending;
            }
            catch (Exception ex)
            {
                VelopackUpdates.Note("update flow threw: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The card's button. Same warning as everywhere else in this file: on success it does not
        /// return, so <c>false</c> is the only answer the caller can ever actually read.
        /// </summary>
        public static async Task<bool> ApplyAsync(PendingUpdate pending)
        {
            if (pending?.Info == null) return false;
            VelopackUpdates.Note("applying " + pending.Version + " because the user asked for it");
            return await VelopackUpdates.ApplyAsync(pending.Info).ConfigureAwait(false);
        }
    }
}
