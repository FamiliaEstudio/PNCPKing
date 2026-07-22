namespace PNCPKing.Core.Models;

public enum ItemHydrationStatus
{
    NotLoaded,
    Loading,
    Complete,
    Partial,
    Stale,
    Failed
}

public sealed record ProcurementItem
{
    public required string ContractId { get; init; }
    public required long ItemNumber { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool HasResult { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public ItemHydrationStatus HydrationStatus { get; init; } = ItemHydrationStatus.NotLoaded;
    public string? LastError { get; init; }
}

public sealed record HomologationResult
{
    public required string ContractId { get; init; }
    public required long ItemNumber { get; init; }
    public required long ResultSequence { get; init; }
    public string SupplierTaxId { get; init; } = string.Empty;
    public string SupplierName { get; init; } = string.Empty;
    public long? HomologatedQuantityScaled { get; init; }
    public long? HomologatedUnitValueScaled { get; init; }
    public long? HomologatedTotalValueScaled { get; init; }
    public DateOnly? ResultDate { get; init; }
    public int ResultStatusId { get; init; }
    public string ResultStatusName { get; init; } = string.Empty;
    public bool IsActive => ResultStatusId == 1;

    public decimal? HomologatedQuantity => DecimalScale.FromScaled(HomologatedQuantityScaled);
    public decimal? HomologatedUnitValue => DecimalScale.FromScaled(HomologatedUnitValueScaled);
    public decimal? HomologatedTotalValue => DecimalScale.FromScaled(HomologatedTotalValueScaled);
}

public sealed record ItemDisplayRow(
    long ItemNumber,
    string Description,
    string Unit,
    string DisplayStatus,
    decimal? HomologatedQuantity,
    decimal? HomologatedUnitValue,
    decimal? HomologatedTotalValue,
    string Supplier,
    string SupplierTaxId,
    DateOnly? ResultDate,
    bool IsCancelled);

public sealed record HydrationProgress(
    int CompletedItems,
    int TotalItemsWithResult,
    int FailedItems,
    string Message)
{
    public double Percentage => TotalItemsWithResult == 0
        ? 100d
        : CompletedItems * 100d / TotalItemsWithResult;
}

public sealed record HydrationPreparation(
    int TotalItems,
    int ItemsWithResult,
    int ItemsToConsult,
    TimeSpan EstimatedMinimumDuration,
    TimeSpan EstimatedMaximumDuration);
