using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class QuotationBasketTransferTests
{
    [Fact]
    public async Task ManualTransferPreservesAdjustmentsEvidenceAndAggregation()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var (projectId, lineId, references) = await SeedAsync(repo);
        var source = await repo.SaveManualBasketAsync(lineId, null, "Origem", ["a", "b"]);
        var destination = await repo.SaveManualBasketAsync(lineId, null, "Destino", ["c"]);
        var internet = Reference(lineId, "internet") with { Source = QuotationReferenceSource.InternetIncisoIII };
        var image = new EvidenceImageDescriptor { Sha256 = new string('a', 64), RelativePath = "evidence/a.png", ByteLength = 100, PixelWidth = 10, PixelHeight = 10 };
        var evidence = new InternetPriceEvidence
        {
            LineId = lineId, ReferenceId = internet.Id, SourceUrl = "https://example.com/preco",
            CapturedAt = DateTimeOffset.UtcNow, PriceImage = image, TaxIdImage = image
        };
        await repo.SaveInternetPriceReferenceAsync(internet, evidence, source.Id, source.Name);
        await repo.SetManualBasketConversionFactorAsync(source.Id, "a", 0.333333m);
        await repo.SetManualBasketPriceDivisorAsync(source.Id, "internet", 3m);
        await repo.SetManualBasketAggregationMethodAsync(source.Id, QuotationAggregationMethod.Median);
        await repo.ConfirmBasketAsync(lineId, source.Key);
        var beforeEvidence = await repo.GetInternetPriceEvidenceAsync(lineId);
        var beforeReferences = await repo.GetReferencesAsync(lineId);
        // Deliberately stale manual snapshots: persisted membership and adjustments must win.
        var result = await repo.TransferBasketReferencesAsync(lineId, Snapshot(source, references),
            Snapshot(destination, references), ["a", "internet"]);
        Assert.Equal(destination.Id, result);
        var baskets = await repo.GetManualBasketsAsync(lineId);
        var remaining = baskets.Single(basket => basket.Id == source.Id);
        Assert.Equal(new[] { "b" }, remaining.ReferenceIds);
        Assert.Equal(QuotationAggregationMethod.Median, remaining.AggregationMethod);
        var received = baskets.Single(basket => basket.Id == destination.Id);
        Assert.Equal(QuotationAggregationMethod.Mean, received.AggregationMethod);
        Assert.Equal(0.333333m, received.ConversionFactors["a"]);
        Assert.Equal(3m, received.PriceDivisors["internet"]);
        Assert.Equal(beforeReferences, await repo.GetReferencesAsync(lineId));
        Assert.Equal(beforeEvidence["internet"], (await repo.GetInternetPriceEvidenceAsync(lineId))["internet"]);
        Assert.False((await repo.GetLineAsync(projectId, lineId))!.SelectionConfirmed);
    }

    [Fact]
    public async Task EmptySourceIsRemovedWithoutDeletingGlobalReferences()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var (_, lineId, references) = await SeedAsync(repo);
        var source = await repo.SaveManualBasketAsync(lineId, null, "Origem", ["a"]);
        var destination = await repo.SaveManualBasketAsync(lineId, null, "Destino", ["c"]);
        await repo.TransferBasketReferencesAsync(lineId, Snapshot(source, references), Snapshot(destination, references), ["a"]);
        Assert.Equal(destination.Id, Assert.Single(await repo.GetManualBasketsAsync(lineId)).Id);
        Assert.Equal(3, (await repo.GetReferencesAsync(lineId)).Count);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("repeated")]
    [InlineData("partial")]
    [InlineData("empty")]
    [InlineData("same")]
    [InlineData("cancelled")]
    [InlineData("cross-item")]
    public async Task InvalidTransferDoesNotChangeEitherBasketOrConfirmation(string scenario)
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var (projectId, lineId, references) = await SeedAsync(repo);
        var source = await repo.SaveManualBasketAsync(lineId, null, "Origem", ["a", "b"]);
        var destination = await repo.SaveManualBasketAsync(lineId, null, "Destino", scenario == "duplicate" ? ["a", "c"] : ["c"]);
        if (scenario == "cross-item")
        {
            var (_, otherLine, otherRefs) = await SeedAsync(repo, "other-");
            destination = await repo.SaveManualBasketAsync(otherLine, null, "Outra", [otherRefs[0].Id]);
        }
        await repo.ConfirmBasketAsync(lineId, source.Key);
        var before = await repo.GetManualBasketsAsync(lineId);
        string[] selected = scenario switch { "repeated" => new[] { "a", "a" }, "partial" => ["a", "missing"], "empty" => [], _ => ["a"] };
        using var cancellation = new CancellationTokenSource();
        if (scenario == "cancelled") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => repo.TransferBasketReferencesAsync(lineId,
            Snapshot(source, references), Snapshot(scenario == "same" ? source : destination, references), selected, cancellation.Token));
        var after = await repo.GetManualBasketsAsync(lineId);
        Assert.Equal(before.Select(basket => (basket.Id, basket.UpdatedAt)), after.Select(basket => (basket.Id, basket.UpdatedAt)));
        foreach (var basket in before)
            Assert.Equal(basket.ReferenceIds, after.Single(value => value.Id == basket.Id).ReferenceIds);
        Assert.True((await repo.GetLineAsync(projectId, lineId))!.SelectionConfirmed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AutomaticSuggestionsAreCopiedWithoutChangingTheirInputs(bool automaticDestination, bool emptySource)
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var (_, lineId, references) = await SeedAsync(repo);
        var source = Automatic("auto-source", references.Take(2).ToArray()) with { AggregationMethod = QuotationAggregationMethod.Median };
        var destination = automaticDestination ? Automatic("auto-destination", [references[2]]) :
            Snapshot(await repo.SaveManualBasketAsync(lineId, null, "Destino", ["c"]), references);
        var returnedId = await repo.TransferBasketReferencesAsync(lineId, source, destination, emptySource ? ["a", "b"] : ["a"]);
        var baskets = await repo.GetManualBasketsAsync(lineId);
        Assert.Equal(emptySource ? 1 : 2, baskets.Count);
        var received = baskets.Single(basket => basket.Id == returnedId);
        Assert.Contains("a", received.ReferenceIds);
        Assert.Contains("c", received.ReferenceIds);
        if (!emptySource)
        {
            var remaining = baskets.Single(basket => basket.Id != returnedId);
            Assert.Equal(new[] { "b" }, remaining.ReferenceIds);
            Assert.Equal(QuotationAggregationMethod.Median, remaining.AggregationMethod);
        }
        Assert.Equal(baskets.Count, baskets.Select(basket => basket.Name).Distinct().Count());
        Assert.Equal(new[] { "a", "b" }, source.References.Select(reference => reference.Id));
        Assert.Equal(new[] { "c" }, destination.References.Select(reference => reference.Id));
        Assert.Equal(references.OrderBy(reference => reference.Id), (await repo.GetReferencesAsync(lineId)).OrderBy(reference => reference.Id));
    }

    [Fact]
    public async Task FailureAfterDestinationInsertRollsBackAllChanges()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var (projectId, lineId, references) = await SeedAsync(repo);
        var source = await repo.SaveManualBasketAsync(lineId, null, "Origem", ["a", "b"]);
        var destination = await repo.SaveManualBasketAsync(lineId, null, "Destino", ["c"]);
        await repo.ConfirmBasketAsync(lineId, source.Key);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db.Repository.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER reject_transfer_delete BEFORE DELETE ON quotation_manual_basket_references
                BEGIN SELECT RAISE(ABORT, 'forced transfer failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => repo.TransferBasketReferencesAsync(lineId,
            Snapshot(source, references), Snapshot(destination, references), ["a"]));
        var baskets = await repo.GetManualBasketsAsync(lineId);
        Assert.Equal(new[] { "a", "b" }, baskets.Single(basket => basket.Id == source.Id).ReferenceIds);
        Assert.Equal(new[] { "c" }, baskets.Single(basket => basket.Id == destination.Id).ReferenceIds);
        Assert.True((await repo.GetLineAsync(projectId, lineId))!.SelectionConfirmed);
    }

    private static QuotationBasket Snapshot(QuotationManualBasket basket, IReadOnlyList<QuotationReference> references) =>
        Automatic(basket.Key, references.Where(reference => basket.ReferenceIds.Contains(reference.Id)).ToArray()) with
        { Kind = QuotationBasketKind.Manual, ManualBasketId = basket.Id, Name = basket.Name };

    private static QuotationBasket Automatic(string key, IReadOnlyList<QuotationReference> references) => new()
    {
        Key = key, References = references, AveragePrice = 10, MinimumPrice = 10,
        MaximumPrice = 10, MaximumDeviationPercent = 0, Score = 0
    };

    private static QuotationReference Reference(Guid lineId, string id) => new()
    {
        Id = id, LineId = lineId, ContractId = "contract-" + id, ItemNumber = 1,
        ResultSequence = 1, UnitPrice = 10, State = QuotationReferenceState.Eligible
    };

    private static async Task<(Guid ProjectId, Guid LineId, QuotationReference[] References)> SeedAsync(SqliteQuotationRepository repo, string prefix = "")
    {
        var project = await repo.CreateProjectAsync("Transferências");
        var lineId = Guid.NewGuid();
        var references = new[] { Reference(lineId, prefix + "a"), Reference(lineId, prefix + "b"), Reference(lineId, prefix + "c") };
        await repo.SaveSampleAsync(project.Id, lineId, new("Café", 1, "un", null, null), references);
        return (project.Id, lineId, references);
    }
}
