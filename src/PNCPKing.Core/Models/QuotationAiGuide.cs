namespace PNCPKing.Core.Models;

public sealed record QuotationAiTemplatePaths(string TriagePath, string GuidePath);

public sealed record QuotationAiGuideItemResult(
    Guid LineId,
    string Name,
    string Status,
    string Message,
    Guid? BasketId = null);

public sealed record QuotationAiGuidePreview(
    Guid PackageId,
    Guid ProjectId,
    IReadOnlyList<QuotationAiGuideItemResult> Items)
{
    public int ReadyCount => Items.Count(item => item.Status == "ready");
    public int PendingCount => Items.Count - ReadyCount;
}

public sealed record QuotationAiGuideApplyResult(IReadOnlyList<QuotationAiGuideItemResult> Items)
{
    public int AppliedCount => Items.Count(item => item.Status == "applied");
    public int UnchangedCount => Items.Count(item => item.Status == "unchanged");
    public int PendingCount => Items.Count - AppliedCount - UnchangedCount;
}

public sealed record QuotationAiBasketSelection(
    QuotationReference Reference,
    decimal ConversionFactor,
    string TechnicalReason,
    string ConversionReason);

public sealed record QuotationAiBasketApplication(
    QuotationLine ExpectedLine,
    bool IsMedication,
    Guid BasketId,
    string Name,
    QuotationAggregationMethod Method,
    IReadOnlyList<QuotationAiBasketSelection> Selections);
