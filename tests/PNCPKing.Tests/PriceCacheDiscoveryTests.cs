using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using static PNCPKing.Tests.PriceCacheTests;

namespace PNCPKing.Tests;

public sealed class PriceCacheDiscoveryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(49, false)]
    [InlineData(50, false)]
    [InlineData(51, false)]
    [InlineData(501, false)]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, true)]
    [InlineData(501, true)]
    public async Task PagesConfirmLookaheadWithoutLosingOrDuplicatingPrices(int count, bool oneItem)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, count, oneItem);
        var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var query = new SearchQuery("café", GeoScope.All, Sort: SearchSort.Nearest);
        var expression = SearchText.Parse(query.Text);
        var actual = new List<string>();
        PriceCacheLocalCursor? cursor = null;
        for (var pageNumber = 1; ; pageNumber++)
        {
            var progress = new List<PriceCacheLocalProgress>();
            var page = await repository.SearchLocalAfterAsync(query, expression, null, null, cursor, 50,
                PriceCacheLocalReadOrder.Discovery, new InlineProgress(progress.Add));
            Assert.Equal(pageNumber, page.Page);
            Assert.Equal(Math.Min(50, count - actual.Count), page.Rows!.Count);
            Assert.Equal(page.Rows.Select(Key), progress.SelectMany(value => value.Rows).Select(Key));
            Assert.True(progress[^1].Completed);
            Assert.Equal(page.Cursor, progress[^1].Cursor);
            actual.AddRange(page.Rows.Select(Key));
            Assert.Equal(actual.Count < count, page.HasMore);
            if (page.Rows.Count > 0) Assert.NotNull(page.Cursor!.ItemRowId);
            if (!page.HasMore) break;
            cursor = page.Cursor;
            Assert.True(pageNumber < 20);
        }
        Assert.Equal(Enumerable.Range(1, count).Select(n => oneItem ? $"prices|1|{n}" : $"prices|{n}|1"), actual);
        Assert.Equal(count, actual.Distinct().Count());
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 50)]
    [InlineData(true, 50)]
    public async Task CancellationKeepsDeliveredPrefixAndResumesBeforeLookahead(bool oneItem, int stopAfter)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, 100, oneItem);
        var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var query = new SearchQuery("café", GeoScope.All);
        var expression = SearchText.Parse(query.Text);
        var delivered = new List<ItemSearchRow>();
        PriceCacheLocalCursor? cursor = null;
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SearchLocalAfterAsync(
            query, expression, null, null, null, 50, PriceCacheLocalReadOrder.Discovery,
            new InlineProgress(value =>
            {
                delivered.AddRange(value.Rows);
                cursor = value.Cursor;
                if (delivered.Count == stopAfter) cancellation.Cancel();
            }), cancellation.Token));
        Assert.Equal(stopAfter, delivered.Count);
        var rest = await ReadAllAsync(repository, query, PriceCacheLocalReadOrder.Discovery, cursor);
        var actual = delivered.Concat(rest).Select(Key).ToArray();
        Assert.Equal(Enumerable.Range(1, 100).Select(n => oneItem ? $"prices|1|{n}" : $"prices|{n}|1"), actual);
    }

    [Theory]
    [InlineData("limpeza -odontológico ~8 \"diária", "all", true)]
    [InlineData("limpeza -odontológico", "SP", true)]
    [InlineData("limpeza", "southeast", false)]
    [InlineData("limpeza", "near", false)]
    [InlineData("\"diária", "all", true)]
    [InlineData("-odontológico \"diária", "all", false)]
    public async Task DiscoveryPreservesTheFullFilteredSet(string text, string geography, bool period)
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contracts = new[]
        {
            RecentContract("old", today.AddDays(-40), 1),
            RecentContract("null", today, 2) with { PublicationDate = null },
            RecentContract("stale", today, 3),
            RecentContract("sp", today.AddDays(-1), 4),
            RecentContract("rj", today, 5) with { Uf = "RJ" },
            RecentContract("ba", today, 6) with { Uf = "BA" },
            RecentContract("future", today.AddDays(1), 7)
        };
        await database.Repository.UpsertContractsAsync(contracts);
        foreach (var contract in contracts)
        {
            var items = Enumerable.Range(1, 9).Select(n => Item(contract, n) with
            {
                Description = n switch { 2 => "limpeza odontológico 8 horas", 3 => "limpeza 80 horas", _ => "limpeza predial 8 horas" },
                Unit = n == 4 ? "HORA" : "DIÁRIAS"
            }).ToArray();
            await database.Repository.UpsertItemsAsync(contract.PncpId, items, false);
            foreach (var item in items)
                await database.Repository.ReplaceItemResultsAsync(contract.PncpId, item.ItemNumber,
                    [Result(contract, item.ItemNumber, 1, item.ItemNumber != 5) with
                    { HomologatedUnitValueScaled = DecimalScale.ToScaled(item.ItemNumber switch { 6 => 0m, 7 => 100m, _ => 25m }) }]);
        }
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE contract_item_snapshots SET source_global_updated_at = '' WHERE contract_id = 'stale';
                UPDATE items SET hydration_status = 0 WHERE item_number = 8;
                UPDATE contracts SET geo_layer = CASE WHEN pncp_id = 'sp' THEN 0 ELSE 1 END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var geo = geography switch
        {
            "SP" => SearchGeoFilter.State("SP"), "southeast" => SearchGeoFilter.Southeast,
            "near" => SearchGeoFilter.NearRibeirao, _ => SearchGeoFilter.All
        };
        var query = new SearchQuery(text, geo, period ? today.AddDays(-30) : null,
            period ? today : null, Sort: SearchSort.Newest);
        var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var expected = await ReadAllAsync(repository, query, PriceCacheLocalReadOrder.RequestedSort, minimum: 20, maximum: 30);
        var actual = await ReadAllAsync(repository, query, PriceCacheLocalReadOrder.Discovery, minimum: 20, maximum: 30);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(Key).Order(), actual.Select(Key).Order());
        Assert.Equal(actual.Count, actual.Select(Key).Distinct().Count());
        Assert.DoesNotContain(actual, row => row.Contract.PncpId == "stale" || row.Item.ItemNumber is 5 or 6 or 7 or 8);
        Assert.All(actual, row => Assert.True(SearchText.Parse(text).MatchesItem(row.Item.Description, row.Item.Unit)));
    }

    [Fact]
    public async Task LaterCandidatesAreVisitedAndRequestedSortDoesNotChangeDiscoveryOrder()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RecentContract("late", DateOnly.FromDateTime(DateTime.Today), 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, Enumerable.Range(1, 603)
            .Select(n => Item(contract, n) with { Unit = n > 600 ? "DIÁRIA" : "HORA" }).ToArray(), false);
        for (var n = 601; n <= 603; n++)
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, n, [Result(contract, n, 1, true)]);
        var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        foreach (var sort in Enum.GetValues<SearchSort>())
        {
            var rows = await ReadAllAsync(repository, new SearchQuery("café \"diária", GeoScope.All, Sort: sort),
                PriceCacheLocalReadOrder.Discovery);
            Assert.Equal(new[] { "late|601|1", "late|602|1", "late|603|1" }, rows.Select(Key));
        }
    }

    [Fact]
    public async Task ExplicitContractPriorityUsesSeparatePassesAndPreservesAllItems()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contracts = new[]
        {
            RecentContract("other", today, 1) with { Object = "Compra escolar" },
            RecentContract("priority", today.AddDays(-1), 2) with { Object = "Compra hospitalar" }
        };
        await database.Repository.UpsertContractsAsync(contracts);
        foreach (var contract in contracts)
        {
            await database.Repository.UpsertItemsAsync(contract.PncpId, [Item(contract, 1)], false);
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1,
                Enumerable.Range(1, 3).Select(n => Result(contract, 1, n, true)).ToArray());
        }
        var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var query = new SearchQuery("café C:(hospitalar)", GeoScope.All, Sort: SearchSort.Newest);
        var rows = await ReadAllAsync(repository, query, PriceCacheLocalReadOrder.Discovery);
        Assert.Equal(new[] { "priority|1|1", "priority|1|2", "priority|1|3", "other|1|1", "other|1|2", "other|1|3" }, rows.Select(Key));
        var expected = await ReadAllAsync(repository, query, PriceCacheLocalReadOrder.RequestedSort);
        Assert.Equal(expected.Select(Key).Order(), rows.Select(Key).Order());
    }

    [Fact]
    public async Task CursorsFromDifferentReadOrdersCannotBeMixed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, 3, false);
        var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var query = new SearchQuery("café", GeoScope.All);
        var expression = SearchText.Parse(query.Text);
        var discovered = await repository.SearchLocalAfterAsync(query, expression, null, null, null, 1, PriceCacheLocalReadOrder.Discovery);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SearchLocalAfterAsync(query, expression,
            null, null, discovered.Cursor, 1));
        var ordered = await repository.SearchLocalAfterAsync(query, expression, null, null, null, 1);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SearchLocalAfterAsync(query, expression,
            null, null, ordered.Cursor, 1, PriceCacheLocalReadOrder.Discovery));
    }

    private static async Task SeedAsync(TestDatabase database, int count, bool oneItem)
    {
        var contract = RecentContract("prices", DateOnly.FromDateTime(DateTime.Today), 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var items = Enumerable.Range(1, oneItem ? 1 : count).Select(n => Item(contract, n)).ToArray();
        await database.Repository.UpsertItemsAsync(contract.PncpId, items, false);
        foreach (var item in items)
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, item.ItemNumber,
                Enumerable.Range(1, oneItem ? count : 1).Select(n => Result(contract, item.ItemNumber, n, true)).ToArray());
    }

    private static async Task<List<ItemSearchRow>> ReadAllAsync(SqlitePriceCacheRepository repository,
        SearchQuery query, PriceCacheLocalReadOrder order, PriceCacheLocalCursor? cursor = null,
        decimal? minimum = null, decimal? maximum = null)
    {
        var rows = new List<ItemSearchRow>();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var page = await repository.SearchLocalAfterAsync(query, SearchText.Parse(query.Text),
                minimum, maximum, cursor, 2, order);
            rows.AddRange(page.Rows!);
            if (!page.HasMore) return rows;
            Assert.NotEqual(cursor, page.Cursor);
            cursor = page.Cursor;
        }
        throw new InvalidOperationException("A pesquisa não esgotou os candidatos.");
    }

    private static string Key(ItemSearchRow row) => $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}";
    private sealed class InlineProgress(Action<PriceCacheLocalProgress> report) : IProgress<PriceCacheLocalProgress>
    { public void Report(PriceCacheLocalProgress value) => report(value); }
}
