using System.Text.Json;
using System.Globalization;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PNCPKing.Infrastructure.Services;

public sealed class PdfTextIndexService : IPdfTextIndexService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly int[] OcrDpiAttempts = [300, 200];

    private readonly IPdfPageRasterizer _rasterizer;
    private readonly IOcrService _ocrService;

    public PdfTextIndexService(IPdfPageRasterizer rasterizer, IOcrService ocrService)
    {
        _rasterizer = rasterizer;
        _ocrService = ocrService;
    }

    public async Task<DocumentTextIndex> BuildAsync(
        CachedPdfDocument pdf,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var indexPath = string.IsNullOrWhiteSpace(pdf.IndexCachePath)
            ? pdf.LocalPath + ".index.json"
            : Path.GetFullPath(pdf.IndexCachePath);
        var cached = await TryLoadAsync(indexPath, pdf, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var pages = new List<DocumentPageIndex>();
        var warnings = new List<string>();
        using (var document = PdfDocument.Open(
                   pdf.LocalPath,
                   new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true }))
        {
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report(new DocumentProcessingProgress(
                        DocumentProcessingStage.Indexing,
                        pageNumber - 1,
                        document.NumberOfPages,
                        $"{pdf.DocumentTitle}: página {pageNumber:N0} de {document.NumberOfPages:N0}"));
                    var page = document.GetPage(pageNumber);
                    var nativeWords = OrderAndNumberLines(
                        NearestNeighbourWordExtractor.Instance
                            .GetWords(page.Letters)
                            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                            .Select(word => new DocumentWord(
                                word.Text,
                                new DocumentRectangle(
                                    word.BoundingBox.Left,
                                    page.Height - word.BoundingBox.Top,
                                    word.BoundingBox.Width,
                                    word.BoundingBox.Height),
                                0))
                            .ToArray());
                    if (HasUsableNativeText(nativeWords))
                    {
                        pages.Add(CreatePageIndex(
                            pageNumber,
                            page.Width,
                            page.Height,
                            DocumentTextSource.Native,
                            nativeWords));
                        continue;
                    }

                    var ocrResult = await RecognizeWithRetryAsync(
                        pdf,
                        pageNumber,
                        document.NumberOfPages,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    if (ocrResult.Words is not null)
                    {
                        pages.Add(CreatePageIndex(
                            pageNumber,
                            page.Width,
                            page.Height,
                            DocumentTextSource.Ocr,
                            OrderAndNumberLines(ocrResult.Words)));
                        continue;
                    }

                    pages.Add(new DocumentPageIndex
                    {
                        PageNumber = pageNumber,
                        Width = page.Width,
                        Height = page.Height,
                        Source = DocumentTextSource.Native,
                        Words = nativeWords,
                        Blocks = BuildBlocks(nativeWords)
                    });
                    var fallback = nativeWords.Count > 0
                        ? "O texto nativo parcial foi preservado."
                        : "A página foi preservada no índice, mas ficou sem texto pesquisável.";
                    warnings.Add(
                        $"Página {pageNumber:N0}: não foi possível executar o OCR após nova tentativa. " +
                        $"{fallback} {string.Join(" | ", ocrResult.Failures)}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(
                        $"Página {pageNumber:N0}: não foi possível interpretar a página; " +
                        $"as demais páginas continuaram sendo processadas " +
                        $"({DocumentExceptionDiagnostics.Describe(exception)}).");
                }
            }
        }

        var result = new DocumentTextIndex
        {
            PdfSha256 = pdf.Sha256,
            SourcePath = pdf.LocalPath,
            Pages = pages,
            Warnings = warnings
        };
        if (warnings.Count == 0)
        {
            try
            {
                await SaveAtomicAsync(indexPath, result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = result with
                {
                    Warnings =
                    [
                        $"O índice foi criado em memória, mas não pôde ser salvo no cache " +
                        $"({DocumentExceptionDiagnostics.Describe(exception)})."
                    ]
                };
            }
        }

        return result;
    }

    public static bool HasUsableNativeText(IReadOnlyList<DocumentWord> words)
    {
        var text = string.Join(' ', words.Select(word => word.Text));
        var alphanumeric = text.Count(char.IsLetterOrDigit);
        if (alphanumeric < 20 || text.Length == 0)
        {
            return false;
        }

        var printable = text.Count(character =>
            CharUnicodeInfo.GetUnicodeCategory(character) is not (
                UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.Surrogate or
                UnicodeCategory.OtherNotAssigned));
        return printable / (double)text.Length >= 0.70;
    }

    private static IReadOnlyList<DocumentWord> OrderAndNumberLines(IEnumerable<DocumentWord> source)
    {
        // Word extractors already return reading order. Re-sorting by visual X/Y
        // reverses phrases on pages whose PDF rotation is 180° or 270°.
        var ordered = source
            .Where(word => word.Bounds.Width >= 0 && word.Bounds.Height >= 0)
            .ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }

        var result = new List<DocumentWord>(ordered.Length);
        var line = 0;
        var lineCenter = ordered[0].Bounds.Y + ordered[0].Bounds.Height / 2;
        var lineHeight = Math.Max(2, ordered[0].Bounds.Height);
        foreach (var word in ordered)
        {
            var center = word.Bounds.Y + word.Bounds.Height / 2;
            if (result.Count > 0 && Math.Abs(center - lineCenter) > Math.Max(2, lineHeight * 0.65))
            {
                line++;
                lineCenter = center;
                lineHeight = Math.Max(2, word.Bounds.Height);
            }
            else
            {
                lineCenter = (lineCenter + center) / 2;
                lineHeight = Math.Max(lineHeight, word.Bounds.Height);
            }

            result.Add(word with { Line = line });
        }

        return result;
    }

    private async Task<OcrAttemptResult> RecognizeWithRetryAsync(
        CachedPdfDocument pdf,
        int pageNumber,
        int pageCount,
        IProgress<DocumentProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>(OcrDpiAttempts.Length);
        for (var attempt = 0; attempt < OcrDpiAttempts.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dpi = OcrDpiAttempts[attempt];
            progress?.Report(new DocumentProcessingProgress(
                DocumentProcessingStage.Ocr,
                pageNumber - 1,
                pageCount,
                attempt == 0
                    ? $"{pdf.DocumentTitle}: OCR da página {pageNumber:N0}…"
                    : $"{pdf.DocumentTitle}: repetindo OCR da página {pageNumber:N0} em {dpi:N0} dpi…"));
            try
            {
                var rendered = await _rasterizer.RenderAsync(
                    pdf.LocalPath,
                    pageNumber,
                    dpi,
                    cancellationToken).ConfigureAwait(false);
                var words = await _ocrService.RecognizeAsync(rendered, cancellationToken)
                    .ConfigureAwait(false);
                return new OcrAttemptResult(words, failures);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(
                    $"{dpi:N0} dpi: {DocumentExceptionDiagnostics.Describe(exception)}");
            }
        }

        return new OcrAttemptResult(null, failures);
    }

    private static DocumentPageIndex CreatePageIndex(
        int pageNumber,
        double width,
        double height,
        DocumentTextSource source,
        IReadOnlyList<DocumentWord> words) =>
        new()
        {
            PageNumber = pageNumber,
            Width = width,
            Height = height,
            Source = source,
            Words = words,
            Blocks = BuildBlocks(words)
        };

    private static IReadOnlyList<DocumentTextBlock> BuildBlocks(IReadOnlyList<DocumentWord> words) =>
        words
            .GroupBy(word => word.Line)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var values = group.OrderBy(word => word.Bounds.X).ToArray();
                var left = values.Min(word => word.Bounds.X);
                var top = values.Min(word => word.Bounds.Y);
                var right = values.Max(word => word.Bounds.X + word.Bounds.Width);
                var bottom = values.Max(word => word.Bounds.Y + word.Bounds.Height);
                return new DocumentTextBlock(
                    string.Join(' ', values.Select(word => word.Text)),
                    new DocumentRectangle(left, top, right - left, bottom - top),
                    group.Key);
            })
            .ToArray();

    private static async Task<DocumentTextIndex?> TryLoadAsync(
        string path,
        CachedPdfDocument pdf,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync<DocumentTextIndex>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return result is
            {
                AnalyzerVersion: DocumentTextIndex.CurrentAnalyzerVersion
            } && string.Equals(result.PdfSha256, pdf.Sha256, StringComparison.OrdinalIgnoreCase)
                ? result
                : null;
        }
        catch (Exception exception) when (exception is
                   JsonException or
                   IOException or
                   UnauthorizedAccessException or
                   NotSupportedException)
        {
            return null;
        }
    }

    private static async Task SaveAtomicAsync(
        string path,
        DocumentTextIndex index,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, index, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record OcrAttemptResult(
        IReadOnlyList<DocumentWord>? Words,
        IReadOnlyList<string> Failures);
}
