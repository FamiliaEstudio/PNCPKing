using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IPncpDocumentClient
{
    Task<IReadOnlyList<PncpDocumentDescriptor>> ListDocumentsAsync(
        PncpContractKey contract,
        CancellationToken cancellationToken = default);

    Task<PncpDocumentContent> DownloadDocumentAsync(
        PncpContractKey contract,
        PncpDocumentDescriptor document,
        CancellationToken cancellationToken = default);
}

public interface IContractDocumentService
{
    Task<DocumentBundleResult> PrepareAsync(
        PncpContractKey contract,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<DocumentBundleResult> CreateConsolidatedPdfAsync(
        PncpContractKey contract,
        string destinationPath,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<long> ClearCacheAsync(CancellationToken cancellationToken = default);
}

public interface IContractRelevantPageService
{
    Task<RelevantDocumentPagesResult> CreateAsync(
        PncpContractKey contract,
        IReadOnlyList<string> expressions,
        string destinationPath,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IPdfPageRasterizer
{
    Task<RenderedPdfPage> RenderAsync(
        string pdfPath,
        int pageNumber,
        int dpi = 300,
        CancellationToken cancellationToken = default);
}

public interface IOcrService
{
    Task<IReadOnlyList<DocumentWord>> RecognizeAsync(
        RenderedPdfPage page,
        CancellationToken cancellationToken = default);
}

public interface IPdfTextIndexService
{
    Task<DocumentTextIndex> BuildAsync(
        CachedPdfDocument pdf,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IPdfToMarkdownConverter
{
    Task<MarkdownConversionResult> ConvertAsync(
        DocumentTextIndex index,
        MarkdownConversionOptions options,
        CancellationToken cancellationToken = default);
}

public interface IQuotationEvidenceExportService
{
    Task<QuotationEvidenceResult> ExportAsync(
        string destinationPath,
        QuotationProjectReport report,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
