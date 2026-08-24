using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IQuotationRepository
{
    Task<IReadOnlyList<QuotationProject>> GetProjectsAsync(CancellationToken cancellationToken = default);
    Task<QuotationProject> CreateProjectAsync(string name, CancellationToken cancellationToken = default);
    Task RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default);
    Task RenameLineDisplayNameAsync(
        Guid lineId,
        string displayName,
        CancellationToken cancellationToken = default);
    Task SetLineCatalogSelectionAsync(
        Guid lineId,
        QuotationCatalogSelection? selection,
        CancellationToken cancellationToken = default);
    Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task DeleteLineAsync(Guid lineId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuotationLine>> GetLinesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<QuotationLine?> GetLineAsync(
        Guid projectId,
        Guid lineId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuotationReference>> GetReferencesAsync(Guid lineId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuotationManualBasket>> GetManualBasketsAsync(
        Guid lineId,
        CancellationToken cancellationToken = default);
    Task<QuotationLine> SaveSampleAsync(
        Guid projectId,
        Guid? lineId,
        QuotationLineInput input,
        IReadOnlyList<QuotationReference> references,
        CancellationToken cancellationToken = default);
    Task ConfirmBasketAsync(Guid lineId, string basketKey, CancellationToken cancellationToken = default);
    Task<QuotationManualBasket> SaveManualBasketAsync(
        Guid lineId,
        Guid? basketId,
        string name,
        IReadOnlyList<string> referenceIds,
        CancellationToken cancellationToken = default);
    Task RenameManualBasketAsync(
        Guid basketId,
        string name,
        CancellationToken cancellationToken = default);
    Task RemoveManualBasketReferenceAsync(
        Guid basketId,
        string referenceId,
        CancellationToken cancellationToken = default);
    Task DeleteManualBasketAsync(Guid basketId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InternetPriceDraft>> GetInternetPriceDraftsAsync(
        Guid lineId,
        CancellationToken cancellationToken = default);
    Task<InternetPriceDraft> SaveInternetPriceDraftAsync(
        InternetPriceDraft draft,
        CancellationToken cancellationToken = default);
    Task DeleteInternetPriceDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, InternetPriceEvidence>> GetInternetPriceEvidenceAsync(
        Guid lineId,
        CancellationToken cancellationToken = default);
    Task<QuotationManualBasket> SaveInternetPriceReferenceAsync(
        QuotationReference reference,
        InternetPriceEvidence evidence,
        Guid basketId,
        string basketName,
        CancellationToken cancellationToken = default);
    Task DeleteInternetPriceReferenceAsync(
        Guid lineId,
        string referenceId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> GetReferencedInternetEvidenceHashesAsync(
        CancellationToken cancellationToken = default);
    Task UpdateWeightsAsync(Guid lineId, AdequacyWeights weights, CancellationToken cancellationToken = default);
    Task<QuotationAutomationRun> CreateAutomationRunAsync(
        Guid projectId,
        string outputPath,
        string responsibleName,
        SearchGeoFilter geoFilter,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<QuotationImportItem> items,
        AdequacyWeights weights,
        CancellationToken cancellationToken = default);
    Task<QuotationAutomationRun> CreateTimedAutomationRunAsync(
        Guid projectId,
        SearchGeoFilter geoFilter,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<QuotationImportItem> items,
        AdequacyWeights weights,
        TimeSpan timeBudget,
        IReadOnlyList<string>? contractSearchPrompts = null,
        Guid? sourceDraftId = null,
        string? sourcePdfSha256 = null,
        CancellationToken cancellationToken = default);
    Task<QuotationAutomationRun?> GetLatestAutomationRunAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
    Task RecoverInterruptedAutomationAsync(CancellationToken cancellationToken = default);
    Task UpdateAutomationItemStateAsync(
        Guid lineId,
        QuotationAutomationItemState state,
        string message,
        CancellationToken cancellationToken = default);
    Task UpdateAutomationRunStateAsync(
        Guid runId,
        QuotationAutomationRunState state,
        string message,
        CancellationToken cancellationToken = default);
    Task SaveSearchCheckpointAsync(
        Guid lineId,
        ItemSearchCheckpoint checkpoint,
        CancellationToken cancellationToken = default);
    Task UpdateAutomationTimingAsync(
        Guid runId,
        TimeSpan activeElapsed,
        TimeSpan? newTimeBudget = null,
        CancellationToken cancellationToken = default);
    Task UpdateAutomationOutputPathAsync(
        Guid runId,
        string outputPath,
        CancellationToken cancellationToken = default);
    Task UpdateAutomationResponsibleNameAsync(
        Guid runId,
        string responsibleName,
        CancellationToken cancellationToken = default);
    Task UpgradeContractSearchStrategyAsync(
        Guid runId,
        int strategyVersion,
        CancellationToken cancellationToken = default);
    Task LinkAutomationDraftAsync(
        Guid runId,
        Guid draftId,
        string pdfSha256,
        CancellationToken cancellationToken = default);
    Task<ItemSearchPromptSet> GetItemSearchPromptSetAsync(
        Guid lineId,
        CancellationToken cancellationToken = default);
    Task SaveItemSearchPromptSetAsync(
        ItemSearchPromptSet promptSet,
        CancellationToken cancellationToken = default);
    Task UpdateItemSearchPromptProgressAsync(
        Guid lineId,
        PromptMatchLevel activeLevel,
        int contractsAtActiveLevel,
        int matchedItems,
        int revealedPrices,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContractSearchPrompt>> GetContractSearchPromptsAsync(
        Guid runId,
        CancellationToken cancellationToken = default);
    Task SaveContractSearchPromptAsync(
        ContractSearchPrompt prompt,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContractSearchCheckpoint>> GetProcessedContractsAsync(
        Guid runId,
        CancellationToken cancellationToken = default);
    Task SaveProcessedContractAsync(
        ContractSearchCheckpoint checkpoint,
        TimedQuotationProgress progress,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ItemSearchPromptSet>> GetPendingPromptRevalidationsAsync(
        Guid runId,
        CancellationToken cancellationToken = default);
    Task MarkPromptRevalidatedAsync(
        Guid runId,
        Guid lineId,
        int promptVersion,
        CancellationToken cancellationToken = default);
}

public interface IQuotationWorkbookService
{
    Task ExportAsync(
        string destinationPath,
        QuotationProjectReport report,
        string responsibleName,
        CancellationToken cancellationToken = default);
}

public interface IQuotationWorkbookImportService
{
    Task<QuotationImportDocument> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}
