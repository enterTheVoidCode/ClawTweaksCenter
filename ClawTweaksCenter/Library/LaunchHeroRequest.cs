using System;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>Keeps one hero request shared by the confirmation and running-screen renders.</summary>
    internal sealed class LaunchHeroRequest
    {
        private readonly Func<GameEntry, Task<string>> _fetch;
        private GameEntry _game;
        private Task<string> _request;

        internal LaunchHeroRequest(Func<GameEntry, Task<string>> fetch) => _fetch = fetch;

        internal void BeginPrompt(GameEntry game)
        {
            // A new prompt may retry a finished failure. While the request is still active,
            // reopening shares it just as the confirmation and running-screen renders do.
            bool completedWithoutArt = _request != null && _request.IsCompleted &&
                (!_request.IsCompletedSuccessfully || _request.Result == null);
            if (_game != game || completedWithoutArt)
            {
                _game = game;
                _request = null;
            }
        }

        internal Task<string> GetAsync(GameEntry game)
        {
            if (_game != game) BeginPrompt(game);
            return _request ??= _fetch(game);
        }
    }
}
