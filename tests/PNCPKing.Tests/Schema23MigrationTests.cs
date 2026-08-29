using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class Schema23MigrationTests
{
    [Fact]
    public async Task Migration22To23_PreservesPricesPinsQueriedDataAndRequiresNewAuthorization()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = PriceCacheTests.RecentContract("schema-23", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(
            contract.PncpId,
            [PriceCacheTests.Item(contract, 1)],
            false);
        await database.Repository.ReplaceItemResultsAsync(
            contract.PncpId,
            1,
            [PriceCacheTests.Result(contract, 1, 1, true)]);

        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                UPDATE price_cache_control
                   SET authorized = 1, enabled = 1, paused = 1, status = 2,
                       window_start = '2026-01-01', window_end = '2026-03-31';
                UPDATE price_cache_contracts
                   SET background_owned = 1, user_pinned = 0;
                UPDATE schema_info SET version = 22 WHERE id = 1;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var result = await database.Repository.InitializeAsync();
        var cached = await database.Repository.GetCachedItemResultsAsync(contract.PncpId, 1);

        Assert.Equal(22, result.PreviousVersion);
        Assert.Equal(25, result.CurrentVersion);
        Assert.Equal([23, 24, 25], result.AppliedMigrations);
        Assert.NotNull(cached);
        Assert.True(cached.IsCurrent);
        Assert.Single(cached.Results);

        await using var verify = new SqliteConnection($"Data Source={database.Repository.DatabasePath}");
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = """
            SELECT ctl.authorized, ctl.enabled, ctl.paused, ctl.window_start, ctl.window_end,
                   pc.status, pc.background_owned, pc.user_pinned
              FROM price_cache_control ctl
              JOIN price_cache_contracts pc ON pc.contract_id = $contract
             WHERE ctl.id = 1;
            """;
        command.Parameters.AddWithValue("$contract", contract.PncpId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
        Assert.True(reader.IsDBNull(3));
        Assert.True(reader.IsDBNull(4));
        Assert.Equal((int)PriceCacheContractStatus.Complete, reader.GetInt32(5));
        Assert.Equal(0, reader.GetInt64(6));
        Assert.Equal(1, reader.GetInt64(7));
    }
}
