using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class ContractRelevantPageService : IContractRelevantPageService
{
    private readonly IContractDocumentService _documents;
    private readonly IPdfTextIndexService _indexes;

    public ContractRelevantPageService(
        IContractDocumentService documents,
        IPdfTextIndexService indexes)
    {
        _documents = documents;
        _indexes = indexes;
    }

    public async Task<RelevantDocumentPagesResult> CreateAsync(
        PncpContractKey contract,
        IReadOnlyList<string> expressions,
        string destinationPath,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var preparedExpressions = FlexiblePhraseMatcher.PrepareExpressions(expressions);
        if (preparedExpressions.Count == 0)
        {
            throw new ArgumentException(
                "Informe ao menos uma expressão válida.",
                nameof(expressions));
        }

        var expressionStatistics = preparedExpressions
            .Select(expression => new ExpressionStatistics(expression))
            .ToArray();
        var bundle = await _documents.PrepareAsync(contract, progress, cancellationToken)
            .ConfigureAwait(false);
        var warnings = bundle.Warnings.ToList();
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporaryPath = fullDestination + ".partial";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var matchedPdfCount = 0;
        var matchedPageCount = 0;
        var occurrenceCount = 0;
        try
        {
            using var output = new PdfDocument();
            for (var pdfIndex = 0; pdfIndex < bundle.Pdfs.Count; pdfIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pdf = bundle.Pdfs[pdfIndex];
                progress?.Report(new DocumentProcessingProgress(
                    DocumentProcessingStage.Matching,
                    pdfIndex,
                    bundle.Pdfs.Count,
                    $"Procurando a referência em {pdf.DocumentTitle}…"));

                DocumentTextIndex index;
                try
                {
                    index = await _indexes.BuildAsync(pdf, progress, cancellationToken)
                        .ConfigureAwait(false);
                    warnings.AddRange(
                        index.Warnings.Select(warning =>
                            $"{GetDocumentLabel(pdf)}: {warning}"));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(
                        $"{GetDocumentLabel(pdf)}: não foi possível pesquisar o PDF " +
                        $"({DocumentExceptionDiagnostics.Describe(exception)}).");
                    continue;
                }

                var matches = index.Pages
                    .Select(page => MatchPage(page, preparedExpressions))
                    .Where(result => result.ExpressionMatches.Count > 0)
                    .OrderBy(result => result.Page.PageNumber)
                    .ToArray();
                if (matches.Length == 0)
                {
                    continue;
                }

                var pagesBefore = matchedPageCount;
                try
                {
                    using var input = PdfReader.Open(
                        pdf.LocalPath,
                        PdfDocumentOpenMode.Import);
                    foreach (var match in matches)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (match.Page.PageNumber < 1 ||
                            match.Page.PageNumber > input.PageCount)
                        {
                            warnings.Add(
                                $"{pdf.DocumentTitle}: a página {match.Page.PageNumber:N0} encontrada " +
                                "não existe mais no PDF armazenado.");
                            continue;
                        }

                        if (!TryAddRelevantPage(
                                output,
                                input.Pages[match.Page.PageNumber - 1],
                                match,
                                pdf,
                                warnings))
                        {
                            continue;
                        }

                        matchedPageCount++;
                        foreach (var expressionMatch in match.ExpressionMatches)
                        {
                            var statistics = expressionStatistics[expressionMatch.ExpressionIndex];
                            statistics.MatchedPageCount++;
                            statistics.OccurrenceCount += expressionMatch.Occurrences.Count;
                            occurrenceCount += expressionMatch.Occurrences.Count;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(
                        $"{pdf.DocumentTitle}/{pdf.ArchivePath}: não foi possível copiar as páginas " +
                        $"relevantes ({exception.Message}).");
                }

                if (matchedPageCount > pagesBefore)
                {
                    matchedPdfCount++;
                }
            }

            if (matchedPageCount > 0)
            {
                progress?.Report(new DocumentProcessingProgress(
                    DocumentProcessingStage.WritingReport,
                    matchedPageCount,
                    matchedPageCount,
                    $"Gravando {matchedPageCount:N0} página(s) relevante(s)…"));
                output.Save(temporaryPath);
                File.Move(temporaryPath, fullDestination, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        if (matchedPageCount == 0)
        {
            warnings.Add(
                "Nenhuma das expressões informadas foi encontrada nos PDFs.");
        }

        foreach (var statistics in expressionStatistics.Where(item => item.OccurrenceCount == 0))
        {
            warnings.Add($"A expressão “{statistics.Expression}” não foi encontrada.");
        }

        progress?.Report(new DocumentProcessingProgress(
            DocumentProcessingStage.Completed,
            matchedPageCount,
            matchedPageCount,
            matchedPageCount == 0
                ? "Nenhuma página relevante foi encontrada."
                : $"{matchedPageCount:N0} página(s) relevante(s) concluída(s)."));

        return new RelevantDocumentPagesResult
        {
            Bundle = bundle,
            Expressions = expressionStatistics
                .Select(item => new RelevantExpressionResult(
                    item.Expression,
                    item.MatchedPageCount,
                    item.OccurrenceCount))
                .ToArray(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            OutputPath = matchedPageCount > 0 ? fullDestination : null,
            MatchedPdfCount = matchedPdfCount,
            MatchedPageCount = matchedPageCount,
            OccurrenceCount = occurrenceCount
        };
    }

    private static string GetDocumentLabel(CachedPdfDocument pdf) =>
        string.IsNullOrWhiteSpace(pdf.ArchivePath)
            ? pdf.DocumentTitle
            : $"{pdf.DocumentTitle}/{pdf.ArchivePath}";

    private static PageMatch MatchPage(
        DocumentPageIndex page,
        IReadOnlyList<string> expressions)
    {
        var expressionMatches = expressions
            .Select((expression, index) => new ExpressionPageMatch(
                index,
                FlexiblePhraseMatcher.Find(expression, page)))
            .Where(match => match.Occurrences.Count > 0)
            .ToArray();
        return new PageMatch(page, expressionMatches);
    }

    private static bool TryAddRelevantPage(
        PdfDocument output,
        PdfPage sourcePage,
        PageMatch match,
        CachedPdfDocument sourcePdf,
        ICollection<string> warnings)
    {
        PdfPage? outputPage = null;
        try
        {
            // Importing the source page preserves its media/crop boxes, rotation,
            // searchable text, vector content and original image resolution.
            outputPage = output.AddPage(sourcePage);
            return true;
        }
        catch (Exception exception)
        {
            if (outputPage is not null)
            {
                output.Pages.Remove(outputPage);
            }

            warnings.Add(
                $"{sourcePdf.DocumentTitle}, página {match.Page.PageNumber:N0}: " +
                $"não foi possível copiar a página relevante ({exception.Message}).");
            return false;
        }
    }

    private sealed record PageMatch(
        DocumentPageIndex Page,
        IReadOnlyList<ExpressionPageMatch> ExpressionMatches);

    private sealed record ExpressionPageMatch(
        int ExpressionIndex,
        IReadOnlyList<TextOccurrence> Occurrences);

    private sealed class ExpressionStatistics(string expression)
    {
        public string Expression { get; } = expression;
        public int MatchedPageCount { get; set; }
        public int OccurrenceCount { get; set; }
    }
}
