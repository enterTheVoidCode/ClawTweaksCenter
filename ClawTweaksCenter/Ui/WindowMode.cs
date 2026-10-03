using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClawTweaksCenter.Core;

namespace ClawTweaksCenter.Ui
{
    /// <summary>
    /// Borderless fullscreen, and the foreground grab that makes it worth having.
    ///
    /// ── Why this exists ──────────────────────────────────────────────────────────────────────────
    /// Center is a gamepad app on a handheld, and it was opening as an ordinary window BEHIND whatever
    /// was already fullscreen — Steam Big Picture in the reported case. Two things went wrong at once
    /// there, and they need separate fixes:
    ///
    ///   1. The window did not come to the front.  <see cref="ForceForeground"/>.
    ///   2. Both apps then reacted to the same stick input. Steam reads the pad regardless of focus, so
    ///      there is nothing to take away from it — the only lever we have is to stop being a window
    ///      sharing the screen with it. Hence fullscreen.
    ///
    /// ── This reverses an earlier decision, on purpose ────────────────────────────────────────────
    /// CenterMenuWindow used to be WindowStyle="None" and size itself to the work area; that was
    /// deliberately replaced by a normal resizable window (see the note in its constructor). This does
    /// not undo that — the windowed mode is still there, still resizable, still with native chrome. The
    /// difference is that fullscreen is now a REMEMBERED CHOICE (<see cref="CenterSettings"/>) rather
    /// than the only shape available, and it can be left with one button.
    /// </summary>
    internal static class WindowMode
    {
        /// <summary>
        /// Applies the stored window mode and keeps it applied. Call once, after
        /// <see cref="ModernWindow.Apply"/> — the ordering matters: ModernWindow clamps MaxWidth and
        /// MaxHeight to the monitor's WORK AREA, which would hold a "fullscreen" window at exactly the
        /// size that leaves the taskbar showing. Attaching afterwards means our SourceInitialized
        /// handler runs after that clamp and can lift it.
        /// </summary>
        public static void Attach(Window window)
        {
            if (window == null) return;
            window.SourceInitialized += (_, __) =>
            {
                if (CenterSettings.BorderlessFullscreen) EnterFullscreen(window);
            };

            AttachDisplayChangeReflow(window);
        }

        /// <summary>
        /// Re-applies fullscreen after a DISPLAY MODE CHANGE, because WPF does not.
        ///
        /// WPF computes the maximized bounds when the state is ENTERED - the note in EnterFullscreen
        /// below says the same thing for a different reason. So when a game drops the panel from
        /// 1200p to 800p and puts it back, the window keeps the bounds it was given at 800p and comes
        /// back visibly too small. Leaving the library and toggling fullscreen fixes it precisely
        /// because that runs Normal -> None -> Maximized again; this does the same thing without
        /// making the user find it.
        ///
        /// Four things this has to get right, and every one of them is a way to make it worse:
        ///   • SystemEvents raises on ITS OWN THREAD. Touching the window from there throws.
        ///   • It fires several times per mode change, and again at game start and game end, so it
        ///     is debounced rather than acted on per event.
        ///   • SystemEvents holds a STATIC handler reference. Without the unhook on Closed the window
        ///     never gets collected.
        ///   • It must NOT pull focus. The resolution change usually happens because a game is
        ///     starting, and stealing the foreground at that exact moment is worse than a small
        ///     window. Re-entering fullscreen does not call ForceForeground, and it must stay that way.
        /// </summary>
        private static void AttachDisplayChangeReflow(Window window)
        {
            DispatcherTimer debounce = null;

            EventHandler onChanged = null;
            onChanged = (_, __) =>
            {
                try
                {
                    window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (debounce == null)
                        {
                            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                            debounce.Tick += (___, ____) =>
                            {
                                debounce.Stop();
                                if (!IsFullscreen(window)) return;

                                // ONLY WHEN THE WINDOW IS ACTUALLY WRONG. Without this it ran the
                                // leave/enter cycle on every display event, and that cycle is visible:
                                // reported as Center flickering three times at start and twice after a
                                // game exits. Most of those events change nothing about our bounds -
                                // a refresh-rate switch, a second adapter settling, the same mode being
                                // re-applied - so the repair ran where there was nothing to repair.
                                //
                                // THE QUESTION IS RIGHT, THE MEASURING STICK WAS WRONG. This used to
                                // compare window.ActualWidth/Height against SystemParameters.Primary-
                                // Screen*, and both of those are WPF's own cached values in DIPs. A
                                // display event is exactly the moment they are stale: the mode has
                                // changed and WPF has not caught up, so the comparison reported a
                                // mismatch that did not exist and the cycle ran anyway.
                                //
                                // That cycle is not cosmetic. It drops WindowStyle, returns the window
                                // to Normal and re-maximizes it, and WPF throws the window's rendering
                                // surface away and rebuilds it. Measured 2026-09-14: the library goes
                                // black except for whatever repainted first, and the UI thread does not
                                // run for about 1.2 s - which is the reported "library black for a
                                // second and a half, right when the virtual controller appears". The
                                // display events that set it off arrive during the mount.
                                //
                                // So ask Win32 instead. GetWindowRect and the monitor rectangle are
                                // both physical pixels straight from the OS, need no DPI conversion,
                                // and are current the moment the mode change lands.
                                if (CoversItsMonitor(window, out string why))
                                {
                                    UiStallTrace.Write("window-mode reflow SKIPPED - " + why);
                                    return;
                                }

                                // The same cycle the toggle performs. Leave first so WPF recomputes
                                // the bounds for the mode that is live NOW; re-entering while already
                                // maximized would keep the stale ones, which is the whole bug.
                                UiStallTrace.Write("window-mode reflow RUNNING (rebuilds the render " +
                                                   "surface, the window goes black) - " + why);
                                var reflow = System.Diagnostics.Stopwatch.StartNew();
                                LeaveFullscreen(window);
                                EnterFullscreen(window);
                                UiStallTrace.Write($"window-mode reflow done in {reflow.ElapsedMilliseconds}ms");
                            };
                        }
                        debounce.Stop();
                        debounce.Start();
                    }));
                }
                catch { }
            };

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += onChanged;
            window.Closed += (_, __) =>
            {
                try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= onChanged; } catch { }
                try { debounce?.Stop(); } catch { }
            };
        }

        /// <summary>
        /// Does this window already cover the monitor it is on, measured in real pixels?
        ///
        /// Deliberately NOT asked of WPF. SystemParameters and ActualWidth/Height are cached DIP
        /// values, and the instant this matters - just after a display mode change - they are the OLD
        /// ones. GetWindowRect and GetMonitorInfo both answer in physical pixels from the OS, so there
        /// is no scale factor in the comparison and nothing to be stale.
        ///
        /// MONITOR_DEFAULTTONEAREST rather than the PRIMARY screen: the window is not necessarily on
        /// the primary one, and comparing it against a monitor it is not on would report a mismatch
        /// that never clears.
        ///
        /// <paramref name="why"/> carries the numbers either way, so the log says what was compared
        /// and not merely what was decided.
        /// </summary>
        private static bool CoversItsMonitor(Window window, out string why)
        {
            why = "no window handle yet";
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return false;

                if (!GetWindowRect(hwnd, out RECT w)) { why = "GetWindowRect failed"; return false; }

                IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi))
                {
                    why = "GetMonitorInfo failed";
                    return false;
                }

                int wWidth = w.Right - w.Left, wHeight = w.Bottom - w.Top;
                int mWidth = mi.rcMonitor.Right - mi.rcMonitor.Left;
                int mHeight = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

                // Two pixels of slack: rounding is not a mismatch.
                bool covers = Math.Abs(wWidth - mWidth) <= 2 && Math.Abs(wHeight - mHeight) <= 2;
                why = $"window {wWidth}x{wHeight} at {w.Left},{w.Top} vs monitor {mWidth}x{mHeight} " +
                      $"at {mi.rcMonitor.Left},{mi.rcMonitor.Top}";
                return covers;
            }
            catch (Exception ex)
            {
                // Never let the guard itself be the reason the window stays the wrong size: an
                // unreadable measurement falls through to the repair, which is what this always did.
                why = "guard threw: " + ex.Message;
                return false;
            }
        }

        /// <summary>True when the window is currently borderless fullscreen.</summary>
        public static bool IsFullscreen(Window window) =>
            window != null && window.WindowStyle == WindowStyle.None;

        /// <summary>Flips the mode and remembers the choice for the next start.</summary>
        public static void Toggle(Window window)
        {
            if (window == null) return;
            bool goFullscreen = !IsFullscreen(window);
            if (goFullscreen) EnterFullscreen(window); else LeaveFullscreen(window);
            CenterSettings.BorderlessFullscreen = goFullscreen;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, WindowedGeometry> Saved = new();

        private sealed class WindowedGeometry
        {
            public double Width, Height, MaxWidth, MaxHeight, Left, Top;
            public ResizeMode Resize;
            public WindowStyle Style;
        }

        private static void EnterFullscreen(Window window)
        {
            if (IsFullscreen(window)) return;
            try
            {
                Saved.Remove(window);
                Saved.Add(window, new WindowedGeometry
                {
                    Width = window.Width,
                    Height = window.Height,
                    MaxWidth = window.MaxWidth,
                    MaxHeight = window.MaxHeight,
                    Left = window.Left,
                    Top = window.Top,
                    Resize = window.ResizeMode,
                    Style = window.WindowStyle,
                });

                // The work-area clamp has to go first, or "maximized" stops short of the taskbar.
                window.MaxWidth = double.PositiveInfinity;
                window.MaxHeight = double.PositiveInfinity;

                // Return to Normal before dropping the chrome. WPF computes the maximized bounds when
                // the state is entered, so changing WindowStyle while already maximized leaves the
                // window sized for the OLD style — a border-width gap down two edges that looks like a
                // rendering fault rather than a mode.
                window.WindowState = WindowState.Normal;
                window.WindowStyle = WindowStyle.None;
                window.ResizeMode = ResizeMode.NoResize;
                window.WindowState = WindowState.Maximized;

                // The DWM corner preference set by ModernWindow survives the style change, and a
                // rounded window over a rectangular screen shows the desktop through all four
                // corners. Square for fullscreen, rounded again on the way back.
                ModernWindow.SetRoundedCorners(window, rounded: false);
            }
            catch
            {
                // A window that refuses to change shape stays usable in the shape it has.
            }
        }

        private static void LeaveFullscreen(Window window)
        {
            try
            {
                window.WindowState = WindowState.Normal;
                if (Saved.TryGetValue(window, out var g))
                {
                    window.WindowStyle = g.Style;
                    window.ResizeMode = g.Resize;
                    window.MaxWidth = g.MaxWidth;
                    window.MaxHeight = g.MaxHeight;
                    if (!double.IsNaN(g.Width)) window.Width = g.Width;
                    if (!double.IsNaN(g.Height)) window.Height = g.Height;
                    if (!double.IsNaN(g.Left)) window.Left = g.Left;
                    if (!double.IsNaN(g.Top)) window.Top = g.Top;
                }
                else
                {
                    window.WindowStyle = WindowStyle.SingleBorderWindow;
                    window.ResizeMode = ResizeMode.CanResize;
                }

                ModernWindow.SetRoundedCorners(window, rounded: true);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Actually takes the foreground, rather than asking for it.
        ///
        /// A bare <c>SetForegroundWindow</c> is a request Windows refuses unless the calling process
        /// already owns the foreground or was handed the right by the process that does (the helper
        /// does exactly that with AllowSetForegroundWindow when it launches Center from the widget).
        /// Started from the Start Menu with Steam Big Picture up, no such grant exists and the call
        /// silently does nothing but flash a taskbar button.
        ///
        /// Attaching our input queue to the current foreground thread makes Windows treat us as part of
        /// the same input context for the duration, which is the documented way out of the foreground
        /// lock. Detaching again immediately is not optional: leaving the queues attached ties our UI
        /// responsiveness to a foreign process.
        /// </summary>
        public static void ForceForeground(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                IntPtr foreground = GetForegroundWindow();
                uint ourThread = GetCurrentThreadId();
                uint foreignThread = foreground == IntPtr.Zero
                    ? ourThread
                    : GetWindowThreadProcessId(foreground, out _);

                bool attached = false;
                if (foreignThread != 0 && foreignThread != ourThread)
                    attached = AttachThreadInput(ourThread, foreignThread, true);

                try
                {
                    ShowWindow(hwnd, SwShow);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) AttachThreadInput(ourThread, foreignThread, false);
                }
            }
            catch
            {
                // Never let a focus tweak stop the window from being usable.
            }
        }

        private const int SwShow = 5;

        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
    }
}
