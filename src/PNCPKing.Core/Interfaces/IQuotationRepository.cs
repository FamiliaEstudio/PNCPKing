using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IQuotationRepository
{
    Task<IReadOnlyList<QuotationProject>> GetProjectsAsync(CancellationToken cancellationToken = default);
    Task<QuotationProject> CreateProjectAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuotationLine>> GetLinesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuotationReference>> GetReferencesAsync(Guid lineId, CancellationToken cancellationToken = default);
    Task<QuotationLine> SaveSampleAsync(
        Guid projectId,
        Guid? lineId,
        QuotationLineInput input,
        IReadOnlyList<QuotationReference> references,
        CancellationToken cancellationToken = default);
    Task ConfirmBasketAsync(Guid lineId, string basketKey, CancellationToken cancellationToken = default);
    Task UpdateWeightsAsync(Guid lineId, AdequacyWeights weights, CancellationToken cancellationToken = default);
}

public interface IQuotationWorkbookService
{
    Task ExportAsync(
        string destinationPath,
        QuotationProjectReport report,
        CancellationToken cancellationToken = default);
}
