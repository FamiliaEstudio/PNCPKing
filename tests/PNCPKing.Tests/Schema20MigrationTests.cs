using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class Schema20MigrationTests
{
    [Fact]
    public async Task Migration19To20_AddsResponsibleNamePreservesRunsAndIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var quotations = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await quotations.CreateProjectAsync("Cotação migrada");
        var created = await quotations.CreateAutomationRunAsync(
            project.Id,
            Path.Combine(database.Directory, "saida.xlsx"),
            "Nome que não existia no schema 19",
            SearchGeoFilter.All,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 8, 1),
            [new QuotationImportItem(1, "cafe", "Café", 1m, "pacote", null, null, 1)],
            AdequacyWeights.Default);
        await DowngradeTo19Async(database.Repository.DatabasePath);
        var repository = new SqliteContractRepository(database.Repository.DatabasePath);

        var result = await repository.InitializeAsync();

        Assert.Equal(19, result.PreviousVersion);
        Assert.Equal(26, result.CurrentVersion);
        Assert.Equal([20, 21, 22, 23, 24, 25, 26], result.AppliedMigrations);
        var restored = Assert.IsType<QuotationAutomationRun>(
            await quotations.GetLatestAutomationRunAsync(project.Id));
        Assert.Equal(created.Id, restored.Id);
        Assert.Equal(created.OutputPath, restored.OutputPath);
        Assert.Empty(restored.ResponsibleName);
        Assert.True(await IsResponsibleNameRequiredAsync(database.Repository.DatabasePath));

        var repeated = await repository.InitializeAsync();

        Assert.Equal(26, repeated.PreviousVersion);
        Assert.Equal(26, repeated.CurrentVersion);
        Assert.Empty(repeated.AppliedMigrations);
    }

    private static async Task DowngradeTo19Async(string path)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE quotation_automation_runs DROP COLUMN responsible_name;
            UPDATE schema_info SET version = 19 WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IsResponsibleNameRequiredAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(quotation_automation_runs);";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == "responsible_name")
            {
                return reader.GetInt32(3) == 1 && reader.GetString(4) == "''";
            }
        }

        return false;
    }
}
