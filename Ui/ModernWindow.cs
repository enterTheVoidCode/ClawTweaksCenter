using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClawTweaksSetup.Ui
{
    /// <summary>
    /// Gives a window native Windows 11 chrome — dark title bar, rounded corners, caption/border
    /// colours matched to App.xaml — and keeps it inside the monitor's work area.
    ///
    /// The work-area clamp is the part that matters on the device: Center's windows are sized for a
    /// desktop, and on the Claw's 8" panel (or any display running a high scale factor) a window
    /// asking for 760 logical pixels of height can end up taller than the screen, with its buttons
    /// under the taskbar and no way to reach them — the windows are not resizable by touch alone.
    /// Sizes are therefore treated as an upper bound, not a demand.
    ///
    /// Every step is best-effort: an unsupported Windows build simply keeps its default chrome, and a
    /// failed interop call falls back to WindowStartupLocation. Nothing here may prevent a window from
    /// opening.
    /// </summary>
    internal static class ModernWindow
    {
        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
        private const int DwmWindowCornerPreference = 33;
        private const int DwmBorderColor = 34;
        private const int DwmCaptionColor = 35;
        private const int DwmTextColor = 36;
        private const int DwmWindowCornerRound = 2;
        private const uint MonitorDefaultToNearest = 2;

        public static void Apply(Window window, double edgeMargin = 32)
        {
            window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            window.SourceInitialized += (_, __) =>
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                ApplyNativeChrome(hwnd);
                CenterAndConstrainToWorkArea(window, hwnd, edgeMargin);
            };
        }

        private static void ApplyNativeChrome(IntPtr hwnd)
        {
            try
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));

                int corners = DwmWindowCornerRound;
                DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref corners, sizeof(int));

                // Match the native caption to App.xaml; COLORREF is 0x00BBGGRR.
                int caption = ColorRef(0x20, 0x20, 0x20);
                int border = ColorRef(0x3A, 0x3A, 0x3A);
                int text = ColorRef(0xFF, 0xFF, 0xFF);
                DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
            }
            catch
            {
                // Unsupported Windows builds retain native chrome.
            }
        }

        private static void CenterAndConstrainToWorkArea(Window window, IntPtr hwnd, double edgeMargin)
        {
            try
            {
                IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

                var source = HwndSource.FromHwnd(hwnd);
                Matrix fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                Point topLeft = fromDevice.Transform(new Point(info.WorkArea.Left, info.WorkArea.Top));
                Point bottomRight = fromDevice.Transform(new Point(info.WorkArea.Right, info.WorkArea.Bottom));
                double workWidth = bottomRight.X - topLeft.X;
                double workHeight = bottomRight.Y - topLeft.Y;

                // Preserve a margin where possible; small displays use the full work area.
                double availableWidth = Math.Max(320, workWidth - edgeMargin * 2);
                double availableHeight = Math.Max(320, workHeight - edgeMargin * 2);
                window.Width = Math.Min(window.Width, availableWidth);
                window.Height = Math.Min(window.Height, availableHeight);
                window.MaxWidth = workWidth;
                window.MaxHeight = workHeight;
                window.Left = topLeft.X + (workWidth - window.Width) / 2;
                window.Top = topLeft.Y + (workHeight - window.Height) / 2;
            }
            catch
            {
                // Fall back to WindowStartupLocation when interop fails.
            }
        }

        private static int ColorRef(byte red, byte green, byte blue) =>
            red | (green << 8) | (blue << 16);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int valueSize);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public RectInt Monitor;
            public RectInt WorkArea;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RectInt
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
