using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationBasketCopyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopiesExactConversionWithoutRemovingSourceOrAdoptingDestination(bool divide)
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Cópia");
        var lineId = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, lineId, new("Café", 1, "un", null, null),
            [Reference(lineId, "a", 30), Reference(lineId, "b", 10), Reference(lineId, "c", 10), Reference(lineId, "d", 10)]);
        var origin = await repo.SaveManualBasketAsync(lineId, null, "Origem", ["a", "b", "c"]);
        var target = await repo.SaveManualBasketAsync(lineId, null, "Destino", ["b", "c", "d"]);
        if (divide) await repo.SetManualBasketPriceDivisorAsync(origin.Id, "a", 3);
        else await repo.SetManualBasketConversionFactorAsync(origin.Id, "a", 0.5m);
        await repo.ConfirmBasketAsync(lineId, origin.Key);
        var analysis = (await service.GetAnalysisAsync(project.Id, lineId))!;
        var id = await service.CopyBasketReferenceAsync(analysis, origin.Key, target.Key, "a");
        Assert.Equal(target.Id, id);
        var baskets = await repo.GetManualBasketsAsync(lineId);
        var saved = baskets.Single(value => value.Id == target.Id);
        Assert.Contains("a", baskets.Single(value => value.Id == origin.Id).ReferenceIds);
        Assert.Equal(4, saved.ReferenceIds.Count);
        if (divide)
        {
            Assert.Equal(3m, saved.PriceDivisors["a"]);
            Assert.Equal(1m, saved.GetConversionFactor("a"));
        }
        else Assert.Equal(0.5m, saved.GetConversionFactor("a"));
        var line = (await repo.GetLineAsync(project.Id, lineId))!;
        Assert.Equal(origin.Key, line.SelectedBasketKey);
        Assert.True(line.SelectionConfirmed);
        var reopened = (await service.GetAnalysisAsync(project.Id, lineId))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CopyBasketReferenceAsync(reopened, origin.Key, target.Key, "a"));
    }

    [Fact]
    public async Task AutomaticDestinationBecomesManualWithoutChangingAutomaticOrConfirmedChoice()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Automática");
        var lineId = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, lineId, new("Café", 1, "un", null, null),
            [Reference(lineId, "a", 10), Reference(lineId, "b", 11), Reference(lineId, "c", 12), Reference(lineId, "d", 13)]);
        var analysis = (await service.GetAnalysisAsync(project.Id, lineId))!;
        var destination = analysis.Baskets.First(value => !value.IsManual && value.References.All(reference => reference.Id != "a"));
        var origin = analysis.Baskets.First(value => value.References.Any(reference => reference.Id == "a"));
        await repo.ConfirmBasketAsync(lineId, destination.Key);
        analysis = (await service.GetAnalysisAsync(project.Id, lineId))!;
        var copied = await service.CopyBasketReferenceAsync(analysis, origin.Key, destination.Key, "a");
        var saved = Assert.Single(await repo.GetManualBasketsAsync(lineId));
        Assert.Equal(copied, saved.Id);
        Assert.Equal(4, saved.ReferenceIds.Count);
        var final = (await service.GetAnalysisAsync(project.Id, lineId))!;
        Assert.Equal(destination.Key, final.Line.SelectedBasketKey);
        Assert.True(final.Line.SelectionConfirmed);
        Assert.Equal(3, final.Baskets.Single(value => value.Key == destination.Key).References.Count);
    }

    [Fact]
    public async Task StaleConversionAndDifferentItemAreRejectedWithoutPartialCopy()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Concorrência");
        var lineId = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, lineId, new("Café", 1, "un", null, null),
            [Reference(lineId, "a", 30), Reference(lineId, "b", 10), Reference(lineId, "c", 10), Reference(lineId, "d", 10)]);
        var source = await repo.SaveManualBasketAsync(lineId, null, "Origem", ["a", "b", "c"]);
        var destination = await repo.SaveManualBasketAsync(lineId, null, "Destino", ["b", "c", "d"]);
        var stale = (await service.GetAnalysisAsync(project.Id, lineId))!;
        await repo.SetManualBasketPriceDivisorAsync(source.Id, "a", 3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CopyBasketReferenceAsync(stale, source.Key, destination.Key, "a"));
        Assert.DoesNotContain("a", (await repo.GetManualBasketsAsync(lineId)).Single(value => value.Id == destination.Id).ReferenceIds);
        var fresh = (await service.GetAnalysisAsync(project.Id, lineId))!;
        var from = fresh.Baskets.Single(value => value.Key == source.Key);
        var to = fresh.Baskets.Single(value => value.Key == destination.Key);
        var foreign = from with { References = from.References.Select(value => value with { LineId = Guid.NewGuid() }).ToArray() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SaveBasketReferenceCopyAsync(fresh.Line, foreign, to, "a", "Cópia"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CopyBasketReferenceAsync(fresh, source.Key, destination.Key, "a", cancelled.Token));
        Assert.DoesNotContain("a", (await repo.GetManualBasketsAsync(lineId)).Single(value => value.Id == destination.Id).ReferenceIds);
    }

    [Fact]
    public async Task RepeatedSupplierContractIsRejectedButOtherContractsRemainAvailable()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Duplicidade");
        var lineId = Guid.NewGuid();
        var sameOrigin = Reference(lineId, "a", 10) with { ContractId = "mesmo" };
        await repo.SaveSampleAsync(project.Id, lineId, new("Café", 1, "un", null, null),
            [sameOrigin, Reference(lineId, "b", 10) with { ContractId = "mesmo" }, Reference(lineId, "c", 10), Reference(lineId, "d", 10)]);
        var source = await repo.SaveManualBasketAsync(lineId, null, "Origem", ["a", "c", "d"]);
        var destination = await repo.SaveManualBasketAsync(lineId, null, "Destino", ["b", "c", "d"]);
        var analysis = (await service.GetAnalysisAsync(project.Id, lineId))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CopyBasketReferenceAsync(analysis, source.Key, destination.Key, "a"));
        Assert.DoesNotContain("a", (await repo.GetManualBasketsAsync(lineId)).Single(value => value.Id == destination.Id).ReferenceIds);
        Assert.All(analysis.Baskets.Where(value => !value.IsManual), basket =>
            Assert.Equal(basket.References.Count, basket.References.Select(value => value.ContractId).Distinct().Count()));
        Assert.Contains(analysis.Baskets, basket => basket.References.Count == 3);
    }

    internal static QuotationReference Reference(Guid lineId, string id, decimal price) => new()
    {
        Id = id, LineId = lineId, ContractId = "contrato-" + id, ItemNumber = 1, ResultSequence = 1,
        SupplierTaxId = "11222333000181", SupplierName = "Fornecedor", UnitPrice = price,
        ItemDescription = "Café", ItemUnit = "un", ResultDate = DateOnly.FromDateTime(DateTime.Today)
    };
}
