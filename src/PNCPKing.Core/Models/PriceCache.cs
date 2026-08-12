namespace PNCPKing.Core.Models;

public enum PriceCacheStatus
{
    NotAuthorized = 0,
    Idle = 1,
    Downloading = 2,
    Paused = 3,
    Complete = 4,
    Failed = 5,
    InsufficientSpace = 6,
    Disabled = 7
}

public enum PriceCacheContractStatus
{
    Pending = 0,
    Downloading = 1,
    Complete = 2,
    Failed = 3
}

public sealed record PriceCachePolicy
{
    public bool Authorized { get; init; }
    public bool Enabled { get; init; }
    public bool Paused { get; init; }
    public PriceCacheStatus Status { get; init; } = PriceCacheStatus.NotAuthorized;
    public DateOnly? WindowStart { get; init; }
    public DateOnly? WindowEnd { get; init; }
    public DateTimeOffset? AuthorizedAt { get; init; }
    public DateTimeOffset? LastStartedAt { get; init; }
    public DateTimeOffset? LastCompletedAt { get; init; }
    public string LastError { get; init; } = string.Empty;
}

public sealed record PriceCacheEstimate
{
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required long ContractCount { get; init; }
    public required long AlreadyCompleteContracts { get; init; }
    public required long EstimatedMinimumBytes { get; init; }
    public required long EstimatedMaximumBytes { get; init; }
    public required long AvailableFreeBytes { get; init; }
    public required long SafetyReserveBytes { get; init; }
    public required TimeSpan EstimatedMinimumDuration { get; init; }
    public required TimeSpan EstimatedMaximumDuration { get; init; }

    public long RemainingContracts => Math.Max(0, ContractCount - AlreadyCompleteContracts);
    public long RequiredFreeBytes => checked(EstimatedMaximumBytes + SafetyReserveBytes);
    public bool HasEnoughSpace => AvailableFreeBytes >= RequiredFreeBytes;
}

public sealed record PriceCacheCheckpoint
{
    public required string ContractId { get; init; }
    public required PriceCacheContractStatus Status { get; init; }
    public DateTimeOffset? SourceGlobalUpdatedAt { get; init; }
    public int ItemCount { get; init; }
    public int ActiveResultCount { get; init; }
    public int CancelledResultCount { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public string LastError { get; init; } = string.Empty;
    public bool BackgroundOwned { get; init; }
    public bool UserPinned { get; init; }
}

public sealed record PriceCacheWorkItem(
    ContractRecord Contract,
    PriceCacheCheckpoint Checkpoint);

public sealed record PriceCacheProgress
{
    public required PriceCacheStatus Status { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required long TotalContracts { get; init; }
    public required long CompletedContracts { get; init; }
    public required long PendingContracts { get; init; }
    public required long FailedContracts { get; init; }
    public required long ItemCount { get; init; }
    public required long ActiveResultCount { get; init; }
    public required long CancelledResultCount { get; init; }
    public required long OccupiedBytes { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
    public string Message { get; init; } = string.Empty;

    public double Percentage => TotalContracts <= 0
        ? Status == PriceCacheStatus.Complete ? 100d : 0d
        : Math.Clamp(CompletedContracts * 100d / TotalContracts, 0d, 100d);
}

public sealed record PriceCacheLocalPage(
    IReadOnlyList<ItemSearchHit> Hits,
    int Page,
    int PageSize,
    bool HasMore,
    long MatchingItems,
    IReadOnlyList<ItemSearchRow>? Rows = null,
    PriceCacheLocalCursor? Cursor = null);

public sealed record PriceCacheLocalCursor(
    int Page,
    int ExplicitPriority,
    double PrimaryRank,
    double SecondaryRank,
    string PublicationDate,
    string ContractId,
    long ItemNumber,
    long ResultSequence = 0);

public enum BackupProfile
{
    Compact = 0,
    Full = 1
}
