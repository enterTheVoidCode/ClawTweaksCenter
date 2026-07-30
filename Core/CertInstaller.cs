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
    /// when a .cer is double-clicked (<see cref="OpenForImport"/>). That wizard raises its own prompt
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

        /// <summary>
        /// Opens the .cer with its default shell handler, which on Windows is the certificate viewer
        /// with the "Install Certificate…" button that starts the import wizard. Plain ShellExecute,
        /// no "runas" verb — the wizard asks for rights itself once the user picks Local Machine.
        /// Returns false (never throws) if the shell could not open it, so the caller can fall back to
        /// showing the path and letting the user open it from Explorer.
        /// </summary>
        public static bool OpenForImport(string cerPath, Action<string> log = null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(cerPath) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not open the certificate: {ex.Message}");
                return false;
            }
        }

        /// <summary>Opens Explorer with the .cer preselected — the fallback for when the shell has no
        /// handler registered for .cer, or the user closed the viewer and wants to find the file.</summary>
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
