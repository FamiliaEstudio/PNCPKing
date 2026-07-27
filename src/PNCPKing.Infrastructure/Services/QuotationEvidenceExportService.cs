using System.Globalization;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationEvidenceExportService : IQuotationEvidenceExportService
{
    private readonly IContractDocumentService _documents;
    private readonly IPdfTextIndexService _indexes;
    private readonly IPdfPageRasterizer _rasterizer;
    private readonly IQuotationRepository? _quotations;
    private readonly IInternetEvidenceStore? _internetEvidence;

    public QuotationEvidenceExportService(
        IContractDocumentService documents,
        IPdfTextIndexService indexes,
        IPdfPageRasterizer rasterizer,
        IQuotationRepository? quotations = null,
        IInternetEvidenceStore? internetEvidence = null)
    {
        _documents = documents;
        _indexes = indexes;
        _rasterizer = rasterizer;
        _quotations = quotations;
        _internetEvidence = internetEvidence;
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
        var internetEvidence = await LoadAndValidateInternetEvidenceAsync(report, cancellationToken)
            .ConfigureAwait(false);
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
                "Quando o descritivo completo não aparece, a busca recua de forma auditável até a " +
                "identidade básica do item.",
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
                    if (reference.Source == QuotationReferenceSource.InternetIncisoIII)
                    {
                        var evidence = internetEvidence[(reference.LineId, reference.Id)];
                        var priceImage = await _internetEvidence!.ReadVerifiedAsync(
                            evidence.PriceImage,
                            cancellationToken).ConfigureAwait(false);
                        var taxIdImage = await _internetEvidence.ReadVerifiedAsync(
                            evidence.TaxIdImage,
                            cancellationToken).ConfigureAwait(false);
                        writer.AddTextPage(
                            heading,
                            lines.Concat(
                            [
                                "Fonte legal: INCISO III",
                                $"Capturado em: {evidence.CapturedAt.LocalDateTime:dd/MM/yyyy HH:mm}",
                                "Os dois prints abaixo foram fornecidos pelo usuário e validados por SHA-256."
                            ]).ToArray(),
                            reference.PortalUrl);
                        writer.AddImageEvidencePage(
                            heading,
                            lines,
                            reference.PortalUrl,
                            priceImage,
                            "Evidência 1 de 2 — print do preço");
                        writer.AddImageEvidencePage(
                            heading,
                            lines,
                            reference.PortalUrl,
                            taxIdImage,
                            "Evidência 2 de 2 — print do CNPJ");
                        occurrenceCount += 2;
                        continue;
                    }

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
                    var indexedDocuments = new List<IndexedEvidenceDocument>();
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

                        indexedDocuments.Add(new IndexedEvidenceDocument(pdf, index));
                    }

                    var searchExpressions = BuildSearchExpressions(analysis, reference);
                    EvidenceSearchExpression? matchedExpression = null;
                    IReadOnlyList<EvidencePageMatch> pageMatches = [];
                    foreach (var expression in searchExpressions)
                    {
                        var candidateMatches = indexedDocuments
                            .SelectMany(document => document.Index.Pages.Select(page => new EvidencePageMatch(
                                document.Pdf,
                                page,
                                FlexiblePhraseMatcher.Find(expression.Text, page))))
                            .Where(match => match.Occurrences.Count > 0)
                            .ToArray();
                        if (candidateMatches.Length == 0)
                        {
                            continue;
                        }

                        matchedExpression = expression;
                        pageMatches = candidateMatches;
                        break;
                    }

                    if (matchedExpression is not null)
                    {
                        foreach (var pageMatch in pageMatches)
                        {
                            foreach (var occurrence in pageMatch.Occurrences)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                try
                                {
                                    if (!renderCache.TryGetValue(
                                            (pageMatch.Pdf.Sha256, pageMatch.Page.PageNumber),
                                            out var rendered))
                                    {
                                        rendered = await _rasterizer.RenderAsync(
                                            pageMatch.Pdf.LocalPath,
                                            pageMatch.Page.PageNumber,
                                            cancellationToken: cancellationToken).ConfigureAwait(false);
                                        renderCache[
                                            (pageMatch.Pdf.Sha256, pageMatch.Page.PageNumber)] = rendered;
                                    }

                                    writer.AddOccurrencePage(
                                        heading,
                                        lines.Concat(
                                        [
                                            $"Critério localizado: {matchedExpression.Label} — " +
                                            matchedExpression.Text,
                                            $"Documento: {pageMatch.Pdf.DocumentTitle}" +
                                            (pageMatch.Pdf.ArchivePath.Length == 0
                                                ? string.Empty
                                                : $" · {pageMatch.Pdf.ArchivePath}"),
                                            $"Página original: {pageMatch.Page.PageNumber:N0} · leitura: " +
                                            (pageMatch.Page.Source == DocumentTextSource.Native
                                                ? "texto nativo"
                                                : "OCR"),
                                            $"Ocorrência: {occurrence.MatchedText}"
                                        ]).ToArray(),
                                        reference.PortalUrl,
                                        rendered,
                                        pageMatch.Page,
                                        occurrence);
                                    referenceOccurrences++;
                                    occurrenceCount++;
                                }
                                catch (Exception exception) when (exception is not OperationCanceledException)
                                {
                                    var warning =
                                        $"{contract.PncpId}/{pageMatch.Pdf.DocumentTitle}, " +
                                        $"página {pageMatch.Page.PageNumber:N0}: " +
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
                            : "Nenhuma ocorrência foi localizada, mesmo após a busca básica por identidade. " +
                              $"Critérios tentados: {string.Join("; ", searchExpressions.Select(
                                  expression => $"{expression.Label} = {expression.Text}"))}.";
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
                    .Where(reference =>
                        reference.State == QuotationReferenceState.Eligible &&
                        reference.Source == QuotationReferenceSource.PncpIncisoII)
                    .OrderByDescending(reference => reference.Adequacy.Total)
                    .ThenBy(reference => reference.DistanceFromRibeiraoKilometers ?? double.MaxValue)
                    .ThenByDescending(reference => reference.ResultDate)
                    .ThenBy(reference => reference.Id, StringComparer.Ordinal)
                    .Take(analysis.Line.RequestedBasketSize)
                    .ToArray())
            .ToArray();
    }

    private static IReadOnlyList<string> BuildReferenceLines(QuotationReference reference) =>
        reference.Source == QuotationReferenceSource.InternetIncisoIII
            ?
            [
                $"Empresa: {reference.SupplierName}",
                $"CNPJ: {reference.SupplierTaxId}",
                $"Preço unitário informado: {reference.UnitPrice.ToString("C4", CultureInfo.GetCultureInfo("pt-BR"))}",
                $"Descrição anunciada: {reference.ItemDescription}",
                $"Fonte: {reference.PortalUrl}"
            ]
            :
            [
                $"Empresa: {reference.SupplierName}",
                $"CNPJ/NI: {reference.SupplierTaxId}",
                $"Preço unitário homologado: {reference.UnitPrice.ToString("C4", CultureInfo.GetCultureInfo("pt-BR"))}",
                $"Contratação: {reference.ContractId} · Item {reference.ItemNumber:N0}",
                $"PNCP: {reference.PortalUrl}"
            ];

    private async Task<Dictionary<(Guid LineId, string ReferenceId), InternetPriceEvidence>>
        LoadAndValidateInternetEvidenceAsync(
            QuotationProjectReport report,
            CancellationToken cancellationToken)
    {
        var referencesByLine = report.Lines
            .Select(analysis => new
            {
                analysis.Line.Id,
                References = SelectExportedReferences(analysis)
                    .Where(reference =>
                        reference.Source == QuotationReferenceSource.InternetIncisoIII)
                    .ToArray()
            })
            .Where(item => item.References.Length > 0)
            .ToArray();
        if (referencesByLine.Length == 0)
        {
            return [];
        }

        if (_quotations is null || _internetEvidence is null)
        {
            throw new InvalidOperationException(
                "O exportador não foi configurado para evidências do Inciso III.");
        }

        var result = new Dictionary<(Guid, string), InternetPriceEvidence>();
        var failures = new List<string>();
        foreach (var line in referencesByLine)
        {
            var stored = await _quotations.GetInternetPriceEvidenceAsync(line.Id, cancellationToken)
                .ConfigureAwait(false);
            foreach (var reference in line.References)
            {
                if (!stored.TryGetValue(reference.Id, out var evidence))
                {
                    failures.Add($"{reference.SupplierName}: cadastro dos prints ausente");
                    continue;
                }

                var priceValid = await _internetEvidence.VerifyAsync(
                    evidence.PriceImage,
                    cancellationToken).ConfigureAwait(false);
                var taxValid = await _internetEvidence.VerifyAsync(
                    evidence.TaxIdImage,
                    cancellationToken).ConfigureAwait(false);
                if (!priceValid || !taxValid)
                {
                    failures.Add(
                        $"{reference.SupplierName}: " +
                        (!priceValid && !taxValid
                            ? "prints do preço e do CNPJ ausentes ou alterados"
                            : !priceValid
                                ? "print do preço ausente ou alterado"
                                : "print do CNPJ ausente ou alterado"));
                    continue;
                }

                result.Add((line.Id, reference.Id), evidence);
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidDataException(
                "A exportação foi interrompida porque há evidências obrigatórias do Inciso III " +
                $"que precisam ser recapturadas:{Environment.NewLine}- " +
                string.Join($"{Environment.NewLine}- ", failures));
        }

        return result;
    }

    private static IReadOnlyList<EvidenceSearchExpression> BuildSearchExpressions(
        QuotationLineAnalysis analysis,
        QuotationReference reference)
    {
        var expressions = new List<EvidenceSearchExpression>();
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        AddExpression(expressions, normalized, "descritivo da cotação", analysis.Line.Description);
        AddExpression(expressions, normalized, "descritivo do item PNCP", reference.ItemDescription);
        AddPromptExpressions(
            expressions,
            normalized,
            "prompt amplo",
            analysis.Line.PromptSet?.BroadText);
        AddPromptExpressions(
            expressions,
            normalized,
            "prompt que encontrou o item",
            reference.MatchedSearchText);

        var basicTerms = new List<string>();
        AddPromptTerms(basicTerms, analysis.Line.PromptSet?.BroadText);
        AddPromptTerms(basicTerms, reference.MatchedSearchText);
        AddDescriptionTerms(basicTerms, reference.ItemDescription);
        AddDescriptionTerms(basicTerms, analysis.Line.Description);
        foreach (var term in basicTerms
                     .Where(IsUsefulBasicTerm)
                     .Distinct(StringComparer.Ordinal)
                     .Take(6))
        {
            AddExpression(expressions, normalized, "termo básico", term);
        }

        return expressions;
    }

    private static void AddPromptExpressions(
        ICollection<EvidenceSearchExpression> expressions,
        ISet<string> normalized,
        string label,
        string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return;
        }

        try
        {
            var parsed = SearchText.Parse(searchText);
            foreach (var group in parsed.PositiveGroups)
            {
                AddExpression(
                    expressions,
                    normalized,
                    label,
                    string.Join(' ', group.Terms.SelectMany(term => term.Words)));
            }
        }
        catch (SearchQueryException)
        {
            // O prompt já foi validado na cotação; uma versão legada inválida
            // simplesmente não participa do fallback documental.
        }
    }

    private static void AddPromptTerms(ICollection<string> terms, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return;
        }

        try
        {
            var parsed = SearchText.Parse(searchText);
            foreach (var term in parsed.PositiveGroups
                         .SelectMany(group => group.Terms)
                         .SelectMany(term => term.Words))
            {
                terms.Add(term);
            }
        }
        catch (SearchQueryException)
        {
            // Consultas antigas inválidas são ignoradas; os descritivos ainda
            // fornecem termos básicos para a busca.
        }
    }

    private static void AddDescriptionTerms(ICollection<string> terms, string? description)
    {
        foreach (var term in FlexiblePhraseMatcher.Normalize(description ?? string.Empty)
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            terms.Add(term);
        }
    }

    private static void AddExpression(
        ICollection<EvidenceSearchExpression> expressions,
        ISet<string> normalized,
        string label,
        string? text)
    {
        var trimmed = text?.Trim();
        var key = FlexiblePhraseMatcher.Normalize(trimmed ?? string.Empty);
        if (key.Length == 0 || !normalized.Add(key))
        {
            return;
        }

        expressions.Add(new EvidenceSearchExpression(label, trimmed!));
    }

    private static bool IsUsefulBasicTerm(string term)
    {
        if (term.Length < 3 || term.All(char.IsDigit))
        {
            return false;
        }

        return !GenericEvidenceTerms.Contains(term);
    }

    private static readonly HashSet<string> GenericEvidenceTerms = new(StringComparer.Ordinal)
    {
        "a", "ao", "aos", "aquisicao", "as", "com", "conjunto", "da", "das", "de",
        "do", "dos", "e", "em", "item", "itens", "kit", "material", "materiais", "na",
        "nas", "no", "nos", "o", "os", "ou", "para", "por", "produto", "produtos",
        "sem", "tipo", "unidade", "unidades", "uso"
    };

    private sealed record EvidenceSearchExpression(string Label, string Text);

    private sealed record IndexedEvidenceDocument(
        CachedPdfDocument Pdf,
        DocumentTextIndex Index);

    private sealed record EvidencePageMatch(
        CachedPdfDocument Pdf,
        DocumentPageIndex Page,
        IReadOnlyList<TextOccurrence> Occurrences);
}
