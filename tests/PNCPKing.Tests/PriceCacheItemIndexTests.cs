using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using static PNCPKing.Tests.PriceCacheTests;

namespace PNCPKing.Tests;

public sealed class PriceCacheItemIndexTests
{
    [Theory]
    [InlineData(SearchSort.Newest, false, false, true)]
    [InlineData(SearchSort.Newest, true, false, true)]
    [InlineData(SearchSort.Nearest, false, false, true)]
    [InlineData(SearchSort.Nearest, true, false, true)]
    [InlineData(SearchSort.Newest, false, true, true)]
    [InlineData(SearchSort.Newest, true, true, true)]
    [InlineData(SearchSort.Nearest, false, true, true)]
    [InlineData(SearchSort.Nearest, true, true, true)]
    [InlineData(SearchSort.Newest, false, false, false)]
    [InlineData(SearchSort.Newest, true, false, false)]
    [InlineData(SearchSort.Nearest, false, false, false)]
    [InlineData(SearchSort.Nearest, true, false, false)]
    [InlineData(SearchSort.Newest, false, true, false)]
    [InlineData(SearchSort.Newest, true, true, false)]
    [InlineData(SearchSort.Nearest, false, true, false)]
    [InlineData(SearchSort.Nearest, true, true, false)]
    public async Task IndexedSearch_PreservesFiltersResultTiesAndCompleteCursorPages(
        SearchSort sort, bool stateOnly, bool batches, bool filterUnit)
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contracts = new[]
        {
            RecentContract("clean-a", today, 1),
            RecentContract("clean-b", today, 2),
            RecentContract("clean-c", today.AddDays(-1), 3),
            RecentContract("clean-rj", today, 4) with { Uf = "RJ" },
            RecentContract("clean-old", today.AddDays(-31), 5),
            RecentContract("clean-future", today.AddDays(1), 6),
            RecentContract("clean-missing", today, 7) with { PublicationDate = null },
            RecentContract("clean-stale", today, 8)
        };
        await database.Repository.UpsertContractsAsync(contracts);
        foreach (var contract in contracts)
        {
            var items = Enumerable.Range(1, 9).Select(number => Item(contract, number) with
            {
                Description = number switch
                {
                    2 => "Serviço de limpeza odontológico 8 horas",
                    4 => "Serviço de pintura 8 horas",
                    _ => "Serviço de limpeza predial 8 horas"
                },
                Unit = number switch { 3 => "HORA", 5 => "Preço por diária", _ => "DIÁRIAS" },
                HydrationStatus = ItemHydrationStatus.Complete
            }).ToArray();
            await database.Repository.UpsertItemsAsync(contract.PncpId, items, false);
            foreach (var item in items)
            {
                var price = item.ItemNumber switch { 6 => 100m, 7 => 0m, 8 => 10m, _ => 25m };
                var results = new List<HomologationResult>
                {
                    Result(contract, item.ItemNumber, 1, item.ItemNumber != 9) with
                    { HomologatedUnitValueScaled = DecimalScale.ToScaled(price) }
                };
                if (item.ItemNumber == 1) results.Add(Result(contract, 1, 2, true));
                await database.Repository.ReplaceItemResultsAsync(contract.PncpId, item.ItemNumber, results);
            }
        }
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE contract_item_snapshots SET source_global_updated_at = '' WHERE contract_id = 'clean-stale';
                UPDATE contracts SET geo_layer = CASE WHEN uf = 'SP' THEN 0 ELSE 1 END,
                    municipality_distance_rank = CASE pncp_id WHEN 'clean-a' THEN 3 WHEN 'clean-b' THEN 1 ELSE 0 END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var text = "Serviço limpeza -odontológico" + (filterUnit ? " \"diária" : "");
        var query = new SearchQuery(text, stateOnly ? SearchGeoFilter.State("SP") : SearchGeoFilter.All,
            today.AddDays(-30), today, Sort: sort);
        var telemetry = new SearchTelemetry();
        var cache = new SqlitePriceCacheRepository(new SqliteConnectionFactory(database.Repository.DatabasePath,
            resourceProbe: new SqliteCalibrationTests.Probe(), tuning: new(32, batches)), telemetry);
        var expression = SearchText.Parse(text);
        var actual = new List<string>();
        PriceCacheLocalCursor? cursor = null;
        var pageCount = 0;
        do
        {
            var page = await cache.SearchLocalAfterAsync(query, expression, 20m, 30m, cursor, 2);
            Assert.NotEmpty(page.Rows!);
            if (page.HasMore) Assert.Equal(2, page.Rows!.Count);
            actual.AddRange(page.Rows!.Select(Key));
            Assert.True(++pageCount <= 10, "A paginação não avançou.");
            cursor = page.Cursor;
            if (!page.HasMore) break;
        } while (true);

        var expected = contracts.Take(stateOnly ? 3 : 4)
            .OrderBy(c => sort == SearchSort.Nearest && c.Uf != "SP" ? 1 : 0)
            .ThenBy(c => sort == SearchSort.Nearest ? c.PncpId switch { "clean-a" => 3, "clean-b" => 1, _ => 0 } : 0)
            .ThenByDescending(c => c.PublicationDate)
            .ThenBy(c => c.PncpId, StringComparer.Ordinal)
            .SelectMany(c => (filterUnit ? new[] { "1|1", "1|2", "5|1" } : new[] { "1|1", "1|2", "3|1", "5|1" })
                .Select(suffix => $"{c.PncpId}|{suffix}"));
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Count, actual.Distinct().Count());
        Assert.DoesNotContain("local-contract-chunks", telemetry.Phases);
        Assert.DoesNotContain("fts-cardinality", telemetry.Phases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexedSearch_HandlesMissingPublicationAndAppliesApproximationBeforeLimit(bool batches)
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var dated = RecentContract("dated", today, 1);
        var undated = RecentContract("undated", today, 2) with { PublicationDate = null };
        await database.Repository.UpsertContractsAsync([dated, undated]);
        foreach (var contract in new[] { dated, undated })
        {
            var items = Enumerable.Range(1, 12).Select(number => Item(contract, number) with
            {
                Description = $"Serviço de limpeza {(number < 11 ? 2 : 8)} horas",
                Unit = number == 12 ? "HORA" : "DIÁRIA",
                HydrationStatus = ItemHydrationStatus.Complete
            }).ToArray();
            await database.Repository.UpsertItemsAsync(contract.PncpId, items, false);
            foreach (var item in items)
                await database.Repository.ReplaceItemResultsAsync(contract.PncpId, item.ItemNumber,
                    [Result(contract, item.ItemNumber, 1, true)]);
        }
        const string text = "limpeza ~8 \"diária \"hora";
        var query = new SearchQuery(text, GeoScope.All, Sort: SearchSort.Newest);
        var cache = new SqlitePriceCacheRepository(new SqliteConnectionFactory(database.Repository.DatabasePath,
            resourceProbe: new SqliteCalibrationTests.Probe(), tuning: new(32, batches)));
        var first = await cache.SearchLocalAfterAsync(query, SearchText.Parse(text), null, null, null, 2);
        var second = await cache.SearchLocalAfterAsync(query, SearchText.Parse(text), null, null, first.Cursor, 2);
        Assert.Equal(new[] { "dated|11|1", "dated|12|1" }, first.Rows!.Select(Key));
        Assert.True(first.HasMore);
        Assert.Equal(new[] { "undated|11|1", "undated|12|1" }, second.Rows!.Select(Key));
        Assert.False(second.HasMore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchesRevealStablePrefixesBeforePageCompletesAndResumeAfterCancellation(bool multipleResults)
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("progressive", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var items = Enumerable.Range(1, multipleResults ? 1 : 100).Select(n => Item(contract, n) with
        { Description = "café torrado", Unit = "PACOTE", HydrationStatus = ItemHydrationStatus.Complete }).ToArray();
        await database.Repository.UpsertItemsAsync(contract.PncpId, items, false);
        foreach (var item in items)
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, item.ItemNumber,
                Enumerable.Range(1, multipleResults ? 100 : 1)
                    .Select(sequence => Result(contract, item.ItemNumber, sequence, true)).ToArray());
        var connections = new SqliteConnectionFactory(database.Repository.DatabasePath,
            resourceProbe: new SqliteCalibrationTests.Probe(), tuning: new(32, true));
        var cache = new SqlitePriceCacheRepository(connections);
        var query = new SearchQuery("café", GeoScope.All, Sort: SearchSort.Newest);
        var expression = SearchText.Parse(query.Text);
        var observed = new List<PriceCacheLocalProgress>();
        var page = await cache.SearchLocalAfterAsync(query, expression, null, null, null, 50,
            new InlineProgress(observed.Add));
        Assert.True(observed.Count(value => value.Rows.Count > 0 && !value.Completed) >= 2);
        Assert.Equal(page.Rows!.Select(Key), observed.SelectMany(value => value.Rows).Select(Key));
        Assert.True(observed[^1].Completed);
        Assert.Equal(page.Cursor, observed[^1].Cursor);
        Assert.Equal(page.HasMore, observed[^1].HasMore);

        using var cancellation = new CancellationTokenSource();
        PriceCacheLocalProgress? prefix = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SearchLocalAfterAsync(query, expression,
            null, null, null, 50, new InlineProgress(value =>
            {
                if (value.Rows.Count > 0) { prefix = value; cancellation.Cancel(); }
            }), cancellation.Token));
        Assert.NotNull(prefix);
        Assert.Single(prefix.Rows);
        var resumed = await cache.SearchLocalAfterAsync(query, expression, null, null, prefix.Cursor, 50);
        var keys = prefix.Rows.Concat(resumed.Rows!).Select(Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, keys.Length).Select(n => multipleResults ? $"progressive|1|{n}" : $"progressive|{n}|1"), keys);
    }

    [Theory]
    [InlineData(SearchSort.Newest)]
    [InlineData(SearchSort.Nearest)]
    public async Task StreamingPreservesMissingDatesAndGeographicGroupsAcrossCursors(SearchSort sort)
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contracts = new[]
        {
            RecentContract("dated", today, 1),
            RecentContract("null-a", today, 2) with { PublicationDate = null },
            RecentContract("empty-b", today, 3) with { PublicationDate = null },
            RecentContract("null-c", today, 4) with { PublicationDate = null },
            RecentContract("distant", today, 5) with { Uf = "RJ" }
        };
        await database.Repository.UpsertContractsAsync(contracts);
        foreach (var contract in contracts)
        {
            await database.Repository.UpsertItemsAsync(contract.PncpId,
                [Item(contract, 1) with { Description = "café", HydrationStatus = ItemHydrationStatus.Complete }], false);
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1,
                [Result(contract, 1, 1, true), Result(contract, 1, 2, true)]);
        }
        var factory = new SqliteConnectionFactory(database.Repository.DatabasePath,
            resourceProbe: new SqliteCalibrationTests.Probe(), tuning: new(32, true));
        await using (var connection = await factory.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE contracts SET publication_date = '' WHERE pncp_id = 'empty-b';
                UPDATE contracts SET geo_layer = CASE WHEN pncp_id = 'distant' THEN 1 ELSE 0 END,
                    municipality_distance_rank = CASE WHEN pncp_id = 'distant' THEN NULL ELSE 3 END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var query = new SearchQuery("café", GeoScope.All, Sort: sort);
        var expression = SearchText.Parse(query.Text);
        var reference = new SqlitePriceCacheRepository(new SqliteConnectionFactory(database.Repository.DatabasePath,
            resourceProbe: new SqliteCalibrationTests.Probe(), tuning: new(32, false)));
        var expected = await reference.SearchLocalAfterAsync(query, expression, null, null, null, 50);
        var streaming = new SqlitePriceCacheRepository(factory);
        var actual = new List<string>();
        PriceCacheLocalCursor? cursor = null;
        for (var pageNumber = 0; pageNumber < 20; pageNumber++)
        {
            var page = await streaming.SearchLocalAfterAsync(query, expression, null, null, cursor, 1);
            actual.AddRange(page.Rows!.Select(Key));
            if (!page.HasMore) break;
            cursor = page.Cursor;
        }
        Assert.Equal(expected.Rows!.Select(Key), actual);
        Assert.Equal(10, actual.Count);
    }

    private sealed class InlineProgress(Action<PriceCacheLocalProgress> action) : IProgress<PriceCacheLocalProgress>
    { public void Report(PriceCacheLocalProgress value) => action(value); }

    private static string Key(ItemSearchRow row) => $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}";

    private sealed class SearchTelemetry : IPerformanceTelemetry
    {
        public List<string> Phases { get; } = [];
        public PerformanceSpan Begin(string operation, string phase = "total")
        {
            Phases.Add(phase);
            return new PerformanceSpan(this, operation, phase);
        }
        public void Record(string operation, string phase, TimeSpan duration, long rows = 0, long bytes = 0,
            bool succeeded = true, string? errorKind = null) { }
        public PerformanceReport CreateReport() => throw new NotSupportedException();
    }
}
