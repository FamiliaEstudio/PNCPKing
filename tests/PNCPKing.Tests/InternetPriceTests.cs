using System.IO.Compression;
using ClosedXML.Excel;
using PdfSharp.Pdf.IO;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using SkiaSharp;

namespace PNCPKing.Tests;

public sealed class InternetPriceTests
{
    [Fact]
    public async Task InternetPrice_IsManualOnly_PersistsEvidenceAndExportsIncisoIii()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var quotations = new QuotationService(repository, new QuotationAnalyzer(new DateOnly(2026, 7, 25)));
        var store = new InternetEvidenceStore(database.Directory);
        var internet = new InternetPriceService(repository, quotations, store);
        var project = await quotations.CreateProjectAsync("Cesta mista");
        var lineId = Guid.NewGuid();
        await repository.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Café torrado", 10, "pacote", null, null),
            [
                Reference(lineId, "pncp-1", 90),
                Reference(lineId, "pncp-2", 100),
                Reference(lineId, "pncp-3", 110)
            ]);
        var initial = Assert.Single(await quotations.GetAnalysesAsync(project.Id));
        var automatic = Assert.Single(initial.Baskets, basket => !basket.IsManual);
        var manual = await quotations.CreateManualCopyAsync(initial, automatic);
        var image = CreatePng(SKColors.CornflowerBlue);
        var priceImage = await store.SavePngAsync(image, 640, 360);
        var samePriceImage = await store.SavePngAsync(image, 640, 360);
        var taxImage = await store.SavePngAsync(CreatePng(SKColors.Beige), 640, 360);
        Assert.Equal(priceImage.Sha256, samePriceImage.Sha256);
        Assert.Equal(2, Directory.EnumerateFiles(store.RootPath, "*.png").Count());

        var now = DateTimeOffset.UtcNow;
        var draft = new InternetPriceDraft
        {
            Id = Guid.NewGuid(),
            LineId = lineId,
            BasketId = manual.Id,
            SourceUrl = "https://loja.exemplo.test/produto/cafe",
            UnitPrice = 105m,
            Description = "Café torrado pacote 500 g",
            SupplierName = "Loja Exemplo",
            SupplierTaxId = "11222333000181",
            PriceImage = priceImage,
            TaxIdImage = taxImage,
            CapturedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        await internet.SaveDraftAsync(draft);
        Assert.True(Assert.Single(await internet.GetDraftsAsync(lineId)).IsComplete);
        var completed = await internet.CompleteDraftAsync(
            project.Id,
            draft,
            manual.Id,
            manual.Name);

        Assert.Equal(QuotationReferenceSource.InternetIncisoIII, completed.Reference.Source);
        Assert.Empty(await internet.GetDraftsAsync(lineId));
        var analysis = completed.Analysis;
        Assert.All(
            analysis.Baskets.Where(basket => !basket.IsManual).SelectMany(basket => basket.References),
            reference => Assert.Equal(QuotationReferenceSource.PncpIncisoII, reference.Source));
        var mixed = analysis.Baskets.Single(basket => basket.ManualBasketId == manual.Id);
        Assert.Equal(4, mixed.References.Count);
        Assert.Equal(QuotationBasketVisualState.ManualRegular, mixed.VisualState);
        await quotations.ConfirmBasketAsync(analysis, mixed.Key);

        var report = await quotations.GetReportAsync(project.Id);
        var workbookPath = Path.Combine(database.Directory, "inciso-iii.xlsx");
        await new QuotationWorkbookService().ExportAsync(
            workbookPath,
            report,
            "Maria de Souza");
        using (var workbook = new XLWorkbook(workbookPath))
        {
            var sheet = Assert.Single(workbook.Worksheets);
            var internetRow = sheet.Column(2).CellsUsed()
                .Single(cell => cell.GetString() == "Loja Exemplo").Address.RowNumber;
            Assert.Equal("11.222.333/0001-81", sheet.Cell(internetRow, 3).GetString());
            Assert.Equal(draft.SourceUrl, sheet.Cell(internetRow, 4).GetString());
            Assert.Equal(105m, sheet.Cell(internetRow, 5).GetValue<decimal>());
            Assert.False(sheet.Hyperlinks.TryGet(sheet.Cell(internetRow, 4).Address, out _));
        }

        var evidencePath = Path.Combine(database.Directory, "evidencias.pdf");
        var evidenceExporter = new QuotationEvidenceExportService(
            new UnusedDocumentService(),
            new UnusedIndexService(),
            new UnusedRasterizer(),
            repository,
            store);
        var evidenceResult = await evidenceExporter.ExportAsync(evidencePath, report);
        Assert.Equal(2, evidenceResult.Occurrences);
        using var pdf = PdfReader.Open(evidencePath, PdfDocumentOpenMode.Import);
        Assert.Equal(
            6,
            pdf.PageCount); // identificação da parte + 3 diagnósticos PNCP + 2 prints web
    }

    [Fact]
    public async Task Backup_CarriesReferencedPrintsAndRestoresTheirHashes()
    {
        await using var database = await TestDatabase.CreateAsync();
        var quotationRepository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var quotations = new QuotationService(quotationRepository, new QuotationAnalyzer());
        var store = new InternetEvidenceStore(database.Directory);
        var internet = new InternetPriceService(quotationRepository, quotations, store);
        var project = await quotations.CreateProjectAsync("Backup web");
        var lineId = Guid.NewGuid();
        await quotationRepository.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Tesoura", 1, "unidade", null, null),
            [Reference(lineId, "pncp", 30, "Tesoura")]);
        var first = Assert.Single(await quotations.GetAnalysesAsync(project.Id));
        var image1 = await store.SavePngAsync(CreatePng(SKColors.Green), 640, 360);
        var image2 = await store.SavePngAsync(CreatePng(SKColors.Gold), 640, 360);
        var now = DateTimeOffset.UtcNow;
        var draft = new InternetPriceDraft
        {
            Id = Guid.NewGuid(),
            LineId = lineId,
            SourceUrl = "https://exemplo.test/tesoura",
            UnitPrice = 31,
            Description = "Tesoura",
            SupplierName = "Fornecedor Web",
            SupplierTaxId = "11222333000181",
            PriceImage = image1,
            TaxIdImage = image2,
            CapturedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        await internet.CompleteDraftAsync(project.Id, draft, null, "Manual 1");

        var backupPath = Path.Combine(database.Directory, "web.pncpking");
        var backup = new BackupService(database.Repository);
        await backup.ExportAsync(backupPath);
        using (var archive = ZipFile.OpenRead(backupPath))
        {
            Assert.NotNull(archive.GetEntry($"internet-evidence/{image1.Sha256}.png"));
            Assert.NotNull(archive.GetEntry($"internet-evidence/{image2.Sha256}.png"));
        }

        File.Delete(Path.Combine(database.Directory, image1.RelativePath));
        File.Delete(Path.Combine(database.Directory, image2.RelativePath));
        Assert.False(await store.VerifyAsync(image1));
        await backup.ImportAsync(backupPath);
        Assert.True(await store.VerifyAsync(image1));
        Assert.True(await store.VerifyAsync(image2));
        var restoredEvidence = await new SqliteQuotationRepository(database.Repository.DatabasePath)
            .GetInternetPriceEvidenceAsync(lineId);
        Assert.Single(restoredEvidence);
    }

    [Fact]
    public async Task EvidenceExport_WritesDiagnosticWhenMandatoryInternetPrintWasAltered()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var quotations = new QuotationService(repository, new QuotationAnalyzer());
        var store = new InternetEvidenceStore(database.Directory);
        var internet = new InternetPriceService(repository, quotations, store);
        var project = await quotations.CreateProjectAsync("Evidência inválida");
        var lineId = Guid.NewGuid();
        await repository.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Tesoura", 1, "unidade", null, null),
            [Reference(lineId, "pncp", 30, "Tesoura")]);
        var png = await store.SavePngAsync(CreatePng(SKColors.Red), 640, 360);
        var png2 = await store.SavePngAsync(CreatePng(SKColors.Blue), 640, 360);
        var now = DateTimeOffset.UtcNow;
        var completed = await internet.CompleteDraftAsync(
            project.Id,
            new InternetPriceDraft
            {
                Id = Guid.NewGuid(),
                LineId = lineId,
                SourceUrl = "https://exemplo.test/tesoura",
                UnitPrice = 30,
                Description = "Tesoura",
                SupplierName = "Fornecedor Web",
                SupplierTaxId = "11222333000181",
                PriceImage = png,
                TaxIdImage = png2,
                CapturedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            },
            null,
            "Manual 1");
        await quotations.ConfirmBasketAsync(
            completed.Analysis,
            completed.Analysis.Baskets.Single(basket => basket.IsManual).Key);
        await File.WriteAllTextAsync(Path.Combine(database.Directory, png.RelativePath), "alterada");

        var exporter = new QuotationEvidenceExportService(
            new UnusedDocumentService(),
            new UnusedIndexService(),
            new UnusedRasterizer(),
            repository,
            store);
        var report = await quotations.GetReportAsync(project.Id);
        var destination = Path.Combine(database.Directory, "diagnostico.pdf");
        var result = await exporter.ExportAsync(destination, report);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("ausente ou alterado", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(destination));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(destination);
        Assert.Equal(2, pdf.NumberOfPages);
    }

    private static QuotationReference Reference(
        Guid lineId,
        string id,
        decimal price,
        string description = "Café torrado") => new()
    {
        Id = id,
        LineId = lineId,
        ContractId = id,
        ItemNumber = 1,
        ResultSequence = 1,
        SupplierName = $"Fornecedor {id}",
        SupplierTaxId = "11222333000181",
        UnitPrice = price,
        ItemDescription = description,
        ItemUnit = "pacote",
        Municipality = "Ribeirão Preto",
        Uf = "SP",
        PortalUrl = "https://pncp.gov.br/app/editais/teste/2026/1"
    };

    private static byte[] CreatePng(SKColor color)
    {
        using var bitmap = new SKBitmap(640, 360);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class UnusedDocumentService : IContractDocumentService
    {
        public Task<DocumentBundleResult> PrepareAsync(
            PncpContractKey contract,
            IProgress<DocumentProcessingProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("O fluxo web não deve consultar documentos PNCP.");

        public Task<DocumentBundleResult> CreateConsolidatedPdfAsync(
            PncpContractKey contract,
            string destinationPath,
            IProgress<DocumentProcessingProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Não utilizado.");

        public Task<long> ClearCacheAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);
    }

    private sealed class UnusedIndexService : IPdfTextIndexService
    {
        public Task<DocumentTextIndex> BuildAsync(
            CachedPdfDocument pdf,
            IProgress<DocumentProcessingProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Não utilizado.");
    }

    private sealed class UnusedRasterizer : IPdfPageRasterizer
    {
        public Task<RenderedPdfPage> RenderAsync(
            string pdfPath,
            int pageNumber,
            int dpi = 300,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Não utilizado.");
    }
}
