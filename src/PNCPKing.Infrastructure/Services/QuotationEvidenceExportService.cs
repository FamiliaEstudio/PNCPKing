using System.Globalization;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationEvidenceExportService : IQuotationEvidenceExportService
{
    private readonly IContractDocumentService _documents;
    private readonly IPdfTextIndexService _indexes;
    private readonly IPdfPageRasterizer _rasterizer;

    public QuotationEvidenceExportService(
        IContractDocumentService documents,
        IPdfTextIndexService indexes,
        IPdfPageRasterizer rasterizer)
    {
        _documents = documents;
        _indexes = indexes;
        _rasterizer = rasterizer;
    }

    public async Task<QuotationEvidenceResult> ExportAsync(
        string destinationPath,
        QuotationProjectReport report,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(report);
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporary = fullDestination + ".partial";
        if (File.Exists(temporary))
        {
            File.Delete(temporary);
        }

        var warnings = new List<string>();
        var bundles = new Dictionary<string, DocumentBundleResult>(StringComparer.Ordinal);
        var renderCache = new Dictionary<(string Hash, int Page), RenderedPdfPage>();
        var itemCount = 0;
        var referenceCount = 0;
        var occurrenceCount = 0;
        using var writer = new EvidencePdfWriter();
        writer.AddTextPage(
            "Relatório de evidências documentais",
            [
                $"Projeto: {report.Project.Name}",
                $"Gerado em: {DateTime.Now:dd/MM/yyyy HH:mm}",
                $"Busca: frase flexível v{FlexiblePhraseMatcher.RulesVersion}; texto nativo primeiro e " +
                "OCR somente em páginas sem texto utilizável.",
                "As falhas e ausências encontradas durante o processamento são registradas neste relatório."
            ]);

        try
        {
            var analyses = report.Lines.OrderBy(line => line.Line.DisplayOrder).ToArray();
            for (var itemIndex = 0; itemIndex < analyses.Length; itemIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var analysis = analyses[itemIndex];
                itemCount++;
                var references = SelectExportedReferences(analysis);
                if (references.Count == 0)
                {
                    writer.AddTextPage(
                        $"Item {itemIndex + 1:N0} — {analysis.Line.Description}",
                        ["Nenhum preço foi exportado; não há documentos para pesquisar."]);
                    continue;
                }

                for (var referenceIndex = 0; referenceIndex < references.Count; referenceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reference = references[referenceIndex];
                    referenceCount++;
                    progress?.Report(new DocumentProcessingProgress(
                        DocumentProcessingStage.Matching,
                        referenceCount,
                        analyses.Sum(item => SelectExportedReferences(item).Count),
                        $"Item {itemIndex + 1:N0}, preço {referenceIndex + 1:N0}: pesquisando documentos…"));
                    var heading =
                        $"Item {itemIndex + 1:N0} · Preço {referenceIndex + 1:N0} — {analysis.Line.Description}";
                    var lines = BuildReferenceLines(reference);
                    var referenceNotes = new List<string>();
                    if (!PncpContractKey.TryParse(reference.ContractId, reference.PortalUrl, out var contract) ||
                        contract is null)
                    {
                        var warning = $"{reference.ContractId}: identificador da contratação inválido.";
                        warnings.Add(warning);
                        writer.AddTextPage(heading, lines.Append(warning).ToArray(), reference.PortalUrl);
                        continue;
                    }

                    if (!bundles.TryGetValue(contract.PncpId, out var bundle))
                    {
                        try
                        {
                            bundle = await _documents.PrepareAsync(
                                contract,
                                progress,
                                cancellationToken).ConfigureAwait(false);
                            bundles.Add(contract.PncpId, bundle);
                            var bundleWarnings = bundle.Warnings
                                .Select(warning => $"{contract.PncpId}: {warning}")
                                .ToArray();
                            warnings.AddRange(bundleWarnings);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            var warning =
                                $"{contract.PncpId}: falha ao obter documentos ({exception.Message}).";
                            warnings.Add(warning);
                            writer.AddTextPage(heading, lines.Append(warning).ToArray(), reference.PortalUrl);
                            continue;
                        }
                    }

                    referenceNotes.AddRange(
                        bundle.Warnings.Select(warning => $"{contract.PncpId}: {warning}"));
                    var referenceOccurrences = 0;
                    foreach (var pdf in bundle.Pdfs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        DocumentTextIndex index;
                        try
                        {
                            index = await _indexes.BuildAsync(pdf, progress, cancellationToken)
                                .ConfigureAwait(false);
                            var indexWarnings = index.Warnings
                                .Select(warning =>
                                    $"{contract.PncpId}/{GetDocumentLabel(pdf)}: {warning}")
                                .ToArray();
                            warnings.AddRange(indexWarnings);
                            referenceNotes.AddRange(indexWarnings);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            var warning =
                                $"{contract.PncpId}/{GetDocumentLabel(pdf)}: PDF ilegível " +
                                $"({DocumentExceptionDiagnostics.Describe(exception)}).";
                            warnings.Add(warning);
                            referenceNotes.Add(warning);
                            continue;
                        }

                        foreach (var page in index.Pages)
                        {
                            var occurrences = FlexiblePhraseMatcher.Find(analysis.Line.Description, page);
                            foreach (var occurrence in occurrences)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                try
                                {
                                    if (!renderCache.TryGetValue((pdf.Sha256, page.PageNumber), out var rendered))
                                    {
                                        rendered = await _rasterizer.RenderAsync(
                                            pdf.LocalPath,
                                            page.PageNumber,
                                            cancellationToken: cancellationToken).ConfigureAwait(false);
                                        renderCache[(pdf.Sha256, page.PageNumber)] = rendered;
                                    }

                                    writer.AddOccurrencePage(
                                        heading,
                                        lines.Concat(
                                        [
                                            $"Documento: {pdf.DocumentTitle}" +
                                            (pdf.ArchivePath.Length == 0 ? string.Empty : $" · {pdf.ArchivePath}"),
                                            $"Página original: {page.PageNumber:N0} · leitura: " +
                                            (page.Source == DocumentTextSource.Native ? "texto nativo" : "OCR"),
                                            $"Ocorrência: {occurrence.MatchedText}"
                                        ]).ToArray(),
                                        reference.PortalUrl,
                                        rendered,
                                        page,
                                        occurrence);
                                    referenceOccurrences++;
                                    occurrenceCount++;
                                }
                                catch (Exception exception) when (exception is not OperationCanceledException)
                                {
                                    var warning =
                                        $"{contract.PncpId}/{pdf.DocumentTitle}, página {page.PageNumber:N0}: " +
                                        "não foi possível incluir a página inteira " +
                                        $"({DocumentExceptionDiagnostics.Describe(exception)}).";
                                    warnings.Add(warning);
                                    referenceNotes.Add(warning);
                                }
                            }
                        }
                    }

                    if (referenceNotes.Count > 0)
                    {
                        writer.AddTextPage(
                            $"{heading} — avisos",
                            lines.Concat(referenceNotes.Distinct(StringComparer.Ordinal)).ToArray(),
                            reference.PortalUrl);
                    }

                    if (referenceOccurrences == 0)
                    {
                        var message = bundle.Pdfs.Count == 0
                            ? "Nenhum PDF processável foi encontrado nesta contratação."
                            : "Nenhuma ocorrência flexível do descritivo foi localizada nos PDFs.";
                        writer.AddTextPage(heading, lines.Append(message).ToArray(), reference.PortalUrl);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            warnings.Add("A exportação foi interrompida pelo usuário; o material concluído foi preservado.");
            writer.AddTextPage(
                "Exportação interrompida",
                ["O relatório contém somente as evidências concluídas antes do cancelamento."]);
        }

        progress?.Report(new DocumentProcessingProgress(
            DocumentProcessingStage.WritingReport,
            occurrenceCount,
            occurrenceCount,
            "Gravando relatório de evidências…"));
        try
        {
            writer.Save(temporary);
            File.Move(temporary, fullDestination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return new QuotationEvidenceResult(
            fullDestination,
            itemCount,
            referenceCount,
            occurrenceCount,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string GetDocumentLabel(CachedPdfDocument pdf) =>
        string.IsNullOrWhiteSpace(pdf.ArchivePath)
            ? pdf.DocumentTitle
            : $"{pdf.DocumentTitle}/{pdf.ArchivePath}";

    private static IReadOnlyList<QuotationReference> SelectExportedReferences(
        QuotationLineAnalysis analysis)
    {
        var selectedBasket = analysis.Line.SelectionConfirmed
            ? analysis.SelectedBasket
            : null;
        selectedBasket ??= analysis.Baskets.FirstOrDefault(basket => basket.IsRecommended);
        return (selectedBasket?.References
                ?? analysis.References
                    .Where(reference => reference.State == QuotationReferenceState.Eligible)
                    .OrderByDescending(reference => reference.Adequacy.Total)
                    .ThenBy(reference => reference.DistanceFromRibeiraoKilometers ?? double.MaxValue)
                    .ThenByDescending(reference => reference.ResultDate)
                    .ThenBy(reference => reference.Id, StringComparer.Ordinal)
                    .Take(analysis.Line.RequestedBasketSize)
                    .ToArray())
            .ToArray();
    }

    private static IReadOnlyList<string> BuildReferenceLines(QuotationReference reference) =>
    [
        $"Empresa: {reference.SupplierName}",
        $"CNPJ/NI: {reference.SupplierTaxId}",
        $"Preço unitário homologado: {reference.UnitPrice.ToString("C4", CultureInfo.GetCultureInfo("pt-BR"))}",
        $"Contratação: {reference.ContractId} · Item {reference.ItemNumber:N0}",
        $"PNCP: {reference.PortalUrl}"
    ];
}
