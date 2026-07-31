using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawTweaksSetup.Core;
using ClawTweaksSetup.Navigation;
using ClawTweaksSetup.Ui;

namespace ClawTweaksSetup.Phases
{
    /// <summary>
    /// Phase 2 — required tools. Per-tool rows describe what each tool does and its live status.
    /// Ⓐ opens the vendor download pages for whatever is missing; Ⓨ re-checks. Content scrolls with
    /// the D-Pad / left stick.
    ///
    /// Center does NOT install these — the user downloads and runs the vendor's own installer, which
    /// carries the vendor's signature and raises its own elevation prompt. See PrerequisiteGuide for
    /// why the winget and download-then-runas paths that used to live here were removed.
    /// </summary>
    public sealed class ToolsPhase : PhaseBase
    {
        private const string DescHidHide = "Hides the physical controller so games only see the virtual one — prevents double input.";
        private const string DescUsbip = "Kernel driver behind the VIIPER virtual controller. Mandatory for virtual mode.";
        private const string DescRtss = "RivaTuner Statistics Server — powers the FPS limiter and the on-screen overlay.";
        private const string DescPawnIO = "Kernel driver used for TDP control.";

        private readonly StackPanel _root = new StackPanel();
        private readonly List<PhaseAction> _actions;
        private bool _busy;
        private bool _anyMissing;
        private bool _rebootNeeded;

        private readonly ProgressBar _spinner = new ProgressBar
        {
            Height = 6, IsIndeterminate = true,
            Foreground = (Brush)Application.Current.Resources["AccentBrush"],
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x38)),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 10),
            Visibility = Visibility.Collapsed,
        };
        private readonly TextBlock _log = new TextBlock
        {
            FontSize = 15, Foreground = UiHelpers.Subtle,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0),
        };

        public override string Title => "Tools";
        public override IReadOnlyList<PhaseAction> Actions => _actions;

        public ToolsPhase()
        {
            Content = _root;
            _actions = new List<PhaseAction>
            {
                new PhaseAction(PadButton.A, "Open download pages", OpenMissingPages, () => !_busy && _anyMissing),
                new PhaseAction(PadButton.Y, "Re-check", () => _ = RefreshAsync(), () => !_busy),
            };
        }

        public override void OnEnter() => _ = RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            State = PhaseState.Working;
            RaiseActionsChanged();

            var hidhide = await Task.Run(() => ToolDetect.HidHide());
            var usbip = await Task.Run(() => ToolDetect.Usbip());
            var rtss = await Task.Run(() => ToolDetect.Rtss());
            var pawnio = await Task.Run(() => ToolDetect.PawnIO());

            _anyMissing = !hidhide.Installed || !usbip.Installed || !rtss.Installed || !pawnio.Installed;
            Render(hidhide, usbip, rtss, pawnio);

            State = _anyMissing ? PhaseState.Action : PhaseState.Ok;
            _busy = false;
            RaiseActionsChanged();
        }

        private void Render(ToolStatus hidhide, ToolStatus usbip, ToolStatus rtss, ToolStatus pawnio)
        {
            _root.Children.Clear();
            _root.Children.Add(UiHelpers.Title("Required tools"));
            _root.Children.Add(UiHelpers.Caption(
                $"Last checked {DateTime.Now:HH:mm:ss}   ·   Scroll with the D-Pad or left stick"));

            // Prominent call-to-action at the top when something is missing.
            if (_anyMissing && !_busy)
                _root.Children.Add(UiHelpers.ActionCallout(
                    "Press Ⓐ to open the download pages for the missing tools, install them, then press Ⓨ to re-check."));

            if (_rebootNeeded)
                _root.Children.Add(UiHelpers.StatusRow(StatusKind.Error, "REBOOT REQUIRED",
                    "HidHide and usbip install kernel drivers. Reboot the device once after installing them, then run this setup again."));

            _root.Children.Add(ToolRow(hidhide, "HidHide", DescHidHide));
            _root.Children.Add(ToolRow(usbip, "usbip  (required for virtual controller)", DescUsbip));
            _root.Children.Add(ToolRow(rtss, "RTSS", DescRtss));
            _root.Children.Add(ToolRow(pawnio, "PawnIO", DescPawnIO));

            _root.Children.Add(_spinner);
            if (_log.Text.Length > 0) _root.Children.Add(_log);
        }

        /// <summary>
        /// Opens the vendor download page for each missing tool, and says which ones need a reboot once
        /// installed. Center does not install them — see PrerequisiteGuide.
        /// </summary>
        private void OpenMissingPages()
        {
            var missing = new[] { ToolDetect.HidHide(), ToolDetect.Usbip(), ToolDetect.Rtss(), ToolDetect.PawnIO() }
                .Where(t => !t.Installed)
                .ToList();
            if (missing.Count == 0) { _ = RefreshAsync(); return; }

            var lines = new List<string>();
            foreach (var tool in missing)
            {
                var info = PrerequisiteGuide.For(tool.Name);
                if (info == null) continue;
                PrerequisiteGuide.OpenPage(info.PageUrl);
                lines.Add($"{info.Name}: {info.WhatToGet}");
                if (info.NeedsReboot) _rebootNeeded = true;
            }
            lines.Add("Install them, then press Ⓨ to re-check.");
            _log.Text = string.Join("\n", lines);

            Render(ToolDetect.HidHide(), ToolDetect.Usbip(), ToolDetect.Rtss(), ToolDetect.PawnIO());
        }

        private static Border ToolRow(ToolStatus s, string label, string desc)
        {
            var kind = s.Installed ? StatusKind.Ok : StatusKind.Warning;
            // Show the Detail on the NOT-installed side too. It used to be dropped, which turned the
            // two cases that need explaining most — "files there but the driver never registered" and
            // "usbip is installed but too new" — into a bare "Not installed" on a machine where the
            // tool is plainly present. Missing() says "not found", which reads fine on its own.
            var status = s.Installed
                ? "Installed — " + s.Detail
                : string.IsNullOrWhiteSpace(s.Detail) || s.Detail == "not found"
                    ? "Not installed"
                    : s.Detail;
            return UiHelpers.ToolRow(kind, label, desc, status);
        }
    }
}
