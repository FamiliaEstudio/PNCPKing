using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class Schema21MigrationTests
{
    [Fact]
    public async Task Migration20To21_AddsBasketCalculationDefaultsAndIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var quotations = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await quotations.CreateProjectAsync("Cotação migrada");
        var lineId = Guid.NewGuid();
        await quotations.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Café", 1m, "pacote", null, null),
            [Reference(lineId, "a", 10m)]);
        var basket = await quotations.SaveManualBasketAsync(
            lineId,
            null,
            "Manual",
            ["a"]);
        await DowngradeTo20Async(database.Repository.DatabasePath);
        var repository = new SqliteContractRepository(database.Repository.DatabasePath);

        var result = await repository.InitializeAsync();

        Assert.Equal(20, result.PreviousVersion);
        Assert.Equal(25, result.CurrentVersion);
        Assert.Equal([21, 22, 23, 24, 25], result.AppliedMigrations);
        var restored = Assert.Single(await quotations.GetManualBasketsAsync(lineId));
        Assert.Equal(basket.Id, restored.Id);
        Assert.Equal(QuotationAggregationMethod.Mean, restored.AggregationMethod);
        Assert.Equal(1m, restored.GetConversionFactor("a"));

        var repeated = await repository.InitializeAsync();
        Assert.Equal(25, repeated.PreviousVersion);
        Assert.Equal(25, repeated.CurrentVersion);
        Assert.Empty(repeated.AppliedMigrations);
    }

    private static async Task DowngradeTo20Async(string path)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE quotation_manual_baskets DROP COLUMN calculation_method;
            ALTER TABLE quotation_manual_basket_references DROP COLUMN conversion_factor_millionths;
            UPDATE schema_info SET version = 20 WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static QuotationReference Reference(Guid lineId, string id, decimal price) => new()
    {
        Id = id,
        LineId = lineId,
        ContractId = id,
        ItemNumber = 1,
        ResultSequence = 1,
        SupplierName = "Fornecedor",
        SupplierTaxId = "11222333000181",
        UnitPrice = price,
        ItemDescription = "Café pacote",
        ItemUnit = "pacote"
    };
}
