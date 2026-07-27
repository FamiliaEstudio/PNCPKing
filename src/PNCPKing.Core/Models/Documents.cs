namespace PNCPKing.Core.Models;

public sealed record PncpContractKey(
    string PncpId,
    string Cnpj,
    int PurchaseYear,
    int PurchaseSequence)
{
    public static PncpContractKey FromContract(ContractRecord contract) =>
        new(contract.PncpId, contract.Cnpj, contract.PurchaseYear, contract.PurchaseSequence);

    public static bool TryParse(string pncpId, string? portalUrl, out PncpContractKey? key)
    {
        key = null;
        if (Uri.TryCreate(portalUrl, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var editais = Array.FindIndex(
                segments,
                segment => string.Equals(segment, "editais", StringComparison.OrdinalIgnoreCase));
            if (editais >= 0 &&
                segments.Length > editais + 3 &&
                int.TryParse(segments[editais + 2], out var year) &&
                int.TryParse(segments[editais + 3], out var sequence))
            {
                key = new PncpContractKey(
                    pncpId,
                    Uri.UnescapeDataString(segments[editais + 1]),
                    year,
                    sequence);
                return true;
            }
        }

        var slash = pncpId.LastIndexOf('/');
        var firstDash = pncpId.IndexOf('-');
        var lastDash = slash > 0 ? pncpId.LastIndexOf('-', slash) : -1;
        if (slash > 0 &&
            firstDash > 0 &&
            lastDash > firstDash &&
            int.TryParse(pncpId[(slash + 1)..], out var parsedYear) &&
            int.TryParse(pncpId[(lastDash + 1)..slash], out var parsedSequence))
        {
            key = new PncpContractKey(
                pncpId,
                pncpId[..firstDash],
                parsedYear,
                parsedSequence);
            return true;
        }

        return false;
    }
}

public sealed record PncpDocumentDescriptor
{
    public required long Sequence { get; init; }
    public required string Title { get; init; }
    public string DocumentType { get; init; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; init; }
    public string DownloadUri { get; init; } = string.Empty;
    public bool Active { get; init; } = true;
}

public sealed record PncpDocumentContent(
    byte[] Bytes,
    string? ContentType,
    string? FileName);

public sealed record CachedPdfDocument
{
    public required string LocalPath { get; init; }
    public required string Sha256 { get; init; }
    public required long DocumentSequence { get; init; }
    public required string DocumentTitle { get; init; }
    public string ArchivePath { get; init; } = string.Empty;
    public string? IndexCachePath { get; init; }
}

public sealed record DocumentBundleResult
{
    public required PncpContractKey Contract { get; init; }
    public required IReadOnlyList<CachedPdfDocument> Pdfs { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int DownloadedFiles { get; init; }
    public int ReusedFiles { get; init; }
    public string? ConsolidatedPath { get; init; }
}

public sealed record RelevantDocumentPagesResult
{
    public required DocumentBundleResult Bundle { get; init; }
    public required IReadOnlyList<RelevantExpressionResult> Expressions { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public string? OutputPath { get; init; }
    public int MatchedPdfCount { get; init; }
    public int MatchedPageCount { get; init; }
    public int OccurrenceCount { get; init; }
}

public sealed record RelevantExpressionResult(
    string Expression,
    int MatchedPageCount,
    int OccurrenceCount);

public enum DocumentProcessingStage
{
    Listing,
    Downloading,
    Extracting,
    Indexing,
    Ocr,
    Matching,
    WritingReport,
    Completed
}

public sealed record DocumentProcessingProgress(
    DocumentProcessingStage Stage,
    int Completed,
    int Total,
    string Message);

public enum DocumentTextSource
{
    Native,
    Ocr
}

public sealed record DocumentRectangle(
    double X,
    double Y,
    double Width,
    double Height);

public sealed record DocumentWord(
    string Text,
    DocumentRectangle Bounds,
    int Line);

public sealed record DocumentTextBlock(
    string Text,
    DocumentRectangle Bounds,
    int Line);

public sealed record DocumentPageIndex
{
    public required int PageNumber { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required DocumentTextSource Source { get; init; }
    public required IReadOnlyList<DocumentWord> Words { get; init; }
    public IReadOnlyList<DocumentTextBlock> Blocks { get; init; } = [];
}

public sealed record DocumentTextIndex
{
    public const int CurrentAnalyzerVersion = 5;

    public required string PdfSha256 { get; init; }
    public required string SourcePath { get; init; }
    public required IReadOnlyList<DocumentPageIndex> Pages { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int AnalyzerVersion { get; init; } = CurrentAnalyzerVersion;
}

public sealed record TextOccurrence(
    int PageNumber,
    DocumentRectangle Bounds,
    IReadOnlyList<int> WordIndexes,
    string MatchedText);

public sealed record RenderedPdfPage(
    byte[] PngBytes,
    int PixelWidth,
    int PixelHeight,
    double PdfWidth,
    double PdfHeight);

public sealed record MarkdownConversionOptions(
    bool IncludePageHeadings = true,
    bool PreserveLineBreaks = true);

public sealed record MarkdownConversionResult(
    string Markdown,
    IReadOnlyList<string> Warnings);

public sealed record QuotationEvidenceResult(
    string ReportPath,
    int Items,
    int References,
    int Occurrences,
    IReadOnlyList<string> Warnings);
