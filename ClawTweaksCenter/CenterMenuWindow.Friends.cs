using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Steam friends in the library: a count at the top right, and behind RT one screen with the
    /// friend list on the left and Steam's friend activity feed on the right. D-pad left/right moves
    /// between the two columns, A opens a chat with the person on the selected row.
    ///
    /// The data comes from the running Steam client - see <see cref="Library.SteamFriends"/>, which
    /// also explains why that read never shows the user as "in game" - and from Steam's cached
    /// activity feed on disk (<see cref="Library.SteamFriendActivity"/>).
    ///
    /// -- Where it is offered (user, 2026-09-11) --------------------------------------------------
    /// Every store tab, Recent and Favorites. NOT the ROM tab, where LT/RT already move between
    /// systems, and not Misc, which is the user's own apps rather than a store. There the corner is
    /// empty and the triggers keep their old meaning.
    /// </summary>
    public partial class CenterMenuWindow
    {
        /// <summary>How often the corner count is refreshed on the shelf. Each refresh starts a short
        /// reader process, so this is a ceiling set by cost: a friend coming online a few seconds late
        /// on a counter is invisible.</summary>
        private static readonly TimeSpan FriendsShelfInterval = TimeSpan.FromSeconds(30);

        /// <summary>While the screen is open somebody is looking at it, so it follows faster.</summary>
        private static readonly TimeSpan FriendsListInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// While Steam is running but has no friend list yet.
        ///
        /// Right after a boot Steam answers with an EMPTY list for a while - signed in, roster not
        /// loaded. Shown as "0 of 0 online" that looked like a broken count (device, 2026-09-11). It is
        /// a spinner now, and it asks again sooner.
        /// </summary>
        private static readonly TimeSpan FriendsLoadingInterval = TimeSpan.FromSeconds(5);

        /// <summary>How many empty answers in a row before the corner stops saying "loading" - about a
        /// minute. A signed-out Steam, or an account with no friends, would otherwise spin forever.</summary>
        private const int FriendsLoadingMaxPolls = 12;

        private const double FriendAvatarSize = 44;
        private const double FeedAchievementIconSize = 36;

        /// <summary>The feed is 266 entries on the dev machine going back two months. The newest few
        /// dozen are what anybody scrolls; the rest is a long list to build on every refresh.</summary>
        private const int FeedMaxEntries = 60;

        private const int FriendsColumnList = 0;
        private const int FriendsColumnFeed = 1;

        private bool _friendsOpen;
        /// <summary>Which column the D-pad is in.</summary>
        private int _friendsColumn = FriendsColumnList;
        private SteamFriendsSnapshot _friends;
        private bool _friendsInFlight;
        private int _friendsEmptyPolls;
        private DispatcherTimer _friendsTimer;

        private readonly List<Border> _friendRows = new List<Border>();
        private int _friendIndex;
        /// <summary>The selection follows the PERSON across a refresh, not the row number - the list
        /// re-sorts as people come and go, and a cursor that stays on row 3 jumps to someone else.</summary>
        private ulong _friendSelectedId;

        /// <summary>The feed entries on screen, index-aligned with <see cref="_feedRows"/>.</summary>
        private readonly List<FriendActivity> _feedShown = new List<FriendActivity>();
        private readonly List<Border> _feedRows = new List<Border>();
        private int _feedIndex;

        /// <summary>
        /// The corner chip belongs on this tab.
        ///
        /// Out of All and Not Installed as well since 2026-09-12 (user): those two are the long
        /// shelves, the letter bar lives in their corner, and friends are what somebody looks for in
        /// Recent and the store tabs. Two things cannot own one corner.
        /// </summary>
        private bool LibraryTabOffersFriends =>
            _libraryGroup != LibraryGroup.Roms && _libraryGroup != LibraryGroup.Misc &&
            _libraryGroup != LibraryGroup.All && _libraryGroup != LibraryGroup.NotInstalled;

        /// <summary>There is a list to show. An empty answer is NOT readable: see FriendsLoadingInterval.</summary>
        private bool FriendsReadable => _friends != null && _friends.Available && _friends.Friends.Count > 0;

        /// <summary>Steam is running, the list has not arrived yet, and it has not been long.</summary>
        private bool FriendsLoading =>
            !FriendsReadable && _friendsEmptyPolls < FriendsLoadingMaxPolls &&
            (_friends == null || _friends.SteamRunning);

        #region Polling
        private void StartFriendsPolling()
        {
            if (_friendsTimer == null)
            {
                _friendsTimer = new DispatcherTimer();
                _friendsTimer.Tick += (_, __) => RequestFriends();
            }
            _friendsEmptyPolls = 0;
            ApplyFriendsInterval();
            _friendsTimer.Start();
            RequestFriends();
        }

        private void StopFriendsPolling()
        {
            _friendsTimer?.Stop();
        }

        private void ApplyFriendsInterval()
        {
            if (_friendsTimer == null) return;
            TimeSpan wanted = FriendsLoading ? FriendsLoadingInterval
                            : _friendsOpen ? FriendsListInterval
                            : FriendsShelfInterval;
            if (_friendsTimer.Interval != wanted) _friendsTimer.Interval = wanted;
        }

        private void RequestFriends()
        {
            // Center spends most of its life hidden in the background. A reader process every half
            // minute for a counter nobody can see is load on a battery for nothing.
            if (_view != View.Library || !IsVisible || WindowState == WindowState.Minimized) return;
            if (!_friendsOpen && !LibraryTabOffersFriends) return;
            if (_friendsInFlight) return;
            _friendsInFlight = true;
            _ = RequestFriendsAsync();
        }

        private async Task RequestFriendsAsync()
        {
            try
            {
                var snapshot = await SteamFriends.ReadAsync().ConfigureAwait(true);
                string cornerBefore = CornerState();
                _friends = snapshot;

                if (FriendsReadable || !snapshot.SteamRunning) _friendsEmptyPolls = 0;
                else _friendsEmptyPolls++;

                ApplyFriendsInterval();
                if (_view != View.Library) return;

                if (_friendsOpen)
                {
                    RenderLibrary();
                    RefreshActionBar();
                }
                else if (CornerState() != cornerBefore)
                {
                    // Only when the corner would actually read differently: the strip rebuilds its
                    // chips, and doing that every half minute for an unchanged number is churn.
                    RefreshTabStrip();
                    RefreshActionBar();
                }
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[SteamFriends] refresh failed: " + ex.Message);
            }
            finally
            {
                _friendsInFlight = false;
            }
        }

        private string CornerState() =>
            FriendsReadable ? _friends.OnlineCount + "/" + _friends.Friends.Count
            : FriendsLoading ? "loading" : "none";
        #endregion

        #region The corner
        /// <summary>
        /// What sits at the right end of the tab strip: the friends count on RT, or a spinner while
        /// Steam has not delivered the list yet.
        ///
        /// Nothing at all when there is nothing to read: a chip whose button does nothing is worse
        /// than no chip. A Big Picture chip on LT stood beside it for one build and was removed on the
        /// user's call.
        /// </summary>
        private UIElement BuildLibraryCorner()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // THE LETTER BAR WINS THE CORNER. The two tabs it works in are All and Not Installed, and
            // the friends chip is not offered on either - they are the shelves nobody browses to see
            // who is online. So the two never actually compete; this order is what makes that a rule
            // rather than a coincidence.
            var letters = BuildLetterCorner();
            if (letters != null) { row.Children.Add(letters); return row; }

            if (!LibraryTabOffersFriends) return row;

            if (FriendsReadable)
            {
                row.Children.Add(BuildCornerChip("RT", "\uE716",
                    Core.Loc.F("{0} of {1} online", _friends.OnlineCount, _friends.Friends.Count),
                    OpenFriends));
            }
            else if (FriendsLoading)
            {
                // No key cap: RT does nothing yet, and a cap would promise that it does.
                var loading = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(18, 0, 4, 0),
                };
                loading.Children.Add(GifSpinner.Create(18));
                loading.Children.Add(new TextBlock
                {
                    Text = Core.Loc.T("Steam friends"),
                    FontSize = 14,
                    Foreground = UiHelpers.Subtle,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                });
                row.Children.Add(loading);
            }

            return row;
        }

        private UIElement BuildCornerChip(string key, string glyph, string label, Action onClick)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(BuildKeyCap(key));
            row.Children.Add(new TextBlock
            {
                // Escapes, not literals: a private-use character is invisible in every diff.
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 6, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 14,
                Foreground = UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var chip = new Border
            {
                Child = row,
                Background = Brushes.Transparent,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            chip.MouseLeftButtonUp += (_, __) => onClick();
            return chip;
        }
        #endregion

        #region Open and close
        private void OpenFriends()
        {
            if (_launchPrompt != LaunchPrompt.None || _settingsOpen || MiscOverlayOpen || GameMenuOverlayOpen) return;
            if (_friendsOpen || _infoOpen || _exitPromptOpen || !FriendsReadable) return;

            _friendsOpen = true;
            _friendsColumn = FriendsColumnList;
            _friendIndex = 0;
            _friendSelectedId = 0;
            _feedIndex = 0;
            RenderLibrary();
            RefreshTabStrip();
            RefreshActionBar();

            ApplyFriendsInterval();
            RequestFriends();
        }

        private void CloseFriends()
        {
            _friendsOpen = false;
            _friendRows.Clear();
            _feedRows.Clear();
            _feedShown.Clear();
            ClearFriendsColumns();
            ApplyFriendsInterval();
            RenderLibrary();
            RefreshTabStrip();
            RefreshActionBar();
        }
        #endregion

        #region The screen
        /// <summary>
        /// Two columns: friends on the left, activity on the right (user, 2026-09-11). The two used to
        /// be separate screens behind X, which made the activity feel bolted on; side by side each one
        /// answers the other - who is there, and what they have been doing.
        /// </summary>
        private void RenderFriends()
        {
            LibraryRoot.Children.Clear();
            LibraryRoot.RowDefinitions.Clear();
            LibraryRoot.ColumnDefinitions.Clear();
            _friendRows.Clear();
            _feedRows.Clear();
            _feedShown.Clear();

            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            LibraryRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            LibraryRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

            RenderFriendList();
            RenderFriendFeed();

            // An empty feed has nothing to stand on.
            if (_friendsColumn == FriendsColumnFeed && _feedRows.Count == 0) _friendsColumn = FriendsColumnList;

            ApplyFriendsSelection();
            BringRowIntoViewLater(_friendRows, _friendIndex);
            BringRowIntoViewLater(_feedRows, _feedIndex);
        }

        /// <summary>
        /// The column scaffolding is shared with the rest of the library, and every other screen expects
        /// a grid with no columns. Cleared here, where the two-column screen is left, so no other
        /// screen needs to know it ever existed.
        /// </summary>
        private void ClearFriendsColumns()
        {
            if (LibraryRoot != null) LibraryRoot.ColumnDefinitions.Clear();
        }
        #endregion

        #region The friend list
        private SteamFriend SelectedFriend =>
            _friends != null && _friendIndex >= 0 && _friendIndex < _friends.Friends.Count
                ? _friends.Friends[_friendIndex] : null;

        private void RenderFriendList()
        {
            var list = _friends?.Friends ?? new List<SteamFriend>();

            // Keep the cursor on the same person after a refresh re-sorted the list.
            if (_friendSelectedId != 0)
            {
                int found = list.FindIndex(f => f.SteamId == _friendSelectedId);
                if (found >= 0) _friendIndex = found;
            }
            if (_friendIndex >= list.Count) _friendIndex = list.Count - 1;
            if (_friendIndex < 0) _friendIndex = 0;

            AddColumnHead(FriendsColumnList, Core.Loc.T("Steam friends"),
                !FriendsReadable
                    ? Core.Loc.T("Steam is not running.")
                    : Core.Loc.F("{0} of {1} online", _friends.OnlineCount, list.Count));

            var stack = new StackPanel();
            if (list.Count == 0)
                stack.Children.Add(BuildOverlayEmpty(Core.Loc.T("No friends on this Steam account.")));

            for (int i = 0; i < list.Count; i++)
            {
                var row = BuildFriendRow(list[i]);
                row.Tag = i;
                int captured = i;
                row.MouseLeftButtonUp += (_, __) =>
                {
                    _friendsColumn = FriendsColumnList;
                    _friendIndex = captured;
                    _friendSelectedId = list[captured].SteamId;
                    ApplyFriendsSelection();
                    RefreshActionBar();
                };
                _friendRows.Add(row);
                stack.Children.Add(row);
            }

            AddColumnBody(FriendsColumnList, stack);
        }

        private Border BuildFriendRow(SteamFriend f)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FriendAvatarSize + 14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatar = BuildAvatar(f, FriendAvatarSize);
            Grid.SetColumn(avatar, 0);
            grid.Children.Add(avatar);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = f.Name,
                FontSize = 16,
                Foreground = f.IsOnline ? UiHelpers.Text : UiHelpers.Subtle,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = StatusText(f),
                FontSize = 13,
                Foreground = StatusBrush(f),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });

            // The last thing they did, from Steam's feed or from what Center saw. Absent rather than
            // a dash when neither knows anything: a dash would read as "did nothing".
            string activity = ActivityLine(f);
            if (!string.IsNullOrEmpty(activity))
                text.Children.Add(new TextBlock
                {
                    Text = activity,
                    FontSize = 12,
                    Foreground = UiHelpers.Subtle,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0),
                });

            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            return BuildOverlayRow(grid);
        }

        /// <summary>The game by name where Steam's cache knows it. A non-Steam game has no appid to look
        /// up, and an unknown one keeps its number rather than pretending to a name.</summary>
        private static string StatusText(SteamFriend f)
        {
            if (f.InGame)
            {
                if (!string.IsNullOrEmpty(f.GameName)) return f.GameName;
                return f.AppId > 0
                    ? Core.Loc.F("In game ({0})", f.AppId.ToString(CultureInfo.InvariantCulture))
                    : Core.Loc.T("In a non-Steam game");
            }

            switch (f.State)
            {
                case SteamPersonaState.Offline: return Core.Loc.T("Offline");
                case SteamPersonaState.Busy: return Core.Loc.T("Busy");
                case SteamPersonaState.Away:
                case SteamPersonaState.Snooze: return Core.Loc.T("Away");
                default: return Core.Loc.T("Online");
            }
        }

        /// <summary>Green online and in a game, yellow away or busy, grey offline (user, 2026-09-11).
        /// A friend in a game is green even when Steam reports them away - Steam's own list does the
        /// same, and "playing X" in yellow reads as a warning about the game.</summary>
        private static Brush StatusBrush(SteamFriend f)
        {
            if (!f.IsOnline) return UiHelpers.Subtle;
            if (f.InGame) return UiHelpers.Ok;
            if (f.State == SteamPersonaState.Away || f.State == SteamPersonaState.Snooze || f.State == SteamPersonaState.Busy)
                return UiHelpers.Warn;
            return UiHelpers.Ok;
        }

        /// <summary>
        /// One line of recent activity: the newest of Steam's feed entry and the last game Center saw
        /// them in, or - offline, when that is newer - when Center last saw them online.
        ///
        /// The game they are in RIGHT NOW is already the line above, so the last-seen game only counts
        /// while they are not in a game.
        /// </summary>
        private static string ActivityLine(SteamFriend f)
        {
            DateTime best = DateTime.MinValue;
            string text = null;

            var feed = f.LatestActivity;
            if (feed != null)
            {
                best = feed.When;
                text = ActivityText(feed);
            }

            var seen = f.Seen;
            if (seen != null && !f.InGame && seen.LastGameUtc.HasValue && !string.IsNullOrEmpty(seen.LastGameName))
            {
                DateTime when = seen.LastGameUtc.Value.ToLocalTime();
                if (when > best)
                {
                    best = when;
                    text = Core.Loc.F("Played {0}", seen.LastGameName);
                }
            }

            if (seen != null && !f.IsOnline && seen.LastOnlineUtc.HasValue)
            {
                DateTime when = seen.LastOnlineUtc.Value.ToLocalTime();
                if (when > best)
                    return Core.Loc.F("Last online {0}", Ago(when));
            }

            return text == null ? null : text + "   ·   " + Ago(best);
        }

        private static string ActivityText(FriendActivity a)
        {
            string game = !string.IsNullOrEmpty(a.GameName) ? a.GameName : a.AppId.ToString(CultureInfo.InvariantCulture);
            switch (a.Kind)
            {
                case FriendActivityKind.Achievement:
                    return a.Achievements.Count > 1
                        ? Core.Loc.F("{0} achievements in {1}", a.Achievements.Count, game)
                        : Core.Loc.F("Achievement in {0}", game);
                case FriendActivityKind.FirstPlayed:
                    return Core.Loc.F("Played {0} for the first time", game);
                case FriendActivityKind.Wishlist:
                    return Core.Loc.F("Added {0} to the wishlist", game);
                default:
                    return game;
            }
        }

        /// <summary>"just now", "12 min ago", "3 h ago", "yesterday", "4 days ago", then the date.</summary>
        private static string Ago(DateTime local)
        {
            TimeSpan span = DateTime.Now - local;
            if (span.TotalMinutes < 1) return Core.Loc.T("just now");
            if (span.TotalHours < 1) return Core.Loc.F("{0} min ago", (int)span.TotalMinutes);
            if (local.Date == DateTime.Today) return Core.Loc.F("{0} h ago", (int)span.TotalHours);
            if (local.Date == DateTime.Today.AddDays(-1)) return Core.Loc.T("yesterday");
            if (span.TotalDays < 7) return Core.Loc.F("{0} days ago", (int)Math.Ceiling((DateTime.Today - local.Date).TotalDays));
            return local.ToString("d MMM", CultureInfo.CurrentCulture);
        }
        #endregion

        #region The activity feed
        private void RenderFriendFeed()
        {
            var byId = (_friends?.Friends ?? new List<SteamFriend>()).ToDictionary(f => f.SteamId);
            var feed = (_friends?.Activity ?? new List<FriendActivity>())
                       .Where(a => byId.ContainsKey(a.SteamId)).Take(FeedMaxEntries).ToList();

            AddColumnHead(FriendsColumnFeed, Core.Loc.T("Friend activity"), null);

            var stack = new StackPanel();
            if (feed.Count == 0)
                stack.Children.Add(BuildOverlayEmpty(Core.Loc.T("No recent activity.")));

            DateTime? day = null;
            foreach (var a in feed)
            {
                // One heading per day, the way Steam's own page reads.
                if (day != a.When.Date)
                {
                    day = a.When.Date;
                    stack.Children.Add(new TextBlock
                    {
                        Text = DayHeading(a.When.Date),
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = UiHelpers.Subtle,
                        Margin = new Thickness(2, _feedRows.Count == 0 ? 0 : 12, 0, 6),
                    });
                }

                var row = BuildFeedRow(a, byId[a.SteamId]);
                int index = _feedRows.Count;
                row.Tag = index;
                row.MouseLeftButtonUp += (_, __) =>
                {
                    _friendsColumn = FriendsColumnFeed;
                    _feedIndex = index;
                    ApplyFriendsSelection();
                    RefreshActionBar();
                };
                _feedRows.Add(row);
                _feedShown.Add(a);
                stack.Children.Add(row);
            }

            if (_feedIndex >= _feedRows.Count) _feedIndex = _feedRows.Count - 1;
            if (_feedIndex < 0) _feedIndex = 0;

            AddColumnBody(FriendsColumnFeed, stack);
        }

        private static string DayHeading(DateTime date)
        {
            if (date == DateTime.Today) return Core.Loc.T("Today");
            if (date == DateTime.Today.AddDays(-1)) return Core.Loc.T("Yesterday");
            return date.ToString("d MMMM", CultureInfo.CurrentCulture);
        }

        /// <summary>Avatar, name in the status colour, what happened, and for achievements the icons.
        /// Name and event on two lines: a column is half the width the single line was written for.</summary>
        private Border BuildFeedRow(FriendActivity a, SteamFriend friend)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FriendAvatarSize + 14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = BuildAvatar(friend, FriendAvatarSize);
            avatar.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(avatar, 0);
            grid.Children.Add(avatar);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = friend.Name,
                FontSize = 15,
                Foreground = StatusBrush(friend),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = ActivityText(a),
                FontSize = 13,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });

            if (a.Kind == FriendActivityKind.Achievement)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var ach in a.Achievements)
                    wrap.Children.Add(BuildFeedAchievement(ach));
                text.Children.Add(wrap);
            }

            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var time = new TextBlock
            {
                Text = a.When.ToString("t", CultureInfo.CurrentCulture),
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10, 2, 0, 0),
            };
            Grid.SetColumn(time, 2);
            grid.Children.Add(time);

            return BuildOverlayRow(grid);
        }

        /// <summary>
        /// One achievement in a feed entry: icon and name.
        ///
        /// A HIDDEN ONE STAYS HIDDEN, the way Steam's own feed shows it: no icon, no name. The feed
        /// cannot tell whether the user has it too, and a spoiler on a screen about somebody else's
        /// progress is the wrong place to guess.
        /// </summary>
        private UIElement BuildFeedAchievement(FriendAchievement ach)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };

            var host = new Border
            {
                Width = FeedAchievementIconSize,
                Height = FeedAchievementIconSize,
                CornerRadius = new CornerRadius(4),
                Background = UiHelpers.Card,
                ClipToBounds = true,
            };
            if (ach.Hidden || string.IsNullOrEmpty(ach.IconUrl))
            {
                host.Child = new TextBlock
                {
                    Text = "?",
                    FontSize = 18,
                    Foreground = UiHelpers.Subtle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else
            {
                var image = new Image { Stretch = Stretch.UniformToFill };
                host.Child = image;
                LoadRemoteInto(image, ach.IconUrl, (int)(FeedAchievementIconSize * 2));
            }
            row.Children.Add(host);

            row.Children.Add(new TextBlock
            {
                Text = ach.Hidden ? Core.Loc.T("Hidden achievement") : ach.Name,
                FontSize = 13,
                Foreground = ach.Hidden ? UiHelpers.Subtle : UiHelpers.Text,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 0, 0),
            });
            return row;
        }

        private FriendActivity SelectedFeedEntry =>
            _feedIndex >= 0 && _feedIndex < _feedShown.Count ? _feedShown[_feedIndex] : null;
        #endregion

        #region Shared pieces
        private void AddColumnHead(int column, string title, string subtitle)
        {
            var head = new StackPanel { Margin = ColumnMargin(column, 14, 10) };
            head.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 4),
            });
            // Both heads keep the same height, so the two lists start on the same line.
            head.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(subtitle) ? " " : subtitle,
                FontSize = 13,
                Foreground = UiHelpers.Subtle,
            });
            Grid.SetRow(head, 0);
            Grid.SetColumn(head, column);
            LibraryRoot.Children.Add(head);
        }

        private void AddColumnBody(int column, UIElement content)
        {
            var scroller = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Margin = ColumnMargin(column, 0, 12),
            };
            Grid.SetRow(scroller, 1);
            Grid.SetColumn(scroller, column);
            LibraryRoot.Children.Add(scroller);
        }

        /// <summary>The outer edge keeps the library's margin; the two inner edges share one gap.</summary>
        private Thickness ColumnMargin(int column, double top, double bottom) =>
            column == FriendsColumnList
                ? new Thickness(LibOuterMargin, top, 12, bottom)
                : new Thickness(12, top, LibOuterMargin, bottom);

        private static TextBlock BuildOverlayEmpty(string text) => new TextBlock
        {
            Text = text,
            FontSize = 15,
            Foreground = UiHelpers.Subtle,
            Margin = new Thickness(0, 12, 0, 0),
        };

        private static Border BuildOverlayRow(UIElement content) => new Border
        {
            Child = content,
            Background = UiHelpers.Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        /// <summary>A card behind the avatar, always, with the status colour as its frame: the picture
        /// comes over the network, and a row that changes width when it lands, or has a hole offline,
        /// reads as a layout fault.</summary>
        private Border BuildAvatar(SteamFriend f, double size)
        {
            var host = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(6),
                Background = UiHelpers.Card,
                ClipToBounds = true,
                BorderThickness = new Thickness(2),
                BorderBrush = StatusBrush(f),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var image = new Image { Stretch = Stretch.UniformToFill, Opacity = f.IsOnline ? 1.0 : 0.55 };
            host.Child = image;
            LoadRemoteInto(image, f.AvatarUrl, (int)(size * 2));
            return host;
        }

        /// <summary>LoadRemoteAsync, never LoadAsync - see GameArt: an http UriSource makes Freeze throw.</summary>
        private static void LoadRemoteInto(Image image, string url, int decodeWidth)
        {
            if (string.IsNullOrEmpty(url)) return;
            image.Tag = url;
            GameArt.LoadRemoteAsync(url, decodeWidth).ContinueWith(t =>
            {
                var bmp = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                if (bmp == null) return;
                image.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(image.Tag, url)) image.Source = bmp;
                }));
            }, TaskScheduler.Default);
        }

        /// <summary>Only the column the D-pad is in shows a cursor. Two lit rows would leave A's target
        /// to be guessed.</summary>
        private void ApplyFriendsSelection()
        {
            ApplyRowSelection(_friendRows, _friendsColumn == FriendsColumnList ? _friendIndex : -1);
            ApplyRowSelection(_feedRows, _friendsColumn == FriendsColumnFeed ? _feedIndex : -1);
        }

        private static void ApplyRowSelection(List<Border> rows, int index)
        {
            foreach (var row in rows)
                row.BorderBrush = row.Tag is int i && i == index ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void BringRowIntoViewLater(List<Border> rows, int index)
        {
            if (index <= 0 || index >= rows.Count) return;
            var target = rows[index];
            Dispatcher.BeginInvoke(new Action(() => { try { target.BringIntoView(); } catch { } }), DispatcherPriority.Loaded);
        }
        #endregion

        #region Navigation and footer
        private void MoveFriendSelection(PadButton dir)
        {
            if (dir == PadButton.Left || dir == PadButton.Right)
            {
                int column = dir == PadButton.Right ? FriendsColumnFeed : FriendsColumnList;
                if (column == _friendsColumn) return;
                if (column == FriendsColumnFeed && _feedRows.Count == 0) return;
                _friendsColumn = column;
                ApplyFriendsSelection();
                RefreshActionBar();
                return;
            }

            int delta = dir == PadButton.Down ? 1 : dir == PadButton.Up ? -1 : 0;
            if (delta == 0) return;

            if (_friendsColumn == FriendsColumnFeed)
            {
                int next = _feedIndex + delta;
                if (next < 0 || next >= _feedRows.Count) return;
                _feedIndex = next;
                ApplyFriendsSelection();
                try { _feedRows[_feedIndex].BringIntoView(); } catch { }
            }
            else
            {
                int next = _friendIndex + delta;
                if (next < 0 || next >= _friendRows.Count) return;
                _friendIndex = next;
                _friendSelectedId = SelectedFriend?.SteamId ?? 0;
                ApplyFriendsSelection();
                try { _friendRows[_friendIndex].BringIntoView(); } catch { }
            }
            RefreshActionBar();
        }

        /// <summary>A chats with the selected friend - in the feed, with whoever the entry is about.</summary>
        private SteamFriend FriendForChat()
        {
            if (_friendsColumn == FriendsColumnList) return SelectedFriend;
            var entry = SelectedFeedEntry;
            return entry == null ? null : _friends?.Friends.FirstOrDefault(f => f.SteamId == entry.SteamId);
        }

        private void ChatWithSelectedFriend()
        {
            var friend = FriendForChat();
            if (friend != null) SteamFriends.OpenChat(friend);
        }

        private void AddFriendsActions()
        {
            AddAction(PadButton.A, "Chat", FriendForChat() != null, ChatWithSelectedFriend);
            AddAction(PadButton.B, "Close", true, CloseFriends);
        }
        #endregion
    }
}
