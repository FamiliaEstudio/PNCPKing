using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using static PNCPKing.Tests.PriceCacheTests;

namespace PNCPKing.Tests;

public sealed class MainSearchLocalSequenceTests
{
    [Theory]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(51)]
    [InlineData(125)]
    public async Task AlternatesPagesAndMatchesTheUnionOfIndependentSearches(int count)
    {
        await using var database = await SeedAsync(count);
        var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var query = new SearchQuery("café -solúvel + açúcar \"pacote + café açúcar", GeoScope.All);
        var expressions = SearchText.ParseMainCriteria(query.Text);
        var sequence = new MainSearchLocalSequence(repository, query, expressions);
        var parts = new List<int>();
        var actual = new HashSet<string>();
        var overlappingRows = 0;
        while (sequence.HasMore)
        {
            var streamed = new List<string>();
            var page = await sequence.LoadNextAsync(null, null, (_, value) =>
                streamed.AddRange(value.Rows.Select(Key)));
            Assert.NotNull(page);
            Assert.InRange(page.Rows!.Count, 0, 50);
            Assert.Equal(page.Rows.Select(Key), streamed);
            parts.Add(sequence.CurrentPart);
            foreach (var row in page.Rows)
                if (!actual.Add(Key(row))) overlappingRows++;
            Assert.True(parts.Count < 30);
        }
        Assert.Equal([0, 1, 2], parts.Take(3));
        Assert.True(overlappingRows > 0);
        var expected = new HashSet<string>();
        foreach (var expression in expressions)
            foreach (var row in await ReadAllAsync(repository, query with { Text = expression.OriginalText }, expression))
                expected.Add(Key(row));
        Assert.Equal(expected.Order(), actual.Order());
        Assert.Null(await sequence.LoadNextAsync(null, null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public async Task CancellationResumesTheSamePartWithoutLosingDeliveredRows(int stopAfter)
    {
        await using var database = await SeedAsync(75);
        var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var query = new SearchQuery("café + açúcar", GeoScope.All);
        var sequence = new MainSearchLocalSequence(repository, query, SearchText.ParseMainCriteria(query.Text));
        var actual = new HashSet<string>();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sequence.LoadNextAsync(null, null, (part, value) =>
        {
            Assert.Equal(0, part);
            foreach (var row in value.Rows) Assert.True(actual.Add(Key(row)));
            if (actual.Count == stopAfter) cancellation.Cancel();
        }, cancellation.Token));
        Assert.Equal(stopAfter, actual.Count);
        var next = await sequence.LoadNextAsync(null, null);
        Assert.Equal(0, sequence.CurrentPart);
        foreach (var row in next!.Rows!) Assert.True(actual.Add(Key(row)));
        while (sequence.HasMore)
        {
            var page = await sequence.LoadNextAsync(null, null);
            foreach (var row in page!.Rows!) actual.Add(Key(row));
        }
        Assert.Equal(150, actual.Count);
    }

    [Fact]
    public async Task EmptyPartsAreSkippedAndOverlappingPagesRemainBounded()
    {
        await using var database = await SeedAsync(75);
        var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var query = new SearchQuery("inexistente + café + café", GeoScope.All);
        var sequence = new MainSearchLocalSequence(repository, query, SearchText.ParseMainCriteria(query.Text));
        var visited = new HashSet<int>();
        var first = await sequence.LoadNextAsync(null, null, (part, _) => visited.Add(part));
        Assert.Equal([0, 1], visited.Order());
        Assert.Equal(1, sequence.CurrentPart);
        var duplicate = await sequence.LoadNextAsync(null, null);
        Assert.Equal(2, sequence.CurrentPart);
        Assert.Equal(first!.Rows!.Select(Key), duplicate!.Rows!.Select(Key));
        var remaining = await sequence.LoadNextAsync(null, null);
        Assert.Equal(1, sequence.CurrentPart);
        Assert.DoesNotContain(remaining!.Rows!, row => first.Rows!.Any(old => Key(old) == Key(row)));

        var emptyQuery = query with { Text = "inexistente + ausente" };
        var empty = new MainSearchLocalSequence(repository, emptyQuery, SearchText.ParseMainCriteria(emptyQuery.Text));
        Assert.Empty((await empty.LoadNextAsync(null, null))!.Rows!);
        Assert.False(empty.HasMore);
        Assert.Equal(1, empty.ActionsStarted);
    }

    [Fact]
    public async Task RestartResetsAllCursorsAndAppliesSharedPriceDateAndGeographyFilters()
    {
        await using var database = await SeedAsync(75);
        var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var query = new SearchQuery("café + açúcar", SearchGeoFilter.State("SP"), today.AddDays(-5), today);
        var expressions = SearchText.ParseMainCriteria(query.Text);
        var sequence = new MainSearchLocalSequence(repository, query, expressions);
        var first = await sequence.LoadNextAsync(null, null);
        await sequence.LoadNextAsync(null, null);
        var restarted = sequence.Restart();
        Assert.Equal(first!.Rows!.Select(Key), (await restarted.LoadNextAsync(null, null))!.Rows!.Select(Key));
        restarted = sequence.Restart();
        var actual = new List<string>();
        while (restarted.HasMore)
            actual.AddRange((await restarted.LoadNextAsync(20m, 30m))!.Rows!.Select(Key));
        var expected = new List<string>();
        foreach (var expression in expressions)
            expected.AddRange((await ReadAllAsync(repository, query with { Text = expression.OriginalText }, expression, 20m, 30m)).Select(Key));
        Assert.NotEmpty(actual);
        Assert.Equal(expected.Distinct().Order(), actual.Distinct().Order());
        foreach (var filters in new[] { query with { GeoFilter = SearchGeoFilter.State("BA") },
                     query with { EndDate = today.AddDays(-10), StartDate = today.AddDays(-20) } })
        {
            var filtered = new MainSearchLocalSequence(repository, filters, expressions);
            Assert.Empty((await filtered.LoadNextAsync(null, null))!.Rows!);
            Assert.False(filtered.HasMore);
        }
    }

    internal static async Task<TestDatabase> SeedAsync(int count, bool longDescriptions = false)
    {
        var database = await TestDatabase.CreateAsync();
        var contract = RecentContract("compound", DateOnly.FromDateTime(DateTime.Today).AddDays(-1), 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var suffix = longDescriptions ? string.Concat(Enumerable.Repeat("\nDescritivo técnico extenso com especificações e condições de fornecimento. ", 40)) : string.Empty;
        var items = Enumerable.Range(1, count * 2).Select(n => Item(contract, n) with
        {
            Description = (n % 2 == 0 ? "Açúcar cristal" : n % 5 == 0 ? "Café açúcar solúvel" : "Café torrado") + suffix,
            Unit = n % 7 == 0 ? "kg" : "pacote"
        }).ToArray();
        await database.Repository.UpsertItemsAsync(contract.PncpId, items, false);
        foreach (var item in items)
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, item.ItemNumber,
                [Result(contract, item.ItemNumber, 1, true) with
                { HomologatedUnitValueScaled = DecimalScale.ToScaled(item.ItemNumber % 3 == 0 ? 25m : 50m) }]);
        return database;
    }

    private static async Task<List<ItemSearchRow>> ReadAllAsync(SqlitePriceCacheRepository repository,
        SearchQuery query, SearchExpression expression, decimal? minimum = null, decimal? maximum = null)
    {
        var rows = new List<ItemSearchRow>();
        PriceCacheLocalCursor? cursor = null;
        while (true)
        {
            var page = await repository.SearchLocalAfterAsync(query, expression, minimum, maximum, cursor, 50,
                PriceCacheLocalReadOrder.Discovery);
            rows.AddRange(page.Rows!);
            if (!page.HasMore) return rows;
            cursor = page.Cursor;
        }
    }

    private static string Key(ItemSearchRow row) => $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}";
}
