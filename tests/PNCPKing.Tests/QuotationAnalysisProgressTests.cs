using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.Tests;

public sealed class QuotationAnalysisProgressTests
{
    [Fact]
    public void ReportsMonotonicProgressWithoutChangingCalculatedBaskets()
    {
        var line = Line();
        var references = Enumerable.Range(1, 70).Select(index => QuotationBasketCopyTests.Reference(line.Id, index.ToString(), 10 + index / 10m)).ToArray();
        var analyzer = new QuotationAnalyzer();
        var normal = analyzer.Analyze(line, references);
        var progress = new List<QuotationAnalysisProgress>();
        var observed = analyzer.Analyze(line, references, progress: new ImmediateProgress(progress.Add));
        Assert.Equal(normal.Baskets.Select(value => value.Key), observed.Baskets.Select(value => value.Key));
        Assert.Equal(100, progress[^1].Percentage);
        Assert.All(progress.Zip(progress.Skip(1)), pair => Assert.True(pair.First.Percentage <= pair.Second.Percentage));
        Assert.True(observed.BasketPoolCount <= QuotationAnalyzer.MaximumBasketPoolSize);
        Assert.True(observed.Baskets.Count <= QuotationAnalyzer.MaximumCuratedBaskets);
    }

    [Fact]
    public void CancelsDuringAlternativeCalculation()
    {
        var line = Line();
        var references = Enumerable.Range(1, 70).Select(index => QuotationBasketCopyTests.Reference(line.Id, index.ToString(), 10)).ToArray();
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(value => { if (value.Percentage > 40) cancellation.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => new QuotationAnalyzer().Analyze(line, references,
            progress: progress, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ManualConversionUsesEffectiveRangeButKeepsRawPriceAndAutomaticFilter()
    {
        var line = Line() with { MinimumUnitPrice = 9, MaximumUnitPrice = 11 };
        var references = new[] { "a", "b", "c" }.Select(id => QuotationBasketCopyTests.Reference(line.Id, id, 120)).ToArray();
        var manual = Manual(line, references) with { PriceDivisors = references.ToDictionary(value => value.Id, _ => 12m) };
        var analysis = new QuotationAnalyzer().Analyze(line, references, [manual]);
        var basket = Assert.Single(analysis.Baskets);
        Assert.True(basket.IsManual);
        Assert.True(basket.IsValid);
        Assert.Equal(10, basket.AdoptedPrice);
        Assert.All(basket.References, reference => Assert.Equal(120, reference.UnitPrice));
        Assert.All(analysis.References, reference => Assert.Equal(QuotationReferenceState.Rejected, reference.State));
        var invalid = new QuotationAnalyzer().Analyze(line, references,
            [manual with { PriceDivisors = references.ToDictionary(value => value.Id, _ => 6m) }]);
        Assert.False(Assert.Single(invalid.Baskets).IsValid);
    }

    [Fact]
    public void TechnicalReviewOnlyAllowsBoundManualLexicalException()
    {
        var line = Line() with { Description = "Café torrado moído intenso selecionado embalado a vácuo com aroma natural" };
        var references = new[] { "a", "b", "c" }.Select(id => QuotationBasketCopyTests.Reference(line.Id, id, 10)).ToArray();
        var manual = Manual(line, references);
        var analyzer = new QuotationAnalyzer();
        Assert.False(analyzer.Analyze(line, references, [manual]).Baskets.Single().IsValid);
        var reviewed = references.Select(reference => reference with
        {
            TechnicalReview = QuotationAiTechnicalReview.Create(line, reference, "Análise técnica expressamente aceita para comparar esta referência.", "Café")
        }).ToArray();
        var result = analyzer.Analyze(line, reviewed, [manual]);
        Assert.True(Assert.Single(result.Baskets).IsValid);
        Assert.All(result.References, reference => Assert.Equal(QuotationReferenceState.Rejected, reference.State));
        Assert.All(result.Baskets.Single().References, reference => Assert.Contains("Revisão técnica", reference.Adequacy.Explanation));
        Assert.False(analyzer.Analyze(line with { Description = line.Description + " alterado" }, reviewed, [manual]).Baskets.Single().IsValid);
    }

    [Fact]
    public void TechnicalReviewDoesNotOverrideMeasureConflictOrPriceRange()
    {
        var line = Line() with { Description = "Café torrado moído intenso selecionado embalado a vácuo 500 g", MinimumUnitPrice = 9, MaximumUnitPrice = 11 };
        var references = new[] { "a", "b", "c" }.Select(id => QuotationBasketCopyTests.Reference(line.Id, id, 10) with
        { ItemDescription = "Café 100 g" }).ToArray();
        references = references.Select(reference => reference with
        { TechnicalReview = QuotationAiTechnicalReview.Create(line, reference, "Revisão informada.", "Café") }).ToArray();
        var manual = Manual(line, references);
        Assert.False(new QuotationAnalyzer().Analyze(line, references, [manual]).Baskets.Single().IsValid);
        var noMeasure = line with { Description = "Café torrado moído intenso selecionado embalado a vácuo" };
        references = references.Select(reference => reference with { UnitPrice = 30, ItemDescription = "Café", TechnicalReview = null }).ToArray();
        references = references.Select(reference => reference with
        { TechnicalReview = QuotationAiTechnicalReview.Create(noMeasure, reference, "Revisão informada.", "Café") }).ToArray();
        Assert.False(new QuotationAnalyzer().Analyze(noMeasure, references, [manual]).Baskets.Single().IsValid);
    }

    private static QuotationLine Line() => new()
    { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Description = "Café", RequestedQuantity = 1, RequestedUnit = "un" };
    private static QuotationManualBasket Manual(QuotationLine line, IReadOnlyList<QuotationReference> references) => new()
    { Id = Guid.NewGuid(), LineId = line.Id, Name = "Manual", ReferenceIds = references.Select(value => value.Id).ToArray() };
    private sealed class ImmediateProgress(Action<QuotationAnalysisProgress> report) : IProgress<QuotationAnalysisProgress>
    { public void Report(QuotationAnalysisProgress value) => report(value); }
}
