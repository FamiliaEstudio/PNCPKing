namespace PNCPKing.Core.Models;

public enum ItemSearchPromptSlot
{
    Restrictive,
    Intermediate,
    Broad,
    Custom
}

public enum ReferenceViewScope
{
    InBasket,
    EligibleOutsideBasket,
    RejectedOrDuplicate,
    All
}

public sealed record QuotationItemSearchCheckpoint
{
    public long RandomPivot { get; init; }
    public ItemCandidateCursor? Cursor { get; init; }
    public int ContractsExamined { get; init; }
    public int BatchesCompleted { get; init; }
    public bool CandidateSetExhausted { get; init; }
}

public sealed record QuotationItemSearchWorkspace
{
    public required Guid LineId { get; init; }
    public ItemSearchPromptSlot Slot { get; init; }
    public string SearchText { get; init; } = string.Empty;
    public SearchGeoFilter GeoFilter { get; init; } = SearchGeoFilter.All;
    public DateOnly StartDate { get; init; } = DateOnly.FromDateTime(DateTime.Today.AddDays(-364));
    public DateOnly EndDate { get; init; } = DateOnly.FromDateTime(DateTime.Today);
    public SearchSort Sort { get; init; } = SearchSort.Nearest;
    public decimal? MinimumUnitPrice { get; init; }
    public decimal? MaximumUnitPrice { get; init; }
    public int BatchCount { get; init; } = ItemSearchDefaults.InitialBatchCount;
    public QuotationItemSearchCheckpoint Checkpoint { get; init; } = new();
    public int MatchedItems { get; init; }
    public int RevealedPrices { get; init; }
    public int ItemListsFromCache { get; init; }
    public int ItemListsFromApi { get; init; }
    public int ItemResultApiCalls { get; init; }
    public int FailedCalls { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record QuotationItemSearchHit
{
    public required Guid LineId { get; init; }
    public ItemSearchPromptSlot Slot { get; init; }
    public required string ContractId { get; init; }
    public long ItemNumber { get; init; }
    public PromptMatchLevel? MatchedPromptLevel { get; init; }
    public string MatchedSearchText { get; init; } = string.Empty;
    public long DiscoveredOrder { get; init; }
}

public sealed record QuotationItemSearchState(
    QuotationItemSearchWorkspace Workspace,
    IReadOnlyList<ItemSearchRow> Rows);

public sealed record QuotationItemSearchProgress
{
    public int RequestedContracts { get; init; }
    public int ProcessedContracts { get; init; }
    public string CurrentContractId { get; init; } = string.Empty;
    public int ContractsExamined { get; init; }
    public int BatchesCompleted { get; init; }
    public int MatchedItems { get; init; }
    public int RevealedPrices { get; init; }
    public int ItemListsFromCache { get; init; }
    public int ItemListsFromApi { get; init; }
    public int ItemResultApiCalls { get; init; }
    public int FailedCalls { get; init; }
    public bool CandidateSetExhausted { get; init; }
    public string Message { get; init; } = string.Empty;

    public double Percentage => RequestedContracts == 0
        ? 0
        : Math.Clamp(ProcessedContracts * 100d / RequestedContracts, 0d, 100d);
}
