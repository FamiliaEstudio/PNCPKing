using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Core.Interfaces;

public interface IPriceCacheRepository
{
    Task<PriceCachePolicy> GetPolicyAsync(CancellationToken cancellationToken = default);
    Task<PriceCacheEstimate> EstimateAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
    Task SetAuthorizationAsync(
        bool authorized,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
    Task SetPausedAsync(
        bool paused,
        string? reason = null,
        CancellationToken cancellationToken = default);
    Task SetStatusAsync(
        PriceCacheStatus status,
        string? message = null,
        CancellationToken cancellationToken = default);
    Task PrepareWindowAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
    Task<PriceCacheWorkItem?> GetNextWorkAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task MarkContractDownloadingAsync(
        string contractId,
        bool backgroundOwned,
        CancellationToken cancellationToken = default);
    Task MarkContractCompleteAsync(
        string contractId,
        DateTimeOffset? sourceGlobalUpdatedAt,
        CancellationToken cancellationToken = default);
    Task MarkContractUnavailableAsync(
        string contractId,
        DateTimeOffset? sourceGlobalUpdatedAt,
        string reason,
        CancellationToken cancellationToken = default);
    Task MarkContractFailedAsync(
        string contractId,
        string error,
        DateTimeOffset nextRetryAt,
        CancellationToken cancellationToken = default);
    Task MarkContractPendingAsync(
        string contractId,
        string? message = null,
        CancellationToken cancellationToken = default);
    Task MarkContractPinnedAsync(
        string contractId,
        CancellationToken cancellationToken = default);
    Task<PriceCacheProgress> GetProgressAsync(CancellationToken cancellationToken = default);
    Task RemoveBackgroundCacheAsync(CancellationToken cancellationToken = default);
    Task<PriceCacheLocalPage> SearchLocalAsync(
        SearchQuery filters,
        SearchExpression expression,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<PriceCacheLocalPage> SearchLocalAfterAsync(
        SearchQuery filters,
        SearchExpression expression,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        PriceCacheLocalCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);
}
