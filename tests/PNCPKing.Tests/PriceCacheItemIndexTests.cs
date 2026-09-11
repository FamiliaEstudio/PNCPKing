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
    [InlineData(SearchSort.Newest, false)]
    [InlineData(SearchSort.Newest, true)]
    [InlineData(SearchSort.Nearest, false)]
    [InlineData(SearchSort.Nearest, true)]
    public async Task IndexedSearch_PreservesFiltersResultTiesAndCompleteCursorPages(SearchSort sort, bool stateOnly)
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

        const string text = "Serviço limpeza -odontológico \"diária";
        var query = new SearchQuery(text, stateOnly ? SearchGeoFilter.State("SP") : SearchGeoFilter.All,
            today.AddDays(-30), today, Sort: sort);
        var telemetry = new SearchTelemetry();
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath, telemetry);
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
            .SelectMany(c => new[] { $"{c.PncpId}|1|1", $"{c.PncpId}|1|2", $"{c.PncpId}|5|1" });
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Count, actual.Distinct().Count());
        Assert.DoesNotContain("local-contract-chunks", telemetry.Phases);
        Assert.DoesNotContain("fts-cardinality", telemetry.Phases);
    }

    [Fact]
    public async Task IndexedSearch_HandlesMissingPublicationAndAppliesApproximationBeforeLimit()
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
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var first = await cache.SearchLocalAfterAsync(query, SearchText.Parse(text), null, null, null, 2);
        var second = await cache.SearchLocalAfterAsync(query, SearchText.Parse(text), null, null, first.Cursor, 2);
        Assert.Equal(new[] { "dated|11|1", "dated|12|1" }, first.Rows!.Select(Key));
        Assert.True(first.HasMore);
        Assert.Equal(new[] { "undated|11|1", "undated|12|1" }, second.Rows!.Select(Key));
        Assert.False(second.HasMore);
    }

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
