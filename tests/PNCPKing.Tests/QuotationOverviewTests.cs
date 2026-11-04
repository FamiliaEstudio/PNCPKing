using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationOverviewTests
{
    [Fact]
    public async Task CompactProject_PreservesBasketResultsAndCounts_WhileSelectedItemCanLoadAllCandidates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(new DateOnly(2026, 11, 4)));
        var project = await repository.CreateProjectAsync("Candidatos importados");
        var input = new QuotationLineInput("Café torrado pacote", 100m, "pacote", null, null);
        var line = await repository.CreateLineAsync(project.Id, input);
        var references = References(line.Id, 150);
        await repository.SaveSampleAsync(project.Id, line.Id, input, references);
        var full = (await service.GetAnalysisAsync(project.Id, line.Id))!;
        var selected = full.Baskets.First(basket => basket.IsRecommended);
        await repository.ConfirmBasketAsync(line.Id, selected.Key);

        var report = await service.GetReportAsync(project.Id, compactReferences: true);
        var compact = Assert.Single(report.Lines);
        Assert.True(compact.ReferencesArePartial);
        Assert.Equal(150, compact.CollectedCount);
        Assert.Equal(full.EligibleCount, compact.EligibleCount);
        Assert.Equal(full.Baskets.Select(basket => basket.Key), compact.Baskets.Select(basket => basket.Key));
        Assert.Equal(selected.AdoptedPrice, compact.SelectedBasket!.AdoptedPrice);
        Assert.InRange(compact.References.Count, 3, QuotationAnalyzer.MaximumBasketPoolSize);
        Assert.All(compact.Baskets.SelectMany(basket => basket.References),
            reference => Assert.Contains(compact.References, retained => retained.Id == reference.Id));

        var hydrated = (await service.GetAnalysisAsync(project.Id, line.Id))!;
        Assert.False(hydrated.ReferencesArePartial);
        Assert.Equal(150, hydrated.References.Count);
        Assert.Equal(references.Select(reference => reference.Id).Order(),
            hydrated.References.Select(reference => reference.Id).Order());
        Assert.Equal(selected.Key, hydrated.SelectedBasket!.Key);
        Assert.Equal(150, (await repository.GetReferencesAsync(line.Id)).Count);
    }

    [Fact]
    public void CompactOverview_RetainsManualPricesOutsideAutomaticPool_AndExactDivision()
    {
        var line = new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Description = "Café torrado pacote",
            RequestedQuantity = 100m, RequestedUnit = "pacote"
        };
        var references = References(line.Id, 180);
        var selectedIds = references.TakeLast(3).Select(reference => reference.Id).ToArray();
        var manual = new QuotationManualBasket
        {
            Id = Guid.NewGuid(), LineId = line.Id, Name = "Escolhida pela IA", ReferenceIds = selectedIds,
            PriceDivisors = selectedIds.ToDictionary(id => id, _ => 12m),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        line = line with { SelectedBasketKey = manual.Key, SelectionConfirmed = true };
        var full = new QuotationAnalyzer().Analyze(line, references, [manual]);
        var compact = full.ToCompactOverview();
        Assert.True(compact.ReferencesArePartial);
        Assert.Same(full.Baskets, compact.Baskets);
        Assert.Same(full.SelectedBasket, compact.SelectedBasket);
        Assert.All(selectedIds, id => Assert.Contains(compact.References, value => value.Id == id));
        Assert.All(compact.SelectedBasket!.PriceEntries, entry => Assert.Equal(12m, entry.PriceDivisor));
        Assert.Equal(full.CollectedCount, compact.CollectedCount);
        Assert.Same(compact, compact.ToCompactOverview());
    }

    [Fact]
    public void ProjectWith450LargeCandidateLists_RetainsBoundedOverviewEvenWithoutValidBaskets()
    {
        var retainedCount = 0;
        foreach (var index in Enumerable.Range(0, 450))
        {
            var line = new QuotationLine
            {
                Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Description = $"Item {index}",
                RequestedQuantity = 1m, RequestedUnit = "unidade"
            };
            var candidates = References(line.Id, 1500);
            var full = new QuotationLineAnalysis(line, candidates, [], 1500, 0, 0, 1500, 0);
            var compact = full.ToCompactOverview();
            Assert.True(compact.ReferencesArePartial);
            Assert.Equal(1500, compact.CollectedCount);
            Assert.Equal(1500, compact.RejectedCount);
            retainedCount += compact.References.Count;
        }
        Assert.Equal(450 * QuotationAnalyzer.MaximumBasketPoolSize, retainedCount);
    }

    private static QuotationReference[] References(Guid lineId, int count) => Enumerable.Range(0, count)
        .Select(index => new QuotationReference
        {
            Id = $"ref-{index:D4}", LineId = lineId, ContractId = $"contract-{index:D4}", ItemNumber = 1,
            ResultSequence = 1, SupplierName = $"Fornecedor {index}", SupplierTaxId = "11222333000181",
            UnitPrice = 10m + index / 1000m, ItemDescription = "Café torrado pacote", ItemUnit = "pacote",
            ResultDate = new DateOnly(2026, 10, 1), HomologatedQuantity = 100m,
            Municipality = "Ribeirão Preto", Uf = "SP", DistanceFromRibeiraoKilometers = 0
        }).ToArray();
}
