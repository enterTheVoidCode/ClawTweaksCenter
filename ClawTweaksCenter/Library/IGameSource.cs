using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// One store's scanner. Every implementation reads only files that are already on this machine —
    /// no account, no token, no network.
    ///
    /// A source that throws must not take the others with it, which is why the contract is "return
    /// what you found" and the scanner (see <see cref="GameLibrary"/>) catches per source. A machine
    /// without Epic installed is the normal case, not an error case.
    /// </summary>
    public interface IGameSource
    {
        GameStore Store { get; }

        Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken ct);
    }
}
