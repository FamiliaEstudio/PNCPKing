using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IContractRepository
{
    string DatabasePath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertContractsAsync(IReadOnlyList<ContractRecord> contracts, CancellationToken cancellationToken = default);
    Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    Task<ContractRecord?> GetContractAsync(string pncpId, CancellationToken cancellationToken = default);
    Task UpsertItemsAsync(string contractId, IReadOnlyList<ProcurementItem> items, bool forceRefresh, CancellationToken cancellationToken = default);
    Task<ContractItemSnapshot?> GetItemSnapshotAsync(string contractId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProcurementItem>> SearchItemsAsync(string contractId, string text, CancellationToken cancellationToken = default);
    Task<CachedItemResults?> GetCachedItemResultsAsync(string contractId, long itemNumber, CancellationToken cancellationToken = default);
    Task ReplaceItemResultsAsync(string contractId, long itemNumber, IReadOnlyList<HomologationResult> results, CancellationToken cancellationToken = default);
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
    Task CheckpointWalAsync(CancellationToken cancellationToken = default);
}
