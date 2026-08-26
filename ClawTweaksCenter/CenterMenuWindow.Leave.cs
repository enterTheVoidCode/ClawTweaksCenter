using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClawTweaksCenter.Core;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// "Uninstall ClawTweaks" — the guided way out, and the screen Windows lands on when someone
    /// uninstalls Center from Settings → Apps (see App.OnStartup's --uninstall).
    ///
    /// Why a wizard rather than a single Remove button: three of the things ClawTweaks changes are
    /// HARDWARE state — the battery charge limit, the fan curve in the EC, and which controller the
    /// device presents. None of them are undone by removing an app. A user who deletes ClawTweaks
    /// first keeps a charge limit they can no longer see, on a device whose fan is following a curve
    /// nothing owns any more. The steps exist to put those back while the software that can still do
    /// it is present.
    ///
    /// The escape hatch is deliberate and never gated: "Uninstall Center" works even when every step
    /// above it is blocked. Someone who already removed ClawTweaks is warned, told what to do about
    /// it, and then allowed to proceed anyway — a wizard that cannot be finished is worse than one
    /// that finishes badly.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private readonly LeaveRunner _leave;          // assigned in the main ctor with the shared pipe client

        private enum LeavePage { Steps, Confirm }
        private LeavePage _leavePage = LeavePage.Steps;
        private int _leaveSelectedIndex;
        private int _leaveConfirmIndex = -1;
        private FrameworkElement _leaveSelectedCard;

        /// <summary>True when this Center was started BY the Windows uninstaller. It changes two
        /// things: the screen says so, and leaving it ends the process rather than going Home —
        /// Windows started us to uninstall, and dumping the user on a start screen instead is how
        /// "the uninstall did nothing" happens.</summary>
        private bool _leaveFromWindowsUninstall;

        // ── Entry ──────────────────────────────────────────────────────────────────────────────
        private void OpenLeave()
        {
            _view = View.Leave;
            _leavePage = LeavePage.Steps;
            _leaveSelectedIndex = 0;
            _leave.ClawTweaksInstalled = _installedVersion != null;
            RenderLeave();
            RefreshActionBar();
            _ = _leave.RefreshAsync();
        }

        private void LeaveLeave()
        {
            // Started by the Windows uninstaller: there is no Home to go back to that the user asked
            // for. Closing is the honest answer to "I changed my mind".
            if (_leaveFromWindowsUninstall) { Application.Current.Shutdown(); return; }
            GoHome();
        }

        // ── Render ─────────────────────────────────────────────────────────────────────────────
        private void RenderLeave()
        {
            BeginContent(centred: false);
            _leaveSelectedCard = null;

            if (_leavePage == LeavePage.Confirm) { RenderLeaveConfirm(); RefreshActionBar(); return; }

            ContentHost.Children.Add(UiHelpers.Title("Uninstall ClawTweaks"));
            ContentHost.Children.Add(UiHelpers.Body(
                "Work down the list. Each step is optional except the last."));

            if (_leaveFromWindowsUninstall)
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Info, "Started from Windows",
                    "Windows asked to uninstall Center. Finish here, or press B to keep everything."));

            RenderLeaveBanner();

            for (int i = 0; i < _leave.Steps.Count; i++)
                ContentHost.Children.Add(BuildLeaveCard(i));

            _leaveSelectedCard?.BringIntoView();
            RefreshActionBar();
        }

        /// <summary>The one thing the user has to understand before anything else on this screen:
        /// whether the device can still be put back. Shown in all three states, not just the bad one
        /// — a note that only appears when something is wrong cannot tell "fine" from "never
        /// checked".</summary>
        private void RenderLeaveBanner()
        {
            if (_leave.ClawTweaksInstalled && _leave.HelperHealthy)
            {
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Ok, "The device can be restored",
                    "ClawTweaks is installed and its helper is answering."));
                return;
            }

            if (!_leave.ClawTweaksInstalled)
            {
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "ClawTweaks is not installed",
                    "The charge limit, the fan curve and the controller mode can only be reset by ClawTweaks. " +
                    "Install it again to restore them, then come back here. You can still uninstall Center."));
                return;
            }

            ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Warning,
                _leave.IsConnecting ? "Connecting to the helper…" : "The helper is not answering",
                _leave.IsConnecting
                    ? "Checking whether the device can be restored."
                    : "Open the Game Bar (Win+G) once to start it, then press Ⓨ. You can still uninstall Center."));
        }

        private Border BuildLeaveCard(int index)
        {
            var step = _leave.Steps[index];
            bool selected = index == _leaveSelectedIndex;
            bool working = step.State == LeaveStepState.Working;

            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement statusEl = working
                ? UiHelpers.Badge(StatusKind.Working, 20)
                : new TextBlock
                {
                    Text = LeaveGlyph(step.State),
                    FontSize = 18, FontWeight = FontWeights.Bold,
                    Foreground = LeaveBrush(step.State),
                };
            statusEl.Width = 26;
            statusEl.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(statusEl, 0);
            row.Children.Add(statusEl);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            text.Children.Add(new TextBlock
            {
                Text = $"{index + 1}. {Core.Loc.T(step.Title)}",
                FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            });
            text.Children.Add(new TextBlock
            {
                Text = Core.Loc.T(step.What),
                FontSize = 13, Foreground = UiHelpers.Subtle, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
            if (!string.IsNullOrEmpty(step.Detail))
                text.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T(step.Detail),
                    FontSize = 13, Foreground = LeaveBrush(step.State), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            bool enabled = step.Actionable && !working && !_leave.IsConnecting;
            var btn = new Button
            {
                Content = Core.Loc.T(working ? "Working…" : LeaveButtonLabel(index)),
                Style = (Style)Application.Current.Resources["SetupButton"],
                IsEnabled = enabled,
                Opacity = enabled ? 1.0 : 0.4,
                MinWidth = 110,
                VerticalAlignment = VerticalAlignment.Center,
            };
            btn.Click += (_, __) => { _leaveSelectedIndex = index; ActivateLeaveStep(index); };
            Grid.SetColumn(btn, 2);
            row.Children.Add(btn);

            var pad = new Thickness(16, 12, 16, 12);
            var card = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 10),
                BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(selected ? 2 : 0),
                Padding = selected ? Deflate(pad, 2) : pad,
                Cursor = Cursors.Hand,
                Child = row,
            };
            if (selected) _leaveSelectedCard = card;
            return card;
        }

        private static string LeaveGlyph(LeaveStepState s) =>
            s == LeaveStepState.Ok ? "✓" :
            s == LeaveStepState.Error ? "✕" :
            s == LeaveStepState.Warning ? "!" :
            s == LeaveStepState.Blocked ? "–" : "○";

        private static Brush LeaveBrush(LeaveStepState s) =>
            s == LeaveStepState.Ok ? UiHelpers.Ok :
            s == LeaveStepState.Error ? UiHelpers.Error :
            s == LeaveStepState.Warning ? UiHelpers.Warn : UiHelpers.Subtle;

        private static string LeaveButtonLabel(int index) =>
            index == LeaveRunner.StepRestore ? "Restore" :
            index == LeaveRunner.StepCenterM ? "Turn on" : "Uninstall";

        // ── Confirm ────────────────────────────────────────────────────────────────────────────
        private void RenderLeaveConfirm()
        {
            int i = _leaveConfirmIndex;
            if (i < 0 || i >= _leave.Steps.Count) { _leavePage = LeavePage.Steps; RenderLeave(); return; }

            ContentHost.Children.Add(UiHelpers.Title(_leave.Steps[i].Title));

            if (i == LeaveRunner.StepRestore)
            {
                ContentHost.Children.Add(UiHelpers.Body("This puts the device back and clears your ClawTweaks settings:"));
                AddLeaveBullets(
                    "• All profiles, fan curves, TDP and controller settings are reset",
                    "• The battery charge limit is switched off",
                    "• The fan goes back to firmware control",
                    "• The hardware controller comes back");
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Info, "A backup is saved first",
                    "Your current settings are backed up before the reset. The Game Bar closes — reopen it with Win+G."));
            }
            else if (i == LeaveRunner.StepRemoveApp)
            {
                ContentHost.Children.Add(UiHelpers.Body("This removes the ClawTweaks app from Windows."));
                AddLeaveBullets(
                    "• The widget disappears from the Game Bar",
                    "• The background helper stops and removes its scheduled task",
                    "• Your profiles and settings are deleted with the app");
                if (_leave.Steps[LeaveRunner.StepRestore].State != LeaveStepState.Ok)
                    ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Restore the device first",
                        "Step 1 has not run. After this, nothing on the device can switch the charge limit " +
                        "off or hand the fan back to firmware."));
            }
            else
            {
                ContentHost.Children.Add(UiHelpers.Body("This removes ClawTweaks Center and closes it."));
                AddLeaveBullets(
                    "• The Start Menu entry and the desktop shortcut go",
                    "• Center's own settings are cleared",
                    "• The ClawTweaks app is NOT removed by this");
                if (_leave.ClawTweaksInstalled)
                    ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "ClawTweaks stays installed",
                        "Center is how you update ClawTweaks and reset the device. Remove ClawTweaks first " +
                        "if you meant to remove both."));
            }
        }

        private void AddLeaveBullets(params string[] lines)
        {
            var stack = new StackPanel { Margin = new Thickness(4, 4, 0, 8) };
            foreach (var line in lines)
                stack.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T(line), FontSize = 15, Foreground = UiHelpers.Text,
                    Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
            ContentHost.Children.Add(stack);
        }

        // ── Navigation ─────────────────────────────────────────────────────────────────────────
        private void MoveLeaveSelection(PadButton dir)
        {
            if (_leavePage != LeavePage.Steps) return;
            int next = _leaveSelectedIndex;
            if (dir == PadButton.Up) next--;
            else if (dir == PadButton.Down) next++;
            else return;

            if (next < 0) next = 0;
            if (next > _leave.Steps.Count - 1) next = _leave.Steps.Count - 1;
            if (next == _leaveSelectedIndex) return;

            _leaveSelectedIndex = next;
            RenderLeave();
        }

        private void RefreshLeaveActionBar()
        {
            if (_leavePage == LeavePage.Confirm)
            {
                AddAction(PadButton.A, LeaveConfirmLabel(_leaveConfirmIndex), true, () => RunLeaveStep(_leaveConfirmIndex));
                AddAction(PadButton.B, "Cancel", true, () => { _leavePage = LeavePage.Steps; RenderLeave(); });
                AddScrollHint();
                return;
            }

            var sel = (_leaveSelectedIndex >= 0 && _leaveSelectedIndex < _leave.Steps.Count)
                ? _leave.Steps[_leaveSelectedIndex] : null;
            bool canRun = sel != null && sel.Actionable && sel.State != LeaveStepState.Working && !_leave.IsConnecting;

            AddAction(PadButton.A, sel == null ? "Run" : LeaveButtonLabel(_leaveSelectedIndex), canRun,
                () => ActivateLeaveStep(_leaveSelectedIndex));
            AddAction(PadButton.Y, "Re-check", !_leave.IsConnecting, () => _ = _leave.RefreshAsync());

            // Only when it is the answer to the banner above: without ClawTweaks the device cannot be
            // restored, and Update & Release is where it is installed from.
            if (!_leave.ClawTweaksInstalled)
                AddAction(PadButton.X, "Get ClawTweaks", true, OpenBrowse);

            AddAction(PadButton.B, _leaveFromWindowsUninstall ? "Keep everything" : "Back", true, LeaveLeave);
            AddScrollHint();
        }

        private static string LeaveConfirmLabel(int index) =>
            index == LeaveRunner.StepRestore ? "Yes, restore the device" :
            index == LeaveRunner.StepRemoveApp ? "Yes, uninstall ClawTweaks" : "Yes, uninstall Center";

        // ── Activation ─────────────────────────────────────────────────────────────────────────
        private void ActivateLeaveStep(int index)
        {
            if (index < 0 || index >= _leave.Steps.Count) return;
            var step = _leave.Steps[index];
            if (!step.Actionable || step.State == LeaveStepState.Working) { RenderLeave(); return; }

            if (step.NeedsConfirm)
            {
                _leaveConfirmIndex = index;
                _leavePage = LeavePage.Confirm;
                RenderLeave();
                return;
            }
            RunLeaveStep(index);
        }

        private void RunLeaveStep(int index)
        {
            _leavePage = LeavePage.Steps;
            switch (index)
            {
                case LeaveRunner.StepRestore: _ = _leave.RestoreDeviceAsync(); break;
                case LeaveRunner.StepCenterM: _ = _leave.ReenableCenterMAsync(); break;
                case LeaveRunner.StepRemoveApp: _ = RemoveClawTweaksThenRefreshAsync(); break;
                case LeaveRunner.StepRemoveCenter: UninstallCenterNow(); break;
            }
            RenderLeave();
        }

        private async Task RemoveClawTweaksThenRefreshAsync()
        {
            await _leave.RemoveClawTweaksAsync();
            // The tab strip, the library tile and the version chip all hang off this one value, and
            // the app it describes is gone.
            _installedVersion = null;
            _installedVersionChecked = true;
            RefreshTabStrip();
            RenderLeave();
        }

        /// <summary>
        /// Removes Center and ends the process. This is the same call the Windows uninstaller made
        /// before this screen existed — the folder delete is handed to a short-lived cmd.exe because
        /// a running exe cannot delete its own file, so shutting down immediately afterwards is not
        /// impatience, it is what lets the delete succeed.
        /// </summary>
        private void UninstallCenterNow()
        {
            var step = _leave.Steps[LeaveRunner.StepRemoveCenter];
            step.State = LeaveStepState.Working;
            step.Detail = "Removing Center…";
            RenderLeave();

            try { SelfInstaller.Uninstall(); }
            catch (Exception ex) { InstallLog.Write("Center uninstall failed: " + ex.Message); }

            Application.Current.Shutdown();
        }
    }
}
