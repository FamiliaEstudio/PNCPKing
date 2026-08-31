using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Core.Interfaces;

public interface IContractRepository
{
    string DatabasePath { get; }
    Task<DatabaseInitializationResult> InitializeAsync(
        CancellationToken cancellationToken = default,
        IProgress<DatabaseInitializationProgress>? progress = null);
    Task UpsertContractsAsync(IReadOnlyList<ContractRecord> contracts, CancellationToken cancellationToken = default);
    Task CommitSyncPageAsync(
        IReadOnlyList<ContractRecord> contracts,
        SyncPartitionCheckpoint checkpoint,
        CancellationToken cancellationToken = default);
    Task<SearchPageSlice> SearchPageAsync(SearchQuery query, CancellationToken cancellationToken = default);
    Task<long> CountSearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    Task<ItemCandidatePage> SearchItemCandidatesAsync(
        SearchQuery filters,
        SearchExpression expression,
        long randomPivot,
        ItemCandidateCursor? cursor,
        int pageSize = 200,
        CancellationToken cancellationToken = default);
    Task<ItemCandidatePage> SearchContractCandidatesAsync(
        SearchQuery filters,
        string contractPrompt,
        long randomPivot,
        ItemCandidateCursor? cursor,
        int pageSize = 200,
        CancellationToken cancellationToken = default);
    Task<StaleItemCandidatePage> SearchStaleItemCandidatesAsync(
        SearchQuery filters,
        SearchExpression expression,
        StaleItemCandidateCursor? cursor,
        int pageSize = 200,
        CancellationToken cancellationToken = default);
    Task<ItemSearchLocalSummary> GetItemSearchLocalSummaryAsync(
        SearchQuery filters,
        SearchExpression expression,
        CancellationToken cancellationToken = default);
    Task<ContractRecord?> GetContractAsync(string pncpId, CancellationToken cancellationToken = default);
    Task UpsertItemsAsync(string contractId, IReadOnlyList<ProcurementItem> items, bool forceRefresh, CancellationToken cancellationToken = default);
    Task<ContractItemSnapshot?> GetItemSnapshotAsync(string contractId, CancellationToken cancellationToken = default);
    async Task<IReadOnlyDictionary<string, ContractItemSnapshot?>> GetItemSnapshotsAsync(
        IReadOnlyList<ContractRecord> contracts,
        CancellationToken cancellationToken = default)
    {
        var snapshots = new Dictionary<string, ContractItemSnapshot?>(StringComparer.Ordinal);
        foreach (var contract in contracts)
        {
            snapshots[contract.PncpId] = await GetItemSnapshotAsync(contract.PncpId, cancellationToken)
                .ConfigureAwait(false);
        }

        return snapshots;
    }
    Task<ProcurementItem?> GetItemAsync(
        string contractId,
        long itemNumber,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProcurementItem>> SearchItemsAsync(string contractId, string text, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ItemSearchHit>> SearchItemsAsync(
        IReadOnlyList<ContractRecord> contracts,
        string text,
        CancellationToken cancellationToken = default);
    Task<CachedItemResults?> GetCachedItemResultsAsync(string contractId, long itemNumber, CancellationToken cancellationToken = default);
    async Task<IReadOnlyDictionary<(string ContractId, long ItemNumber), CachedItemResults?>> GetCachedItemResultsAsync(
        IReadOnlyList<ItemSearchHit> hits,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<(string ContractId, long ItemNumber), CachedItemResults?>();
        foreach (var hit in hits)
        {
            var key = (ContractId: hit.Contract.PncpId, ItemNumber: hit.Item.ItemNumber);
            if (!results.ContainsKey(key))
            {
                results[key] = await GetCachedItemResultsAsync(
                        key.ContractId,
                        key.ItemNumber,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return results;
    }
    Task ReplaceItemResultsAsync(string contractId, long itemNumber, IReadOnlyList<HomologationResult> results, CancellationToken cancellationToken = default);
    Task ReplaceBackgroundItemResultsAsync(
        string contractId,
        long itemNumber,
        IReadOnlyList<HomologationResult> results,
        CancellationToken cancellationToken = default);
    Task SetItemHydrationStatusAsync(string contractId, long itemNumber, ItemHydrationStatus status, string? error = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProcurementItem>> GetPendingItemsAsync(string contractId, bool forceRefresh, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ItemDisplayRow>> GetItemDisplayRowsAsync(string contractId, CancellationToken cancellationToken = default);
    Task<DatasetState> GetDatasetStateAsync(CancellationToken cancellationToken = default);
    Task<IncompleteSyncState?> GetLatestIncompleteSyncAsync(CancellationToken cancellationToken = default);
    Task SetDatasetStateAsync(DateOnly startDate, DateOnly endDate, GeoScope scope, DateTimeOffset completedAt, CancellationToken cancellationToken = default);
    Task PruneContractsBeforeAsync(DateOnly cutoff, CancellationToken cancellationToken = default);
    Task<long> GetCacheSizeBytesAsync(CancellationToken cancellationToken = default);
    Task ClearItemCacheAsync(CancellationToken cancellationToken = default);
    Task<int?> GetPartitionNextPageAsync(string partitionKey, CancellationToken cancellationToken = default);
    Task SavePartitionProgressAsync(string partitionKey, int nextPage, bool completed, CancellationToken cancellationToken = default);
    Task<SyncPartitionCheckpoint?> GetPartitionCheckpointAsync(string partitionKey, CancellationToken cancellationToken = default);
    Task SavePartitionCheckpointAsync(SyncPartitionCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<string> StartSyncRunAsync(SyncMode mode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task CompleteSyncRunAsync(string runId, bool succeeded, long contractsSaved, string? error, CancellationToken cancellationToken = default);
    Task<(long Contracts, long Items, long Results)> GetCountsAsync(CancellationToken cancellationToken = default);
    Task MarkOptimizePendingAsync(CancellationToken cancellationToken = default);
    Task OptimizeAsync(CancellationToken cancellationToken = default);
    Task CheckpointWalAsync(CancellationToken cancellationToken = default);
    Task MaintainWalAsync(CancellationToken cancellationToken = default);
}
