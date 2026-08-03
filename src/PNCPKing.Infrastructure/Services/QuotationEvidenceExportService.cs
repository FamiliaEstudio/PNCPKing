using System.Globalization;
using PdfSharp.Pdf.IO;
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
        var temporaryRoot = Path.Combine(
            Path.GetDirectoryName(fullDestination)!,
            $".pncpking-evidencias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var warnings = new List<string>();
        var bundles = new Dictionary<string, DocumentBundleResult>(StringComparer.Ordinal);
        var renderCache = new Dictionary<(string Hash, int Page), RenderedPdfPage>();
        var itemCount = 0;
        var referenceCount = 0;
        var occurrenceCount = 0;
        var units = new List<EvidenceUnit>();
        try
        {
            var internetEvidence = await LoadAndValidateInternetEvidenceAsync(report, warnings, cancellationToken)
                .ConfigureAwait(false);
            var analyses = report.Lines.OrderBy(line => line.Line.DisplayOrder).ToArray();
            var totalReferences = analyses.Sum(item => SelectExportedReferences(item).Count);
            for (var itemIndex = 0; itemIndex < analyses.Length; itemIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var analysis = analyses[itemIndex];
                itemCount++;
                var references = SelectExportedReferences(analysis);
                var itemTitle = BuildItemTitle(itemIndex + 1, analysis.Line);
                if (references.Count == 0)
                {
                    var unitPath = GetUnitPath(temporaryRoot, units.Count);
                    using var emptyWriter = new EvidencePdfWriter();
                    emptyWriter.AddTextPage(
                        itemTitle,
                        ["Nenhum preço foi exportado; não há documentos para pesquisar."]);
                    emptyWriter.Save(unitPath);
                    units.Add(new EvidenceUnit(unitPath, emptyWriter.PageCount));
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
                        totalReferences,
                        $"Item {itemIndex + 1:N0}, preço {referenceIndex + 1:N0}: pesquisando documentos…"));
                    var heading = $"{itemTitle} · Preço {referenceIndex + 1:N0}";
                    var lines = BuildReferenceLines(reference);
                    var referenceNotes = new List<string>();
                    var unitPath = GetUnitPath(temporaryRoot, units.Count);
                    using var unitWriter = new EvidencePdfWriter();
                    if (reference.Source == QuotationReferenceSource.InternetIncisoIII)
                    {
                        if (!internetEvidence.TryGetValue((reference.LineId, reference.Id), out var evidence))
                        {
                            var message = "Os dois prints obrigatórios estão ausentes ou foram alterados; " +
                                          "recapture a evidência do preço e do CNPJ.";
                            unitWriter.AddTextPage(heading, lines.Append(message).ToArray(), reference.PortalUrl);
                            unitWriter.Save(unitPath);
                            units.Add(new EvidenceUnit(unitPath, unitWriter.PageCount));
                            continue;
                        }
                        var priceImage = await _internetEvidence!.ReadVerifiedAsync(
                            evidence.PriceImage,
                            cancellationToken).ConfigureAwait(false);
                        var taxIdImage = await _internetEvidence.ReadVerifiedAsync(
                            evidence.TaxIdImage,
                            cancellationToken).ConfigureAwait(false);
                        var imageLines = lines.Concat(
                        [
                            "Fonte legal: INCISO III",
                            $"Capturado em: {evidence.CapturedAt.LocalDateTime:dd/MM/yyyy HH:mm}",
                            "Os dois prints foram fornecidos pelo usuário e validados por SHA-256."
                        ]).ToArray();
                        unitWriter.AddImageEvidencePage(
                            heading,
                            imageLines,
                            reference.PortalUrl,
                            priceImage,
                            "Evidência 1 de 2 — print do preço");
                        unitWriter.AddImageEvidencePage(
                            heading,
                            imageLines,
                            reference.PortalUrl,
                            taxIdImage,
                            "Evidência 2 de 2 — print do CNPJ");
                        occurrenceCount += 2;
                        unitWriter.Save(unitPath);
                        units.Add(new EvidenceUnit(unitPath, unitWriter.PageCount));
                        continue;
                    }

                    if (!PncpContractKey.TryParse(reference.ContractId, reference.PortalUrl, out var contract) ||
                        contract is null)
                    {
                        var warning = $"{reference.ContractId}: identificador da contratação inválido.";
                        warnings.Add(warning);
                        unitWriter.AddTextPage(heading, lines.Append(warning).ToArray(), reference.PortalUrl);
                        unitWriter.Save(unitPath);
                        units.Add(new EvidenceUnit(unitPath, unitWriter.PageCount));
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
                            unitWriter.AddTextPage(heading, lines.Append(warning).ToArray(), reference.PortalUrl);
                            unitWriter.Save(unitPath);
                            units.Add(new EvidenceUnit(unitPath, unitWriter.PageCount));
                            continue;
                        }
                    }

                    referenceNotes.AddRange(
                        bundle.Warnings.Select(warning => $"{contract.PncpId}: {warning}"));
                    var referenceOccurrences = 0;
                    var indexedDocuments = new List<IndexedEvidenceDocument>();
                    for (var documentOrder = 0; documentOrder < bundle.Pdfs.Count; documentOrder++)
                    {
                        var pdf = bundle.Pdfs[documentOrder];
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

                        indexedDocuments.Add(new IndexedEvidenceDocument(pdf, index, documentOrder));
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
                                FlexiblePhraseMatcher.Find(expression.Text, page),
                                document.DocumentOrder)))
                            .Where(match => match.Occurrences.Count > 0)
                            .GroupBy(
                                match => (match.Pdf.Sha256, match.Page.PageNumber),
                                match => match)
                            .Select(group =>
                            {
                                var first = group.First();
                                return first with
                                {
                                    Occurrences = group
                                        .SelectMany(match => match.Occurrences)
                                        .DistinctBy(occurrence =>
                                            $"{occurrence.PageNumber}:{string.Join(',', occurrence.WordIndexes)}")
                                        .ToArray()
                                };
                            })
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
                        var rankedPages = pageMatches
                            .OrderBy(match => match.Page.Source == DocumentTextSource.Native ? 0 : 1)
                            .ThenByDescending(match => match.Occurrences.Count)
                            .ThenBy(match => match.DocumentOrder)
                            .ThenBy(match => match.Page.PageNumber)
                            .ToArray();
                        var renderedSelections = new List<(EvidencePageMatch Match, RenderedPdfPage Rendered)>();
                        foreach (var pageMatch in rankedPages)
                        {
                            if (renderedSelections.Count == 2) break;
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
                                    renderCache[(pageMatch.Pdf.Sha256, pageMatch.Page.PageNumber)] = rendered;
                                }

                                renderedSelections.Add((pageMatch, rendered));
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

                        foreach (var (pageMatch, rendered) in renderedSelections)
                        {
                            var evidenceLines = lines.Concat(
                            [
                                $"Critério localizado: {matchedExpression.Label} — {matchedExpression.Text}",
                                $"Documento: {pageMatch.Pdf.DocumentTitle}" +
                                (pageMatch.Pdf.ArchivePath.Length == 0
                                    ? string.Empty
                                    : $" · {pageMatch.Pdf.ArchivePath}"),
                                $"Página original: {pageMatch.Page.PageNumber:N0} · leitura: " +
                                (pageMatch.Page.Source == DocumentTextSource.Native ? "texto nativo" : "OCR"),
                                $"Ocorrências destacadas nesta página: {pageMatch.Occurrences.Count:N0}",
                                $"Páginas incluídas: {renderedSelections.Count:N0} de {rankedPages.Length:N0} páginas localizadas."
                            ]).Concat(referenceNotes.Distinct(StringComparer.Ordinal).Take(1).Select(note => $"Aviso: {note}"))
                            .ToArray();
                            unitWriter.AddOccurrencePage(
                                heading,
                                evidenceLines,
                                reference.PortalUrl,
                                rendered,
                                pageMatch.Page,
                                pageMatch.Occurrences);
                            referenceOccurrences += pageMatch.Occurrences.Count;
                            occurrenceCount += pageMatch.Occurrences.Count;
                        }
                    }

                    if (referenceOccurrences == 0)
                    {
                        var message = bundle.Pdfs.Count == 0
                            ? "Nenhum PDF processável foi encontrado nesta contratação."
                            : "Nenhuma ocorrência foi localizada, mesmo após a busca básica por identidade. " +
                              $"Critérios tentados: {string.Join("; ", searchExpressions.Select(
                                  expression => $"{expression.Label} = {expression.Text}"))}.";
                        unitWriter.AddTextPage(
                            heading,
                            lines.Concat(referenceNotes.Distinct(StringComparer.Ordinal)).Append(message).ToArray(),
                            reference.PortalUrl);
                    }

                    unitWriter.Save(unitPath);
                    if (unitWriter.PageCount is < 1 or > 2)
                    {
                        throw new InvalidDataException(
                            $"O preço {referenceIndex + 1:N0} do item {itemIndex + 1:N0} gerou " +
                            $"{unitWriter.PageCount:N0} páginas; o limite é duas.");
                    }

                    units.Add(new EvidenceUnit(unitPath, unitWriter.PageCount));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DocumentProcessingProgress(
                DocumentProcessingStage.WritingReport,
                occurrenceCount,
                occurrenceCount,
                "Validando e publicando as partes do relatório de evidências…"));
            var partitions = PartitionUnits(units);
            var reportPaths = partitions.Count == 1
                ? new[] { fullDestination }
                : Enumerable.Range(1, partitions.Count)
                    .Select(index => GetPartPath(fullDestination, index))
                    .ToArray();
            var generatedAt = DateTime.Now;
            var temporaryParts = new List<string>(partitions.Count);
            for (var partIndex = 0; partIndex < partitions.Count; partIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partPath = Path.Combine(temporaryRoot, $"relatorio-{partIndex + 1:D3}.pdf");
                using var partWriter = new EvidencePdfWriter();
                partWriter.AddTextPage(
                    "Relatório de evidências documentais",
                    [
                        $"Projeto: {report.Project.Name}",
                        $"Gerado em: {generatedAt:dd/MM/yyyy HH:mm}",
                        $"Parte {partIndex + 1:N0} de {partitions.Count:N0}.",
                        "Cada preço possui no máximo duas páginas distintas de evidência; texto nativo, " +
                        "quantidade de correspondências e ordem documental definem a prioridade.",
                        "Cada arquivo contém no máximo 49 páginas e as páginas de um preço nunca são separadas."
                    ]);
                foreach (var unit in partitions[partIndex])
                {
                    partWriter.AppendPdf(unit.Path);
                }

                if (partWriter.PageCount is < 1 or > 49)
                {
                    throw new InvalidDataException(
                        $"A parte {partIndex + 1:N0} teria {partWriter.PageCount:N0} páginas.");
                }

                partWriter.Save(partPath);
                using var validation = PdfReader.Open(partPath, PdfDocumentOpenMode.Import);
                if (validation.PageCount != partWriter.PageCount || new FileInfo(partPath).Length == 0)
                {
                    throw new InvalidDataException($"A parte {partIndex + 1:N0} não passou na validação.");
                }

                temporaryParts.Add(partPath);
            }

            PublishParts(fullDestination, reportPaths, temporaryParts, temporaryRoot);
            return new QuotationEvidenceResult(
                reportPaths[0],
                itemCount,
                referenceCount,
                occurrenceCount,
                warnings.Distinct(StringComparer.Ordinal).ToArray())
            {
                ReportPaths = reportPaths
            };
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static string BuildItemTitle(int itemNumber, QuotationLine line)
    {
        var selection = line.CatalogSelection;
        var suffix = selection is null ? string.Empty : $" ({selection.Label})";
        return $"Item {itemNumber:N0} — {line.EffectiveDisplayName}{suffix}";
    }

    private static string GetUnitPath(string temporaryRoot, int index) =>
        Path.Combine(temporaryRoot, $"unidade-{index + 1:D6}.pdf");

    private static List<List<EvidenceUnit>> PartitionUnits(IReadOnlyList<EvidenceUnit> units)
    {
        var partitions = new List<List<EvidenceUnit>>();
        var current = new List<EvidenceUnit>();
        var pages = 1;
        foreach (var unit in units)
        {
            if (unit.PageCount > 48)
            {
                throw new InvalidDataException("Uma unidade de preço excede o limite útil de 48 páginas.");
            }

            if (current.Count > 0 && pages + unit.PageCount > 49)
            {
                partitions.Add(current);
                current = [];
                pages = 1;
            }

            current.Add(unit);
            pages += unit.PageCount;
        }

        if (current.Count > 0 || partitions.Count == 0) partitions.Add(current);
        return partitions;
    }

    private static string GetPartPath(string destinationPath, int part)
    {
        var directory = Path.GetDirectoryName(destinationPath)!;
        var extension = Path.GetExtension(destinationPath);
        var name = Path.GetFileNameWithoutExtension(destinationPath);
        return Path.Combine(directory, $"{name}_parte-{part:D3}{extension}");
    }

    private static void PublishParts(
        string singlePath,
        IReadOnlyList<string> reportPaths,
        IReadOnlyList<string> temporaryParts,
        string temporaryRoot)
    {
        var directory = Path.GetDirectoryName(singlePath)!;
        var name = Path.GetFileNameWithoutExtension(singlePath);
        var extension = Path.GetExtension(singlePath);
        var oldPaths = Directory.GetFiles(directory, $"{name}_parte-*{extension}")
            .Append(singlePath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var backups = new List<(string Original, string Backup)>();
        var published = new List<string>();
        try
        {
            for (var index = 0; index < oldPaths.Length; index++)
            {
                var backup = Path.Combine(temporaryRoot, $"anterior-{index + 1:D3}.pdf");
                File.Move(oldPaths[index], backup);
                backups.Add((oldPaths[index], backup));
            }

            for (var index = 0; index < temporaryParts.Count; index++)
            {
                File.Move(temporaryParts[index], reportPaths[index]);
                published.Add(reportPaths[index]);
            }
        }
        catch
        {
            foreach (var path in published.Where(File.Exists)) File.Delete(path);
            foreach (var (original, backup) in backups.Where(value => File.Exists(value.Backup)))
            {
                File.Move(backup, original);
            }

            throw;
        }
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
            ICollection<string> warnings,
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
            foreach (var failure in failures)
            {
                warnings.Add($"Inciso III: {failure}.");
            }
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
        DocumentTextIndex Index,
        int DocumentOrder);

    private sealed record EvidencePageMatch(
        CachedPdfDocument Pdf,
        DocumentPageIndex Page,
        IReadOnlyList<TextOccurrence> Occurrences,
        int DocumentOrder);

    private sealed record EvidenceUnit(string Path, int PageCount);
}
