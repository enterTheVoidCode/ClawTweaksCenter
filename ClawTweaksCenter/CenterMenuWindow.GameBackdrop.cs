using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClawTweaksCenter.Library;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The selected game's backdrop behind the whole library, on Recent (user, 2026-10-03). A
    /// setting, OFF by default.
    ///
    /// ONLY AFTER THE CURSOR RESTS. Scrolling through the reel must not load a 1920 px picture per
    /// cover or flash the screen at every step: a timer restarts on every selection change and the
    /// picture is loaded when it runs out (<see cref="GameBackdropDwell"/>). A selection that moved
    /// on while the picture was loading throws the picture away.
    ///
    /// Which picture: the game's hero (Steam's cached one, Xbox's from the catalogue, or SteamGridDB's
    /// on demand - the same chain the launch screen uses). A game without one fades the layer out
    /// rather than showing a blurred cover: full-screen, a stretched 600 px cover is mud.
    ///
    /// Its own layer above the user's background (CenterMenuWindow.xaml, GameBackdropLayer) with its
    /// own scrim, so turning it off or leaving Recent simply fades the layer out and the user's
    /// background is back untouched.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private static readonly TimeSpan GameBackdropDwell = TimeSpan.FromMilliseconds(450);
        private static readonly Duration GameBackdropFade = new Duration(TimeSpan.FromMilliseconds(260));

        private DispatcherTimer _gameBackdropTimer;
        private GameEntry _gameBackdropPending;
        private GameEntry _gameBackdropShown;
        private int _gameBackdropGeneration;

        private bool GameBackdropWanted =>
            Core.CenterSettings.RecentGameBackdrop
            && _view == View.Library
            && _libraryGroup == LibraryGroup.Recent;

        /// <summary>Called whenever the selected game changes. Cheap: it only restarts a timer.</summary>
        private void ScheduleGameBackdrop(GameEntry g)
        {
            if (!GameBackdropWanted || g == null) { HideGameBackdrop(); return; }
            if (ReferenceEquals(g, _gameBackdropShown)) { _gameBackdropTimer?.Stop(); return; }
            _gameBackdropPending = g;
            if (_gameBackdropTimer == null)
            {
                _gameBackdropTimer = new DispatcherTimer { Interval = GameBackdropDwell };
                _gameBackdropTimer.Tick += (_, __) =>
                {
                    _gameBackdropTimer.Stop();
                    _ = ShowGameBackdropAsync(_gameBackdropPending);
                };
            }
            _gameBackdropTimer.Stop();
            _gameBackdropTimer.Start();
        }

        private async Task ShowGameBackdropAsync(GameEntry g)
        {
            if (g == null || !GameBackdropWanted || GameBackdropLayer == null) return;
            int generation = ++_gameBackdropGeneration;

            string path = g.HeroPath;
            if (path == null && SteamGridDb.HasKey)
            {
                try { path = await SteamGridDb.EnsureHeroAsync(g, CancellationToken.None).ConfigureAwait(true); }
                catch { path = null; }
                if (path != null) g.HeroPath = path;
            }
            if (generation != _gameBackdropGeneration || !GameBackdropWanted) return;
            if (path == null) { FadeGameBackdrop(false); _gameBackdropShown = g; return; }

            var bmp = await GameArt.LoadAsync(path, 1920).ConfigureAwait(true);
            if (generation != _gameBackdropGeneration || !GameBackdropWanted) return;
            if (bmp == null) { FadeGameBackdrop(false); _gameBackdropShown = g; return; }

            GameBackdropImage.Source = bmp;
            _gameBackdropShown = g;
            FadeGameBackdrop(true);
        }

        /// <summary>Fades the layer out and forgets the game. Called on leaving Recent or the
        /// library, from the setting, and from <see cref="ScheduleGameBackdrop"/>.</summary>
        private void HideGameBackdrop()
        {
            _gameBackdropTimer?.Stop();
            _gameBackdropGeneration++;
            _gameBackdropPending = null;
            _gameBackdropShown = null;
            FadeGameBackdrop(false);
        }

        private void FadeGameBackdrop(bool visible)
        {
            if (GameBackdropLayer == null) return;
            if (visible) GameBackdropLayer.Visibility = Visibility.Visible;
            else if (GameBackdropLayer.Visibility != Visibility.Visible) return;
            var anim = new DoubleAnimation(visible ? 1.0 : 0.0, GameBackdropFade);
            if (!visible)
                anim.Completed += (_, __) =>
                {
                    // Only if nothing faded it back in meanwhile.
                    if (GameBackdropLayer.Opacity <= 0.01)
                    {
                        GameBackdropLayer.Visibility = Visibility.Collapsed;
                        GameBackdropImage.Source = null;
                    }
                };
            GameBackdropLayer.BeginAnimation(OpacityProperty, anim);
        }
    }
}
