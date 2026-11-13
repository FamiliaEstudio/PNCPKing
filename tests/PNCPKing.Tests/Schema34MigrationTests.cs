using System.Globalization;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class Schema34MigrationTests
{
    [Fact]
    public async Task Migration33To34PreservesHistoryWithoutPublicParentAndKeepsQuotationOwnership()
    {
        await using var database = await TestDatabase.CreateAsync();
        var path = database.Repository.DatabasePath;
        var quotations = new SqliteQuotationRepository(path);
        var project = await quotations.CreateProjectAsync("Local history");
        var runId = Guid.NewGuid();
        var contract = PriceCacheTests.RecentContract("history-public-parent", DateOnly.FromDateTime(DateTime.Today), 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await ExecuteAsync(path, """
            INSERT INTO quotation_automation_runs(id, project_id, output_path, geo_filter_kind,
                start_date, end_date, created_at, updated_at)
            VALUES($run, $project, 'local.xlsx', 0, '2026-01-01', '2026-11-11',
                '2026-11-11T10:00:00Z', '2026-11-11T10:00:00Z');
            INSERT INTO quotation_processed_contracts
                VALUES($run, 'history-public-parent', 2, '2026-11-11T10:00:00Z', 3, 4);
            """, ("$run", runId.ToString("N")), ("$project", project.Id.ToString("N")));
        await DowngradeTo33Async(path);
        var before = Assert.Single(await quotations.GetProcessedContractsAsync(runId));
        var publicCounts = await database.Repository.GetCountsAsync();
        Assert.Equal(1L, await ScalarAsync(path,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('quotation_processed_contracts') WHERE \"table\"='contracts';"));

        var migration = await database.Repository.InitializeAsync();

        Assert.Equal(33, migration.PreviousVersion);
        Assert.Equal(SqliteContractRepository.CurrentSchemaVersion, migration.CurrentVersion);
        Assert.Equal([34], migration.AppliedMigrations);
        Assert.Equal(before, Assert.Single(await quotations.GetProcessedContractsAsync(runId)));
        Assert.Equal(publicCounts, await database.Repository.GetCountsAsync());
        Assert.Equal(0L, await ScalarAsync(path,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('quotation_processed_contracts') WHERE \"table\"='contracts';"));
        Assert.Equal(1L, await ScalarAsync(path,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('quotation_processed_contracts') WHERE \"table\"='quotation_automation_runs' AND on_delete='CASCADE';"));
        Assert.Equal(1L, await ScalarAsync(path,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_processed_contracts_run_time';"));

        await ExecuteAsync(path, "DELETE FROM contracts WHERE pncp_id='history-public-parent';");
        Assert.Equal(before, Assert.Single(await quotations.GetProcessedContractsAsync(runId)));
        Assert.Null(await ScalarAsync(path, "PRAGMA foreign_key_check;"));
        Assert.Empty((await database.Repository.InitializeAsync()).AppliedMigrations);

        // The local parent relationship still rejects invalid runs and cascades project deletion.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(path, """
            INSERT INTO quotation_processed_contracts VALUES('missing-run', 'missing-contract', 0, '2026-11-11', 0, 0);
            """));
        await ExecuteAsync(path, "DELETE FROM quotation_projects WHERE id=$project;", ("$project", project.Id.ToString("N")));
        Assert.Empty(await quotations.GetProcessedContractsAsync(runId));
    }

    [Fact]
    public async Task RemovingPublicContractKeepsSnapshotsCalculationsDivisorsAndExcelWordExports()
    {
        await using var database = await TestDatabase.CreateAsync();
        var path = database.Repository.DatabasePath;
        var repo = new SqliteQuotationRepository(path);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var contract = PriceCacheTests.RecentContract("removed-public-parent", DateOnly.FromDateTime(DateTime.Today), 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var project = await repo.CreateProjectAsync("Quotation snapshot");
        var lineId = Guid.NewGuid();
        var references = new[] { "a", "b", "c" }.Select((id, index) => new QuotationReference
        {
            Id = id, LineId = lineId, ContractId = contract.PncpId, ItemNumber = index + 1, ResultSequence = 1,
            SupplierName = "Fornecedor " + id,
            SupplierTaxId = index switch { 0 => "11222333000181", 1 => "60701190000104", _ => "33000167000101" },
            UnitPrice = 30m, ItemDescription = "Café", ItemUnit = "un",
            ResultDate = DateOnly.FromDateTime(DateTime.Today), State = QuotationReferenceState.Eligible
        }).ToArray();
        await repo.SaveSampleAsync(project.Id, lineId, new("Café", 10, "un", null, null), references);
        var basket = await repo.SaveManualBasketAsync(lineId, null, "Manual", ["a", "b", "c"]);
        foreach (var id in basket.ReferenceIds) await repo.SetManualBasketPriceDivisorAsync(basket.Id, id, 3m);
        await repo.ConfirmBasketAsync(lineId, basket.Key);
        await repo.SetLineCatalogSelectionAsync(lineId,
            new() { Kind = CatalogKind.Catmat, Code = "missing-catalog-code", Description = "Descrição preservada" });
        await service.OrganizeProjectAsync(project.Id);
        var before = await service.GetReportAsync(project.Id);

        await ExecuteAsync(path, "DELETE FROM contracts WHERE pncp_id='removed-public-parent';");

        var report = await service.GetReportAsync(project.Id);
        var analysis = Assert.Single(report.Lines);
        Assert.True(analysis.Line.SelectionConfirmed);
        Assert.False(report.OrganizationIsStale);
        Assert.Equal(before.Project.Organization!.ToJson(), report.Project.Organization!.ToJson());
        Assert.Equal("Descrição preservada", analysis.Line.CatalogSelection!.Description);
        Assert.Equal(3, analysis.References.Count);
        Assert.Equal(10m, analysis.SelectedBasket!.AdoptedPrice);
        Assert.All(analysis.SelectedBasket.PriceEntries, entry =>
        {
            Assert.Equal(30m, entry.Reference.UnitPrice);
            Assert.Equal(3m, entry.PriceDivisor);
            Assert.Equal(10m, entry.EffectiveUnitPrice);
        });
        Assert.Null(await ScalarAsync(path, "PRAGMA foreign_key_check;"));
        var excelPath = Path.Combine(database.Directory, "preserved.xlsx");
        await new QuotationWorkbookService().ExportAsync(excelPath, report, "Responsável");
        using (var workbook = new XLWorkbook(excelPath))
            Assert.Equal(10m, workbook.Worksheet(1).Cell("F6").GetValue<decimal>());
        var wordPath = Path.Combine(database.Directory, "preserved.docx");
        await new QuotationWordService().ExportPriceTableAsync(wordPath, report);
        using var word = WordprocessingDocument.Open(wordPath, false);
        Assert.Contains(10m.ToString("N2", CultureInfo.GetCultureInfo("pt-BR")), word.MainDocumentPart!.Document.InnerText);
    }

    private static async Task DowngradeTo33Async(string path) => await ExecuteAsync(path, """
        CREATE TABLE quotation_processed_contracts_v33(
            run_id TEXT NOT NULL REFERENCES quotation_automation_runs(id) ON DELETE CASCADE,
            contract_id TEXT NOT NULL REFERENCES contracts(pncp_id) ON DELETE CASCADE,
            prompt_order INTEGER NOT NULL,
            processed_at TEXT NOT NULL,
            matched_items INTEGER NOT NULL DEFAULT 0,
            revealed_prices INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(run_id, contract_id)
        );
        INSERT INTO quotation_processed_contracts_v33 SELECT * FROM quotation_processed_contracts;
        DROP TABLE quotation_processed_contracts;
        ALTER TABLE quotation_processed_contracts_v33 RENAME TO quotation_processed_contracts;
        CREATE INDEX idx_processed_contracts_run_time ON quotation_processed_contracts(run_id, processed_at, contract_id);
        UPDATE schema_info SET version=33 WHERE id=1;
        """);

    private static async Task ExecuteAsync(string path, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
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
}
