using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IQuotationWordService
{
    Task ExportAsync(string destinationPath, QuotationProjectReport report,
        QuotationWordExportOptions options, CancellationToken cancellationToken = default);

    Task ExportPriceTableAsync(string destinationPath, QuotationProjectReport report,
        CancellationToken cancellationToken = default);
}
