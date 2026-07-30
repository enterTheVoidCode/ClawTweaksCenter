using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// Everything about the ClawTweaks signing certificate EXCEPT installing it: finding the .cer,
    /// reading its subject/thumbprint, answering whether it is already trusted, and handing it to
    /// Windows' own import wizard when it isn't.
    ///
    /// ── Why Center no longer imports it ──────────────────────────────────────────────────────────
    /// The certificate has to land in LocalMachine\TrustedPeople for the self-signed MSIX to be
    /// sideloadable. That store is machine-wide, so writing to it needs administrator rights — and it
    /// was the last thing in Center that did. Keeping it would have meant Center still raising a UAC
    /// prompt, which defeats the whole point of the unelevated rebuild (see <see cref="ElevationGate"/>).
    ///
    /// Note it is NOT the Root CA store: that would be a genuine AV/EDR red flag and is unnecessary for
    /// sideloading. TrustedPeople is the narrow, correct store, and Install.ps1 uses the same one.
    ///
    /// So the user does the import, through the Certificate Import Wizard that Windows itself opens
    /// when the user double-clicks a .cer (<see cref="ShowInExplorer"/> puts them in front of it — we
    /// deliberately never launch it ourselves, see that method). That wizard raises its own prompt
    /// when the user picks "Local Machine" — Windows asking for the rights, for a store the user chose,
    /// rather than an unsigned third-party app asking for admin and then writing there silently.
    /// <see cref="ImportSteps"/> is the on-screen instruction text; the store name has to be exact,
    /// because picking the wrong one leaves the MSIX install failing with an unhelpful error.
    /// </summary>
    public static class CertInstaller
    {
        /// <summary>
        /// The ClawTweaks signing cert's Subject — verified identical across versions (same pfx signs
        /// every build), so this can answer "is our cert already trusted" without needing a local .cer
        /// to hash first. Used by the Center menu to decide msix-only vs. full-zip before downloading.
        /// </summary>
        private const string KnownSubject = "CN=ClawTweaks Dev, O=MSIClaw";

        /// <summary>Exactly where the certificate has to go, in the wizard's own words. Shown to the
        /// user verbatim — any paraphrase here turns into a support case.</summary>
        public static IReadOnlyList<string> ImportSteps { get; } = new[]
        {
            "Click \"Install Certificate…\" on the General tab.",
            "Store Location: choose \"Local Machine\" (not \"Current User\"), then Next. " +
                "Windows asks for administrator rights here — that prompt comes from Windows itself.",
            "Choose \"Place all certificates in the following store\", click Browse…",
            "Select \"Trusted People\" — NOT \"Trusted Root Certification Authorities\".",
            "Next, then Finish. You should see \"The import was successful.\"",
        };

        /// <summary>True if a cert with the known ClawTweaks Subject is already in LocalMachine\TrustedPeople.</summary>
        public static bool IsKnownCertAlreadyTrusted()
        {
            try
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly);
                return store.Certificates
                    .Cast<X509Certificate2>()
                    .Any(c => string.Equals(c.Subject, KnownSubject, StringComparison.Ordinal));
            }
            catch { return false; }
        }

        /// <summary>Finds the signing .cer shipped next to <see cref="SetupContext.AssetRoot"/> (and its package subfolder).</summary>
        public static string FindSiblingCer()
        {
            try
            {
                string dir = SetupContext.AssetRoot;
                foreach (var d in new[] { dir, Path.Combine(dir, "Package") })
                {
                    if (!Directory.Exists(d)) continue;
                    var cer = Directory.GetFiles(d, "*.cer", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (cer != null) return cer;
                }
            }
            catch { }
            return null;
        }

        public static string ThumbprintOf(string cerPath)
        {
            try
            {
                using var c = X509CertificateLoader.LoadCertificateFromFile(cerPath);
                return c.Thumbprint;
            }
            catch { return null; }
        }

        public static string SubjectOf(string cerPath)
        {
            try
            {
                using var c = X509CertificateLoader.LoadCertificateFromFile(cerPath);
                return c.Subject;
            }
            catch { return null; }
        }

        /// <summary>True if a cert with this thumbprint is already in LocalMachine\TrustedPeople.</summary>
        public static bool IsTrusted(string thumbprint)
        {
            if (string.IsNullOrEmpty(thumbprint)) return false;
            try
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly);
                return store.Certificates
                    .Cast<X509Certificate2>()
                    .Any(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        /// <summary>Where a copy of our signing certificate was found, if anywhere.</summary>
        public enum CertPlacement
        {
            /// <summary>Not in any store we look at — the user hasn't imported it yet.</summary>
            Missing,
            /// <summary>LocalMachine\TrustedPeople. The one that works.</summary>
            Correct,
            /// <summary>CurrentUser\TrustedPeople — right store, wrong scope ("Current User" picked
            /// instead of "Local Machine" on the wizard's first page). MSIX deployment does not consult
            /// the per-user store, so this looks installed to the user and changes nothing.</summary>
            WrongScopeCurrentUser,
            /// <summary>A Root CA store. Overshoots badly: it makes the cert a trust anchor for
            /// everything, not just our package, and it still is not where sideloading looks.</summary>
            WrongStoreRoot,
        }

        /// <summary>
        /// Works out WHERE the user actually put the certificate, not just whether the correct store
        /// has it. The two mistakes below are the ones people really make in that wizard, and both
        /// leave them convinced they did the step — a bare "still not trusted" would send them round
        /// the same loop. Checks the correct location first, so the LocalMachine copy always wins:
        /// the CurrentUser view of TrustedPeople mirrors the machine store, and reading it first would
        /// mis-report a correct install as the per-user mistake.
        /// </summary>
        public static CertPlacement Diagnose(string thumbprint)
        {
            if (string.IsNullOrEmpty(thumbprint)) return CertPlacement.Missing;

            if (Contains(StoreName.TrustedPeople, StoreLocation.LocalMachine, thumbprint))
                return CertPlacement.Correct;
            if (Contains(StoreName.Root, StoreLocation.LocalMachine, thumbprint) ||
                Contains(StoreName.Root, StoreLocation.CurrentUser, thumbprint))
                return CertPlacement.WrongStoreRoot;
            if (Contains(StoreName.TrustedPeople, StoreLocation.CurrentUser, thumbprint))
                return CertPlacement.WrongScopeCurrentUser;

            return CertPlacement.Missing;
        }

        private static bool Contains(StoreName name, StoreLocation location, string thumbprint)
        {
            try
            {
                using var store = new X509Store(name, location);
                store.Open(OpenFlags.ReadOnly);
                return store.Certificates
                    .Cast<X509Certificate2>()
                    .Any(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        /// <summary>
        /// Opens Explorer with the .cer preselected. The user double-clicks it themselves.
        ///
        /// This deliberately does NOT launch the .cer for them. Center used to (plain ShellExecute of
        /// the file, which opens the certificate viewer and its "Install Certificate…" wizard) and
        /// Windows Defender flagged Center for it on 2026-07-30 with
        /// <c>Behavior:Win32/DefenseEvasion.A!ml</c> — "Dieses Programm ist gefährlich" — then deleted
        /// the exe and kept flagging every later install. Which is fair: an UNSIGNED process driving a
        /// certificate into a machine trust store is a documented attack technique (MITRE T1553.004,
        /// Install Root Certificate), and behavioural ML cannot tell our reason from an attacker's.
        ///
        /// Opening a folder is not that. The user launching a .cer from Explorer is ordinary desktop
        /// behaviour attributed to Explorer, not to us, and the trust decision visibly belongs to them.
        /// Do not "improve" this back into launching the file.
        /// </summary>
        public static bool ShowInExplorer(string cerPath, Action<string> log = null)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{cerPath}\"")
                {
                    UseShellExecute = true,
                });
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not open the folder: {ex.Message}");
                return false;
            }
        }
    }
}
