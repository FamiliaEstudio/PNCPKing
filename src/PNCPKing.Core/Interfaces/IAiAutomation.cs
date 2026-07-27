using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IAiCredentialStore
{
    Task<string?> ReadAsync(string target, CancellationToken cancellationToken = default);
    Task SaveAsync(string target, string secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(string target, CancellationToken cancellationToken = default);
}

public interface IAiQuotationProvider
{
    Task<AiProviderResponse> AnalyzeAsync(
        AiProviderRequest request,
        IProgress<AiAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IAiQuotationDraftService
{
    Task<AiMarkdownPreparation> PrepareAsync(
        string pdfPath,
        IProgress<AiAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AiQuotationDraft> CreateAsync(
        AiDraftAnalysisRequest request,
        IProgress<AiAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IAiPromptRefinementService
{
    Task<AiPromptRefinementResult> RefineAsync(
        AiPromptRefinementRequest request,
        IProgress<AiAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IAiCostEstimator
{
    Task<AiCostEstimate> EstimateAsync(
        string markdown,
        AiProviderConfiguration provider,
        int probableItemCount,
        decimal safetyMarginPercent = 10m,
        CancellationToken cancellationToken = default);

    Task SaveManualUsdSellRateAsync(
        decimal sellRate,
        DateOnly date,
        CancellationToken cancellationToken = default);
}

public interface IExchangeRateClient
{
    Task<ExchangeRateQuote> GetUsdSellRateAsync(CancellationToken cancellationToken = default);
    Task SaveManualUsdSellRateAsync(
        decimal sellRate,
        DateOnly date,
        CancellationToken cancellationToken = default);
}

public interface IAiDraftCache
{
    Task<AiQuotationDraft?> LoadAsync(
        string pdfSha256,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        AiQuotationDraft draft,
        string markdown,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string pdfSha256,
        CancellationToken cancellationToken = default);

    Task<long> ClearAsync(CancellationToken cancellationToken = default);
    Task<AiQuotationDraft?> FindCompatibleAsync(
        IReadOnlyList<QuotationLine> lines,
        CancellationToken cancellationToken = default);
}

public interface ITimedQuotationAutomationService
{
    Task RunAsync(
        QuotationAutomationRun run,
        IProgress<TimedQuotationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
