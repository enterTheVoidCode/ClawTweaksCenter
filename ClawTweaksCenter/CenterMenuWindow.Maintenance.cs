using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ClawTweaksCenter.Core;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The "Reset · Backup · Restore" maintenance column (third Home tile). Center is a pure trigger:
    /// every privileged file/process/ZIP operation runs in the elevated helper over the ClawTweaksCenter
    /// pipe (no UAC — the helper is already elevated). See Doku/PLAN_Backup_Restore.md +
    /// Doku/RESET_StoreMap_and_FactoryReset_Gaps.md and Core/MaintenanceRunner.cs.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private readonly MaintenanceRunner _maintenance; // assigned in the main ctor with the shared pipe client

        private enum MaintPage { Menu, ResetConfirm, BackupConfirm, RestoreList, RestoreConfirm, Busy, Result }
        private MaintPage _maintPage = MaintPage.Menu;

        private int _maintMenuIndex;                 // 0 = Reset, 1 = Backup, 2 = Restore
        private int _maintRestoreIndex;              // cursor over the backup list
        private List<MaintenanceRunner.BackupInfo> _maintBackups = new List<MaintenanceRunner.BackupInfo>();
        private MaintenanceRunner.BackupInfo _maintSelectedBackup;

        private string _maintBusyText = "";
        private StatusKind _maintResultKind = StatusKind.Info;
        private string _maintResultTitle = "";
        private string _maintResultDetail = "";
        private FrameworkElement _maintSelectedCard;

        private const int MaintMenuCount = 3;

        /// <summary>The Reset/Backup/Restore pipe verbs + result pushes (CenterResetResult /
        /// CenterBackupResult / CenterRestoreResult, and the Center-path widget-hive wipe) only exist in
        /// the helper from this version on. On older ClawTweaks the whole maintenance column is locked
        /// with an update prompt — the ops would otherwise silently time out waiting for a reply the old
        /// helper never sends. 0.1.8+ is naturally covered by the >= comparison.</summary>
        private static readonly Version MaintenanceMinVersion = new Version(0, 1, 7, 153);

        /// <summary>True only when a new-enough ClawTweaks is installed for the maintenance tools to work.</summary>
        private bool MaintenanceUnlocked =>
            _installedVersionChecked && _installedVersion != null && _installedVersion >= MaintenanceMinVersion;

        // ── Entry ──────────────────────────────────────────────────────────────────────────────
        private void OpenMaintenance()
        {
            _view = View.Maintenance;
            _maintPage = MaintPage.Menu;
            _maintMenuIndex = 0;
            RenderMaintenance();
            RefreshActionBar();

            // Warm the helper connection in the background so an op doesn't pay the connect cost on first
            // press — purely opportunistic; each op also connects on demand.
            _ = Task.Run(async () =>
            {
                try { await _maintenance.EnsureConnectedAsync().ConfigureAwait(false); } catch { }
            });
        }

        private void LeaveMaintenance() => GoHome();

        // ── Render ─────────────────────────────────────────────────────────────────────────────
        private void RenderMaintenance()
        {
            ContentHost.Children.Clear();
            _maintSelectedCard = null;

            switch (_maintPage)
            {
                case MaintPage.Menu: RenderMaintenanceMenu(); break;
                case MaintPage.ResetConfirm: RenderResetConfirm(); break;
                case MaintPage.BackupConfirm: RenderBackupConfirm(); break;
                case MaintPage.RestoreList: RenderRestoreList(); break;
                case MaintPage.RestoreConfirm: RenderRestoreConfirm(); break;
                case MaintPage.Busy: RenderMaintenanceBusy(); break;
                case MaintPage.Result: RenderMaintenanceResult(); break;
            }

            _maintSelectedCard?.BringIntoView();
            RefreshActionBar();
        }

        private void RenderMaintenanceMenu()
        {
            ContentHost.Children.Add(UiHelpers.Title("Reset · Backup · Restore"));
            ContentHost.Children.Add(UiHelpers.Body(
                "Manage your ClawTweaks settings — back them up, restore a previous backup, or reset everything to a clean state."));

            bool unlocked = MaintenanceUnlocked;
            if (!unlocked)
            {
                // Locked on older ClawTweaks: name the required version and point at the update path.
                string have = !_installedVersionChecked
                    ? "checking the installed version…"
                    : (_installedVersion != null ? $"you have {_installedVersion}." : "ClawTweaks is not installed.");
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Warning,
                    $"Requires ClawTweaks {MaintenanceMinVersion} or newer",
                    $"Reset, Backup and Restore need ClawTweaks {MaintenanceMinVersion} (or 0.1.8+) — {have} " +
                    "Update ClawTweaks from \"Update & Release\" first."));
            }

            var items = new (string title, string detail)[]
            {
                ("CTW Full Reset", "Wipe every ClawTweaks setting back to a clean state (all profiles, fan curves, TDP, controller). This cannot be undone — take a backup first if unsure."),
                ("Create Backup", "Save all your profiles and settings into a single ZIP you can restore later."),
                ("Restore Backup", "Bring back a previous backup. A safety copy of the current state is taken automatically first."),
            };

            // Two columns — the three cards are far too wide at full window width in one column. 3 items
            // fill row0: [Reset][Backup], row1: [Restore]. D-pad nav is 2D (see MoveMaintenanceSelection).
            var grid = new UniformGrid { Columns = 2 };
            ContentHost.Children.Add(grid);

            for (int i = 0; i < items.Length; i++)
            {
                int index = i;
                bool selected = index == _maintMenuIndex;
                var card = BuildMaintCard(items[i].title, items[i].detail, selected, () => ActivateMaintMenu(index));
                // Gap between the two columns (right margin on the left column only) on top of the card's
                // own bottom margin, so the grid cells don't touch.
                card.Margin = new Thickness(0, 0, index % 2 == 0 ? 12 : 0, 12);
                // Dim the cards when locked so it's clear they're not usable yet (the banner above says why).
                if (!unlocked) card.Opacity = 0.45;
                if (selected) _maintSelectedCard = card;
                grid.Children.Add(card);
            }
        }

        private void RenderResetConfirm()
        {
            ContentHost.Children.Add(UiHelpers.Title("CTW Full Reset"));
            ContentHost.Children.Add(UiHelpers.Body(
                "This resets ALL ClawTweaks settings to a clean state:"));

            var bullets = new StackPanel { Margin = new Thickness(4, 4, 0, 8) };
            foreach (var line in new[]
            {
                "• Global and per-game profiles (TDP, fan curves, controller, gyro)",
                "• Helper settings (global TDP, fan curve, controller emulation)",
                "• The widget's stored profile containers",
            })
                bullets.Children.Add(new TextBlock { Text = line, FontSize = 15, Foreground = UiHelpers.Text, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            ContentHost.Children.Add(bullets);

            ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Info, "A safety backup is saved first",
                "An automatic backup of your current settings is created before the reset, so you can restore it later from Restore Backup. The Game Bar will be closed — reopen it (Win+G) afterwards."));
        }

        private void RenderBackupConfirm()
        {
            ContentHost.Children.Add(UiHelpers.Title("Create Backup"));
            ContentHost.Children.Add(UiHelpers.Body(
                "Saves all your profiles and settings into a single ZIP. The Game Bar is briefly closed so the widget's data can be copied — reopen it (Win+G) afterwards."));

            var target = Path.Combine(MaintenanceRunner.BackupsFolder, MaintenanceRunner.SuggestedBackupFileName());
            ContentHost.Children.Add(new Border
            {
                Background = UiHelpers.Card, CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 6, 0, 0),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Will be saved to", FontSize = 13, Foreground = UiHelpers.Subtle },
                        new TextBlock { Text = target, FontSize = 15, Foreground = UiHelpers.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) },
                    },
                },
            });
        }

        private void RenderRestoreList()
        {
            ContentHost.Children.Add(UiHelpers.Title("Restore Backup"));
            ContentHost.Children.Add(UiHelpers.Body(
                "Pick a backup to restore. A safety copy of the current state is taken automatically first, then the helper restarts to load the restored settings."));

            if (_maintBackups.Count == 0)
            {
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Info, "No backups found",
                    $"Backups live in {MaintenanceRunner.BackupsFolder}. Create one first."));
                return;
            }

            if (_maintRestoreIndex < 0) _maintRestoreIndex = 0;
            if (_maintRestoreIndex >= _maintBackups.Count) _maintRestoreIndex = _maintBackups.Count - 1;

            for (int i = 0; i < _maintBackups.Count; i++)
            {
                int index = i;
                var b = _maintBackups[i];
                bool selected = index == _maintRestoreIndex;

                var stack = new StackPanel();
                string when = b.CreatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? b.FileTime.ToString("yyyy-MM-dd HH:mm");
                stack.Children.Add(new TextBlock
                {
                    Text = (b.IsPreRestore ? "Auto pre-restore — " : "") + when,
                    FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Text, TextWrapping = TextWrapping.Wrap,
                });

                string sub = b.ManifestValid
                    ? $"ClawTweaks {b.AppVersion ?? "?"} · {b.DeviceModel ?? "?"} · {b.StoreCount} stores · {FormatSize(b.SizeBytes)}"
                    : $"⚠ No valid backup manifest · {FormatSize(b.SizeBytes)}";
                stack.Children.Add(new TextBlock { Text = Core.Loc.T(sub), FontSize = 13, Foreground = UiHelpers.Subtle, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
                stack.Children.Add(new TextBlock { Text = b.FileName, FontSize = 12, Foreground = UiHelpers.Subtle, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });

                var card = WrapSelectableCard(stack, selected, () => { _maintRestoreIndex = index; SelectBackupForRestore(index); });
                if (selected) _maintSelectedCard = card;
                ContentHost.Children.Add(card);
            }
        }

        private void RenderRestoreConfirm()
        {
            var b = _maintSelectedBackup;
            ContentHost.Children.Add(UiHelpers.Title("Restore this backup?"));
            if (b == null) { ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Error, "No backup selected", "")); return; }

            string when = b.CreatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? b.FileTime.ToString("yyyy-MM-dd HH:mm");
            ContentHost.Children.Add(UiHelpers.Body($"{when} — ClawTweaks {b.AppVersion ?? "?"} — {b.DeviceModel ?? "?"}"));

            foreach (var w in BuildRestoreWarnings(b))
                ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, w.Item1, w.Item2));

            ContentHost.Children.Add(UiHelpers.StatusRow(StatusKind.Info, "What happens",
                "A safety copy of your current settings is saved first, the Game Bar closes, the backup is written back, and the helper restarts. Reopen the Game Bar (Win+G) when it's done."));
        }

        private void RenderMaintenanceBusy()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
            row.Children.Add(new ContentControl
            {
                Width = 26, Height = 26, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
                Content = UiHelpers.Badge(StatusKind.Working, 26),
            });
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(_maintBusyText) ? "Working…" : _maintBusyText,
                FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            });
            ContentHost.Children.Add(row);
        }

        private void RenderMaintenanceResult()
        {
            ContentHost.Children.Add(UiHelpers.Title(_maintResultTitle));
            ContentHost.Children.Add(UiHelpers.StatusRow(_maintResultKind, _maintResultTitle, _maintResultDetail));
        }

        // ── Card builders ───────────────────────────────────────────────────────────────────────
        private Border BuildMaintCard(string title, string detail, bool selected, Action onClick)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = Core.Loc.T(title), FontSize = 18, FontWeight = FontWeights.Bold, Foreground = UiHelpers.Text });
            stack.Children.Add(new TextBlock { Text = Core.Loc.T(detail), FontSize = 14, Foreground = UiHelpers.Subtle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            return WrapSelectableCard(stack, selected, onClick);
        }

        private Border WrapSelectableCard(UIElement child, bool selected, Action onClick)
        {
            var cardPad = new Thickness(16, 14, 16, 14);
            var border = new Border
            {
                Background = UiHelpers.Card, CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 10),
                BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(selected ? 2 : 0),
                Padding = selected ? Deflate(cardPad, 2) : cardPad, // see Deflate: keeps the card from resizing
                Cursor = Cursors.Hand,
                Child = child,
            };
            border.MouseLeftButtonUp += (_, __) => onClick?.Invoke();
            return border;
        }

        // ── Navigation ──────────────────────────────────────────────────────────────────────────
        private void MoveMaintenanceSelection(PadButton dir)
        {
            if (_maintPage == MaintPage.Menu)
            {
                // 2-column grid nav: Left/Right step one card, Up/Down jump a row (±2). Items:
                // 0=(r0,c0) 1=(r0,c1) 2=(r1,c0). Clamped to the valid range.
                int idx = _maintMenuIndex;
                if (dir == PadButton.Left) idx -= 1;
                else if (dir == PadButton.Right) idx += 1;
                else if (dir == PadButton.Up) idx -= 2;
                else if (dir == PadButton.Down) idx += 2;
                if (idx < 0) idx = 0;
                if (idx > MaintMenuCount - 1) idx = MaintMenuCount - 1;
                if (idx != _maintMenuIndex) { _maintMenuIndex = idx; RenderMaintenance(); }
            }
            else if (_maintPage == MaintPage.RestoreList && _maintBackups.Count > 0)
            {
                if (dir == PadButton.Up && _maintRestoreIndex > 0) { _maintRestoreIndex--; RenderMaintenance(); }
                else if (dir == PadButton.Down && _maintRestoreIndex < _maintBackups.Count - 1) { _maintRestoreIndex++; RenderMaintenance(); }
            }
        }

        // ── Footer action bar ───────────────────────────────────────────────────────────────────
        private void RefreshMaintenanceActionBar()
        {
            switch (_maintPage)
            {
                case MaintPage.Menu:
                    AddAction(PadButton.A, "Open", MaintenanceUnlocked, () => ActivateMaintMenu(_maintMenuIndex));
                    AddAction(PadButton.B, "Back", true, LeaveMaintenance);
                    break;

                case MaintPage.ResetConfirm:
                    AddAction(PadButton.A, "Yes, reset everything", true, () => _ = DoResetAsync());
                    AddAction(PadButton.B, "Cancel", true, BackToMaintMenu);
                    AddScrollHint();
                    break;

                case MaintPage.BackupConfirm:
                    AddAction(PadButton.A, "Create backup", true, () => _ = DoBackupAsync());
                    AddAction(PadButton.B, "Cancel", true, BackToMaintMenu);
                    break;

                case MaintPage.RestoreList:
                    AddAction(PadButton.A, "Select", _maintBackups.Count > 0, () => SelectBackupForRestore(_maintRestoreIndex));
                    AddAction(PadButton.Y, "Refresh", true, OpenRestoreList);
                    AddAction(PadButton.B, "Back", true, BackToMaintMenu);
                    AddScrollHint();
                    break;

                case MaintPage.RestoreConfirm:
                    AddAction(PadButton.A, "Yes, restore", _maintSelectedBackup != null, () => _ = DoRestoreAsync());
                    AddAction(PadButton.B, "Cancel", true, () => { _maintPage = MaintPage.RestoreList; RenderMaintenance(); });
                    AddScrollHint();
                    break;

                case MaintPage.Busy:
                    // Nothing actionable mid-operation.
                    break;

                case MaintPage.Result:
                    AddAction(PadButton.B, "Back", true, BackToMaintMenu);
                    AddScrollHint();
                    break;
            }
        }

        private void BackToMaintMenu()
        {
            _maintPage = MaintPage.Menu;
            RenderMaintenance();
        }

        // ── Flow: activation ────────────────────────────────────────────────────────────────────
        private void ActivateMaintMenu(int index)
        {
            _maintMenuIndex = index;
            // Locked on older ClawTweaks — the banner in RenderMaintenanceMenu already explains why; a
            // press just re-renders (keeps the cursor responsive) instead of entering a dead sub-screen.
            if (!MaintenanceUnlocked) { RenderMaintenance(); return; }
            switch (index)
            {
                case 0: _maintPage = MaintPage.ResetConfirm; RenderMaintenance(); break;
                case 1: _maintPage = MaintPage.BackupConfirm; RenderMaintenance(); break;
                case 2: OpenRestoreList(); break;
            }
        }

        private void OpenRestoreList()
        {
            _maintPage = MaintPage.RestoreList;
            _maintRestoreIndex = 0;
            _maintBackups = new List<MaintenanceRunner.BackupInfo>(); // show empty while loading
            RenderMaintenance();

            _ = Task.Run(() => MaintenanceRunner.ListLocalBackups())
                .ContinueWith(t =>
                {
                    _maintBackups = t.Result ?? new List<MaintenanceRunner.BackupInfo>();
                    if (_maintPage == MaintPage.RestoreList) RenderMaintenance();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void SelectBackupForRestore(int index)
        {
            if (index < 0 || index >= _maintBackups.Count) return;
            _maintSelectedBackup = _maintBackups[index];
            _maintPage = MaintPage.RestoreConfirm;
            RenderMaintenance();
        }

        // ── Flow: operations ────────────────────────────────────────────────────────────────────
        private async Task DoResetAsync()
        {
            EnterBusy("Resetting all ClawTweaks settings…");
            var r = await _maintenance.ResetAsync();
            if (r.Ok)
            {
                string pre = string.IsNullOrEmpty(r.Path) ? "" : $"\nSafety copy of the previous state: {r.Path}";
                ShowResult(StatusKind.Ok, "Reset complete",
                    $"All ClawTweaks settings were reset to a clean state. Reopen the Game Bar (Win+G) to continue.{pre}");
            }
            else
                ShowResult(StatusKind.Error, "Reset failed", r.Error ?? "Unknown error.");
        }

        private async Task DoBackupAsync()
        {
            var target = Path.Combine(MaintenanceRunner.BackupsFolder, MaintenanceRunner.SuggestedBackupFileName());
            EnterBusy("Creating backup…");
            var r = await _maintenance.BackupAsync(target);
            if (r.Ok)
                ShowResult(StatusKind.Ok, "Backup created",
                    $"Saved {r.Count} stores to:\n{r.Path ?? target}");
            else
                ShowResult(StatusKind.Error, "Backup failed", r.Error ?? "Unknown error.");
        }

        private async Task DoRestoreAsync()
        {
            var b = _maintSelectedBackup;
            if (b == null) { BackToMaintMenu(); return; }

            EnterBusy("Restoring backup — the helper will restart when done…");
            var r = await _maintenance.RestoreAsync(b.FilePath);
            if (r.Ok)
            {
                string pre = string.IsNullOrEmpty(r.Path) ? "" : $"\nSafety copy of the previous state: {r.Path}";
                ShowResult(StatusKind.Ok, "Restore complete",
                    $"Restored {r.Count} files. The helper is restarting — reopen the Game Bar (Win+G) to continue.{pre}");
            }
            else if (r.TimedOut)
            {
                // The helper restarts right after replying, so a race can swallow the ack even on success.
                ShowResult(StatusKind.Warning, "Restore likely completed",
                    "The helper restarted before confirming. Reopen the Game Bar (Win+G) and check your profiles — if anything looks off, restore the auto pre-restore backup.");
            }
            else
                ShowResult(StatusKind.Error, "Restore failed", r.Error ?? "Unknown error.");
        }

        private void EnterBusy(string text)
        {
            _maintBusyText = text;
            _maintPage = MaintPage.Busy;
            RenderMaintenance();
        }

        private void ShowResult(StatusKind kind, string title, string detail)
        {
            _maintResultKind = kind;
            _maintResultTitle = title;
            _maintResultDetail = detail;
            _maintPage = MaintPage.Result;
            RenderMaintenance();
        }

        // ── Restore compatibility warnings (warn, never block — Doku/PLAN_Backup_Restore.md §6) ──
        private List<Tuple<string, string>> BuildRestoreWarnings(MaintenanceRunner.BackupInfo b)
        {
            var warnings = new List<Tuple<string, string>>();
            if (b == null) return warnings;

            if (!b.ManifestValid)
            {
                warnings.Add(Tuple.Create("Not a recognized backup",
                    "This ZIP has no valid ClawTweaks manifest — the helper will refuse it if it isn't a real backup."));
                return warnings;
            }

            // Device mismatch: the backup's device token doesn't match the detected device.
            var localToken = LocalDeviceToken();
            if (!string.IsNullOrEmpty(localToken) && !string.IsNullOrEmpty(b.DeviceModel)
                && b.DeviceModel.IndexOf(localToken, StringComparison.OrdinalIgnoreCase) < 0)
            {
                warnings.Add(Tuple.Create("Different device",
                    $"This backup is from '{b.DeviceModel}', but this device looks like {localToken}. Device-specific values (TDP limits, fan scale) will be restored as-is."));
            }

            // App-version mismatch (best-effort — only when the installed version is known).
            if (_installedVersion != null && !string.IsNullOrEmpty(b.AppVersion)
                && Version.TryParse(b.AppVersion, out var backupVer) && backupVer != _installedVersion)
            {
                warnings.Add(Tuple.Create("Different ClawTweaks version",
                    $"Backup is from {backupVer}; you have {_installedVersion} installed. Restoring across versions usually works, but isn't guaranteed."));
            }

            return warnings;
        }

        /// <summary>A short token for the locally detected device, matched loosely against the backup's
        /// (helper-written) device model string. Empty when unknown — no warning then.</summary>
        private string LocalDeviceToken()
        {
            switch (_deviceModel)
            {
                case DeviceDetect.Model.A2VM: return "A2VM";
                case DeviceDetect.Model.Ex: return "EX";
                default: return "";
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
            return $"{bytes} B";
        }
    }
}
