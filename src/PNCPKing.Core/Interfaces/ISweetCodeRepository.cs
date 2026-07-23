using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface ISweetCodeRepository
{
    Task<SweetCodeLibrary> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(
        bool enabled,
        IReadOnlyList<string> expressions,
        CancellationToken cancellationToken = default);
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
