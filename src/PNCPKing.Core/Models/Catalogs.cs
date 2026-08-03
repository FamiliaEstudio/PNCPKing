namespace PNCPKing.Core.Models;

public enum CatalogKind
{
    Catmat = 1,
    Catser = 2
}

public enum CatalogSyncStatus
{
    Missing = 0,
    Downloading = 1,
    Complete = 2,
    Failed = 3,
    Paused = 4
}

public enum CatalogMatchState
{
    Missing = 0,
    Match = 1,
    Conflict = 2
}

public enum CatalogRuleKind
{
    Alias = 1,
    UnitConversion = 2
}

public sealed record QuotationCatalogSelection
{
    public required CatalogKind Kind { get; init; }
    public required string Code { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset SelectedAt { get; init; }
    public bool IsActive { get; init; } = true;

    public string Label => $"{Kind.ToString().ToUpperInvariant()} {Code}";
}

public sealed record CatalogEntry
{
    public required CatalogKind Kind { get; init; }
    public required string Code { get; init; }
    public required string Description { get; init; }
    public bool Active { get; init; } = true;
    public string Level1Code { get; init; } = string.Empty;
    public string Level1Name { get; init; } = string.Empty;
    public string Level2Code { get; init; } = string.Empty;
    public string Level2Name { get; init; } = string.Empty;
    public string Level3Code { get; init; } = string.Empty;
    public string Level3Name { get; init; } = string.Empty;
    public string Level4Code { get; init; } = string.Empty;
    public string Level4Name { get; init; } = string.Empty;
    public string Level5Code { get; init; } = string.Empty;
    public string Level5Name { get; init; } = string.Empty;
    public string NcmCode { get; init; } = string.Empty;
    public bool Sustainable { get; init; }
    public bool ExclusiveCentralPurchasing { get; init; }
    public DateTimeOffset? RemoteUpdatedAt { get; init; }
    public string SearchText { get; init; } = string.Empty;

    public string KindLabel => Kind == CatalogKind.Catmat ? "CATMAT" : "CATSER";
    public string Hierarchy => string.Join(
        " › ",
        new[] { Level1Name, Level2Name, Level3Name, Level4Name, Level5Name }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record CatalogPage(
    CatalogKind Kind,
    int Page,
    int TotalPages,
    long TotalRecords,
    IReadOnlyList<CatalogEntry> Entries);

public sealed record CatalogSyncState
{
    public required CatalogKind Kind { get; init; }
    public CatalogSyncStatus Status { get; init; }
    public string Generation { get; init; } = string.Empty;
    public int NextPage { get; init; } = 1;
    public int TotalPages { get; init; }
    public long TotalRecords { get; init; }
    public long StagedRecords { get; init; }
    public long ActiveRecords { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string LastError { get; init; } = string.Empty;

    public double Percentage => TotalPages <= 0
        ? Status == CatalogSyncStatus.Complete ? 100d : 0d
        : Math.Clamp((NextPage - 1) * 100d / TotalPages, 0d, 100d);
}

public sealed record CatalogSyncProgress(
    CatalogKind Kind,
    int CompletedPages,
    int TotalPages,
    long Records,
    string Message)
{
    public double Percentage => TotalPages <= 0
        ? 0d
        : Math.Clamp(CompletedPages * 100d / TotalPages, 0d, 100d);
}

public sealed record CatalogHierarchyFilter(
    string Level1Code = "",
    string Level2Code = "",
    string Level3Code = "",
    string Level4Code = "",
    string Level5Code = "");

public sealed record CatalogHierarchyPath(
    CatalogKind Kind,
    string Level1Code,
    string Level1Name,
    string Level2Code,
    string Level2Name,
    string Level3Code,
    string Level3Name,
    string Level4Code,
    string Level4Name,
    string Level5Code,
    string Level5Name);

public sealed record CatalogSearchQuery(
    string Text,
    CatalogKind? Kind = null,
    CatalogHierarchyFilter? Hierarchy = null,
    int Page = 1,
    int PageSize = 50);

public sealed record CatalogMatchSignal(
    string Requested,
    string Found,
    CatalogMatchState State,
    string Explanation);

public sealed record CatalogSearchResult(
    CatalogEntry Entry,
    decimal Score,
    IReadOnlyList<CatalogMatchSignal> Signals,
    int MatchCount,
    int ConflictCount,
    int MissingCount);

public sealed record CatalogSearchPage(
    IReadOnlyList<CatalogSearchResult> Results,
    int Page,
    int PageSize,
    int TotalCandidates);

public sealed record CatalogEquivalenceRule
{
    public required Guid Id { get; init; }
    public required CatalogRuleKind Kind { get; init; }
    public required string Canonical { get; init; }
    public required string Alias { get; init; }
    public string Dimension { get; init; } = string.Empty;
    public decimal Factor { get; init; } = 1m;
    public bool IsDefault { get; init; }
}
