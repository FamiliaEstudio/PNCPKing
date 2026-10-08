using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationDocumentDetailsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("7")]
    public async Task ImportOptionalColumnsPersistsNominalAndCode(string minimum)
    {
        await using var database = await TestDatabase.CreateAsync();
        var path = Path.Combine(database.Directory, "entrada.xlsx");
        CreateWorkbook(path, minimum);
        var document = await new QuotationWorkbookImportService().ReadAsync(path);
        var item = Assert.Single(document.Items);
        Assert.Equal("001234", item.CatmatCodeOverride);
        Assert.Equal(minimum.Length == 0 ? null : 7m, item.MinimumOrderQuantity);
        Assert.Equal(3, item.RequestedBasketSize);
        var repo = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repo.CreateProjectAsync("Importação");
        await repo.CreateAutomationRunAsync(project.Id, Path.Combine(database.Directory, "saida.xlsx"),
            "Responsável", SearchGeoFilter.All, new(2026, 1, 1), new(2026, 9, 1),
            document.Items, AdequacyWeights.Default);
        var restored = Assert.Single(await new SqliteQuotationRepository(database.Repository.DatabasePath).GetLinesAsync(project.Id));
        Assert.Equal(item.CatmatCodeOverride, restored.CatmatCodeOverride);
        Assert.Equal(item.MinimumOrderQuantity, restored.MinimumOrderQuantity);
        Assert.NotNull(restored.AutomationRunId);
        await repo.UpdateLineDocumentDetailsAsync(restored.Id, "765", 9);
        Assert.Equal(9m, (await repo.GetLineAsync(project.Id, restored.Id))!.MinimumOrderQuantity);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1,5")]
    [InlineData("10%")]
    public async Task ImportReportsInvalidMinimumCell(string minimum)
    {
        await using var database = await TestDatabase.CreateAsync();
        var path = Path.Combine(database.Directory, "entrada.xlsx");
        CreateWorkbook(path, minimum);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new QuotationWorkbookImportService().ReadAsync(path));
        Assert.Contains("Itens!J1", error.Message);
        Assert.Contains("Quantidade mínima por pedido", error.Message);
    }

    private static void CreateWorkbook(string path, string minimum)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Itens");
        sheet.Cell(1, 1).Value = "cafe";
        sheet.Cell(1, 2).Value = "Café Premium";
        sheet.Cell(1, 3).Value = 100;
        sheet.Cell(1, 4).Value = "pacote";
        sheet.Cell(1, 7).Value = 1;
        sheet.Cell(1, 9).Value = "001234";
        sheet.Cell(1, 10).Value = minimum;
        workbook.SaveAs(path);
    }

    [Fact]
    public async Task EditingPreservesCatalogConfirmationOrganizationAndSampleUpdates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Item editável");
        var line = await ConfirmedItem(repo, service, project);
        await repo.SetLineCatalogSelectionAsync(line.Id, new() { Kind = CatalogKind.Catmat, Code = "987", Description = "CAFÉ" });
        await service.OrganizeProjectAsync(project.Id);
        var before = await service.GetReportAsync(project.Id);
        await service.UpdateLineDocumentDetailsAsync(line.Id, " 001234 ", 10);
        var reopened = new QuotationService(new SqliteQuotationRepository(database.Repository.DatabasePath), new QuotationAnalyzer());
        var after = await reopened.GetReportAsync(project.Id);
        var saved = Assert.Single(after.Lines).Line;
        Assert.Equal("001234", saved.CatmatCodeOverride);
        Assert.Equal(10m, saved.MinimumOrderQuantity);
        Assert.Equal("987", saved.CatalogSelection!.Code);
        Assert.True(saved.SelectionConfirmed);
        Assert.False(after.OrganizationIsStale);
        Assert.Equal(before.Project.Organization!.ToJson(), after.Project.Organization!.ToJson());
        await repo.SaveSampleAsync(project.Id, line.Id, new("Café", 100, "pacote", null, null), await repo.GetReferencesAsync(line.Id));
        saved = (await repo.GetLineAsync(project.Id, line.Id))!;
        Assert.Equal("001234", saved.CatmatCodeOverride);
        Assert.Equal(10m, saved.MinimumOrderQuantity);
        await repo.UpdateLineDocumentDetailsAsync(line.Id, "", null);
        saved = (await repo.GetLineAsync(project.Id, line.Id))!;
        Assert.Empty(saved.CatmatCodeOverride);
        Assert.Null(saved.MinimumOrderQuantity);
        Assert.Equal("987", saved.CatalogSelection!.Code);
    }

    [Fact]
    public async Task Migration32To33PreservesExistingDataAndIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repo.CreateProjectAsync("Legado");
        var line = await repo.CreateLineAsync(project.Id, new("Café", 100, "pacote", null, null));
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE quotation_lines DROP COLUMN catmat_code_override;
                ALTER TABLE quotation_lines DROP COLUMN minimum_order_quantity_scaled;
                UPDATE schema_info SET version = 32 WHERE id = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var migrated = await database.Repository.InitializeAsync();
        Assert.Equal([33, 34], migrated.AppliedMigrations);
        var saved = (await repo.GetLineAsync(project.Id, line.Id))!;
        Assert.Equal(100, saved.RequestedQuantity);
        Assert.Empty(saved.CatmatCodeOverride);
        Assert.Null(saved.MinimumOrderQuantity);
        await repo.UpdateLineDocumentDetailsAsync(line.Id, "001234", 10);
        Assert.Empty((await database.Repository.InitializeAsync()).AppliedMigrations);
        Assert.Equal(10, (await repo.GetLineAsync(project.Id, line.Id))!.MinimumOrderQuantity);
    }

    [Theory]
    [InlineData(false, QuotationPackageImportMode.Copy)]
    [InlineData(false, QuotationPackageImportMode.PreserveIdentity)]
    [InlineData(false, QuotationPackageImportMode.Replace)]
    [InlineData(true, QuotationPackageImportMode.Copy)]
    public async Task PackagesPreserveFieldsAndAcceptSchema32(bool legacy, QuotationPackageImportMode mode)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Portátil");
        var line = await ConfirmedItem(repo, service, project);
        await repo.UpdateLineDocumentDetailsAsync(line.Id, "001234", 10);
        await service.OrganizeProjectAsync(project.Id);
        var path = Path.Combine(source.Directory, "cotacao.pncpcotacao");
        await new QuotationPackageService(source.Repository.DatabasePath, source.Directory).ExportAsync(path, project.Id);
        if (legacy) await DowngradeTo32(path);
        var packages = new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory);
        if (mode == QuotationPackageImportMode.Replace)
            await packages.ImportAsync(path, QuotationPackageImportMode.PreserveIdentity);
        var result = await packages.ImportAsync(path, mode);
        var saved = await new QuotationService(new SqliteQuotationRepository(destination.Repository.DatabasePath),
            new QuotationAnalyzer()).GetReportAsync(result.ProjectId);
        Assert.Equal(legacy ? "" : "001234", Assert.Single(saved.Lines).Line.CatmatCodeOverride);
        Assert.Equal(legacy ? null : 10m, saved.Lines[0].Line.MinimumOrderQuantity);
        Assert.False(saved.OrganizationIsStale);
        Assert.True(saved.Lines[0].Line.SelectionConfirmed);
    }

    private static async Task DowngradeTo32(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var data = archive.GetEntry("quotation.json")!;
        JsonObject payload;
        using (var reader = new StreamReader(data.Open())) payload = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        foreach (var row in payload["tables"]!["quotation_lines"]!.AsArray())
        {
            row!.AsObject().Remove("catmat_code_override");
            row.AsObject().Remove("minimum_order_quantity_scaled");
        }
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        data.Delete();
        using (var stream = archive.CreateEntry("quotation.json").Open()) await stream.WriteAsync(bytes);
        var manifestEntry = archive.GetEntry("manifest.json")!;
        JsonObject manifest;
        using (var reader = new StreamReader(manifestEntry.Open())) manifest = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        manifest["databaseSchemaVersion"] = 32;
        manifest["dataSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        manifestEntry.Delete();
        using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
        await writer.WriteAsync(manifest.ToJsonString());
    }

    private static async Task<QuotationLine> ConfirmedItem(SqliteQuotationRepository repo, QuotationService service, QuotationProject project)
    {
        var id = Guid.NewGuid();
        var references = Enumerable.Range(1, 3).Select(index => new QuotationReference
        {
            Id = $"{id}-{index}", LineId = id, ContractId = $"{id}-{index}", ItemNumber = 1, ResultSequence = 1,
            SupplierTaxId = index.ToString("D14"), UnitPrice = 1000, ItemDescription = "Café", ItemUnit = "pacote",
            HomologatedQuantity = 100, ItemRequestedQuantity = 100, State = QuotationReferenceState.Eligible,
            ResultDate = DateOnly.FromDateTime(DateTime.Today), PublicationDate = DateTimeOffset.Now
        }).ToArray();
        var line = await repo.SaveSampleAsync(project.Id, id, new("Café", 100, "pacote", null, null), references);
        var analysis = (await service.GetAnalysisAsync(project.Id, id))!;
        await repo.ConfirmBasketAsync(id, analysis.Baskets.First(basket => basket.IsRecommended).Key);
        return line;
    }
}
