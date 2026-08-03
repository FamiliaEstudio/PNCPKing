using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IComprasCatalogClient
{
    Task<CatalogPage> GetPageAsync(
        CatalogKind kind,
        int page,
        int pageSize = 500,
        CancellationToken cancellationToken = default);
}

public interface ICatalogRepository
{
    Task<CatalogSyncState> GetSyncStateAsync(
        CatalogKind kind,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogSyncState>> GetSyncStatesAsync(
        CancellationToken cancellationToken = default);
    Task BeginSyncAsync(
        CatalogKind kind,
        string generation,
        CancellationToken cancellationToken = default);
    Task StagePageAsync(
        CatalogPage page,
        string generation,
        CancellationToken cancellationToken = default);
    Task PublishAsync(
        CatalogKind kind,
        string generation,
        CancellationToken cancellationToken = default);
    Task MarkFailedAsync(
        CatalogKind kind,
        string error,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogEntry>> FindCandidatesAsync(
        CatalogSearchQuery query,
        int limit,
        CancellationToken cancellationToken = default);
    Task<CatalogEntry?> GetEntryAsync(
        CatalogKind kind,
        string code,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogHierarchyPath>> GetHierarchyAsync(
        CatalogKind? kind = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogEquivalenceRule>> GetEquivalenceRulesAsync(
        CancellationToken cancellationToken = default);
    Task SaveEquivalenceRuleAsync(
        CatalogEquivalenceRule rule,
        CancellationToken cancellationToken = default);
    Task DeleteEquivalenceRuleAsync(
        Guid id,
        CancellationToken cancellationToken = default);
    Task ResetDefaultEquivalenceRulesAsync(CancellationToken cancellationToken = default);
}

public interface ICatalogSearchService
{
    Task<CatalogSearchPage> SearchAsync(
        CatalogSearchQuery query,
        CancellationToken cancellationToken = default);
}
