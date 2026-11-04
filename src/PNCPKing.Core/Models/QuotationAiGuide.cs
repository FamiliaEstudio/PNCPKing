namespace PNCPKing.Core.Models;

public sealed record QuotationAiTemplatePaths(string TriagePath, string GuidePath);

public sealed record QuotationAiGuideItemResult(
    Guid LineId,
    string Name,
    string Status,
    string Message,
    Guid? BasketId = null)
{
    public bool CanImportCandidates { get; init; }
    public long CandidateCount { get; init; }
    public int ImportedCandidateCount { get; init; }
    public bool CandidatesImported { get; init; }
    public int TechnicalReviewCount { get; init; }
    public QuotationAiPriceRangeAdjustment? PriceRangeAdjustment { get; init; }
}

public sealed record QuotationAiGuidePreview(
    Guid PackageId,
    Guid ProjectId,
    IReadOnlyList<QuotationAiGuideItemResult> Items)
{
    public string GuideSha256 { get; init; } = string.Empty;
    public int ReadyCount => Items.Count(item => item.Status == "ready");
    public int PendingCount => Items.Count - ReadyCount;
    public int ImportableCount => Items.Count(item => item.CanImportCandidates);
}

public sealed record QuotationAiGuideApplyResult(IReadOnlyList<QuotationAiGuideItemResult> Items)
{
    public int AppliedCount => Items.Count(item => item.Status == "applied");
    public int UnchangedCount => Items.Count(item => item.Status == "unchanged");
    public int PendingCount => Items.Count - AppliedCount - UnchangedCount;
    public int ImportedCandidateCount => Items.Sum(item => item.ImportedCandidateCount);
    public int CandidateItemCount => Items.Count(item => item.CandidatesImported);
}

public sealed record QuotationAiCandidateImportResult(QuotationLine Line, int AddedCount);

public sealed record QuotationAiPriceRangeAdjustment(
    decimal? MinimumUnitPrice, decimal? MaximumUnitPrice, string Reason);

public sealed record QuotationAiBasketSelection(
    QuotationReference Reference,
    decimal ConversionFactor,
    string TechnicalReason,
    string ConversionReason,
    decimal? PriceDivisor = null);

public sealed record QuotationAiBasketApplication(
    QuotationLine ExpectedLine,
    bool IsMedication,
    Guid BasketId,
    string Name,
    QuotationAggregationMethod Method,
    IReadOnlyList<QuotationAiBasketSelection> Selections)
{
    public QuotationAiPriceRangeAdjustment? PriceRangeAdjustment { get; init; }
}
