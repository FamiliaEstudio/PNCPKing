namespace PNCPKing.Core.Models;

public enum SyncMode
{
    Publication,
    GlobalUpdate
}

public sealed record PreflightEstimate
{
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required GeoScope Scope { get; init; }
    public required long ExactContractCount { get; init; }
    public required long EstimatedTransferBytes { get; init; }
    public required long EstimatedDatabaseMinBytes { get; init; }
    public required long EstimatedDatabaseMaxBytes { get; init; }
    public required long EstimatedFullCacheMinBytes { get; init; }
    public required long EstimatedFullCacheMaxBytes { get; init; }
    public required long RequiredFreeBytes { get; init; }
    public required long AvailableFreeBytes { get; init; }
    public required long EstimatedRequests { get; init; }
    public required TimeSpan EstimatedDuration { get; init; }
    public bool HasEnoughSpace => AvailableFreeBytes >= RequiredFreeBytes;
}

public sealed record SyncProgress(
    long ContractsSaved,
    int CompletedPartitions,
    int TotalPartitions,
    string Message)
{
    public double Percentage => TotalPartitions == 0
        ? 0d
        : CompletedPartitions * 100d / TotalPartitions;
}

/// <summary>
/// Controls a synchronization execution without changing the legacy behavior
/// of <c>SyncService.SynchronizeAsync</c>. Automatic gap filling disables
/// dataset finalization and performs it once, after the complete maintenance
/// transaction has succeeded.
/// </summary>
public sealed record SyncExecutionOptions
{
    public static SyncExecutionOptions Default { get; } = new();

    public IReadOnlyList<Modality>? KnownModalities { get; init; }

    public IReadOnlySet<long>? ModalityIds { get; init; }

    public bool FinalizeDataset { get; init; } = true;
}

public enum SyncPartitionStatus
{
    Pending = 0,
    Downloading = 1,
    Partial = 2,
    Complete = 3,
    Failed = 4
}

/// <summary>
/// Checkpoint self-contained enough to be inspected and resumed even if the
/// textual partition key format changes in a later application version.
/// </summary>
public sealed record SyncPartitionCheckpoint
{
    public required string PartitionKey { get; init; }
    public required SyncMode Mode { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required long ModalityId { get; init; }
    public required string Uf { get; init; }
    public required int NextPage { get; init; }
    public long? TotalPages { get; init; }
    public required SyncPartitionStatus Status { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsComplete => Status == SyncPartitionStatus.Complete;
}

public sealed record DatasetState(
    DateOnly? StartDate,
    DateOnly? EndDate,
    GeoScope Scope,
    DateTimeOffset? LastSuccessfulSync,
    long ContractCount,
    long CachedItemCount,
    long CachedResultCount);

public sealed record IncompleteSyncState(
    SyncMode Mode,
    DateOnly StartDate,
    DateOnly EndDate,
    DateTimeOffset StartedAt);

public sealed record DatasetManifest
{
    public int SchemaVersion { get; init; }
    public string AppVersion { get; init; } = string.Empty;
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string Scope { get; init; } = string.Empty;
    public long ContractCount { get; init; }
    public long ItemCount { get; init; }
    public long ResultCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string DatabaseSha256 { get; init; } = string.Empty;
    public bool? DatabaseIntegrityValidatedAtExport { get; init; }
    public IReadOnlyList<EvidenceAssetManifest> EvidenceAssets { get; init; } = [];
    public BackupProfile? BackupProfile { get; init; }
    public bool? ContainsPriceCache { get; init; }
    public long? PriceCacheContractCount { get; init; }
    public long? PriceCacheItemCount { get; init; }
    public long? PriceCacheResultCount { get; init; }
}

public enum BackupImportStage
{
    Inspecting = 0,
    Extracting = 1,
    VerifyingChecksum = 2,
    CheckingIntegrity = 3,
    Migrating = 4,
    InstallingEvidence = 5,
    PreservingCurrentDatabase = 6,
    InstallingDatabase = 7,
    Completed = 8
}

public sealed record BackupImportProgress(
    BackupImportStage Stage,
    double Percentage,
    string Message,
    long BytesProcessed = 0,
    long TotalBytes = 0);

public sealed record BackupInspection
{
    public required string SourcePath { get; init; }
    public required int SchemaVersion { get; init; }
    public BackupProfile? Profile { get; init; }
    public required long ArchiveBytes { get; init; }
    public required long DatabaseBytes { get; init; }
    public required long ExistingDatabaseBytes { get; init; }
    public required string TemporaryRoot { get; init; }
    public required string DataRoot { get; init; }
    public required long TemporaryAvailableBytes { get; init; }
    public required long DataAvailableBytes { get; init; }
    public required long TemporaryRequiredBytes { get; init; }
    public required long DataRequiredBytes { get; init; }
    public required bool SharesTemporaryAndDataVolume { get; init; }

    public bool HasEnoughSpace =>
        TemporaryAvailableBytes >= TemporaryRequiredBytes &&
        DataAvailableBytes >= DataRequiredBytes;
}

public sealed record EvidenceAssetManifest
{
    public required string Sha256 { get; init; }
    public required string ArchivePath { get; init; }
    public long ByteLength { get; init; }
}
