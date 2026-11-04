using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationAiImportTests
{
    [Fact]
    public async Task VersionedTemplate_PreservesFullSpecificationAndUsesMainPlusSyntax()
    {
        await using var database = await TestDatabase.CreateAsync();
        var path = Path.Combine(database.Directory, "entrada.xlsx");
        var service = new QuotationWorkbookImportService();
        await service.CreateExternalAiTemplateAsync(path);
        using (var package = SpreadsheetDocument.Open(path, false))
            Assert.Empty(new OpenXmlValidator().Validate(package));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet(QuotationWorkbookImportService.ExternalAiSheetName);
            WriteItem(sheet, 2, "copo descartável + copo plástico + copo polipropileno");
            sheet.Cell(2, 9).SetValue("001234");
            sheet.Cell(2, 11).Value = "Copo descartável de polipropileno, capacidade de 180 ml, embalagem com 100 unidades.";
            workbook.Save();
        }

        var imported = await service.ReadAsync(path);

        Assert.True(imported.IsExternalAi);
        var item = Assert.Single(imported.Items);
        Assert.Equal(2, item.SourceRow);
        Assert.Equal("Copo", item.OutputDescription);
        Assert.Contains("180 ml", item.FullDescription);
        Assert.Equal("001234", item.CatmatCodeOverride);
        Assert.Equal(SearchPromptOrigin.User, item.PromptOrigin);
        Assert.Equal(3, SearchText.ParseMainCriteria(item.SearchText).Count);
    }

    [Fact]
    public async Task NewFormat_RejectsEmptyPlusCriteriaAndUnknownVersionWithoutReinterpretingLegacy()
    {
        await using var database = await TestDatabase.CreateAsync();
        var path = Path.Combine(database.Directory, "entrada.xlsx");
        var service = new QuotationWorkbookImportService();
        await service.CreateExternalAiTemplateAsync(path);
        using (var workbook = new XLWorkbook(path))
        {
            WriteItem(workbook.Worksheet(1), 2, "copo + + plástico");
            workbook.Save();
        }
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(path));
        Assert.Contains("A2", exception.Message);
        Assert.Contains("critério", exception.Message);

        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet(1).Name = "PNCPKing_IA_v2";
            workbook.Save();
        }
        exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(path));
        Assert.Contains("Versão", exception.Message);

        using (var workbook = new XLWorkbook())
        {
            WriteItem(workbook.Worksheets.Add("Legado"), 1, "copo + plástico");
            workbook.Worksheet(1).Cell(1, 11).Value = "Ignorado no formato legado";
            workbook.SaveAs(path);
        }
        var legacy = await service.ReadAsync(path);
        Assert.False(legacy.IsExternalAi);
        Assert.Equal("copo + plástico", Assert.Single(legacy.Items).SearchText);
        Assert.Empty(legacy.Items[0].FullDescription);
    }

    [Fact]
    public async Task CreateExternalRun_Inserts450ItemsInOrderWithProgressAndNoSchemaChange()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repository.CreateProjectAsync("Coleta grande");
        await repository.CreateLineAsync(project.Id, new QuotationLineInput("Item anterior", 1, "unidade", null, null));
        var items = Enumerable.Range(1, 450).Select(index => new QuotationImportItem(
            index + 1, $"copo {index} + recipiente {index}", $"Item {index}", index,
            "unidade", null, null, 2)
        {
            FullDescription = $"Especificação integral {index}",
            CatmatCodeOverride = "001234",
            MinimumOrderQuantity = 1
        }).ToArray();
        var progress = new List<QuotationAiProgress>();

        var run = await repository.CreateExternalAiRunAsync(project.Id,
            Path.Combine(database.Directory, "resultado.zip"), SearchGeoFilter.All,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 1), items, AdequacyWeights.Default,
            new InlineProgress(value => progress.Add(value)));

        var lines = await repository.GetLinesAsync(project.Id);
        Assert.Equal(451, lines.Count);
        var created = lines.Where(line => line.AutomationRunId == run.Id).OrderBy(line => line.DisplayOrder).ToArray();
        Assert.Equal(450, created.Length);
        Assert.Equal(QuotationAutomationMode.ExternalAi, run.Mode);
        Assert.Equal(1, run.StrategyVersion);
        Assert.Equal(19, progress.Count);
        Assert.Equal(450, progress[^1].CompletedItems);
        for (var index = 0; index < created.Length; index++)
        {
            Assert.Equal($"Item {index + 1}", created[index].DisplayName);
            Assert.Equal($"Especificação integral {index + 1}", created[index].Description);
            Assert.Equal(index + 1, created[index].RequestedQuantity);
            Assert.Equal("001234", created[index].CatmatCodeOverride);
            Assert.False(created[index].SelectionConfirmed);
        }
        Assert.Equal(run.Id, (await repository.GetLatestAutomationRunAsync(project.Id))!.Id);
        var prompts = await repository.GetItemSearchPromptSetAsync(created[0].Id);
        Assert.Equal(SearchPromptOrigin.User, prompts.Origin);
        Assert.Equal("copo 1", prompts.RestrictiveText);
        Assert.Equal("recipiente 1", prompts.IntermediateText);
        Assert.Empty(prompts.BroadText);
        await using var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
        Assert.Equal(33L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CancelDuringInsertion_RollsBackRunAndAllNewItems()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repository.CreateProjectAsync("Cancelamento");
        var existing = await repository.CreateLineAsync(project.Id,
            new QuotationLineInput("Preservado", 1, "unidade", null, null));
        var items = Enumerable.Range(1, 450).Select(index => new QuotationImportItem(
            index + 1, "copo + recipiente", "Copo", 10, "unidade", null, null, 1)).ToArray();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.CompletedItems == 25) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.CreateExternalAiRunAsync(
            project.Id, Path.Combine(database.Directory, "resultado.zip"), SearchGeoFilter.All,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 1), items, AdequacyWeights.Default,
            progress, cancellation.Token));

        Assert.Equal(existing.Id, Assert.Single(await repository.GetLinesAsync(project.Id)).Id);
        Assert.Null(await repository.GetLatestAutomationRunAsync(project.Id));
    }

    private static void WriteItem(IXLWorksheet sheet, int row, string query)
    {
        sheet.Cell(row, 1).Value = query;
        sheet.Cell(row, 2).Value = "Copo";
        sheet.Cell(row, 3).Value = 100;
        sheet.Cell(row, 4).Value = "unidade";
        sheet.Cell(row, 7).Value = 2;
        sheet.Cell(row, 8).Value = 3;
    }

    private sealed class InlineProgress(Action<QuotationAiProgress> action) : IProgress<QuotationAiProgress>
    {
        public void Report(QuotationAiProgress value) => action(value);
    }
}
