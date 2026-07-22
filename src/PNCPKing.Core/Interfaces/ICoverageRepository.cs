using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface ICoverageRepository
{
    Task EnsureCoverageWindowAsync(
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<long> activeModalityIds,
        string uf = "ALL",
        CancellationToken cancellationToken = default);

    Task SetCoverageStatusAsync(
        DateOnly startDate,
        DateOnly endDate,
        long modalityId,
        string uf,
        CoverageStatus status,
        long? recordsCount = null,
        string? error = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CoverageDay>> GetCoverageDaysAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CoverageWorkItem>> GetIncompleteCoverageAsync(
        DateOnly startDate,
        DateOnly endDate,
        int limit,
        bool newestFirst,
        CancellationToken cancellationToken = default);

    Task<bool> IsCoverageCompleteAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
}
