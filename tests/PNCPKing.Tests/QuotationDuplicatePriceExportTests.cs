using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using ClosedXML.Excel;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using SkiaSharp;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

public sealed class QuotationDuplicatePriceExportTests(ITestOutputHelper output)
{
    private static readonly QuotationLineInput Input = new("Produto sintético", 10, "un", null, null);
    private const string Url = "https://loja.exemplo.test/produto-cotado";
    private const string WebTax = "11222333000181";
    private const string WebName = "Loja sintética III";

    private static QuotationReference Price(Guid line, string id, int purchase, decimal price, int day) => new()
    {
        LineId = line, Id = id, ContractId = $"11222333000181-1-{purchase:D6}/2026", ItemNumber = day,
        ResultSequence = 1, ItemDescription = Input.Description, ItemUnit = Input.RequestedUnit,
        UnitPrice = price, SupplierName = $"Empresa {id}", SupplierTaxId = "08236940000196", SupplierType = "PJ",
        PortalUrl = $"https://pncp.gov.br/app/editais/11222333000181/2026/{purchase}", ResultDate = new(2026, 9, day)
    };

    // The PNCP feed has no documents in this synthetic scenario. The actual exports and web evidence use local files.
    private sealed class LocalDocuments : IContractDocumentService
    {
        public Task<DocumentBundleResult> PrepareAsync(PncpContractKey contract, IProgress<DocumentProcessingProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new DocumentBundleResult { Contract = contract, Pdfs = [] });
        public Task<DocumentBundleResult> CreateConsolidatedPdfAsync(PncpContractKey contract, string destinationPath,
            IProgress<DocumentProcessingProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ClearCacheAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
    }

    private static QuotationService Open(TestDatabase db) =>
        new(new SqliteQuotationRepository(db.Repository.DatabasePath), new QuotationAnalyzer());

    private async Task<QuotationProjectReport> Clean(TestDatabase db, QuotationProjectReport loaded,
        IReadOnlyList<QuotationDuplicatePrice> preview, bool accept)
    {
        var svc = Open(db);
        var displayed = loaded.Lines.Select(line => new QuotationLineDisplay(line, loaded.Project.Organization)).ToArray();
        var consent = preview.Where(row => accept && row.Impact.NeedsAcceptance).Select(row => (row.LineId, row.BasketKey)).ToHashSet();
        var changed = await svc.ApplyDuplicatePriceReviewAsync(loaded.Project.Id, preview, consent);
        Assert.Single(changed);
        var updated = await svc.GetCleanedItemsAsync(loaded.Project.Id, changed);
        return QuotationLineDisplay.ReplaceCleanedItems(loaded, displayed, updated).Report;
    }

    private async Task<string> ExportAndCheck(TestDatabase db, QuotationProjectReport cached, string label,
        decimal expectedValue, params (string Name, decimal Effective)[] expected)
    {
        var repository = new SqliteQuotationRepository(db.Repository.DatabasePath);
        // Excel and PDF request a new report. Word 9.1 receives the report held by the quotation screen.
        var fromDisk = await Open(db).GetReportAsync(cached.Project.Id);
        var prefix = Path.Combine(db.Directory, label);
        await new QuotationWorkbookService().ExportAsync(prefix + ".xlsx", fromDisk, "Responsável sintético");
        using (var workbook = new XLWorkbook(prefix + ".xlsx"))
        {
            var sheet = workbook.Worksheet(1);
            var refs = workbook.Worksheet("Referências");
            Assert.Equal(expected.Length, refs.Column(5).CellsUsed().Count() - 1);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.StartsWith(expected[i].Name, sheet.Cell(6 + i, 2).GetString());
                Assert.Equal(expected[i].Effective, sheet.Cell(6 + i, 6).GetValue<decimal>());
                Assert.Equal(expected[i].Name, refs.Cell(2 + i, 5).GetString());
                Assert.Equal(expected[i].Effective, refs.Cell(2 + i, 7).GetValue<decimal>());
            }
            Assert.Equal(expectedValue, refs.Cell("W2").GetValue<decimal>());
            Assert.Equal(expectedValue, sheet.Cell(6 + Math.Max(3, expected.Length), 3).GetValue<decimal>());
            if (expected.Length == 2) Assert.Equal("Preço 3 não obtido", sheet.Cell("B8").GetString());
        }
        await new QuotationWordService().ExportPriceTableAsync(prefix + ".docx", cached);
        using (var zip = ZipFile.OpenRead(prefix + ".docx"))
        using (var stream = zip.GetEntry("word/document.xml")!.Open())
        {
            var cells = XDocument.Load(stream).Descendants().Where(x => x.Name.LocalName == "tc")
                .Select(cell => string.Concat(cell.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value))
                    .Replace("\u00a0", "").Replace(" ", "")).ToArray();
            var culture = CultureInfo.GetCultureInfo("pt-BR");
            Assert.Contains("R$" + expectedValue.ToString("N2", culture), cells);
            Assert.Contains("R$" + (expectedValue * cached.Lines.Single().Line.RequestedQuantity).ToString("N2", culture), cells);
        }
        var evidence = new QuotationEvidenceExportService(new LocalDocuments(), null!, null!, repository, new InternetEvidenceStore(db.Directory));
        var result = await evidence.ExportAsync(prefix + ".pdf", fromDisk);
        Assert.Empty(result.Warnings);
        Assert.Equal(expected.Length, result.References);
        using (var pdf = PdfDocument.Open(Assert.Single(result.ReportPaths)))
        {
            // The PDF contains JPEG pages. Compare the printed page, including the raw price and ordinal,
            // against a page composed explicitly from the expected supplier's source record.
            var line = fromDisk.Lines.Single();
            var pages = pdf.GetPages().Skip(1).ToArray();
            Assert.Equal(expected.Length + expected.Count(e => e.Name == WebName), pages.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                var entry = expected[i];
                if (entry.Name == WebName) continue;
                var source = line.References.Where(r => r.SupplierName == entry.Name)
                    .OrderBy(r => Math.Abs(r.UnitPrice - entry.Effective)).First();
                var heading = $"Item 1 — {line.Line.EffectiveDisplayName} · Preço {i + 1}";
                var printed = source.UnitPrice.ToString("C4", CultureInfo.GetCultureInfo("pt-BR"));
                var sample = Path.Combine(db.Directory, "esperada.pdf");
                using (var writer = new EvidencePdfWriter())
                {
                    writer.AddTextPage(heading, [$"Empresa: {source.SupplierName}", $"CNPJ/NI: {source.SupplierTaxId}",
                        $"Preço unitário homologado: {printed}", $"Data de homologação: {source.ResultDate:dd/MM/yyyy}",
                        $"Contratação: {source.ContractId} · Item {source.ItemNumber:N0}", $"PNCP: {source.PortalUrl}",
                        "Nenhum PDF processável foi encontrado nesta contratação."], source.PortalUrl);
                    writer.Save(sample);
                }
                using var oracle = PdfDocument.Open(sample);
                Assert.Equal(Assert.Single(oracle.GetPage(1).GetImages()).RawBytes.ToArray(), Assert.Single(pages[i].GetImages()).RawBytes.ToArray());
                Assert.Contains(pages[i].GetHyperlinks(), link => link.Uri == source.PortalUrl);
            }
        }
        output.WriteLine($"{label}: Excel, PDF e Word 9.1 · {string.Join(", ", expected.Select(e => $"{e.Name} {e.Effective}"))} · adotado {expectedValue}");
        var evidenceDir = Environment.GetEnvironmentVariable("PNCPKING_CLEANUP_EVIDENCE_DIR");
        if (!string.IsNullOrEmpty(evidenceDir))
            foreach (var ext in new[] { ".xlsx", ".docx", ".pdf" }) File.Copy(prefix + ext, Path.Combine(evidenceDir, label + ext), true);
        return prefix;
    }

    [Fact]
    public async Task RegularManualExportsImmediatelyWithSavedOrderFactorDivisorAndMedian()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var svc = Open(db);
        var project = await svc.CreateProjectAsync("Manual regular");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input with { RequestedBasketSize = 4 },
            [Price(line, "z", 1, 12, 1), Price(line, "retirada", 1, 10, 2), Price(line, "m", 2, 22, 3), Price(line, "n", 3, 11, 4)]);
        var basket = await repo.SaveManualBasketAsync(line, null, "Manual", ["z", "retirada", "m", "n"]);
        await repo.SetManualBasketConversionFactorAsync(basket.Id, "z", 1.1m);
        await repo.SetManualBasketPriceDivisorAsync(basket.Id, "m", 2m);
        await repo.SetManualBasketAggregationMethodAsync(basket.Id, QuotationAggregationMethod.Median);
        await svc.ConfirmBasketAsync((await svc.GetAnalysisAsync(project.Id, line))!, basket.Key);
        var loaded = await svc.GetReportAsync(project.Id);
        var preview = await svc.FindDuplicatePricesAsync(project.Id);
        var row = Assert.Single(preview);
        Assert.Empty(await svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, null));
        Assert.Equal(4, (await repo.GetManualBasketsAsync(line)).Single().ReferenceIds.Count);
        Assert.Equal(("z", 1, 1.1m, "retirada", 2), (row.Kept.ReferenceId, row.Kept.Position, row.Kept.ConversionFactor, row.Removed.ReferenceId, row.Removed.Position));
        Assert.Equal((4, 3, 3, 11m, false), (row.Impact.Before, row.Impact.After, row.Impact.Minimum, row.Impact.AdoptedPrice, row.Impact.NeedsAcceptance));
        var report = await Clean(db, loaded, preview, false);
        var cleaned = report.Lines.Single();
        Assert.True(cleaned.Line.SelectionConfirmed);
        Assert.Equal(basket.Key, cleaned.SelectedBasket!.Key);
        Assert.Equal(QuotationBasketVisualState.ManualRegular, cleaned.SelectedBasket.VisualState);
        var saved = Assert.Single(await repo.GetManualBasketsAsync(line));
        Assert.Equal(2m, saved.PriceDivisors["m"]);
        Assert.Equal(1.1m, saved.ConversionFactors["z"]);
        Assert.Equal(QuotationAggregationMethod.Median, saved.AggregationMethod);
        await ExportAndCheck(db, report, "manual-4-3", 11m, ("Empresa m", 11), ("Empresa n", 11), ("Empresa z", 13.2m));
        Assert.Empty(await Open(db).FindDuplicatePricesAsync(project.Id));
    }

    private static byte[] Image(SKColor color)
    {
        using var bmp = new SKBitmap(104, 72);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(color);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task MixedBasketExportsTheRightImagesLinkAndSupplierImmediatelyAndAfterImport()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var svc = Open(db);
        var project = await svc.CreateProjectAsync("Mista");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input, [Price(line, "pncp", 1, 10, 1), Price(line, "retirada", 1, 10.40m, 2)]);
        var manual = await svc.CreateManualBasketAsync(line, "Manual mista", ["pncp", "retirada"]);
        var store = new InternetEvidenceStore(db.Directory);
        var price = await store.SavePngAsync(Image(SKColors.Blue), 104, 72);
        var tax = await store.SavePngAsync(Image(SKColors.Red), 104, 72);
        var internet = await new InternetPriceService(repo, svc, store).CompleteDraftAsync(project.Id, new()
        {
            Id = Guid.NewGuid(), LineId = line, SourceUrl = Url, Description = Input.Description, UnitPrice = 11.8m,
            SupplierName = WebName, SupplierTaxId = WebTax, CapturedAt = new(2026, 11, 9, 12, 15, 0, TimeSpan.Zero),
            PriceImage = price, TaxIdImage = tax
        }, manual.Id, manual.Name);
        await svc.ConfirmBasketAsync(internet.Analysis, manual.Key);
        var loaded = await svc.GetReportAsync(project.Id);
        var preview = await svc.FindDuplicatePricesAsync(project.Id);
        var impact = Assert.Single(preview).Impact;
        Assert.Equal((3, 2, 3, 10.90m, true), (impact.Before, impact.After, impact.Minimum, impact.AdoptedPrice, impact.NeedsAcceptance));
        Assert.Empty(await svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, new HashSet<(Guid, string)>()));
        Assert.Equal(3, (await repo.GetManualBasketsAsync(line)).Single().ReferenceIds.Count);
        var cleaned = await Clean(db, loaded, preview, true);
        async Task Check(TestDatabase target, QuotationProjectReport report, string label)
        {
            var prefix = await ExportAndCheck(target, report, label, 10.90m, ("Empresa pncp", 10), (WebName, 11.80m));
            using var wb = new XLWorkbook(prefix + ".xlsx");
            Assert.Equal("Inciso III", wb.Worksheet(1).Cell("D7").GetString());
            using var pdf = PdfDocument.Open(prefix + ".pdf");
            var pages = pdf.GetPages().Where(p => p.GetHyperlinks().Any(link => link.Uri == Url)).ToArray();
            Assert.Equal(2, pages.Length);
            var sample = Path.Combine(target.Directory, "esperada-web.pdf");
            using (var writer = new EvidencePdfWriter())
            {
                var captured = new DateTimeOffset(2026, 11, 9, 12, 15, 0, TimeSpan.Zero).LocalDateTime;
                string[] lines = [$"Empresa: {WebName}", $"CNPJ: {WebTax}",
                    $"Preço unitário informado: {11.8m.ToString("C4", CultureInfo.GetCultureInfo("pt-BR"))}",
                    "Data de homologação: não se aplica (preço de internet).", $"Descrição anunciada: {Input.Description}",
                    $"Fonte: {Url}", "Fonte legal: INCISO III", $"Capturado em: {captured:dd/MM/yyyy HH:mm}",
                    "Os dois prints foram fornecidos pelo usuário e validados por SHA-256."];
                writer.AddImageEvidencePage("Item 1 — Produto sintético · Preço 2", lines, Url, Image(SKColors.Blue), "Evidência 1 de 2 — print do preço");
                writer.AddImageEvidencePage("Item 1 — Produto sintético · Preço 2", lines, Url, Image(SKColors.Red), "Evidência 2 de 2 — print do CNPJ");
                writer.Save(sample);
            }
            using var oracle = PdfDocument.Open(sample);
            for (var i = 0; i < 2; i++)
                Assert.Equal(Assert.Single(oracle.GetPage(i + 1).GetImages()).RawBytes.ToArray(), Assert.Single(pages[i].GetImages()).RawBytes.ToArray());
            var disk = new SqliteQuotationRepository(target.Repository.DatabasePath);
            var original = (await disk.GetInternetPriceEvidenceAsync(line))[internet.Reference.Id];
            var destination = new InternetEvidenceStore(target.Directory);
            Assert.Equal(price.Sha256, original.PriceImage.Sha256);
            Assert.Equal(tax.Sha256, original.TaxIdImage.Sha256);
            Assert.True(await destination.VerifyAsync(original.PriceImage) && await destination.VerifyAsync(original.TaxIdImage));
        }
        await Check(db, cleaned, "mista-3-2");
        var file = Path.Combine(db.Directory, "mista.pncpcotacao");
        await new QuotationPackageService(db.Repository.DatabasePath, db.Directory).ExportAsync(file, project.Id);
        await using var target = await TestDatabase.CreateAsync();
        var imported = await new QuotationPackageService(target.Repository.DatabasePath, target.Directory).ImportAsync(file, QuotationPackageImportMode.PreserveIdentity);
        await Check(target, await Open(target).GetReportAsync(imported.ProjectId), "mista-reimportada");
    }

    [Fact]
    public async Task SavedOrganizationKeepsItsExistingExportGuardAndNeedsNoBasketReconfirmation()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var svc = Open(db);
        var project = await svc.CreateProjectAsync("Organização salva");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input,
            [Price(line, "A", 1, 10, 1), Price(line, "retirada", 1, 10.4m, 2), Price(line, "B", 2, 11, 3), Price(line, "C", 3, 12, 4)]);
        var manual = await repo.SaveManualBasketAsync(line, null, "Manual", ["A", "retirada", "B", "C"]);
        await svc.ConfirmBasketAsync((await svc.GetAnalysisAsync(project.Id, line))!, manual.Key);
        await svc.OrganizeProjectAsync(project.Id);
        var loaded = await svc.GetReportAsync(project.Id);
        Assert.False(loaded.OrganizationIsStale);
        var preview = await svc.FindDuplicatePricesAsync(project.Id);
        var updated = await Clean(db, loaded, preview, false);
        Assert.True(updated.Lines.Single().Line.SelectionConfirmed);
        Assert.True(updated.OrganizationIsStale);
        Assert.Contains("Organizar Itens", Assert.Throws<InvalidOperationException>(updated.ValidateExport).Message);
        await svc.OrganizeProjectAsync(project.Id);
        var organized = await Open(db).GetReportAsync(project.Id);
        Assert.False(organized.OrganizationIsStale);
        Assert.Equal(manual.Key, organized.Lines.Single().Line.SelectedBasketKey);
        await ExportAndCheck(db, organized, "organizacao-revisada", 11m, ("Empresa A", 10), ("Empresa B", 11), ("Empresa C", 12));
    }

    [Fact]
    public async Task ThreeRemainingPricesWithDispersionStillRequireBasketSpecificAcceptance()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var svc = Open(db);
        var project = await svc.CreateProjectAsync("Ressalva de dispersão");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input,
            [Price(line, "A", 1, 10, 1), Price(line, "retirada", 1, 10.4m, 2), Price(line, "B", 2, 10, 3), Price(line, "C", 3, 13.5m, 4)]);
        var manual = await repo.SaveManualBasketAsync(line, null, "Manual", ["A", "retirada", "B", "C"]);
        await svc.ConfirmBasketAsync((await svc.GetAnalysisAsync(project.Id, line))!, manual.Key);
        var loaded = await svc.GetReportAsync(project.Id);
        var preview = await svc.FindDuplicatePricesAsync(project.Id);
        var impact = Assert.Single(preview).Impact;
        Assert.Equal((3, 3, 10m, true), (impact.After, impact.Minimum, impact.AdoptedPrice, impact.NeedsAcceptance));
        Assert.Contains("desvio", impact.Validation);
        Assert.Empty(await svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, new HashSet<(Guid, string)>()));
        Assert.Equal(4, (await repo.GetManualBasketsAsync(line)).Single().ReferenceIds.Count);
        var updated = await Clean(db, loaded, preview, true);
        Assert.Equal(manual.Key, updated.Lines.Single().SelectedBasket!.Key);
        await ExportAndCheck(db, updated, "manual-dispersao", 10m, ("Empresa A", 10), ("Empresa B", 10), ("Empresa C", 13.5m));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task BlockedAutomaticStillExportsItsOriginalConfirmedPrices(int minimum)
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var svc = Open(db);
        var project = await svc.CreateProjectAsync("Automática preservada");
        var line = Guid.NewGuid();
        QuotationReference[] prices = minimum == 5
            ? [Price(line, "A", 1, 10, 1), Price(line, "B", 1, 10, 2), Price(line, "C", 2, 10, 3),
                Price(line, "D", 2, 10, 4), Price(line, "E", 3, 10, 5)]
            : [Price(line, "A", 1, 10, 1), Price(line, "B", 1, 12, 2), Price(line, "C", 3, 13.5m, 3)];
        await repo.SaveSampleAsync(project.Id, line, Input with { RequestedBasketSize = minimum }, prices);
        var initial = (await svc.GetAnalysisAsync(project.Id, line))!;
        await svc.ConfirmBasketAsync(initial, Assert.Single(initial.Baskets).Key);
        var loaded = await svc.GetReportAsync(project.Id);
        var preview = await svc.FindDuplicatePricesAsync(project.Id);
        Assert.NotEmpty(preview);
        Assert.All(preview, row => Assert.NotNull(row.Impact.BlockReason));
        var consent = preview.Select(row => (row.LineId, row.BasketKey)).ToHashSet();
        Assert.Empty(await svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, consent));
        await ExportAndCheck(db, loaded, minimum == 5 ? "minimo-cinco-preservado" : "valor-zero-evitado",
            minimum == 5 ? 10m : 11.83m, prices.Select(p => (p.SupplierName, p.UnitPrice)).ToArray());
    }

    [Fact]
    public async Task NativeAutomaticExportsTheAcceptedPriceImmediately()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var svc = Open(db);
        var project = await svc.CreateProjectAsync("Automática local");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input,
            [Price(line, "A", 1, 10, 1), Price(line, "retirada", 1, 10.4m, 2), Price(line, "B", 2, 11.8m, 3)]);
        var analysis = (await svc.GetAnalysisAsync(project.Id, line))!;
        var auto = Assert.Single(analysis.Baskets);
        await svc.ConfirmBasketAsync(analysis, auto.Key);
        var loaded = await svc.GetReportAsync(project.Id);
        var preview = await svc.FindDuplicatePricesAsync(project.Id);
        Assert.False(Assert.Single(preview).CanRemove(null));
        var updated = await Clean(db, loaded, preview, true);
        Assert.True(updated.Lines.Single().Line.SelectionConfirmed);
        await ExportAndCheck(db, updated, "automatica-local", 10.90m, ("Empresa A", 10m), ("Empresa B", 11.8m));
    }

    [Theory]
    [InlineData(QuotationPackageImportMode.PreserveIdentity)]
    [InlineData(QuotationPackageImportMode.Copy)]
    public async Task AutomaticFromPublicBaseRetainsExportableExceptionAndCanBeReconfirmed(QuotationPackageImportMode mode)
    {
        await using var db = await TestDatabase.CreateAsync();
        var packages = new QuotationPackageService(db.Repository.DatabasePath, db.Directory);
        var imported = await packages.ImportAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "confirmada-3-v1.2.52.pncpcotacao"), mode);
        var svc = Open(db);
        var loaded = await svc.GetReportAsync(imported.ProjectId);
        var original = loaded.Lines.Single();
        var automatic = original.SelectedBasket!;
        Assert.False(automatic.IsManual);
        var preview = await svc.FindDuplicatePricesAsync(imported.ProjectId);
        var row = Assert.Single(preview);
        Assert.True(row.IsAutomatic);
        Assert.Equal((3, 2, 3, 10.90m, true), (row.Impact.Before, row.Impact.After, row.Impact.Minimum, row.Impact.AdoptedPrice, row.Impact.NeedsAcceptance));
        Assert.Empty(await svc.ApplyDuplicatePriceReviewAsync(imported.ProjectId, preview, new HashSet<(Guid, string)>()));
        Assert.Equal(automatic.Key, (await Open(db).GetReportAsync(imported.ProjectId)).Lines.Single().Line.SelectedBasketKey);
        var refreshed = await Clean(db, loaded, preview, true);
        var cleaned = refreshed.Lines.Single();
        Assert.True(cleaned.Line.SelectionConfirmed && cleaned.SelectedBasket!.IsManual && cleaned.SelectedBasket.IsIncomplete);
        Assert.Contains(cleaned.Baskets, b => b.Key == automatic.Key && b.References.Count == 3);
        await ExportAndCheck(db, refreshed, "automatica-" + mode, 10.90m, ("Empresa Alfa", 10m), ("Empresa Beta", 11.80m));
        Assert.Empty(await Open(db).FindDuplicatePricesAsync(imported.ProjectId));
        await svc.ConfirmBasketAsync(cleaned, cleaned.SelectedBasket!.Key);
        var file = Path.Combine(db.Directory, "limpa.pncpcotacao");
        await packages.ExportAsync(file, imported.ProjectId);
        await using var target = await TestDatabase.CreateAsync();
        var copy = await new QuotationPackageService(target.Repository.DatabasePath, target.Directory).ImportAsync(file, QuotationPackageImportMode.Copy);
        var saved = await Open(target).GetReportAsync(copy.ProjectId);
        Assert.True(saved.Lines.Single().Line.SelectionConfirmed);
        Assert.NotEqual(cleaned.SelectedBasket.Key, saved.Lines.Single().SelectedBasket!.Key);
        await ExportAndCheck(target, saved, "automatica-reimportada-" + mode, 10.90m, ("Empresa Alfa", 10m), ("Empresa Beta", 11.8m));
    }
}
