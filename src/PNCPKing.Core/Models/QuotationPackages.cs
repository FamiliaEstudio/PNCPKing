namespace PNCPKing.Core.Models;

public enum QuotationPackageImportMode
{
    PreserveIdentity,
    Copy,
    Replace
}

public sealed record QuotationPackagePreview
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public DateTimeOffset ExportedAt { get; init; }
    public int ItemCount { get; init; }
    public int ReferenceCount { get; init; }
    public int ManualBasketCount { get; init; }
    public int EvidenceCount { get; init; }
    public bool HasProjectConflict { get; init; }
    public bool HasIncompleteAutomation { get; init; }
}

public sealed record QuotationPackageImportResult
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public bool ImportedAsCopy { get; init; }
    public string? RecoveryPackagePath { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
