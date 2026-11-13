using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using PdfSharp.Pdf.IO;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationEvidenceExportService : IQuotationEvidenceExportService
{
    public const string DiagnosticKey = "PNCPKing.EvidenceExportDiagnostic";
    internal const string ImageUnavailableMessage = "Não foi possível obter a imagem pelo arquivo.";
    private readonly IContractDocumentService _documents;
    private readonly IPdfTextIndexService _indexes;
    private readonly IPdfPageRasterizer _rasterizer;
    private readonly IQuotationRepository? _quotations;
    private readonly IInternetEvidenceStore? _internetEvidence;
    private readonly Action<string, Exception?>? _diagnostic;

    public QuotationEvidenceExportService(
        IContractDocumentService documents,
        IPdfTextIndexService indexes,
        IPdfPageRasterizer rasterizer,
        IQuotationRepository? quotations = null,
        IInternetEvidenceStore? internetEvidence = null,
        Action<string, Exception?>? diagnostic = null)
    {
        _documents = documents;
        _indexes = indexes;
        _rasterizer = rasterizer;
        _quotations = quotations;
        _internetEvidence = internetEvidence;
        _diagnostic = diagnostic;
    }

    public async Task<QuotationEvidenceResult> ExportAsync(
        string destinationPath,
        QuotationProjectReport report,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(report);
        report.ValidateExport();
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporaryRoot = Path.Combine(
            Path.GetDirectoryName(fullDestination)!,
            $".pncpking-evidencias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var warnings = new List<string>();
        var bundles = new Dictionary<string, DocumentBundleResult>(StringComparer.Ordinal);
        var renderCache = new EvidenceRenderCache();
        var exportId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        var completed = false;
        var itemCount = 0;
        var referenceCount = 0;
        var occurrenceCount = 0;
        var units = new List<EvidenceUnit>();
        try
        {
            var internetEvidence = await LoadAndValidateInternetEvidenceAsync(report, warnings, cancellationToken)
                .ConfigureAwait(false);
            var analyses = report.Lines.ToArray();
            var totalReferences = analyses.Sum(item => SelectExportedReferences(item).Count);
            for (var itemIndex = 0; itemIndex < analyses.Length; itemIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var analysis = analyses[itemIndex];
                itemCount++;
                var references = SelectExportedReferences(analysis);
                var itemTitle = BuildItemTitle(report.ItemNumbers(itemIndex), analysis.Line);
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
                        $"Item {report.ItemNumbers(itemIndex)}, preço {referenceIndex + 1:N0}: pesquisando documentos…"));
                    var heading = $"{itemTitle} · Preço {referenceIndex + 1:N0}";
                    var lines = BuildReferenceLines(reference);
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
                        var imageLines = lines.Concat(
                        [
                            "Fonte legal: INCISO III",
                            $"Capturado em: {evidence.CapturedAt.LocalDateTime:dd/MM/yyyy HH:mm}",
                            "Os dois prints foram fornecidos pelo usuário e validados por SHA-256."
                        ]).ToArray();
                        var images = new[]
                        {
                            (Image: evidence.PriceImage, Caption: "Evidência 1 de 2 — print do preço"),
                            (Image: evidence.TaxIdImage, Caption: "Evidência 2 de 2 — print do CNPJ")
                        };
                        foreach (var (image, caption) in images)
                        {
                            var stage = "ler_print";
                            try
                            {
                                var png = await _internetEvidence!.ReadVerifiedAsync(image, cancellationToken)
                                    .ConfigureAwait(false);
                                stage = "inserir_print";
                                unitWriter.AddImageEvidencePage(
                                    heading, imageLines, reference.PortalUrl, png, caption);
                                occurrenceCount++;
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                RecordFailure(exception, stage, exportId, report, report.ItemNumbers(itemIndex),
                                    referenceIndex + 1, reference, renderCache, image: image);
                                warnings.Add($"{heading}: {ImageUnavailableMessage}");
                                unitWriter.AddTextPage(heading,
                                    lines.Concat([caption, ImageUnavailableMessage]).ToArray(), reference.PortalUrl);
                            }
                        }
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
                            RecordFailure(exception, "obter_documentos", exportId, report,
                                report.ItemNumbers(itemIndex), referenceIndex + 1, reference, renderCache);
                            warnings.Add($"{heading}: {ImageUnavailableMessage}");
                            unitWriter.AddTextPage(heading, lines.Append(ImageUnavailableMessage).ToArray(),
                                reference.PortalUrl);
                            unitWriter.Save(unitPath);
                            units.Add(new EvidenceUnit(unitPath, unitWriter.PageCount));
                            continue;
                        }
                    }

                    var referenceOccurrences = 0;
                    var imageFailed = false;
                    var failureNoted = false;
                    // Reuse indexes while trying fallback expressions, but open later documents only if needed.
                    var indexedDocuments = new Dictionary<string, DocumentTextIndex?>(StringComparer.Ordinal);
                    var attemptedPages = new HashSet<(string Hash, int Page)>();
                    foreach (var expression in BuildSearchExpressions(analysis, reference))
                    {
                        var expressionMatched = false;
                        for (var documentOrder = 0; documentOrder < bundle.Pdfs.Count; documentOrder++)
                        {
                            if (unitWriter.PageCount == 2) break;
                            cancellationToken.ThrowIfCancellationRequested();
                            var pdf = bundle.Pdfs[documentOrder];
                            if (!indexedDocuments.TryGetValue(pdf.Sha256, out var index))
                            {
                                try
                                {
                                    index = await _indexes.BuildAsync(pdf, progress, cancellationToken)
                                        .ConfigureAwait(false);
                                    warnings.AddRange(index.Warnings.Select(warning =>
                                        $"{contract.PncpId}/{GetDocumentLabel(pdf)}: {warning}"));
                                }
                                catch (Exception exception) when (exception is not OperationCanceledException)
                                {
                                    imageFailed = true;
                                    RecordFailure(exception, "ler_pdf", exportId, report,
                                        report.ItemNumbers(itemIndex), referenceIndex + 1, reference, renderCache, pdf);
                                    warnings.Add($"{heading}: {ImageUnavailableMessage}");
                                    index = null;
                                }
                                indexedDocuments.Add(pdf.Sha256, index);
                            }
                            if (index is null) continue;

                            var rankedPages = index.Pages
                                .Select(page => new EvidencePageMatch(pdf, page,
                                    FlexiblePhraseMatcher.Find(expression.Text, page), documentOrder))
                                .Where(match => match.Occurrences.Count > 0)
                                .OrderBy(match => match.Page.Source == DocumentTextSource.Native ? 0 : 1)
                                .ThenByDescending(match => match.Occurrences.Count)
                                .ThenBy(match => match.Page.PageNumber);
                            foreach (var pageMatch in rankedPages)
                            {
                                if (unitWriter.PageCount == 2) break;
                                expressionMatched = true;
                                var key = (pdf.Sha256, pageMatch.Page.PageNumber);
                                if (!attemptedPages.Add(key)) continue;
                                cancellationToken.ThrowIfCancellationRequested();
                                RenderedPdfPage? rendered = null;
                                var stage = "renderizar_pagina";
                                var cacheHit = false;
                                try
                                {
                                    cacheHit = renderCache.TryGetValue(key, out rendered);
                                    if (!cacheHit)
                                    {
                                        rendered = await _rasterizer.RenderAsync(pdf.LocalPath,
                                            pageMatch.Page.PageNumber, cancellationToken: cancellationToken)
                                            .ConfigureAwait(false);
                                    }
                                    var evidenceLines = lines.Concat(
                                    [
                                        $"Documento: {pdf.DocumentTitle}" +
                                        (pdf.ArchivePath.Length == 0 ? string.Empty : $" · {pdf.ArchivePath}"),
                                        $"Página original: {pageMatch.Page.PageNumber:N0} · leitura: " +
                                        (pageMatch.Page.Source == DocumentTextSource.Native ? "texto nativo" : "OCR")
                                    ]);
                                    if (imageFailed) evidenceLines = evidenceLines.Append(ImageUnavailableMessage);
                                    stage = "inserir_imagem";
                                    unitWriter.AddDocumentPage(heading, evidenceLines.ToArray(),
                                        reference.PortalUrl, rendered!);
                                    if (!cacheHit) renderCache.Add(key, rendered!);
                                    failureNoted |= imageFailed;
                                    referenceOccurrences += pageMatch.Occurrences.Count;
                                    occurrenceCount += pageMatch.Occurrences.Count;
                                }
                                catch (Exception exception) when (exception is not OperationCanceledException)
                                {
                                    imageFailed = true;
                                    renderCache.Remove(key);
                                    RecordFailure(exception, stage, exportId, report, report.ItemNumbers(itemIndex),
                                        referenceIndex + 1, reference, renderCache, pdf, pageMatch.Page.PageNumber,
                                        rendered, cacheHit);
                                    warnings.Add($"{heading}: {ImageUnavailableMessage}");
                                }
                            }
                        }
                        // Keep the existing priority of exact descriptions over broader fallback terms.
                        if (expressionMatched) break;
                    }

                    if (imageFailed && !failureNoted && unitWriter.PageCount < 2)
                    {
                        unitWriter.AddTextPage(heading, lines.Append(ImageUnavailableMessage).ToArray(),
                            reference.PortalUrl);
                    }
                    else if (referenceOccurrences == 0)
                    {
                        var message = bundle.Pdfs.Count == 0
                            ? "Nenhum PDF processável foi encontrado nesta contratação."
                            : "Nenhuma página relevante foi localizada nos documentos da contratação.";
                        unitWriter.AddTextPage(heading, lines.Append(message).ToArray(), reference.PortalUrl);
                    }

                    unitWriter.Save(unitPath);
                    if (unitWriter.PageCount is < 1 or > 2)
                    {
                        throw new InvalidDataException(
                            $"O preço {referenceIndex + 1:N0} do item {report.ItemNumbers(itemIndex)} gerou " +
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
                        "Cada preço possui no máximo duas páginas distintas de evidência, " +
                        "selecionadas automaticamente pelo programa.",
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
            completed = true;
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
            WriteDiagnostic($"exportacao={exportId}; projeto={report.Project.Id}; concluida={completed}; " +
                $"duracao_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}; " +
                $"precos={referenceCount}; cache_limite_bytes={renderCache.MaximumBytes}; " +
                $"cache_pico_bytes={renderCache.PeakBytes}; cache_paginas={renderCache.Count}; " +
                $"cache_acertos={renderCache.Hits}; cache_faltas={renderCache.Misses}; " +
                $"cache_descartes={renderCache.Evictions}; cache_paginas_grandes={renderCache.OversizedPages}", null);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private void RecordFailure(
        Exception exception, string stage, Guid exportId, QuotationProjectReport report,
        string itemNumber, int priceNumber, QuotationReference reference, EvidenceRenderCache cache,
        CachedPdfDocument? pdf = null, int? pageNumber = null, RenderedPdfPage? rendered = null,
        bool cacheHit = false, EvidenceImageDescriptor? image = null)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var resources = new SystemResourceProbe().GetSnapshot();
            var detail = new StringBuilder()
                .AppendLine($"exportacao={exportId}; etapa={stage}")
                .AppendLine($"projeto={report.Project.Id}; nome={report.Project.Name}; item={itemNumber}; preco={priceNumber}")
                .AppendLine($"linha={reference.LineId}; referencia={reference.Id}; contratacao={reference.ContractId}; url={reference.PortalUrl}")
                .AppendLine($"arquivo={pdf?.LocalPath ?? image?.RelativePath}; titulo={pdf?.DocumentTitle}; arquivo_interno={pdf?.ArchivePath}; sha256={pdf?.Sha256 ?? image?.Sha256}; pagina={pageNumber}")
                .AppendLine($"png_bytes={rendered?.PngBytes.LongLength ?? image?.ByteLength}; pixels={rendered?.PixelWidth ?? image?.PixelWidth}x{rendered?.PixelHeight ?? image?.PixelHeight}; pdf_pontos={rendered?.PdfWidth}x{rendered?.PdfHeight}; dpi=300")
                .AppendLine($"cache_acerto={cacheHit}; cache_bytes={cache.Bytes}; cache_pico_bytes={cache.PeakBytes}; cache_limite_bytes={cache.MaximumBytes}")
                .AppendLine($"memoria_privada={process.PrivateMemorySize64}; working_set={process.WorkingSet64}; heap_gc={GC.GetTotalMemory(false)}; limite_gc={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}")
                .AppendLine($"ram_total={resources.TotalPhysicalMemoryBytes}; ram_livre={resources.AvailablePhysicalMemoryBytes}; carga_memoria={resources.MemoryLoadPercent}")
                .AppendLine($"so={RuntimeInformation.OSDescription}; arquitetura={RuntimeInformation.ProcessArchitecture}; runtime={RuntimeInformation.FrameworkDescription}; skia={typeof(SkiaSharp.SKImage).Assembly.GetName().Version}")
                .AppendLine($"cpu_sse2={Sse2.IsSupported}; cpu_sse41={Sse41.IsSupported}; cpu_sse42={Sse42.IsSupported}; cpu_avx={Avx.IsSupported}; cpu_avx2={Avx2.IsSupported}");
            for (var cause = exception; cause is not null; cause = cause.InnerException)
                detail.AppendLine($"excecao={cause.GetType().FullName}; hresult=0x{cause.HResult:X8}; mensagem={cause.Message}");
            exception.Data[DiagnosticKey] = detail.ToString();
            WriteDiagnostic($"Falha na evidência: exportacao={exportId}; etapa={stage}; item={itemNumber}; preco={priceNumber}.", exception);
        }
        catch
        {
            // Diagnostics must never turn a recoverable page failure into a failed export.
            WriteDiagnostic($"Falha na evidência: exportacao={exportId}; etapa={stage}; " +
                $"item={itemNumber}; preco={priceNumber}; arquivo={pdf?.LocalPath ?? image?.RelativePath}; " +
                $"pagina={pageNumber}; referencia={reference.Id}; hresult=0x{exception.HResult:X8}.", exception);
        }
    }

    private void WriteDiagnostic(string message, Exception? exception)
    {
        try { _diagnostic?.Invoke(message, exception); }
        catch { /* A logging failure must not interrupt evidence publication. */ }
    }

    private static string BuildItemTitle(string itemNumber, QuotationLine line)
    {
        var selection = line.CatalogSelection;
        var suffix = selection is null ? string.Empty : $" ({selection.Label})";
        return $"Item {itemNumber} — {line.EffectiveDisplayName}{suffix}";
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
                "Data de homologação: não se aplica (preço de internet).",
                $"Descrição anunciada: {reference.ItemDescription}",
                $"Fonte: {reference.PortalUrl}"
            ]
            :
            [
                $"Empresa: {reference.SupplierName}",
                $"CNPJ/NI: {reference.SupplierTaxId}",
                $"Preço unitário homologado: {reference.UnitPrice.ToString("C4", CultureInfo.GetCultureInfo("pt-BR"))}",
                $"Data de homologação: {reference.ResultDate?.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")) ?? "não informada"}",
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

    private sealed record EvidencePageMatch(
        CachedPdfDocument Pdf,
        DocumentPageIndex Page,
        IReadOnlyList<TextOccurrence> Occurrences,
        int DocumentOrder);

    private sealed record EvidenceUnit(string Path, int PageCount);
}
