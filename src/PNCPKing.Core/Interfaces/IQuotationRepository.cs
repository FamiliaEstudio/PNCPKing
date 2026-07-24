using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IQuotationRepository
{
    Task<IReadOnlyList<QuotationProject>> GetProjectsAsync(CancellationToken cancellationToken = default);
    Task<QuotationProject> CreateProjectAsync(string name, CancellationToken cancellationToken = default);
    Task RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default);
    Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task DeleteLineAsync(Guid lineId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuotationLine>> GetLinesAsync(Guid projectId, CancellationToken cancellationToken = default);
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
    Task UpdateWeightsAsync(Guid lineId, AdequacyWeights weights, CancellationToken cancellationToken = default);
    Task<QuotationAutomationRun> CreateAutomationRunAsync(
        Guid projectId,
        string outputPath,
        SearchGeoFilter geoFilter,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<QuotationImportItem> items,
        AdequacyWeights weights,
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
}

public interface IQuotationWorkbookService
{
    Task ExportAsync(
        string destinationPath,
        QuotationProjectReport report,
        CancellationToken cancellationToken = default);
}

public interface IQuotationWorkbookImportService
{
    Task<QuotationImportDocument> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}
