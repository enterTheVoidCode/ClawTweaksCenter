using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ClawTweaksSetup.Core
{
    /// <summary>
    /// "Is this downloaded installer really signed by who we expect?" — WinVerifyTrust for the chain,
    /// plus a publisher-name check so a validly-signed file from someone else is still refused.
    ///
    /// Shared by every place Center fetches a third-party installer (<see cref="UsbipSetup"/>,
    /// <see cref="HidHideSetup"/>). It is the substitute for pinning a URL: HidHide's asset filenames
    /// carry their version, so its download URL has to be resolved at runtime from the release API,
    /// and the signature is then what proves the bytes are the vendor's.
    /// </summary>
    public static class AuthenticodeVerifier
    {
        /// <summary>
        /// True only when the file carries a valid Authenticode signature AND the signing certificate's
        /// subject contains one of <paramref name="expectedSignerSubstrings"/>. Any failure — unsigned,
        /// broken chain, revoked, wrong publisher, unreadable — returns false. Never throws: a
        /// verification error must read as "do not run this", never as an exception the caller might
        /// end up swallowing into a success path.
        /// </summary>
        public static bool IsSignedBy(string path, params string[] expectedSignerSubstrings)
        {
            if (!WinVerifyTrustValid(path)) return false;
            if (expectedSignerSubstrings == null || expectedSignerSubstrings.Length == 0) return true;

            try
            {
                var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                string subject = cert.Subject ?? string.Empty;
                foreach (var expected in expectedSignerSubstrings)
                {
                    if (!string.IsNullOrEmpty(expected) &&
                        subject.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        #region WinVerifyTrust
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private static bool WinVerifyTrustValid(string path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                pcwszFilePath = path,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,          // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,
                    dwProvFlags = 0x00000010, // WTD_SAFER_FLAG
                };
                IntPtr pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                try
                {
                    Marshal.StructureToPtr(data, pData, false);
                    uint result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);
                    return result == 0;
                }
                finally { Marshal.FreeHGlobal(pData); }
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(pFile); }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hWnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }
        #endregion
    }
}
