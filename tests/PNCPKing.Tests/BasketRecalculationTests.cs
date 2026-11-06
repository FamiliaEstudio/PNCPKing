using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class BasketRecalculationTests
{
    [Fact]
    public async Task ChangingWeightsChangesRecommendedBasketWithoutCollectingAgain()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer());
        var project = await service.CreateProjectAsync("Preferências");
        var input = new QuotationLineInput("Café torrado", 100, "pacote", null, null);
        var line = await repository.CreateLineAsync(project.Id, input);
        await repository.SaveSampleAsync(project.Id, line.Id, input,
            Enumerable.Range(0, 6).Select(index => Candidate(line.Id, index, 100m) with
            {
                DistanceFromRibeiraoKilometers = index < 3 ? 0 : 1000,
                ResultDate = DateOnly.FromDateTime(DateTime.Today).AddDays(index < 3 ? -250 : -1)
            }).ToArray());
        await service.UpdateBasketSettingsAsync(line.Id, new(0, 0, 0, 100, 0), null, null);
        var nearby = (await service.GetAnalysisAsync(project.Id, line.Id))!;
        Assert.All(Assert.Single(nearby.Baskets, basket => basket.IsRecommended).References,
            reference => Assert.Equal(0d, reference.DistanceFromRibeiraoKilometers));
        await service.UpdateBasketSettingsAsync(line.Id, new(0, 0, 0, 0, 100), null, null);
        var recent = (await service.GetAnalysisAsync(project.Id, line.Id))!;
        Assert.All(Assert.Single(recent.Baskets, basket => basket.IsRecommended).References,
            reference => Assert.Equal(1000d, reference.DistanceFromRibeiraoKilometers));
        Assert.Equal(nearby.Line.SampleVersion, recent.Line.SampleVersion);
        Assert.Equal(6, recent.CollectedCount);
    }

    [Fact]
    public async Task ExcelItem_RecalculatesSavedCandidatesAndPreservesOtherItemsAndManualBaskets()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer());
        var project = await service.CreateProjectAsync("Recálculo por item");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var run = await service.CreateAutomationRunAsync(project.Id, "unused.xlsx", "Teste", SearchGeoFilter.All,
            today.AddDays(-30), today,
            [new(1, "café", "Café torrado", 100, "pacote", null, null, 1, PromptOrigin: SearchPromptOrigin.Excel),
             new(2, "café", "Café torrado", 100, "pacote", null, null, 1, PromptOrigin: SearchPromptOrigin.Excel)],
            AdequacyWeights.Default);
        var lines = await repository.GetLinesAsync(project.Id);
        var input = new QuotationLineInput("Café torrado", 100, "pacote", null, null);
        var prices = new[] { 90m, 100m, 110m, 200m, 210m, 220m };
        foreach (var line in lines)
        {
            await repository.SaveSampleAsync(project.Id, line.Id, input,
                prices.Select((price, index) => Candidate(line.Id, index, price)).ToArray());
            await repository.UpdateAutomationItemStateAsync(line.Id, QuotationAutomationItemState.Completed, "Coletado");
        }
        await repository.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Completed, "Concluído");
        var before = (await service.GetAnalysisAsync(project.Id, lines[0].Id))!;
        var otherBefore = (await service.GetAnalysisAsync(project.Id, lines[1].Id))!;
        var manual = await service.CreateManualBasketAsync(before.Line.Id, "Escolha manual",
            before.References.Take(3).Select(reference => reference.Id).ToArray());
        await service.SetManualBasketPriceDivisorAsync(manual.Id, before.References[0].Id, 2m);
        await service.ConfirmBasketAsync(before, before.Baskets.First().Key);
        await service.ConfirmBasketAsync(otherBefore, otherBefore.Baskets.First().Key);
        var manualBefore = Assert.Single(await repository.GetManualBasketsAsync(before.Line.Id));
        var rawBefore = await repository.GetReferencesAsync(before.Line.Id);
        var weights = new AdequacyWeights(0, 0, 0, 0, 100);

        await service.UpdateBasketSettingsAsync(before.Line.Id, weights, 200m, 220m);
        var reopened = new QuotationService(new SqliteQuotationRepository(database.Repository.DatabasePath), new QuotationAnalyzer());
        var result = (await reopened.GetAnalysisAsync(project.Id, before.Line.Id))!;
        var automatic = Assert.Single(result.Baskets, basket => !basket.IsManual);
        Assert.Equal(new[] { 200m, 210m, 220m }, automatic.References.Select(reference => reference.UnitPrice).Order());
        Assert.Equal(weights, result.Line.Weights);
        Assert.Equal(200m, result.Line.MinimumUnitPrice);
        Assert.Equal(220m, result.Line.MaximumUnitPrice);
        Assert.False(result.Line.SelectionConfirmed);
        Assert.Equal(before.Line.SelectedBasketKey ?? before.Baskets.First().Key, result.Line.SelectedBasketKey);
        Assert.Equal(before.Line.SampleVersion, result.Line.SampleVersion);
        Assert.Equal(before.Line.SampledAt, result.Line.SampledAt);
        Assert.Equal(before.Line.RequestedBasketSize, result.Line.RequestedBasketSize);
        Assert.Equal(before.Line.PromptSet, result.Line.PromptSet);
        Assert.Equal(rawBefore, await repository.GetReferencesAsync(before.Line.Id));
        var manualAfter = Assert.Single(await repository.GetManualBasketsAsync(before.Line.Id));
        Assert.Equal(manualBefore.Id, manualAfter.Id);
        Assert.Equal(manualBefore.ReferenceIds, manualAfter.ReferenceIds);
        Assert.Equal(manualBefore.PriceDivisors, manualAfter.PriceDivisors);
        Assert.All(result.References, reference => Assert.Equal(0m, reference.Adequacy.DescriptionScore));
        var other = (await reopened.GetAnalysisAsync(project.Id, lines[1].Id))!;
        Assert.True(other.Line.SelectionConfirmed);
        Assert.Equal(otherBefore.Line.Weights, other.Line.Weights);
        Assert.Null(other.Line.MinimumUnitPrice);
        Assert.Equal(otherBefore.Baskets.Select(basket => basket.Key), other.Baskets.Select(basket => basket.Key));

        await service.UpdateBasketSettingsAsync(before.Line.Id, weights, 500m, 600m);
        var empty = (await reopened.GetAnalysisAsync(project.Id, before.Line.Id))!;
        Assert.DoesNotContain(empty.Baskets, basket => !basket.IsManual);
        Assert.Single(empty.Baskets, basket => basket.IsManual);
        Assert.Equal(6, empty.CollectedCount);
        await service.UpdateBasketSettingsAsync(before.Line.Id, weights, null, null);
        var restored = (await reopened.GetAnalysisAsync(project.Id, before.Line.Id))!;
        Assert.Equal(6, restored.EligibleCount);
        Assert.Contains(restored.Baskets, basket => !basket.IsManual);
        Assert.Equal(rawBefore, await repository.GetReferencesAsync(before.Line.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RangeSupportsOpenLimitsAndTruncatesUsingProjectPrecision(bool medication)
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repository.CreateProjectAsync("Precisão");
        await repository.SetProjectMedicationAsync(project.Id, medication);
        var line = await repository.CreateLineAsync(project.Id, new("Café", 1, "pacote", null, null));
        var minimum = medication ? 1.2399m : 1.23m;
        var maximum = medication ? 2.9999m : 2.99m;
        await repository.UpdateBasketSettingsAsync(line.Id, AdequacyWeights.Default, 1.23999m, null);
        var saved = (await repository.GetLineAsync(project.Id, line.Id))!;
        Assert.Equal(minimum, saved.MinimumUnitPrice);
        Assert.Null(saved.MaximumUnitPrice);
        await repository.UpdateBasketSettingsAsync(line.Id, AdequacyWeights.Default, null, 2.99999m);
        await repository.UpdateWeightsAsync(line.Id, new(0, 0, 0, 100, 0));
        saved = (await repository.GetLineAsync(project.Id, line.Id))!;
        Assert.Null(saved.MinimumUnitPrice);
        Assert.Equal(maximum, saved.MaximumUnitPrice); // the existing weights-only action keeps the range
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    [InlineData(101, 100)]
    public async Task InvalidRangeLeavesWeightsAndSelectionUntouched(int minimum, int maximum)
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repository.CreateProjectAsync("Inválido");
        var line = await repository.CreateLineAsync(project.Id, new("Café", 1, "pacote", 1m, 10m));
        await repository.ConfirmBasketAsync(line.Id, "saved-selection");
        var before = await repository.GetLineAsync(project.Id, line.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateBasketSettingsAsync(
            line.Id, new(0, 0, 0, 100, 0), minimum, maximum));
        Assert.Equal(before, await repository.GetLineAsync(project.Id, line.Id));
    }

    [Fact]
    public async Task RunningAutomationCannotOverwriteRecalculationSettings()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repository.CreateProjectAsync("Em coleta");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var run = await repository.CreateAutomationRunAsync(project.Id, "unused.xlsx", "Teste", SearchGeoFilter.All,
            today, today, [new(1, "café", "Café", 1, "pacote", null, null, 1)], AdequacyWeights.Default);
        var line = Assert.Single(await repository.GetLinesAsync(project.Id));
        await repository.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Running, "Em coleta");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateBasketSettingsAsync(
            line.Id, AdequacyWeights.Default, 1m, 10m));
        Assert.Equal(line, await repository.GetLineAsync(project.Id, line.Id));
    }

    private static QuotationReference Candidate(Guid lineId, int index, decimal price) => new()
    {
        Id = $"{lineId:N}-{index}", LineId = lineId, ContractId = $"contract-{index}",
        ItemNumber = 1, ResultSequence = 1, UnitPrice = price,
        ItemDescription = "Café torrado", ItemUnit = "pacote", HomologatedQuantity = 100,
        SupplierTaxId = "11222333000181", ResultDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-index),
        MatchedPromptLevel = PromptMatchLevel.Restrictive, MatchedSearchText = "café"
    };
}
