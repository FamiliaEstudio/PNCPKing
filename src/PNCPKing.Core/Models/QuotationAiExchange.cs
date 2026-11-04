namespace PNCPKing.Core.Models;

/// <summary>Progress contains counters only; candidate rows never travel through the UI.</summary>
public sealed record QuotationAiProgress(
    string Stage,
    int CompletedItems,
    int TotalItems,
    long CandidateCount,
    string Message)
{
    public double Percentage => TotalItems == 0 ? 0 :
        Math.Clamp(CompletedItems * 100d / TotalItems, 0d, 100d);
}

public sealed record QuotationAiPackageManifest
{
    public string Format { get; init; } = "pncpking-ai-result";
    public int Version { get; init; } = 1;
    public required Guid PackageId { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid RunId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsMedication { get; init; }
    public string Stage { get; init; } = "collection";
    public Guid? ParentPackageId { get; init; }
    public string ParentPackageSha256 { get; init; } = string.Empty;
    public string TriageWorkbookSha256 { get; init; } = string.Empty;
    public long TriageRejectedCount { get; init; }
    public long TriageReviewedCount { get; init; }
    public SearchGeoFilterKind GeoFilterKind { get; init; }
    public string? GeoFilterUf { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public IReadOnlyList<QuotationAiPackageItem> Items { get; init; } = [];
    public IReadOnlyList<QuotationAiPackagePart> Parts { get; init; } = [];
}

public sealed record QuotationAiPackageItem
{
    public required QuotationLine Line { get; init; }
    public required string SpecificationHash { get; init; }
    public IReadOnlyList<string> Criteria { get; init; } = [];
    public long CandidateCount { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>True only if every criterion was exhausted, rather than stopped by a page budget.</summary>
    public bool Complete { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record QuotationAiPackagePart
{
    public required string Path { get; init; }
    public required Guid LineId { get; init; }
    public long CandidateCount { get; init; }
    public long ByteLength { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record QuotationAiJsonRecord
{
    public required string Type { get; init; }
    public QuotationAiPackageItem? Item { get; init; }
    public QuotationReference? Reference { get; init; }
}
