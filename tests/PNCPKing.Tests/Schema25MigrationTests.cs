using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class Schema25MigrationTests
{
    [Fact]
    public async Task Migration24To25_PreservesItemsAndResultsAndRequiresPriceAuthorization()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = PriceCacheTests.RecentContract("schema-25", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(
            contract.PncpId,
            [PriceCacheTests.Item(contract, 1)],
            false);
        await database.Repository.ReplaceItemResultsAsync(
            contract.PncpId,
            1,
            [PriceCacheTests.Result(contract, 1, 1, true)]);
        await DowngradeTo24Async(database.Repository.DatabasePath);

        var repository = new SqliteContractRepository(database.Repository.DatabasePath);
        var result = await repository.InitializeAsync();
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var policy = await cache.GetNationalPriceIndexPolicyAsync();
        await cache.SetNationalPriceIndexAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareNationalPriceIndexAsync(today.AddDays(-364), today);
        var progress = await cache.GetNationalPriceIndexProgressAsync();
        var cached = await repository.GetCachedItemResultsAsync(contract.PncpId, 1);

        Assert.Equal(24, result.PreviousVersion);
        Assert.Equal(25, result.CurrentVersion);
        Assert.Equal([25], result.AppliedMigrations);
        Assert.False(policy.Authorized);
        Assert.False(policy.Enabled);
        Assert.Equal(1, progress.EligibleItems);
        Assert.Equal(1, progress.CompletedItems);
        Assert.Equal(1, progress.PricedItems);
        Assert.Equal(1, progress.ResultRows);
        Assert.NotNull(cached);
        Assert.Single(cached.Results);

        var repeated = await repository.InitializeAsync();
        Assert.Equal(25, repeated.PreviousVersion);
        Assert.Equal(25, repeated.CurrentVersion);
        Assert.Empty(repeated.AppliedMigrations);
    }

    private static async Task DowngradeTo24Async(string path)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS national_price_statistics_insert;
            DROP TRIGGER IF EXISTS national_price_statistics_delete;
            DROP TRIGGER IF EXISTS national_price_statistics_update;
            DROP TRIGGER IF EXISTS contracts_mark_items_stale;
            DROP INDEX IF EXISTS idx_price_cache_contracts_price_work;
            DROP TABLE IF EXISTS national_price_index_control;
            ALTER TABLE price_cache_contracts DROP COLUMN price_index_result_count;
            ALTER TABLE price_cache_contracts DROP COLUMN price_index_priced_item_count;
            ALTER TABLE price_cache_contracts DROP COLUMN price_index_completed_item_count;
            ALTER TABLE price_cache_contracts DROP COLUMN price_index_eligible_item_count;
            ALTER TABLE price_cache_contracts DROP COLUMN price_index_completed_at;
            ALTER TABLE price_cache_contracts DROP COLUMN price_index_started_at;
            ALTER TABLE price_cache_contracts DROP COLUMN price_index_next_retry_at;
            ALTER TABLE price_cache_contracts DROP COLUMN price_index_last_error;
            ALTER TABLE price_cache_contracts DROP COLUMN price_index_attempts;
            ALTER TABLE price_cache_contracts DROP COLUMN price_index_status;
            UPDATE schema_info SET version = 24 WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
