using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The FAQ: the questions this project answers over and over, in the app rather than in a chat
    /// log. One line per statement, collapsed until asked for — a wall of prose on a handheld is a
    /// wall nobody reads, and the point of the collapse is that the QUESTIONS are the index.
    ///
    /// Two rules for anything added here, both of which this list is built to keep:
    ///
    /// **Only what the code actually does.** Every answer below is checkable against this repo or
    /// the helper: the virtual controller really does roll itself back, the task really is
    /// version-free so updates cost no prompt, Center really never elevates. A FAQ that drifts from
    /// the software is worse than no FAQ, because it is believed.
    ///
    /// **Say where to go, not how it works.** These answer "what do I do"; the reasoning belongs in
    /// the guidelines, not on a 7-inch screen.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private int _faqSelectedIndex;
        private readonly HashSet<int> _faqExpanded = new HashSet<int>();
        private FrameworkElement _faqSelectedCard;

        private sealed class FaqEntry
        {
            public string Question;
            public string[] Answer;
            public FaqEntry(string q, params string[] a) { Question = q; Answer = a; }
        }

        private static readonly FaqEntry[] FaqEntries =
        {
            new FaqEntry("What is the virtual controller for?",
                "It replaces the Claw's own gamepad with one ClawTweaks drives.",
                "Button remaps, gyro and per-game controller profiles need it.",
                "Without it the gamepad still works, but those settings do nothing.",
                "Turn it on in Onboarding. It switches itself back if no pad appears."),

            new FaqEntry("Do I have to switch MSI Center M off?",
                "Yes, if ClawTweaks should own the controller, the fan and the LEDs.",
                "Both write the same hardware, and the last one to write wins.",
                "Onboarding switches it off. Uninstall ClawTweaks switches it back on."),

            new FaqEntry("How do I uninstall everything?",
                "Open Uninstall ClawTweaks on the start screen and work down the list.",
                "Step 1 puts the charge limit, the fan and the controller back.",
                "Do that before removing the app: afterwards nothing can undo them.",
                "The last step removes Center and always works."),

            new FaqEntry("Why does ClawTweaks ask for admin rights?",
                "Once, at the first install, to register its background task.",
                "Updates cost no prompt — the task does not carry a version number.",
                "The signing certificate uses Windows' own prompt, once per device.",
                "Center itself never asks for admin rights."),

            new FaqEntry("What does the background helper do?",
                "It writes TDP, fan curve, LEDs, controller and the on-screen display.",
                "The widget shows what the helper does; it changes nothing by itself.",
                "It starts with Windows through its scheduled task.",
                "Open the Game Bar once if it is not running."),

            new FaqEntry("Where are my settings, and how do I back them up?",
                "Open Reset · Backup · Restore on the start screen.",
                "Create Backup writes one ZIP to Documents\\ClawTweaks\\Backups.",
                "Restore Backup takes a safety copy before it writes.",
                "A full reset backs up your settings first as well."),

            new FaqEntry("Do I need the Game Bar for the game library?",
                "No. The library runs on its own, without the Game Bar.",
                "Only the ClawTweaks widget lives in the Game Bar.",
                "Set the library as the screen Center opens on in Library Settings."),

            new FaqEntry("The MSI button does not open the widget.",
                "Open Onboarding and enter the slot ClawTweaks sits at in the Game Bar.",
                "The helper hops to that slot; it cannot read the position itself.",
                "Raise \"Wait before jumping\" in the widget if a game is busy."),
        };

        // ── Entry ──────────────────────────────────────────────────────────────────────────────
        private void OpenFaq()
        {
            _view = View.Faq;
            _faqSelectedIndex = 0;
            RenderFaq();
            RefreshActionBar();
        }

        // ── Render ─────────────────────────────────────────────────────────────────────────────
        private void RenderFaq()
        {
            ContentHost.Children.Clear();
            _faqSelectedCard = null;

            ContentHost.Children.Add(UiHelpers.Title("FAQ"));
            ContentHost.Children.Add(UiHelpers.Body("Press Ⓐ on a question to open it."));

            for (int i = 0; i < FaqEntries.Length; i++)
                ContentHost.Children.Add(BuildFaqCard(i));

            _faqSelectedCard?.BringIntoView();
            RefreshActionBar();
        }

        private Border BuildFaqCard(int index)
        {
            var entry = FaqEntries[index];
            bool selected = index == _faqSelectedIndex;
            bool open = _faqExpanded.Contains(index);

            var stack = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var q = new TextBlock
            {
                Text = Core.Loc.T(entry.Question),
                FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(q, 0);
            head.Children.Add(q);

            // The chevron is the affordance and it is NOT translated: it is the same mark the
            // expandable cards elsewhere in Center use, and a glyph has no language.
            var chevron = new TextBlock
            {
                Text = open ? "⌃" : "⌄",
                FontSize = 16, FontWeight = FontWeights.Bold,
                Foreground = selected ? UiHelpers.Accent : UiHelpers.Subtle,
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(chevron, 1);
            head.Children.Add(chevron);
            stack.Children.Add(head);

            if (open)
            {
                var body = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                foreach (var line in entry.Answer)
                    body.Children.Add(new TextBlock
                    {
                        Text = Core.Loc.T(line),
                        FontSize = 14, Foreground = UiHelpers.Subtle,
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
                    });
                stack.Children.Add(body);
            }

            var pad = new Thickness(16, 13, 16, 13);
            var card = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(selected ? 2 : 0),
                Padding = selected ? Deflate(pad, 2) : pad,
                Cursor = Cursors.Hand,
                Child = stack,
            };
            card.MouseLeftButtonUp += (_, __) => { _faqSelectedIndex = index; ToggleFaqEntry(index); };
            if (selected) _faqSelectedCard = card;
            return card;
        }

        // ── Navigation ─────────────────────────────────────────────────────────────────────────
        private void MoveFaqSelection(PadButton dir)
        {
            int next = _faqSelectedIndex;
            if (dir == PadButton.Up) next--;
            else if (dir == PadButton.Down) next++;
            else return;

            if (next < 0) next = 0;
            if (next > FaqEntries.Length - 1) next = FaqEntries.Length - 1;
            if (next == _faqSelectedIndex) return;

            _faqSelectedIndex = next;
            RenderFaq();
        }

        private void ToggleFaqEntry(int index)
        {
            if (index < 0 || index >= FaqEntries.Length) return;
            if (!_faqExpanded.Remove(index)) _faqExpanded.Add(index);
            RenderFaq();
        }

        private void RefreshFaqActionBar()
        {
            bool open = _faqExpanded.Contains(_faqSelectedIndex);
            AddAction(PadButton.A, open ? "Close" : "Open", true, () => ToggleFaqEntry(_faqSelectedIndex));
            // Opening one question at a time and then closing eight of them by hand is the version of
            // this screen that gets abandoned halfway down.
            AddAction(PadButton.Y, _faqExpanded.Count == FaqEntries.Length ? "Close all" : "Open all", true, ToggleAllFaq);
            AddAction(PadButton.B, "Back", true, GoHome);
            AddScrollHint();
        }

        private void ToggleAllFaq()
        {
            if (_faqExpanded.Count == FaqEntries.Length) _faqExpanded.Clear();
            else for (int i = 0; i < FaqEntries.Length; i++) _faqExpanded.Add(i);
            RenderFaq();
        }
    }
}
