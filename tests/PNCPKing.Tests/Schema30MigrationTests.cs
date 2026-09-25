using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class Schema30MigrationTests
{
    [Fact]
    public async Task Migration30To31_DefaultsToOriginalOrderAndPreservesItems()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repository.CreateProjectAsync("Cotação anterior");
        var line = await repository.CreateLineAsync(project.Id,
            new QuotationLineInput("Item existente", 1m, "unidade", null, null));
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE quotation_projects DROP COLUMN sort_items_alphabetically; UPDATE schema_info SET version = 30 WHERE id = 1;";
            await command.ExecuteNonQueryAsync();
        }

        var result = await database.Repository.InitializeAsync();
        Assert.Equal(30, result.PreviousVersion);
        Assert.Equal(32, result.CurrentVersion);
        Assert.Equal([31, 32], result.AppliedMigrations);
        Assert.False(Assert.Single(await repository.GetProjectsAsync()).SortItemsAlphabetically);
        Assert.Equal(line.Id, Assert.Single(await repository.GetLinesAsync(project.Id)).Id);
    }

    [Fact]
    public async Task Migration29To30_DefaultsToCommonModePreservesPricesAndIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repository.CreateProjectAsync("Cotação existente");
        var line = await repository.SaveSampleAsync(project.Id, null,
            new QuotationLineInput("Medicamento", 1m, "unidade", null, null),
            [new QuotationReference { Id = "a", LineId = Guid.Empty, ContractId = "ca", ItemNumber = 1, ResultSequence = 1, UnitPrice = 0.1234m }]);
        await repository.ConfirmBasketAsync(line.Id, "a");
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE quotation_projects DROP COLUMN is_medication; UPDATE schema_info SET version = 29 WHERE id = 1;";
            await command.ExecuteNonQueryAsync();
        }
        var result = await database.Repository.InitializeAsync();
        Assert.Equal(29, result.PreviousVersion);
        Assert.Equal(32, result.CurrentVersion);
        Assert.Equal([30, 31, 32], result.AppliedMigrations);
        Assert.False(Assert.Single(await repository.GetProjectsAsync()).IsMedication);
        Assert.Equal(0.1234m, Assert.Single(await repository.GetReferencesAsync(line.Id)).UnitPrice);
        Assert.True((await repository.GetLineAsync(project.Id, line.Id))!.SelectionConfirmed);
        await repository.SetProjectMedicationAsync(project.Id, true);
        var repeated = await database.Repository.InitializeAsync();
        Assert.Empty(repeated.AppliedMigrations);
        Assert.True(Assert.Single(await repository.GetProjectsAsync()).IsMedication);
    }
}
