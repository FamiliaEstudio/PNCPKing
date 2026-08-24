namespace PNCPKing.Core.Models;

public static class ItemSearchDefaults
{
    public const int InitialBatchCount = 3;
    public const int ContractsPerBatch = 50;
}

/// <summary>
/// Proves that the complete item list of a contract was committed atomically.
/// </summary>
public sealed record ContractItemSnapshot(
    string ContractId,
    DateTimeOffset FetchedAt,
    int ItemCount,
    DateTimeOffset? SourceGlobalUpdatedAt)
{
    public bool IsCurrentFor(ContractRecord contract) =>
        string.Equals(ContractId, contract.PncpId, StringComparison.Ordinal) &&
        Nullable.Equals(SourceGlobalUpdatedAt, contract.GlobalUpdatedAt);
}

public sealed record CachedItemResults(
    ProcurementItem Item,
    IReadOnlyList<HomologationResult> Results)
{
    public bool IsCurrent => Item.HydrationStatus == ItemHydrationStatus.Complete;
}

public sealed record ItemSearchSession(
    Guid Id,
    string Text,
    DateTimeOffset StartedAt,
    int CandidateContractCount,
    int PageSize = ItemSearchDefaults.ContractsPerBatch,
    long RandomPivot = 0);

public sealed record ItemSearchLocalSummary(
    long CandidateContracts,
    long CachedMatchingItems,
    long CachedItemsWithActivePrices)
{
    public bool IsPartial => true;
}

public sealed record ItemSearchHit(
    ContractRecord Contract,
    ProcurementItem Item);

public enum ItemSearchPriceState
{
    Pending,
    Homologated,
    NoHomologatedResult,
    Cancelled,
    Failed
}

public sealed record ItemSearchRow(
    ContractRecord Contract,
    ProcurementItem Item,
    HomologationResult? Result,
    ItemSearchPriceState PriceState,
    string DisplayStatus,
    bool IsTemporary,
    PromptMatchLevel? MatchedPromptLevel = null,
    string MatchedSearchText = "")
{
    public decimal? HomologatedQuantity => Result?.HomologatedQuantity;
    public decimal? HomologatedUnitValue => Result?.HomologatedUnitValue;
    public decimal? HomologatedTotalValue => Result?.HomologatedTotalValue;
}

public sealed record ItemSearchPage(
    IReadOnlyList<ItemSearchRow> Rows,
    int Page,
    int PageSize,
    int MatchedItemsDiscovered,
    bool HasMoreCandidates,
    int ContractsScanned = 0,
    int FreshItemListsUsed = 0,
    bool ItemListBudgetExhausted = false,
    string GeographicStage = "",
    int CachedItemListsReused = 0);

public enum PriceBatchBudgetMode
{
    CandidateContracts = 0,
    UnresolvedContracts = 1
}

public sealed record PriceBatchRequest(
    int BatchCount,
    bool LargeRequestConfirmed = false,
    PriceBatchBudgetMode BudgetMode = PriceBatchBudgetMode.CandidateContracts)
{
    public int RequestedContracts => checked(BatchCount * ItemSearchDefaults.ContractsPerBatch);
}

public sealed record PriceBatchProgress(
    int RequestedItemCalls,
    int CompletedItemCalls,
    int FailedItemCalls,
    int ItemListCalls,
    long PayloadBytes,
    TimeSpan Elapsed,
    bool CandidateSetExhausted,
    string Message,
    ItemSearchNetworkMetrics? Network = null,
    int ContractsScanned = 0,
    int FreshItemListsUsed = 0,
    bool ItemListBudgetExhausted = false,
    string GeographicStage = "",
    int CachedItemListsReused = 0,
    int RequestedContracts = 0,
    int ProcessedContracts = 0,
    int MatchedItems = 0,
    int RevealedPrices = 0,
    int ExpandedContracts = 0,
    int FullyResolvedContracts = 0,
    long RemainingCandidateContracts = -1,
    int ItemResultCalls = 0,
    int TotalFailedCalls = 0);

public sealed record TimedPriceBatchResult(
    IReadOnlyList<ItemSearchRow> Rows,
    ItemSearchCheckpoint Checkpoint,
    PriceBatchProgress Progress);

public sealed record ContractItemPrompt(
    Guid LineId,
    PromptMatchLevel Level,
    string Text);

public sealed record ContractEvaluationResult(
    ContractRecord Contract,
    IReadOnlyDictionary<Guid, IReadOnlyList<ItemSearchRow>> RowsByLine,
    int MatchedItems,
    int RevealedPrices,
    int ItemListsFromCache,
    int ItemListsFromApi,
    int ItemResultApiCalls,
    int FailedCalls);

/// <summary>
/// Network measurements isolated to one item-search session. HTTP calls include
/// retries, whereas <see cref="PriceBatchProgress.CompletedItemCalls"/> counts
/// logical item-result consultations completed by the session coordinator.
/// </summary>
public sealed record ItemSearchNetworkMetrics(
    long ItemListHttpCalls,
    long ItemResultHttpCalls,
    long ItemListBytesReceived,
    long ItemResultBytesReceived,
    TimeSpan ItemListDuration,
    TimeSpan ItemResultDuration)
{
    public long TotalHttpCalls => checked(ItemListHttpCalls + ItemResultHttpCalls);
    public long TotalBytesReceived => checked(ItemListBytesReceived + ItemResultBytesReceived);

    public double AverageBytesPerItemList => ItemListHttpCalls == 0
        ? 0d
        : (double)ItemListBytesReceived / ItemListHttpCalls;

    public double AverageBytesPerItemResult => ItemResultHttpCalls == 0
        ? 0d
        : (double)ItemResultBytesReceived / ItemResultHttpCalls;

    public TimeSpan AverageItemListDuration => ItemListHttpCalls == 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(ItemListDuration.Ticks / ItemListHttpCalls);

    public TimeSpan AverageItemResultDuration => ItemResultHttpCalls == 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(ItemResultDuration.Ticks / ItemResultHttpCalls);
}
