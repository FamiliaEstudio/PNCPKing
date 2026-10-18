using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationDivisorTests
{
    [Theory]
    [InlineData("30", "3", 2, "10")]
    [InlineData("100", "12", 2, "8.33")]
    [InlineData("100", "12", 4, "8.3333")]
    [InlineData("30", "2.5", 2, "12")]
    [InlineData("30", "0.5", 2, "60")]
    [InlineData("30", "1", 2, "30")]
    [InlineData("0.0003", "3", 4, "0.0001")]
    public void DivisionUsesTheOriginalPriceAndTruncatesOnlyAfterDividing(string priceText, string divisorText, int places, string expectedText)
    {
        var price = decimal.Parse(priceText, CultureInfo.InvariantCulture);
        var divisor = decimal.Parse(divisorText, CultureInfo.InvariantCulture);
        var expected = decimal.Parse(expectedText, CultureInfo.InvariantCulture);
        var line = new QuotationLine { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Description = "Café", RequestedQuantity = 1, RequestedUnit = "un" };
        var reference = Reference(line.Id, "a", price);
        var manual = new QuotationManualBasket
        {
            Id = Guid.NewGuid(), LineId = line.Id, Name = "Divisão", ReferenceIds = ["a"],
            PriceDivisors = new Dictionary<string, decimal> { ["a"] = divisor }
        };
        var basket = new QuotationAnalyzer().Analyze(line, [reference], [manual], places).Baskets.Single(value => value.IsManual);
        Assert.Equal(expected, basket.PriceEntries.Single().EffectiveUnitPrice);
        Assert.Equal(divisor, basket.PriceEntries.Single().PriceDivisor);
        var displayed = new QuotationPriceDisplayRow(reference, true, priceDecimalPlaces: places, priceDivisor: divisor);
        Assert.Equal(expected, displayed.EffectiveUnitPrice);
        Assert.Equal(divisor.ToString("0.######"), displayed.PriceDivisorInput);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0000001")]
    [InlineData("1.0000001")]
    [InlineData("9223372036854.775808")]
    [InlineData("79228162514264337593543950335")]
    public void InvalidDivisorsAreRejected(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => QuotationMoney.ValidatePriceDivisor(decimal.Parse(value, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("1", "1")]
    [InlineData("0.5", "2")]
    [InlineData("2", "0.5")]
    [InlineData("3", "")]
    [InlineData("0.333333", "")]
    public void LegacyEditorOffersOnlyExactReciprocals(string factorText, string expected)
    {
        var factor = decimal.Parse(factorText, CultureInfo.InvariantCulture);
        var row = new QuotationPriceDisplayRow(Reference(Guid.NewGuid(), "a", 30m), true, factor);
        var expectedInput = expected.Length == 0 ? "" : decimal.Parse(expected, CultureInfo.InvariantCulture).ToString("0.######");
        Assert.Equal(expectedInput, row.PriceDivisorInput);
        Assert.Equal(QuotationMoney.Truncate(30m * factor, 2), row.EffectiveUnitPrice);
        if (expected.Length == 0) Assert.Contains("Conversão existente", row.PriceDivisorPrompt);
    }

    [Fact]
    public async Task OptionalTableIsCreatedOnlyOnFirstDivisorAndKeepsSchema33()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var project = await repo.CreateProjectAsync("Divisores");
        var lineId = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, lineId, new("Café", 10, "un", null, null),
            [Reference(lineId, "a", 30), Reference(lineId, "b", 10), Reference(lineId, "c", 10)]);
        var basket = await repo.SaveManualBasketAsync(lineId, null, "Manual", ["a", "b"]);
        await repo.SetManualBasketConversionFactorAsync(basket.Id, "a", 0.333333m);
        await repo.ConfirmBasketAsync(lineId, basket.Key);
        Assert.Empty((await db.Repository.InitializeAsync()).AppliedMigrations);
        Assert.Equal(33L, await ScalarAsync(db.Repository.DatabasePath, "SELECT version FROM schema_info;"));
        Assert.Null(await ScalarAsync(db.Repository.DatabasePath,
            "SELECT 1 FROM sqlite_master WHERE name = 'quotation_manual_price_divisors';"));
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var legacy = (await service.GetAnalysisAsync(project.Id, lineId))!;
        Assert.True(legacy.Line.SelectionConfirmed);
        Assert.Equal(9.99m, legacy.SelectedBasket!.PriceEntries.Single(value => value.Reference.Id == "a").EffectiveUnitPrice);
        Assert.Null(legacy.SelectedBasket.PriceEntries.Single(value => value.Reference.Id == "a").PriceDivisor);
        await repo.SetManualBasketPriceDivisorAsync(basket.Id, "a", 3);
        Assert.Equal(33L, await ScalarAsync(db.Repository.DatabasePath, "SELECT version FROM schema_info;"));
        Assert.Equal(1L, await ScalarAsync(db.Repository.DatabasePath,
            "SELECT 1 FROM sqlite_master WHERE name = 'quotation_manual_price_divisors';"));
        Assert.Empty((await db.Repository.InitializeAsync()).AppliedMigrations);
        await repo.SaveManualBasketAsync(lineId, basket.Id, basket.Name, ["a", "b", "c"]);
        var reopened = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var saved = Assert.Single(await reopened.GetManualBasketsAsync(lineId));
        Assert.Equal(3m, saved.PriceDivisors["a"]);
        Assert.Equal(1m, saved.ConversionFactors["a"]);
        Assert.False((await reopened.GetLineAsync(project.Id, lineId))!.SelectionConfirmed);
        var analysis = (await service.GetAnalysisAsync(project.Id, lineId))!;
        var source = analysis.Baskets.Single(value => value.Key == basket.Key);
        Assert.Equal(10m, source.AdoptedPrice);
        var copy = await service.CreateManualCopyAsync(analysis, source, "Cópia", "c");
        Assert.Equal(3m, copy.PriceDivisors["a"]);
        await repo.SetManualBasketConversionFactorAsync(basket.Id, "a", 0.5m);
        Assert.Empty((await repo.GetManualBasketsAsync(lineId)).Single(value => value.Id == basket.Id).PriceDivisors);
        await repo.SetManualBasketPriceDivisorAsync(basket.Id, "a", 1);
        Assert.Equal(30m, (await service.GetAnalysisAsync(project.Id, lineId))!.Baskets.Single(value => value.Key == basket.Key)
            .PriceEntries.Single(value => value.Reference.Id == "a").EffectiveUnitPrice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageExcelAndWordKeepDirectDivision(bool medication)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Exportar divisão");
        await repo.SetProjectMedicationAsync(project.Id, medication);
        var lineId = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, lineId, new("Café", 10, "un", null, null),
            [Reference(lineId, "a", 100), Reference(lineId, "b", 100), Reference(lineId, "c", 100)]);
        var basket = await repo.SaveManualBasketAsync(lineId, null, "Manual", ["a", "b", "c"]);
        foreach (var id in basket.ReferenceIds) await repo.SetManualBasketPriceDivisorAsync(basket.Id, id, 12);
        await repo.ConfirmBasketAsync(lineId, basket.Key);
        await service.OrganizeProjectAsync(project.Id);
        var path = Path.Combine(source.Directory, "divisao.pncpcotacao");
        await new QuotationPackageService(source.Repository.DatabasePath, source.Directory).ExportAsync(path, project.Id);
        var imported = await new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory)
            .ImportAsync(path, QuotationPackageImportMode.Copy);
        var report = await new QuotationService(new SqliteQuotationRepository(destination.Repository.DatabasePath), new QuotationAnalyzer())
            .GetReportAsync(imported.ProjectId);
        var selected = report.Lines.Single().SelectedBasket!;
        var expected = medication ? 8.3333m : 8.33m;
        Assert.False(report.OrganizationIsStale);
        Assert.All(selected.PriceEntries, entry => { Assert.Equal(12m, entry.PriceDivisor); Assert.Equal(expected, entry.EffectiveUnitPrice); });
        var excelPath = Path.Combine(destination.Directory, "divisao.xlsx");
        await new QuotationWorkbookService().ExportAsync(excelPath, report, "Responsável");
        using var workbook = new XLWorkbook(excelPath);
        Assert.Equal(expected, workbook.Worksheet(1).Cell("F6").GetValue<decimal>());
        Assert.Equal(expected, workbook.Worksheet(1).Cell("C9").GetValue<decimal>());
        var wordPath = Path.Combine(destination.Directory, "divisao.docx");
        await new QuotationWordService().ExportPriceTableAsync(wordPath, report);
        using var word = WordprocessingDocument.Open(wordPath, false);
        Assert.Contains(expected.ToString(medication ? "N4" : "N2", CultureInfo.GetCultureInfo("pt-BR")), word.MainDocumentPart!.Document.InnerText);
    }

    [Fact]
    public async Task Schema33PackageKeepsItsLegacyCalculationAndSelection()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var project = await repo.CreateProjectAsync("Pacote legado");
        var lineId = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, lineId, new("Café", 1, "un", null, null), [Reference(lineId, "a", 30)]);
        var basket = await repo.SaveManualBasketAsync(lineId, null, "Manual", ["a"]);
        await repo.SetManualBasketConversionFactorAsync(basket.Id, "a", 0.333333m);
        await repo.ConfirmBasketAsync(lineId, basket.Key);
        var path = Path.Combine(source.Directory, "legado.pncpcotacao");
        await new QuotationPackageService(source.Repository.DatabasePath, source.Directory).ExportAsync(path, project.Id);
        using (var archive = ZipFile.OpenRead(path))
        using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
        {
            var manifest = JsonNode.Parse(await reader.ReadToEndAsync())!;
            Assert.Equal(1, manifest["formatVersion"]!.GetValue<int>());
            Assert.Equal(33, manifest["databaseSchemaVersion"]!.GetValue<int>());
        }
        await new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory).ImportAsync(path, QuotationPackageImportMode.PreserveIdentity);
        var analysis = (await new QuotationService(new SqliteQuotationRepository(destination.Repository.DatabasePath), new QuotationAnalyzer())
            .GetAnalysisAsync(project.Id, lineId))!;
        Assert.True(analysis.Line.SelectionConfirmed);
        Assert.Equal(9.99m, analysis.SelectedBasket!.PriceEntries.Single().EffectiveUnitPrice);
        Assert.Null(analysis.SelectedBasket.PriceEntries.Single().PriceDivisor);
    }

    [Theory]
    [InlineData(BackupProfile.Full, 4)]
    [InlineData(BackupProfile.Compact, 4)]
    [InlineData(BackupProfile.FullWithoutQuotations, 3)]
    public async Task BackupPreservesDivisorsWithoutSchemaMigration(BackupProfile profile, int format)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var project = await repo.CreateProjectAsync("Backup com divisor");
        var lineId = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, lineId, new("Café", 1, "un", null, null), [Reference(lineId, "a", 30)]);
        var basket = await repo.SaveManualBasketAsync(lineId, null, "Manual", ["a"]);
        await repo.SetManualBasketPriceDivisorAsync(basket.Id, "a", 3);
        await repo.ConfirmBasketAsync(lineId, basket.Key);
        var path = Path.Combine(source.Directory, "divisor.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, profile);
        using (var archive = ZipFile.OpenRead(path))
        using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<DatasetManifest>(await reader.ReadToEndAsync())!;
            Assert.Equal(format, manifest.ArchiveFormatVersion);
            Assert.Equal(33, manifest.SchemaVersion);
        }
        var importer = new BackupService(destination.Repository);
        Assert.False((await importer.InspectAsync(path)).RequiresMigration);
        await importer.ImportAsync(path);
        var restored = new SqliteQuotationRepository(destination.Repository.DatabasePath);
        if (profile == BackupProfile.FullWithoutQuotations)
        {
            Assert.Empty(await restored.GetProjectsAsync());
            Assert.Equal(0L, await ScalarAsync(destination.Repository.DatabasePath, "SELECT COUNT(*) FROM quotation_manual_price_divisors;"));
        }
        else
        {
            var analysis = (await new QuotationService(restored, new QuotationAnalyzer()).GetAnalysisAsync(project.Id, lineId))!;
            Assert.True(analysis.Line.SelectionConfirmed);
            Assert.Equal(10m, analysis.SelectedBasket!.PriceEntries.Single().EffectiveUnitPrice);
            Assert.Equal(3m, analysis.SelectedBasket.PriceEntries.Single().PriceDivisor);
        }
    }

    private static async Task<object?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static QuotationReference Reference(Guid lineId, string id, decimal price) => new()
    {
        Id = id, LineId = lineId, ContractId = "contrato-" + id, ItemNumber = 1, ResultSequence = 1,
        SupplierTaxId = id switch { "a" => "11222333000181", "b" => "60701190000104", _ => "33000167000101" },
        SupplierName = "Fornecedor " + id, UnitPrice = price, ItemDescription = "Café", ItemUnit = "un",
        ResultDate = DateOnly.FromDateTime(DateTime.Today), State = QuotationReferenceState.Eligible
    };
}
