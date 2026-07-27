using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IQuotationPackageService
{
    Task ExportAsync(
        string destinationPath,
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<QuotationPackagePreview> InspectAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task<QuotationPackageImportResult> ImportAsync(
        string sourcePath,
        QuotationPackageImportMode mode,
        CancellationToken cancellationToken = default);
}
