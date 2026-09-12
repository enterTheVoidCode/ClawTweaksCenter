using System;
using System.Windows;
using ClawTweaksCenter.Audio;
using ClawTweaksCenter.Navigation;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Where the library asks for sounds and decides when the music plays. The engine is
    /// Audio/UiSounds.cs; this file only says WHEN.
    ///
    /// Effects are chosen in ONE place, <see cref="Invoke"/>, from the button and whether it did
    /// something - not in each screen's handlers. Forty screens each remembering to click is forty
    /// places to forget it. An action that wants a different sound (starting a game) plays it itself,
    /// and the generic one then stays quiet - see UiSounds.PlayCount.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private void InitializeSounds()
        {
            // Neither of these reaches RefreshActionBar, and both change whether music belongs on
            // screen: the tray hides the window, a minimise takes it away.
            IsVisibleChanged += (_, __) => UpdateLibraryMusic();
            StateChanged += (_, __) => UpdateLibraryMusic();
            Closed += (_, __) => UiSounds.Shutdown();
        }

        /// <summary>The sound for a button that ran an action, when the action chose none.</summary>
        private static void PlayButtonSound(PadButton button)
        {
            switch (button)
            {
                case PadButton.A: UiSounds.Play(UiSound.Confirm); break;
                case PadButton.B: UiSounds.Play(UiSound.Back); break;
            }
        }

        /// <summary>
        /// Music plays while the library is the view, the window is on screen and no game runs.
        ///
        /// Called from RefreshActionBar, which every screen change already goes through, and from the
        /// window's visibility and state events. It is cheap to call too often and wrong to miss once,
        /// so it is recomputed from scratch every time.
        ///
        /// ⚠️ NOT IsActive. A Center plainly on screen routinely reports IsActive == false (see
        /// CenterMenuWindow.Tray.cs) - music would stop and start for no reason anyone could see. The
        /// running game is covered by the launch prompt instead: it stays on Running for as long as
        /// the game does, also when Center is left open behind it.
        /// </summary>
        private void UpdateLibraryMusic()
        {
            bool play = Core.CenterSettings.BackgroundMusic
                        && _view == View.Library
                        && IsVisible
                        && WindowState != WindowState.Minimized
                        && _launchPrompt != LaunchPrompt.Running;

            if (play) _ = UiSounds.WarmAsync();
            UiSounds.SetMusicPlaying(play);
        }
    }
}
