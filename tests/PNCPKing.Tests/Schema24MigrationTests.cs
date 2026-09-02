using Microsoft.Data.Sqlite;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class Schema24MigrationTests
{
    [Fact]
    public async Task Migration23To24_BackfillsQueueDateStatisticsAndPreparedWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-364);
        var contract = PriceCacheTests.RecentContract("schema-24", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, start, today);
        await cache.PrepareWindowAsync(start, today);

        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                UPDATE price_cache_control
                   SET prepared_window_start = NULL,
                       prepared_window_end = NULL,
                       indexed_contract_count = 0,
                       indexed_complete_count = 0,
                       indexed_pending_count = 0,
                       indexed_failed_count = 0;
                UPDATE price_cache_contracts SET publication_date = '';
                DROP INDEX IF EXISTS idx_price_cache_contracts_status_publication;
                UPDATE schema_info SET version = 23 WHERE id = 1;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var result = await database.Repository.InitializeAsync();
        Assert.Equal(23, result.PreviousVersion);
        Assert.Equal(26, result.CurrentVersion);
        Assert.Equal([24, 25, 26], result.AppliedMigrations);

        await using var verify = new SqliteConnection($"Data Source={database.Repository.DatabasePath}");
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = """
            SELECT pc.publication_date,
                   ctl.prepared_window_start, ctl.prepared_window_end,
                   ctl.indexed_contract_count, ctl.indexed_pending_count,
                   EXISTS(SELECT 1 FROM sqlite_master
                           WHERE type = 'index'
                             AND name = 'idx_price_cache_contracts_status_publication')
              FROM price_cache_contracts pc
              JOIN price_cache_control ctl ON ctl.id = 1
             WHERE pc.contract_id = $contract;
            """;
        command.Parameters.AddWithValue("$contract", contract.PncpId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.StartsWith(today.ToString("yyyy-MM-dd"), reader.GetString(0));
        Assert.Equal(start.ToString("yyyy-MM-dd"), reader.GetString(1));
        Assert.Equal(today.ToString("yyyy-MM-dd"), reader.GetString(2));
        Assert.Equal(1, reader.GetInt64(3));
        Assert.Equal(1, reader.GetInt64(4));
        Assert.Equal(1, reader.GetInt64(5));
    }
}
