using ClosedXML.Excel;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using static PNCPKing.Tests.PriceCacheTests;

namespace PNCPKing.Tests;

public sealed class QuotationAiWorkflowTests
{
    [Fact]
    public async Task ExcelCollectionTriageAndGuide_CreateOnlyChosenBasketAndReimportWithoutDuplicates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var number = 1; number <= 4; number++)
        {
            var contract = RecentContract($"workflow-{number}", today, number);
            await database.Repository.UpsertContractsAsync([contract]);
            await database.Repository.UpsertItemsAsync(contract.PncpId,
                [Item(contract, 1) with { Description = number == 4 ? "Açúcar cristal" : "Café torrado em grãos" }], false);
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1,
                [Result(contract, 1, 1, true) with
                {
                    SupplierTaxId = "11222333000181", SupplierName = "Fornecedor de teste",
                    HomologatedUnitValueScaled = DecimalScale.ToScaled(number == 4 ? 5m : 19m + number)
                }]);
        }

        var importer = new QuotationWorkbookImportService();
        var inputPath = Path.Combine(database.Directory, "entrada.xlsx");
        await importer.CreateExternalAiTemplateAsync(inputPath);
        using (var workbook = new XLWorkbook(inputPath))
        {
            var sheet = workbook.Worksheet(QuotationWorkbookImportService.ExternalAiSheetName);
            sheet.Cell(2, 1).Value = "café + açúcar + café torrado";
            sheet.Cell(2, 2).Value = "Café";
            sheet.Cell(2, 3).Value = 100;
            sheet.Cell(2, 4).Value = "KG";
            sheet.Cell(2, 5).Value = 15;
            sheet.Cell(2, 6).Value = 30;
            sheet.Cell(2, 7).Value = 1;
            sheet.Cell(2, 8).Value = 3;
            sheet.Cell(2, 11).Value = "Café torrado em grãos";
            workbook.Save();
        }
        var document = await importer.ReadAsync(inputPath);
        var quotations = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await quotations.CreateProjectAsync("Fluxo completo");
        var packagePath = Path.Combine(database.Directory, "resultado.zip");
        var run = await quotations.CreateExternalAiRunAsync(project.Id, packagePath,
            SearchGeoFilter.All, today.AddDays(-1), today, document.Items, AdequacyWeights.Default);
        var collector = new QuotationAiCollectionService(
            new SqlitePriceCacheRepository(database.Repository.DatabasePath), quotations, database.Repository.DatabasePath);
        var manifest = await collector.CollectAsync(run);
        var item = Assert.Single(manifest.Items);
        Assert.Equal(4, item.CandidateCount);
        Assert.Empty(await quotations.GetReferencesAsync(item.Line.Id));

        var guides = new QuotationAiGuideService(quotations);
        var templates = await guides.CreateTemplatesAsync(packagePath, database.Directory);
        using (var workbook = new XLWorkbook(templates.TriagePath))
        {
            var sheet = workbook.Worksheet("Triagem");
            sheet.Cell(2, 1).Value = item.Line.Id.ToString("N");
            sheet.Cell(2, 2).Value = "workflow-4|1|1";
            sheet.Cell(2, 3).Value = "rejeitar";
            sheet.Cell(2, 4).Value = "Açúcar não corresponde ao café solicitado.";
            workbook.Save();
        }
        var filteredPath = Path.Combine(database.Directory, "triado.zip");
        var filtered = await guides.FilterAsync(packagePath, templates.TriagePath, filteredPath);
        Assert.Equal(manifest.PackageId, filtered.ParentPackageId);
        Assert.Equal(3, Assert.Single(filtered.Items).CandidateCount);
        var finalTemplates = await guides.CreateTemplatesAsync(filteredPath, database.Directory);
        using (var workbook = new XLWorkbook(finalTemplates.GuidePath))
        {
            var items = workbook.Worksheet("Itens");
            items.Cell(2, 3).Value = "pronto";
            items.Cell(2, 5).Value = "Três referências com correspondência integral do café em grãos.";
            var selections = workbook.Worksheet("Selecoes");
            for (var number = 1; number <= 3; number++)
            {
                selections.Cell(number + 1, 1).Value = item.Line.Id.ToString("N");
                selections.Cell(number + 1, 2).Value = $"workflow-{number}|1|1";
                selections.Cell(number + 1, 3).Value = number;
                selections.Cell(number + 1, 4).Value = 1;
                selections.Cell(number + 1, 5).Value = "Mesmo produto e unidade KG.";
                selections.Cell(number + 1, 7).Value = "Café torrado em grãos";
            }
            workbook.Save();
        }
        var preview = await guides.PreviewGuideAsync(filteredPath, finalTemplates.GuidePath);
        Assert.True(preview.ReadyCount == 1, string.Join("; ", preview.Items.Select(value => value.Message)));
        var applied = await guides.ApplyGuideAsync(filteredPath, finalTemplates.GuidePath);
        Assert.Equal(1, applied.AppliedCount);
        var repeated = await guides.ApplyGuideAsync(filteredPath, finalTemplates.GuidePath);
        Assert.Equal(1, repeated.UnchangedCount);
        Assert.Equal(3, (await quotations.GetReferencesAsync(item.Line.Id)).Count);
        Assert.Single(await quotations.GetManualBasketsAsync(item.Line.Id));
        var report = await new QuotationService(quotations, new QuotationAnalyzer(today)).GetReportAsync(project.Id);
        Assert.Equal(21m, Assert.Single(report.Lines).SelectedBasket!.AdoptedPrice);
        Assert.Empty(Directory.GetFiles(database.Directory, "*.pdf", SearchOption.AllDirectories));
        Assert.Equal(33, SqliteContractRepository.CurrentSchemaVersion);
    }
}
