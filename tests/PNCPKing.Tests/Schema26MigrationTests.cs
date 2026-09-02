using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class Schema26MigrationTests
{
    [Fact]
    public async Task Migration25To26_RebuildsPrefixIndexPreservesContentAndTriggersAndIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        Assert.Equal(
            1,
            CountOccurrences(
                await ReadItemsFtsSqlAsync(database.Repository.DatabasePath),
                "prefix='2 3'"));
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = PriceCacheTests.RecentContract("schema-26", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(
            contract.PncpId,
            [PriceCacheTests.Item(contract, 1)],
            false);
        await database.Repository.ReplaceItemResultsAsync(
            contract.PncpId,
            1,
            [PriceCacheTests.Result(contract, 1, 1, true)]);
        await DowngradeTo25Async(database.Repository.DatabasePath);
        var progress = new List<DatabaseInitializationProgress>();
        var repository = new SqliteContractRepository(database.Repository.DatabasePath);

        var result = await repository.InitializeAsync(
            progress: new InlineProgress<DatabaseInitializationProgress>(progress.Add));

        Assert.Equal(25, result.PreviousVersion);
        Assert.Equal(26, result.CurrentVersion);
        Assert.Equal([26], result.AppliedMigrations);
        Assert.Contains(progress, value =>
            value.Phase == "Preparando pesquisa por prefixo" && value.Percentage == 75);
        Assert.Equal(1, await CountFtsMatchesAsync(database.Repository.DatabasePath, "cafe*"));
        Assert.Single((await repository.GetCachedItemResultsAsync(contract.PncpId, 1))!.Results);
        var ftsSql = await ReadItemsFtsSqlAsync(database.Repository.DatabasePath);
        Assert.Equal(1, CountOccurrences(ftsSql, "prefix='2 3'"));

        await ExecuteAsync(
            database.Repository.DatabasePath,
            "UPDATE items SET search_text = 'arame galvanizado' " +
            "WHERE contract_id = $contract AND item_number = 1;",
            ("$contract", contract.PncpId));
        Assert.Equal(0, await CountFtsMatchesAsync(database.Repository.DatabasePath, "cafe*"));
        Assert.Equal(1, await CountFtsMatchesAsync(database.Repository.DatabasePath, "ara*"));

        await ExecuteAsync(
            database.Repository.DatabasePath,
            "DELETE FROM items WHERE contract_id = $contract AND item_number = 1;",
            ("$contract", contract.PncpId));
        Assert.Equal(0, await CountFtsMatchesAsync(database.Repository.DatabasePath, "ara*"));

        var repeated = await repository.InitializeAsync();
        Assert.Equal(26, repeated.PreviousVersion);
        Assert.Equal(26, repeated.CurrentVersion);
        Assert.Empty(repeated.AppliedMigrations);
    }

    [Fact]
    public async Task Migration25To26_CancellationLeavesVersionAndOldIndexIntact()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = PriceCacheTests.RecentContract(
            "schema-26-cancel",
            DateOnly.FromDateTime(DateTime.Today),
            1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(
            contract.PncpId,
            [PriceCacheTests.Item(contract, 1)],
            false);
        await DowngradeTo25Async(database.Repository.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<DatabaseInitializationProgress>(value =>
        {
            if (value.Phase == "Preparando pesquisa por prefixo")
            {
                cancellation.Cancel();
            }
        });
        var repository = new SqliteContractRepository(database.Repository.DatabasePath);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.InitializeAsync(cancellation.Token, progress));
        SqliteConnection.ClearAllPools();

        Assert.Equal(25, await ReadSchemaVersionAsync(database.Repository.DatabasePath));
        Assert.DoesNotContain(
            "prefix=",
            await ReadItemsFtsSqlAsync(database.Repository.DatabasePath),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await CountFtsMatchesAsync(database.Repository.DatabasePath, "cafe*"));

        var recovered = await repository.InitializeAsync();
        Assert.Equal(26, recovered.CurrentVersion);
        Assert.Equal([26], recovered.AppliedMigrations);
    }

    private static async Task DowngradeTo25Async(string path)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS items_fts_insert;
            DROP TRIGGER IF EXISTS items_fts_delete;
            DROP TRIGGER IF EXISTS items_fts_update;
            DROP TABLE IF EXISTS items_fts;
            CREATE VIRTUAL TABLE items_fts USING fts5(
                search_text,
                content='items',
                content_rowid='rowid',
                tokenize='unicode61 remove_diacritics 2'
            );
            INSERT INTO items_fts(items_fts) VALUES('rebuild');
            CREATE TRIGGER items_fts_insert AFTER INSERT ON items BEGIN
                INSERT INTO items_fts(rowid, search_text) VALUES(new.rowid, new.search_text);
            END;
            CREATE TRIGGER items_fts_delete AFTER DELETE ON items BEGIN
                INSERT INTO items_fts(items_fts, rowid, search_text)
                VALUES('delete', old.rowid, old.search_text);
            END;
            CREATE TRIGGER items_fts_update AFTER UPDATE OF search_text ON items BEGIN
                INSERT INTO items_fts(items_fts, rowid, search_text)
                VALUES('delete', old.rowid, old.search_text);
                INSERT INTO items_fts(rowid, search_text) VALUES(new.rowid, new.search_text);
            END;
            UPDATE schema_info SET version = 25 WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountFtsMatchesAsync(string path, string match)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items_fts WHERE items_fts MATCH $match;";
        command.Parameters.AddWithValue("$match", match);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadItemsFtsSqlAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'items_fts';";
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async Task<int> ReadSchemaVersionAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(
        string path,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }
        return count;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
