using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The two side columns of the Library quick menu (CenterMenuWindow.Library.cs's exit prompt,
    /// renamed and widened 2026-09-03): tray apps on the left, curated Windows tools on the right.
    /// Neither column is new UI grammar - both reuse BuildRowVisual and the same three-list-per-
    /// column navigation model the middle column already had, extended to three columns in Library.cs.
    ///
    /// WHY THIS GOES THROUGH THE HELPER, AND NOT CENTER ITSELF. Center never elevates (project rule);
    /// the helper always does (its scheduled task runs at RunLevel Highest). Resolving another
    /// process's exe path reliably needs that elevation - measured live, 2026-09-03: the same call
    /// that fails or comes back empty for RTSS/Steam from an unelevated shell resolves both instantly
    /// once elevated. Center already talks to the helper for everything else on this screen (power
    /// actions, backup/restore) - this is the same pipe, not a new one.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private sealed class TrayAppEntry
        {
            public string Exe { get; set; }
            public int Pid { get; set; }
            public string Name { get; set; }
            public string ExePath { get; set; }
            public bool CanOpen { get; set; }
            public bool CanClose { get; set; }
            public string Reason { get; set; }
        }

        // Null = never asked yet / still waiting on the helper; empty = asked, got nothing. The tray
        // column reads the difference to show "Loading..." only the first time, not on every refresh.
        private List<TrayAppEntry> _trayApps;

        private const string TrayGlyph = "";   // GlobalNavButton - generic, always renders
        private const string ToolGlyph = "";   // Setting (gear)

        /// <summary>Asks the helper to build the tray list and re-renders the column when it answers.
        /// Fire-and-forget from OpenExitPrompt's point of view - a beat of "Loading..." on a menu the
        /// user just opened is the accepted cost, not a bug to route around.</summary>
        private async Task RequestTrayAppsAsync()
        {
            try
            {
                if (_helperPipe == null) { _trayApps = new List<TrayAppEntry>(); RefreshTrayColumnIfStillOpen(); return; }

                if (!_helperPipe.IsConnected)
                {
                    bool connected = await _helperPipe.ConnectAsync(TimeSpan.FromSeconds(6), m => Core.InstallLog.Write(m));
                    if (!connected) { _trayApps = new List<TrayAppEntry>(); RefreshTrayColumnIfStillOpen(); return; }
                }

                string json = await _helperPipe.RequestWithResultAsync(
                    "TrayAppList", "", Shared.Enums.Function.TrayAppList, TimeSpan.FromSeconds(5));

                _trayApps = ParseTrayApps(json);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("RequestTrayAppsAsync failed: " + ex.Message);
                _trayApps = new List<TrayAppEntry>();
            }

            RefreshTrayColumnIfStillOpen();
        }

        private static List<TrayAppEntry> ParseTrayApps(string json)
        {
            if (string.IsNullOrEmpty(json)) return new List<TrayAppEntry>();
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<List<TrayAppEntry>>(json, options) ?? new List<TrayAppEntry>();
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("TrayAppList JSON could not be parsed: " + ex.Message);
                return new List<TrayAppEntry>();
            }
        }

        /// <summary>The whole exit prompt re-renders wholesale, the same as ShowPowerActionFailed
        /// already does for its own note line - a second, narrower "just repaint the tray column"
        /// path would be a second way this screen can be wrong.</summary>
        private void RefreshTrayColumnIfStillOpen()
        {
            if (_exitPromptOpen) RenderExitPrompt();
        }

        // The side columns start BELOW the middle one. The middle column is the answer to "what did I
        // open this menu for" and it earns the top of the screen; the two lists beside it are places
        // to go afterwards. Dropping them clear of the heading also stops the three columns reading as
        // one three-part row (user, 2026-09-04).
        private const double SideColumnTopOffset = 64;

        // Six rows, then scroll. Six is what the curated tools column holds exactly, and the tray list
        // on a busy machine is otherwise long enough to run off the bottom of an eight-inch panel.
        //
        // The two columns no longer end on the same line, and that is the cost of the gear fix
        // (2026-09-05): a tools row has one line where a tray row has two, so it is genuinely
        // shorter. Rows that pretend to a height they do not need is what put the gear above its
        // own label in the first place - see BuildRowVisual.
        private const int SideColumnMaxRows = 6;

        // 58, not 50. This is the height the scroller is capped at PER ROW, and 50 was an estimate
        // that came out under the real thing - so the sixth row ("Installed apps", the last of the
        // curated tools) was clipped by about a fifth. Measured against the row it has to fit: two
        // lines of 14/11 pt plus 7 px of padding top and bottom plus a 2 px margin.
        //
        // ⚠️ Erring HIGH is the safe direction. Too large only means the scroller stops short of its
        // cap; too small silently cuts the last row in half, which is what happened here.
        private const double SideColumnRowHeight = 58;

        /// <summary>The RIGHT column (it moved there on 2026-09-05). Every row that resolved a window (CanOpen/CanClose from the
        /// helper) is fully interactive; every row that did not is drawn dim, with the helper's own
        /// reason as its subtitle, and is skipped by navigation entirely - see BuildRowVisual's own
        /// doc comment for why "there but unreachable" beats either extreme.</summary>
        private UIElement BuildTrayColumn()
        {
            var panel = new StackPanel();

            if (_trayApps == null)
            {
                panel.Children.Add(SidebarNote("Loading…"));
            }
            else if (_trayApps.Count == 0)
            {
                panel.Children.Add(SidebarNote("Nothing found"));
            }
            else
            {
                foreach (var app in _trayApps)
                {
                    bool enabled = app.CanOpen;
                    string subtitle = enabled ? app.Exe : Core.Loc.T(app.Reason ?? "");
                    var row = BuildRowVisual(TrayGlyph, app.Name, subtitle, inCard: false, dim: !enabled, compact: true);

                    if (enabled)
                    {
                        int index = _exitPromptTrayRows.Count;
                        row.Tag = index;
                        var capturedApp = app;
                        row.MouseLeftButtonUp += (_, __) =>
                        {
                            _exitPromptColumn = ExitPromptColumnTray;
                            _exitPromptTrayIndex = index;
                            ActivateExitPromptSelection();
                        };
                        _exitPromptTrayRows.Add(row);
                        _exitPromptTrayActions.Add(() => SendTrayAppOpen(capturedApp));
                        // NULL, not "skip": the two lists are read by index from the action bar, so a
                        // row that cannot be closed still needs its slot. The action bar reads the
                        // null and leaves the X prompt off instead of offering a button that does
                        // nothing - a labelled button that silently no-ops is the worse of the two.
                        _exitPromptTrayCloseActions.Add(
                            app.CanClose ? (Action)(() => SendTrayAppClose(capturedApp)) : null);
                    }

                    panel.Children.Add(row);
                }
            }

            return WrapSideColumn("System tray / Processes", panel, new Thickness(16, SideColumnTopOffset, 0, 0));
        }

        /// <summary>A side column: its heading, then a height-capped scroller holding the rows.
        ///
        /// The heading sits OUTSIDE the scroller on purpose - inside, it scrolls away on the first
        /// press and the column loses its label exactly when the user is furthest into it.
        /// </summary>
        private static UIElement WrapSideColumn(string headingKey, UIElement rows, Thickness margin)
        {
            var outer = new StackPanel { Margin = margin };
            outer.Children.Add(SidebarHeading(headingKey));
            outer.Children.Add(new ScrollViewer
            {
                Content = rows,
                MaxHeight = SideColumnMaxRows * SideColumnRowHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            return outer;
        }

        private void SendTrayAppOpen(TrayAppEntry app)
        {
            if (_helperPipe == null || !_helperPipe.IsConnected) return;

            // Center is the foreground process at this instant; the helper is a background service and
            // the app it is about to raise is a third process entirely. Without this, Windows' own
            // foreground lock refuses the raise and the app comes back BEHIND Center - measured as
            // exactly that failure in the Quick Settings panel, where SetForegroundWindow returned
            // False four times out of four while another app held the foreground. Only the process
            // that currently HAS the foreground can hand that right on, so it has to happen here.
            try { AllowSetForegroundWindow(ASFW_ANY); } catch { /* not fatal, only less reliable */ }

            _helperPipe.SendRequest("TrayAppOpen", $"{app.Pid}|{app.Exe}");
        }

        private const int ASFW_ANY = -1;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        private void SendTrayAppClose(TrayAppEntry app)
        {
            if (_helperPipe == null || !_helperPipe.IsConnected) return;
            _helperPipe.SendRequest("TrayAppClose", $"{app.Pid}|{app.Exe}");

            // Nice-to-have, not load-bearing: refresh the list a moment later so a closed app
            // disappears on its own instead of needing the user to back out and back in to see it.
            _ = RefreshTrayAppsSoonAsync();
        }

        private async Task RefreshTrayAppsSoonAsync()
        {
            await Task.Delay(1200);
            if (_exitPromptOpen) await RequestTrayAppsAsync();
        }

        private static UIElement SidebarNote(string text) => new TextBlock
        {
            Text = Core.Loc.T(text),
            FontSize = 12,
            Foreground = UiHelpers.Subtle,
            Margin = new Thickness(8, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        /// <summary>The title over a side column. Centred, and NOT InfoHeading: that one is the
        /// left-aligned section heading of the Library info page, where headings sit above prose that
        /// also starts at the left edge. Here the heading labels a whole column, so it belongs over
        /// the column's middle - requested on device, 2026-09-04.</summary>
        private static UIElement SidebarHeading(string text) => new TextBlock
        {
            Text = Core.Loc.T(text),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiHelpers.Subtle,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };

        // ── The left column: curated Windows tools ─────────────────────────────────────────────
        //
        // Curated, not enumerated - there is no registry key listing "the tools a handheld user
        // might want mid-game" the way NotifyIconSettings lists tray apps. Launched directly by
        // Center itself: none of these need the helper (they need no elevation Center does not
        // already have, and UseShellExecute resolves .msc/ms-settings: the same way double-clicking
        // them would), so this is the one half of the menu that stays entirely local.
        //
        // No Event Viewer: Computer Management hosts it as its first node, so a second row for it is
        // a second door into the same room (user, 2026-09-04).
        private static readonly (string NameKey, string Target)[] QuickTools =
        {
            ("Windows Explorer", "explorer.exe"),
            ("Task Manager", "taskmgr.exe"),
            ("Windows Settings", "ms-settings:"),
            ("Control Panel (classic)", "control.exe"),
            ("Computer Management", "compmgmt.msc"),
            ("Installed apps", "ms-settings:appsfeatures"),
        };

        private UIElement BuildToolsColumn()
        {
            var panel = new StackPanel();

            foreach (var tool in QuickTools)
            {
                var row = BuildRowVisual(ToolGlyph, tool.NameKey, null, inCard: false, compact: true);
                int index = _exitPromptToolsRows.Count;
                row.Tag = index;
                string capturedTarget = tool.Target;
                row.MouseLeftButtonUp += (_, __) =>
                {
                    _exitPromptColumn = ExitPromptColumnTools;
                    _exitPromptToolsIndex = index;
                    ActivateExitPromptSelection();
                };
                _exitPromptToolsRows.Add(row);
                _exitPromptToolsActions.Add(() => LaunchTool(capturedTarget));
                panel.Children.Add(row);
            }

            return WrapSideColumn("Windows tools", panel, new Thickness(0, SideColumnTopOffset, 16, 0));
        }

        /// <summary>UseShellExecute, not a direct exe launch - it is what resolves ".msc" to mmc.exe
        /// and "ms-settings:appsfeatures" to the Settings app the same way double-clicking either
        /// would, without Center needing to know that mapping itself.</summary>
        private static void LaunchTool(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write($"Could not launch '{target}': {ex.Message}");
            }
        }
    }
}
