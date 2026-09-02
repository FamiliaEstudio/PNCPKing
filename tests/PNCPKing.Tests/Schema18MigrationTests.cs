using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class Schema18MigrationTests
{
    [Fact]
    public async Task Migration17To18_CreatesExactIndexPreservesDataAndIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            LocatedContract("ribeirao", "Ribeirão Preto", "3543402", "SP", 1),
            LocatedContract("itamogi", "Itamogi", "3132909", "MG", 2)
        ]);
        await DowngradeTo17Async(database.Repository.DatabasePath);
        var progress = new List<DatabaseInitializationProgress>();
        var repository = new SqliteContractRepository(database.Repository.DatabasePath);

        var result = await repository.InitializeAsync(progress: new InlineProgress<DatabaseInitializationProgress>(
            value => progress.Add(value)));

        Assert.Equal(17, result.PreviousVersion);
        Assert.Equal(26, result.CurrentVersion);
        Assert.Equal([18, 19, 20, 21, 22, 23, 24, 25, 26], result.AppliedMigrations);
        Assert.Equal((2L, 0L, 0L), await repository.GetCountsAsync());
        Assert.Contains(progress, value =>
            value.Message == "Otimizando banco v17 → v18; nenhum backup está sendo importado.");
        var indexSql = await ReadIndexSqlAsync(
            database.Repository.DatabasePath,
            "idx_contracts_nearest_order");
        Assert.Contains("CASE WHEN geo_layer = 0", indexSql, StringComparison.Ordinal);
        Assert.Contains("municipality_distance_rank", indexSql, StringComparison.Ordinal);
        Assert.Contains("state_proximity_rank", indexSql, StringComparison.Ordinal);
        Assert.Null(await ReadIndexSqlAsync(
            database.Repository.DatabasePath,
            "idx_contracts_geo_publication_id"));
        Assert.Contains(
            "idx_contracts_nearest_order",
            await ReadNearestQueryPlanAsync(database.Repository.DatabasePath),
            StringComparison.Ordinal);

        var repeated = await repository.InitializeAsync();

        Assert.Equal(26, repeated.PreviousVersion);
        Assert.Equal(26, repeated.CurrentVersion);
        Assert.Empty(repeated.AppliedMigrations);
    }

    [Fact]
    public async Task Migration17To18_CancellationRollsBackIndexAndVersion()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            LocatedContract("preserved", "Ribeirão Preto", "3543402", "SP", 1)
        ]);
        await DowngradeTo17Async(database.Repository.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        var repository = new SqliteContractRepository(database.Repository.DatabasePath);
        var progress = new InlineProgress<DatabaseInitializationProgress>(value =>
        {
            if (value.Percentage >= 40)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.InitializeAsync(cancellation.Token, progress));
        SqliteConnection.ClearAllPools();

        Assert.Equal(17, await ReadSchemaVersionAsync(database.Repository.DatabasePath));
        Assert.NotNull(await ReadIndexSqlAsync(
            database.Repository.DatabasePath,
            "idx_contracts_geo_publication_id"));
        Assert.Null(await ReadIndexSqlAsync(
            database.Repository.DatabasePath,
            "idx_contracts_nearest_order"));

        var recovered = new SqliteContractRepository(database.Repository.DatabasePath);
        var result = await recovered.InitializeAsync();
        Assert.Equal(26, result.CurrentVersion);
        Assert.Equal((1L, 0L, 0L), await recovered.GetCountsAsync());
    }

    [Fact]
    public async Task Migration17To18_PreservesNearestOrderingAcrossPagesAndFilters()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            LocatedContract("ribeirao-new", "Ribeirão Preto", "3543402", "SP", 8),
            LocatedContract("ribeirao-old", "Ribeirão Preto", "3543402", "SP", 2),
            LocatedContract("itamogi", "Itamogi", "3132909", "MG", 7),
            LocatedContract("sao-paulo", "São Paulo", "3550308", "SP", 6),
            LocatedContract("belo-horizonte", "Belo Horizonte", "3106200", "MG", 5),
            LocatedContract("salvador", "Salvador", "2927408", "BA", 4),
            RepositorySearchTests.Contract("excluded-text", "Aquisição de açúcar", "SP", 3)
        ]);
        await DowngradeTo17Async(database.Repository.DatabasePath);
        var repository = new SqliteContractRepository(database.Repository.DatabasePath);
        var query = new SearchQuery(
            "cafe",
            SearchGeoFilter.NearRibeirao,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            Page: 1,
            PageSize: 2,
            Sort: SearchSort.Nearest);
        var before = await ReadAllPagesAsync(repository, query);

        await repository.InitializeAsync();
        var after = await ReadAllPagesAsync(repository, query);

        Assert.Equal(before, after);
        Assert.Equal(after.Count, after.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("ribeirao-new", after[0]);
        Assert.Equal("ribeirao-old", after[1]);
        Assert.DoesNotContain("excluded-text", after);
    }

    private static async Task<IReadOnlyList<string>> ReadAllPagesAsync(
        SqliteContractRepository repository,
        SearchQuery query)
    {
        var ids = new List<string>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await repository.SearchPageAsync(query with { Page = pageNumber });
            ids.AddRange(page.Results.Select(value => value.PncpId));
            if (!page.MayHaveMore)
            {
                return ids;
            }
        }
    }

    private static async Task DowngradeTo17Async(string path)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX IF EXISTS idx_contracts_nearest_order;
            CREATE INDEX IF NOT EXISTS idx_contracts_geo_publication_id
                ON contracts(geo_layer, municipality_distance_rank, publication_date DESC, pncp_id);
            UPDATE schema_info SET version = 17 WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ReadSchemaVersionAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadIndexSqlAsync(string path, string name)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<string> ReadNearestQueryPlanAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT c.pncp_id
              FROM contracts c
             ORDER BY c.geo_layer,
                      CASE WHEN c.geo_layer = 0
                           THEN COALESCE(c.municipality_distance_rank, 999999)
                           ELSE COALESCE(c.state_proximity_rank, 999)
                      END,
                      c.publication_date DESC,
                      c.pncp_id
             LIMIT 20;
            """;
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            details.Add(reader.GetString(3));
        }

        return string.Join(Environment.NewLine, details);
    }

    private static ContractRecord LocatedContract(
        string id,
        string municipality,
        string ibgeCode,
        string uf,
        int sequence) =>
        RepositorySearchTests.Contract(id, "Aquisição de café", uf, sequence) with
        {
            Municipality = municipality,
            MunicipalityIbgeCode = ibgeCode
        };

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
