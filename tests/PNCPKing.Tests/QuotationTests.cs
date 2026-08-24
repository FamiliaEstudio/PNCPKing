using ClosedXML.Excel;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationTests
{
    private static readonly DateOnly Today = new(2026, 7, 21);

    public static TheoryData<string, decimal, string> InvalidEmptyLineInputs => new()
    {
        { "", 1m, "unidade" },
        { "Café", 0m, "unidade" },
        { "Café", 1m, "" }
    };

    [Fact]
    public void WeightSlider_RebalancesOtherComponentsAndAlwaysTotalsOneHundred()
    {
        var weights = AdequacyWeights.Default.Rebalance(AdequacyWeightComponent.Proximity, 40);

        Assert.Equal(new AdequacyWeights(35, 14, 7, 40, 4), weights);
        Assert.Equal(100, weights.Total);
    }

    [Fact]
    public async Task ManualLine_CanStartWithoutPricesAndIsReadyForItemSearch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Material escolar");
        var input = new QuotationLineInput("Papel sulfite", 20m, "resma", null, null);

        var first = await service.CreateLineAsync(project.Id, input);
        var second = await service.CreateLineAsync(project.Id, input);
        var lines = await repository.GetLinesAsync(project.Id);

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].DisplayOrder < lines[1].DisplayOrder);
        Assert.Equal(0, first.Line.SampleVersion);
        Assert.Equal(QuotationAutomationItemState.Manual, first.Line.AutomationState);
        Assert.Null(first.Line.AutomationRunId);
        Assert.Equal("Papel sulfite", first.Line.SearchText);
        Assert.Equal("Papel sulfite", first.Line.PromptSet?.RestrictiveText);
        Assert.Equal(AdequacyWeights.Default, first.Line.Weights);
        Assert.Equal(3, first.Line.RequestedBasketSize);
        Assert.Empty(first.References);
        Assert.Empty(first.Baskets);
        Assert.Empty(await repository.GetReferencesAsync(first.Line.Id));
        Assert.Empty(await repository.GetManualBasketsAsync(first.Line.Id));

        var display = new QuotationLineDisplay(first);
        Assert.Equal("Aguardando preços", display.Status);
        Assert.Null(display.SampledAt);
        Assert.Equal(0, display.SampleVersion);
        Assert.Equal(first.Line.Description, second.Line.Description);
    }

    [Theory]
    [MemberData(nameof(InvalidEmptyLineInputs))]
    public async Task ManualLine_RequiresDescriptionPositiveQuantityAndUnit(
        string description,
        decimal quantity,
        string unit)
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Validação");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.CreateLineAsync(
            project.Id,
            new QuotationLineInput(description, quantity, unit, null, null)));

        Assert.Empty(await repository.GetLinesAsync(project.Id));
    }

    [Fact]
    public async Task ManualLine_MissingProjectRollsBackAndFirstPriceCreatesItsSampleAndBasket()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var input = new QuotationLineInput("Café", 10m, "pacote", null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateLineAsync(Guid.NewGuid(), input));

        var project = await service.CreateProjectAsync("Compra mensal");
        var created = await service.CreateLineAsync(project.Id, input);
        Assert.Equal(0, created.Line.DisplayOrder);
        var saved = await service.SaveManualBasketAsync(
            project.Id,
            created.Line.Id,
            input,
            null,
            "Cesta 1",
            [Row("c1", "11222333000181", 98m)]);

        Assert.Equal(created.Line.Id, saved.Analysis.Line.Id);
        Assert.Equal(created.Line.Description, saved.Analysis.Line.Description);
        Assert.Equal(created.Line.RequestedQuantity, saved.Analysis.Line.RequestedQuantity);
        Assert.Equal(created.Line.RequestedUnit, saved.Analysis.Line.RequestedUnit);
        Assert.Equal(1, saved.Analysis.Line.SampleVersion);
        Assert.Single(saved.Analysis.References);
        Assert.Single(saved.Basket.ReferenceIds);
        Assert.Contains(saved.Analysis.Baskets, basket => basket.ManualBasketId == saved.Basket.Id);
    }

    [Fact]
    public void HigherProximityWeight_ChangesIndexButNotEligibility()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café", 100m, "pacote") with
        {
            Weights = new AdequacyWeights(30, 10, 10, 45, 5)
        };
        var far = analyzer.ScoreReference(
            line,
            Reference("far", "c1", "11222333000181", 35m) with
            {
                Municipality = "Seberi",
                Uf = "RS",
                DistanceFromRibeiraoKilometers = null
            });
        var local = analyzer.ScoreReference(line, Reference("local", "c2", "60701190000104", 35m));

        Assert.Equal(0m, far.Adequacy.ProximityScore);
        Assert.Equal(55m, far.Adequacy.Total);
        Assert.Equal(QuotationReferenceState.Eligible, far.State);
        Assert.Equal(45m, local.Adequacy.ProximityScore);
        Assert.Equal(100m, local.Adequacy.Total);
        Assert.Equal(QuotationReferenceState.Eligible, local.State);
    }

    [Theory]
    [InlineData("11.222.333/0001-81")]
    [InlineData("12.ABC.345/01DE-35")]
    public void CnpjValidation_AcceptsNumericAndNewAlphanumericFormats(string cnpj)
    {
        Assert.True(QuotationAnalyzer.IsValidCnpj(cnpj));
    }

    [Fact]
    public void GenericUnit_UsesPackageAndMeasureFromDescription()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café torrado e moído pacote de 500 g", 100m, "pacote");
        var reference = Reference(
            "ref",
            "contract",
            "11222333000181",
            40m,
            "Café torrado e moído, embalagem pacote 500 g",
            "UNIDADE",
            100m);

        var scored = analyzer.ScoreReference(line, reference);

        Assert.Equal(QuotationReferenceState.Eligible, scored.State);
        Assert.Equal(18m, scored.Adequacy.UnitScore);
        Assert.Contains("inferida", scored.Adequacy.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Termos coincidentes", scored.Adequacy.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ausentes", scored.Adequacy.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompatiblePackageMeasure_IsInformativeAndDoesNotRejectPrice()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café pacote de 500 g", 10m, "pacote");

        var scored = analyzer.ScoreReference(
            line,
            Reference("ref", "contract", "11222333000181", 40m, "Café pacote de 250 g", "pacote", 10m));

        Assert.Equal(QuotationReferenceState.Eligible, scored.State);
        Assert.Contains("informativo", scored.StateReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("divergente", scored.StateReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClearCoffeeReferences_WithDifferentPurchaseQuantities_FormABasket()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café", 5000m, "pacote");
        var result = analyzer.Analyze(line, [
            Reference(
                "galeti",
                "c1",
                "42386723000110",
                35.49m,
                "CAFÉ TORRADO E MOIDO PCT DE 500G",
                "PACOTE (PAC)",
                5m) with
            {
                Municipality = "Seberi",
                Uf = "RS",
                DistanceFromRibeiraoKilometers = null
            },
            Reference(
                "sete-barras",
                "c2",
                "05244997000149",
                33.59m,
                "CAFÉ 500GR",
                "PACOTE",
                69m) with
            {
                Municipality = "Sete Barras",
                DistanceFromRibeiraoKilometers = null
            },
            Reference(
                "sao-paulo",
                "c3",
                "08824503000193",
                34.37m,
                "Café apresentação: torrado moído, intensidade: média, tipo: tradicional, empacotamento: vácuo, prazo validade mínimo: 12 meses, característica adicional: blend arábica e conilon",
                "Pacote 500 G",
                1500m) with
            {
                Municipality = "São Paulo",
                DistanceFromRibeiraoKilometers = null
            }
        ]);

        Assert.Equal(3, result.EligibleCount);
        Assert.All(result.References, reference => Assert.Equal(50m, reference.Adequacy.DescriptionScore));
        Assert.Equal([2m, 4m, 8m], result.References.Select(reference => reference.Adequacy.QuantityScore).Order().ToArray());
        Assert.Single(result.Baskets);
        Assert.InRange(result.Baskets[0].AveragePrice, 34.48m, 34.49m);
    }

    [Fact]
    public void CompactGramNotation_IsEquivalentToSpacedGramNotation()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var scored = analyzer.ScoreReference(
            Line("Café 500 g", 10m, "pacote"),
            Reference(
                "compact-measure",
                "contract",
                "11222333000181",
                34m,
                "CAFÉ 500GR",
                "PACOTE",
                10m));

        Assert.Equal(QuotationReferenceState.Eligible, scored.State);
        Assert.Equal(50m, scored.Adequacy.DescriptionScore);
    }

    [Fact]
    public void BasketDispersion_IsInformativeEvenAboveTwentyFivePercent()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café torrado", 100m, "pacote");
        var exact = analyzer.Analyze(line, [
            Reference("a", "c1", "11222333000181", 75m, quantity: 100m),
            Reference("b", "c2", "60701190000104", 100m, quantity: 100m),
            Reference("c", "c3", "33000167000101", 125m, quantity: 100m)
        ]);
        var above = analyzer.Analyze(line, [
            Reference("d", "c4", "11222333000181", 74.99m, quantity: 100m),
            Reference("e", "c5", "60701190000104", 100m, quantity: 100m),
            Reference("f", "c6", "33000167000101", 125.01m, quantity: 100m)
        ]);

        Assert.Single(exact.Baskets);
        Assert.Equal(25m, exact.Baskets[0].MaximumDeviationPercent);
        Assert.Equal(QuotationBasketVisualState.AutomaticRegular, exact.Baskets[0].VisualState);
        Assert.True(exact.Baskets[0].IsValid);
        Assert.Single(above.Baskets);
        Assert.True(above.Baskets[0].MaximumDeviationPercent > 25m);
        Assert.Equal(QuotationBasketVisualState.AutomaticHighDispersion, above.Baskets[0].VisualState);
        Assert.True(above.Baskets[0].IsValid);
    }

    [Fact]
    public void AutomaticBaskets_UseRequestedTargetReduceToTwoAndStopBelowTwo()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var five = analyzer.Analyze(
            Line("Café", 100m, "pacote") with { RequestedBasketSize = 5 },
            Enumerable.Range(1, 7)
                .Select(number => Reference(
                    $"r{number}",
                    $"c{number}",
                    "11222333000181",
                    95m + number))
                .ToArray());
        var reduced = analyzer.Analyze(
            Line("Café", 100m, "pacote") with { RequestedBasketSize = 10 },
            [
                Reference("a", "ca", "11222333000181", 99m),
                Reference("b", "cb", "60701190000104", 101m)
            ]);
        var insufficient = analyzer.Analyze(
            Line("Café", 100m, "pacote") with { RequestedBasketSize = 3 },
            [Reference("only", "co", "11222333000181", 100m)]);

        Assert.NotEmpty(five.Baskets);
        Assert.All(five.Baskets, basket => Assert.Equal(5, basket.References.Count));
        var reducedBasket = Assert.Single(reduced.Baskets);
        Assert.Equal(2, reducedBasket.References.Count);
        Assert.True(reducedBasket.IsIncomplete);
        Assert.Contains("reduzida", reducedBasket.ValidationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(insufficient.Baskets);
        Assert.Single(insufficient.References);
    }

    [Fact]
    public void AutomaticBaskets_AreDeterministicCuratedAndLimitedToOneHundred()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café", 100m, "pacote") with { RequestedBasketSize = 6 };
        var references = Enumerable.Range(1, 80)
            .Select(number => Reference(
                $"ref-{number:D3}",
                $"contract-{number:D3}",
                "11222333000181",
                70m + number))
            .ToArray();

        var first = analyzer.Analyze(line, references);
        var second = analyzer.Analyze(line, references.AsEnumerable().Reverse().ToArray());

        Assert.InRange(first.Baskets.Count, 3, QuotationAnalyzer.MaximumCuratedBaskets);
        Assert.Equal(first.Baskets.Select(basket => basket.Key), second.Baskets.Select(basket => basket.Key));
        Assert.Single(first.Baskets, basket => basket.IsRecommended);
        Assert.Single(first.Baskets, basket => basket.IsCheapest);
        Assert.Single(first.Baskets, basket => basket.IsMostExpensive);
        Assert.Equal(first.Baskets.Max(basket => basket.Score), first.Baskets.Single(basket => basket.IsRecommended).Score);
        Assert.Equal(QuotationAnalyzer.MaximumBasketPoolSize, first.BasketPoolCount);
    }

    [Fact]
    public void AutomaticBaskets_UsePromptLevelOnlyAsTieBreaker()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café", 100m, "pacote");
        var references = new[]
        {
            Reference("strict", "contract-1", "11222333000181", 100m) with
            {
                MatchedPromptLevel = PromptMatchLevel.Restrictive
            },
            Reference("intermediate", "contract-2", "60701190000104", 100m) with
            {
                MatchedPromptLevel = PromptMatchLevel.Intermediate
            },
            Reference("broad", "contract-3", "33000167000101", 100m) with
            {
                MatchedPromptLevel = PromptMatchLevel.Broad
            },
            Reference("legacy", "contract-4", "00360305000104", 100m)
        };

        var result = analyzer.Analyze(line, references);
        var recommended = Assert.Single(result.Baskets, basket => basket.IsRecommended);

        Assert.Equal(
            ["broad", "intermediate", "strict"],
            recommended.References.Select(reference => reference.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ManualBaskets_ClassifyIncompleteRegularAndInvalidStates()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café", 100m, "pacote") with
        {
            MinimumUnitPrice = 70m,
            MaximumUnitPrice = 120m
        };
        var references = new[]
        {
            Reference("a", "ca", "11222333000181", 75m),
            Reference("b", "cb", "60701190000104", 100m),
            Reference("c", "cc", "33000167000101", 125m),
            Reference("d", "cd", "00360305000104", 100m)
        };
        var definitions = new[]
        {
            Manual(line, "Incompleta", "a", "b"),
            Manual(line, "Regular", "a", "b", "d"),
            Manual(line, "Inválida", "a", "b", "c")
        };

        var analysis = analyzer.Analyze(line, references, definitions);
        var incomplete = analysis.Baskets.Single(basket => basket.Name == "Incompleta");
        var regular = analysis.Baskets.Single(basket => basket.Name == "Regular");
        var invalid = analysis.Baskets.Single(basket => basket.Name == "Inválida");

        Assert.Equal(QuotationBasketVisualState.ManualIncomplete, incomplete.VisualState);
        Assert.Equal(QuotationBasketVisualState.ManualRegular, regular.VisualState);
        Assert.Equal(QuotationBasketVisualState.ManualInvalid, invalid.VisualState);
        Assert.StartsWith("manual:", incomplete.Key, StringComparison.Ordinal);
        Assert.Contains("inelegível", invalid.ValidationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManualBasket_AppliesPerBasketFactorsAndMedianWithCentTruncation()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café", 100m, "pacote");
        var references = new[]
        {
            Reference("a", "ca", "11222333000181", 10.999m),
            Reference("b", "cb", "60701190000104", 10.02m),
            Reference("c", "cc", "33000167000101", 20.01m)
        };
        var converted = Manual(line, "Convertida", "a", "b", "c") with
        {
            AggregationMethod = QuotationAggregationMethod.Median,
            ConversionFactors = new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["a"] = 1.5m,
                ["b"] = 1m,
                ["c"] = 0.5m
            }
        };
        var unchanged = Manual(line, "Sem conversão", "a", "b", "c");

        var analysis = analyzer.Analyze(line, references, [converted, unchanged]);
        var basket = analysis.Baskets.Single(value => value.Name == "Convertida");
        var originalBasket = analysis.Baskets.Single(value => value.Name == "Sem conversão");

        Assert.Equal(QuotationAggregationMethod.Median, basket.AggregationMethod);
        Assert.Equal(16.49m, basket.PriceEntries.Single(entry => entry.Reference.Id == "a").EffectiveUnitPrice);
        Assert.Equal(10.00m, basket.PriceEntries.Single(entry => entry.Reference.Id == "c").EffectiveUnitPrice);
        Assert.Equal(12.17m, basket.AveragePrice);
        Assert.Equal(10.02m, basket.MedianPrice);
        Assert.Equal(10.02m, basket.AdoptedPrice);
        Assert.Equal(10.99m, originalBasket.PriceEntries.Single(entry => entry.Reference.Id == "a").EffectiveUnitPrice);
        Assert.Equal(10.999m, references[0].UnitPrice);
    }

    [Fact]
    public void MedianWithEvenPrices_TruncatesInsteadOfRounding()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café", 100m, "pacote");
        var manual = Manual(line, "Mediana", "a", "b") with
        {
            AggregationMethod = QuotationAggregationMethod.Median
        };

        var basket = analyzer.Analyze(line, [
            Reference("a", "ca", "11222333000181", 10.01m),
            Reference("b", "cb", "60701190000104", 10.02m)
        ], [manual]).Baskets.Single(value => value.IsManual);

        Assert.Equal(10.01m, basket.AveragePrice);
        Assert.Equal(10.01m, basket.MedianPrice);
        Assert.Equal(10.01m, basket.AdoptedPrice);
    }

    [Fact]
    public void BasketOrigin_IsInformativeAndDoesNotPreventCombination()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café torrado", 100m, "pacote");
        var sameSupplier = analyzer.Analyze(line, [
            Reference("a", "c1", "11222333000181", 90m),
            Reference("b", "c2", "11222333000181", 100m),
            Reference("c", "c3", "33000167000101", 110m)
        ]);
        var sameContract = analyzer.Analyze(line, [
            Reference("d", "c1", "11222333000181", 90m),
            Reference("e", "c1", "60701190000104", 100m),
            Reference("f", "c3", "33000167000101", 110m)
        ]);

        Assert.Single(sameSupplier.Baskets);
        Assert.Single(sameContract.Baskets);
    }

    [Fact]
    public void ProbableDuplicate_RemainsEligibleForUserReview()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café torrado", 100m, "pacote");
        var result = analyzer.Analyze(line, [
            Reference("best", "c1", "11222333000181", 100m, date: Today.AddDays(-5)),
            Reference("repeat", "c2", "11222333000181", 100.50m, date: Today.AddDays(-10))
        ]);

        Assert.Equal(2, result.EligibleCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.All(result.References, reference => Assert.Equal(QuotationReferenceState.Eligible, reference.State));
    }

    [Fact]
    public void OnlyPriceRangeAndDescriptionBlockQuotationEligibility()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café torrado", 100m, "pacote") with
        {
            MinimumUnitPrice = 30m,
            MaximumUnitPrice = 50m,
            Weights = new AdequacyWeights(10, 10, 10, 65, 5)
        };
        var informativeProblems = analyzer.ScoreReference(
            line,
            Reference("info", "same", "CNPJ INVÁLIDO", 40m, "Café torrado 250 g", "caixa") with
            {
                Municipality = "Cidade distante",
                Uf = "RR",
                DistanceFromRibeiraoKilometers = null
            });
        var outsideRange = analyzer.ScoreReference(
            line,
            Reference("range", "c2", "", 60m, "Café torrado", "pacote"));
        var wrongDescription = analyzer.ScoreReference(
            line,
            Reference("description", "c3", "", 40m, "Açúcar cristal", "pacote"));

        Assert.Equal(QuotationReferenceState.Eligible, informativeProblems.State);
        Assert.Contains("informativo", informativeProblems.StateReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(QuotationReferenceState.Rejected, outsideRange.State);
        Assert.Contains("máximo", outsideRange.StateReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(QuotationReferenceState.Rejected, wrongDescription.State);
        Assert.Contains("descritiva", wrongDescription.StateReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapturedSample_PersistsAcrossRepositoryInstancesAndRequiresReconfirmationOnUpdate()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Café escolar");
        var input = new QuotationLineInput("Café torrado", 100m, "pacote", null, null);
        var first = await service.CaptureSampleAsync(project.Id, null, input, [
            Row("c1", "11222333000181", 90m),
            Row("c2", "60701190000104", 100m),
            Row("c3", "33000167000101", 110m)
        ]);
        await service.ConfirmBasketAsync(first, first.Baskets.Single().Key);

        var reopened = new QuotationService(
            new SqliteQuotationRepository(database.Repository.DatabasePath),
            new QuotationAnalyzer(Today));
        var restored = (await reopened.GetAnalysesAsync(project.Id)).Single();
        Assert.True(restored.Line.SelectionConfirmed);
        Assert.All(restored.References, reference =>
        {
            Assert.Equal("Sertãozinho", reference.SupplierMunicipality);
            Assert.Equal("SP", reference.SupplierUf);
        });

        var updated = await reopened.CaptureSampleAsync(project.Id, restored.Line.Id, input, [
            Row("c1", "11222333000181", 95m),
            Row("c4", "00360305000104", 105m)
        ]);
        Assert.Equal(2, updated.Line.SampleVersion);
        Assert.False(updated.Line.SelectionConfirmed);
        Assert.Equal(4, updated.CollectedCount);
        Assert.Equal(restored.Line.SelectedBasketKey, updated.Line.SelectedBasketKey);
        Assert.NotNull(updated.SelectedBasket);
        Assert.Equal(95m, updated.References.Single(reference => reference.ContractId == "c1").UnitPrice);
    }

    [Fact]
    public async Task PriceRange_PreservesTheSnapshotAndMarksOutsideReferencesAsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Faixa posterior");

        var analysis = await service.CaptureSampleAsync(
            project.Id,
            null,
            new QuotationLineInput("Café torrado", 100m, "pacote", 95m, 105m),
            [
                Row("c1", "11222333000181", 90m),
                Row("c2", "60701190000104", 100m),
                Row("c3", "33000167000101", 110m)
            ]);
        var persisted = await repository.GetReferencesAsync(analysis.Line.Id);

        Assert.Equal(3, analysis.CollectedCount);
        Assert.Equal(3, persisted.Count);
        Assert.Equal(2, analysis.RejectedCount);
        Assert.Equal(1, analysis.EligibleCount);
        Assert.Contains(persisted, reference => reference.UnitPrice == 100m);
    }

    [Fact]
    public async Task UpdatingWeights_RecalculatesLocallyPersistsAndRequiresReconfirmation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new QuotationService(
            new SqliteQuotationRepository(database.Repository.DatabasePath),
            new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Pesos configuráveis");
        var analysis = await service.CaptureSampleAsync(
            project.Id,
            null,
            new QuotationLineInput("Café", 100m, "pacote", null, null),
            [
                Row("c1", "11222333000181", 90m),
                Row("c2", "60701190000104", 100m),
                Row("c3", "33000167000101", 110m)
            ]);
        await service.ConfirmBasketAsync(analysis, analysis.Baskets.Single().Key);
        var customWeights = new AdequacyWeights(35, 14, 7, 40, 4);

        await service.UpdateWeightsAsync(analysis.Line.Id, customWeights);
        var reopened = Assert.Single(await service.GetAnalysesAsync(project.Id));

        Assert.Equal(customWeights, reopened.Line.Weights);
        Assert.False(reopened.Line.SelectionConfirmed);
        Assert.Equal(analysis.Line.SampleVersion, reopened.Line.SampleVersion);
    }

    [Fact]
    public async Task ManualBasket_CrudPersistsCompositionAndReevaluatesAfterRestart()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Cestas manuais");
        var input = new QuotationLineInput("Café", 100m, "pacote", null, null);
        var analysis = await service.CaptureSampleAsync(project.Id, null, input, [
            Row("c1", "11222333000181", 98m),
            Row("c2", "60701190000104", 100m),
            Row("c3", "33000167000101", 102m)
        ]);

        var first = await service.SaveManualBasketAsync(
            project.Id,
            analysis.Line.Id,
            input,
            null,
            "Minha seleção",
            [Row("c1", "11222333000181", 98m), Row("c2", "60701190000104", 100m)]);
        var incomplete = first.Analysis.Baskets.Single(basket => basket.ManualBasketId == first.Basket.Id);
        Assert.Equal(QuotationBasketVisualState.ManualIncomplete, incomplete.VisualState);
        Assert.Equal(2, first.Basket.ReferenceIds.Count);

        var expanded = await service.SaveManualBasketAsync(
            project.Id,
            analysis.Line.Id,
            input,
            first.Basket.Id,
            "Minha seleção",
            [Row("c2", "60701190000104", 100m), Row("c3", "33000167000101", 102m)]);
        var convertedReferenceId = expanded.Basket.ReferenceIds[0];
        await service.SetManualBasketAggregationMethodAsync(
            expanded.Basket.Id,
            QuotationAggregationMethod.Median);
        await service.SetManualBasketConversionFactorAsync(
            expanded.Basket.Id,
            convertedReferenceId,
            1.5m);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SetManualBasketConversionFactorAsync(
                expanded.Basket.Id,
                convertedReferenceId,
                0m));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SetManualBasketConversionFactorAsync(
                expanded.Basket.Id,
                convertedReferenceId,
                1.0000001m));
        var configured = Assert.IsType<QuotationLineAnalysis>(
            await service.GetAnalysisAsync(project.Id, analysis.Line.Id));
        await service.ConfirmBasketAsync(configured, expanded.Basket.Key);
        Assert.True(Assert.IsType<QuotationLineAnalysis>(
            await service.GetAnalysisAsync(project.Id, analysis.Line.Id)).Line.SelectionConfirmed);
        await service.SetManualBasketConversionFactorAsync(
            expanded.Basket.Id,
            convertedReferenceId,
            1.5m);
        var requiresConfirmation = Assert.IsType<QuotationLineAnalysis>(
            await service.GetAnalysisAsync(project.Id, analysis.Line.Id));
        Assert.False(requiresConfirmation.Line.SelectionConfirmed);
        await service.ConfirmBasketAsync(requiresConfirmation, expanded.Basket.Key);
        await service.UpdateWeightsAsync(analysis.Line.Id, new AdequacyWeights(40, 20, 10, 25, 5));

        var reopened = new QuotationService(
            new SqliteQuotationRepository(database.Repository.DatabasePath),
            new QuotationAnalyzer(Today));
        var restored = Assert.Single(await reopened.GetAnalysesAsync(project.Id));
        var manual = restored.Baskets.Single(basket => basket.ManualBasketId == first.Basket.Id);
        Assert.Equal(3, manual.References.Count);
        Assert.Equal(QuotationBasketVisualState.ManualInvalid, manual.VisualState);
        Assert.Contains("permanecem no cálculo", manual.ValidationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(QuotationAggregationMethod.Median, manual.AggregationMethod);
        Assert.Equal(1.5m, manual.PriceEntries.Single(entry =>
            entry.Reference.Id == convertedReferenceId).ConversionFactor);
        Assert.False(restored.Line.SelectionConfirmed);

        await reopened.RenameManualBasketAsync(first.Basket.Id, "Renomeada");
        Assert.Equal(
            "Renomeada",
            Assert.Single(await repository.GetManualBasketsAsync(analysis.Line.Id)).Name);
        await reopened.RemoveManualBasketReferenceAsync(first.Basket.Id, manual.References[0].Id);
        Assert.Equal(
            2,
            Assert.Single(await repository.GetManualBasketsAsync(analysis.Line.Id)).ReferenceIds.Count);
        await reopened.DeleteManualBasketAsync(first.Basket.Id);
        Assert.Empty(await repository.GetManualBasketsAsync(analysis.Line.Id));
    }

    [Fact]
    public async Task Backup_IncludesQuotationProjectsSnapshotsAndConfirmedChoice()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var quotation = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await quotation.CreateProjectAsync("Projeto preservado");
        var analysis = await quotation.CaptureSampleAsync(
            project.Id,
            null,
            new QuotationLineInput("Café torrado", 100m, "pacote", null, null),
            [
                Row("c1", "11222333000181", 90m),
                Row("c2", "60701190000104", 100m),
                Row("c3", "33000167000101", 110m)
            ]);
        await quotation.ConfirmBasketAsync(analysis, analysis.Baskets.Single().Key);

        var backupPath = Path.Combine(database.Directory, "quotation-backup.pncpking");
        var backup = new BackupService(database.Repository);
        await backup.ExportAsync(backupPath);
        await quotation.CreateProjectAsync("Projeto posterior");
        await backup.ImportAsync(backupPath);

        var restored = new QuotationService(
            new SqliteQuotationRepository(database.Repository.DatabasePath),
            new QuotationAnalyzer(Today));
        var projects = await restored.GetProjectsAsync();
        var restoredAnalysis = Assert.Single(await restored.GetAnalysesAsync(Assert.Single(projects).Id));
        Assert.Equal("Projeto preservado", projects[0].Name);
        Assert.True(restoredAnalysis.Line.SelectionConfirmed);
        Assert.Equal(3, restoredAnalysis.References.Count);
        Assert.NotNull(restoredAnalysis.SelectedBasket);
    }

    [Fact]
    public async Task AutomationRun_PersistsImportedSearchesAndRecoversInterruptedItems()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Automática");
        var run = await service.CreateAutomationRunAsync(
            project.Id,
            Path.Combine(database.Directory, "saida.xlsx"),
            "Maria de Souza",
            SearchGeoFilter.Southeast,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 7, 1),
            [
                new QuotationImportItem(1, "cafe -maquina \"pacote", "Café", 100m, "pacote", 30m, 45m, 2, 7),
                new QuotationImportItem(2, "acucar", "Açúcar", 50m, "kg", null, null, 1)
            ],
            AdequacyWeights.Default);

        var lines = await repository.GetLinesAsync(project.Id);
        Assert.Equal(2, lines.Count);
        Assert.Equal("cafe -maquina \"pacote", lines[0].SearchText);
        Assert.Equal(2, lines[0].RequestedBatchCount);
        Assert.Equal(7, lines[0].RequestedBasketSize);
        Assert.Equal(3, lines[1].RequestedBasketSize);
        Assert.Equal(QuotationAutomationItemState.Pending, lines[0].AutomationState);
        Assert.Equal(run.Id, lines[0].AutomationRunId);
        Assert.True(lines[0].DisplayOrder < lines[1].DisplayOrder);

        await service.UpdateAutomationItemStateAsync(
            lines[0].Id,
            QuotationAutomationItemState.Running,
            "executando");
        await service.UpdateAutomationRunStateAsync(
            run.Id,
            QuotationAutomationRunState.Running,
            "executando");
        await service.RecoverInterruptedAutomationAsync();

        lines = await repository.GetLinesAsync(project.Id);
        Assert.Equal(QuotationAutomationItemState.Pending, lines[0].AutomationState);
        var recovered = await service.GetLatestAutomationRunAsync(project.Id);
        Assert.NotNull(recovered);
        Assert.Equal(QuotationAutomationRunState.Pending, recovered.State);
        Assert.Equal(SearchGeoFilterKind.Southeast, recovered.GeoFilter.Kind);
        Assert.Equal("Maria de Souza", recovered.ResponsibleName);
    }

    [Fact]
    public async Task RenameAndCatalogSelection_DoNotChangeTechnicalSampleOrBasketState()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repository.CreateProjectAsync("Nomes");
        var lineId = Guid.NewGuid();
        await repository.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Café torrado técnico", 10m, "pacote", null, null),
            [
                Reference("rename-a", "c1", "11222333000181", 90m) with { LineId = lineId },
                Reference("rename-b", "c2", "60701190000104", 100m) with { LineId = lineId },
                Reference("rename-c", "c3", "33000167000101", 110m) with { LineId = lineId }
            ]);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var before = Assert.Single(await service.GetAnalysesAsync(project.Id));
        await repository.ConfirmBasketAsync(lineId, before.Baskets.Single().Key);
        var confirmed = Assert.Single(await repository.GetLinesAsync(project.Id));

        await repository.RenameLineDisplayNameAsync(lineId, "  Café   premium  ");
        await repository.SetLineCatalogSelectionAsync(lineId, new QuotationCatalogSelection
        {
            Kind = CatalogKind.Catmat,
            Code = "123456",
            Description = "CAFÉ TORRADO",
            SelectedAt = DateTimeOffset.UtcNow
        });

        var after = Assert.Single(await repository.GetLinesAsync(project.Id));
        Assert.Equal("Café premium", after.DisplayName);
        Assert.Equal(confirmed.Description, after.Description);
        Assert.Equal(confirmed.SampleVersion, after.SampleVersion);
        Assert.Equal(confirmed.SampledAt, after.SampledAt);
        Assert.Equal(confirmed.SearchText, after.SearchText);
        Assert.Equal(confirmed.SelectedBasketKey, after.SelectedBasketKey);
        Assert.Equal(confirmed.SelectionConfirmed, after.SelectionConfirmed);
        Assert.Equal("CATMAT 123456", after.CatalogSelection?.Label);
    }

    [Fact]
    public async Task Workbook_UsesDisplayNameAndCatalogCodeInItemTitle()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Descrição técnica", 1m, "unidade") with
        {
            DisplayName = "Nome amigável",
            CatalogSelection = new QuotationCatalogSelection
            {
                Kind = CatalogKind.Catser,
                Code = "9876",
                Description = "SERVIÇO",
                SelectedAt = DateTimeOffset.UtcNow
            }
        };
        var analysis = analyzer.Analyze(line, []);
        var path = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"), "catalog-title.xlsx");
        try
        {
            await new QuotationWorkbookService().ExportAsync(
                path,
                new QuotationProjectReport(
                    new QuotationProject(Guid.NewGuid(), "Catálogo", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    [analysis]),
                "Maria de Souza");
            using var workbook = new XLWorkbook(path);
            Assert.Equal("Item 1 - Nome amigável (CATSER 9876)", workbook.Worksheet(1).Cell("B4").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var folder = Path.GetDirectoryName(path)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Workbook_PreservesTemplateAndBuildsConsecutiveDynamicBlocks()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var project = new QuotationProject(Guid.NewGuid(), "Cotação teste", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var resolvedLine = Line("Café torrado", 100m, "pacote");
        var resolved = analyzer.Analyze(resolvedLine, [
            Reference("a", "c1", "11222333000181", 90m) with
            {
                SupplierMunicipality = "Ribeirão Preto",
                SupplierUf = "SP"
            },
            Reference("b", "c2", "60701190000104", 100m),
            Reference("c", "c3", "33000167000101", 110m)
        ]);
        resolved = resolved with
        {
            Line = resolved.Line with
            {
                SelectedBasketKey = resolved.Baskets.Single().Key,
                SelectionConfirmed = true
            }
        };
        var pending = analyzer.Analyze(Line("Açúcar", 10m, "pacote"), []);
        var path = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"), "quote.xlsx");
        try
        {
            await new QuotationWorkbookService().ExportAsync(
                path,
                new QuotationProjectReport(project, [resolved, pending]),
                "Maria de Souza");

            using var workbook = new XLWorkbook(path);
            Assert.Equal(2, workbook.Worksheets.Count);
            var sheet = workbook.Worksheet(1);
            Assert.Equal(" Análise 1 a 5", sheet.Name);
            Assert.Equal("PLANILHA DE AVALIAÇÃO DE PREÇOS", sheet.Cell("B2").GetString());
            var picture = Assert.Single(sheet.Pictures);
            Assert.Equal(2, picture.TopLeftCell.Address.ColumnNumber);
            Assert.Equal(2, picture.TopLeftCell.Address.RowNumber);
            Assert.True(picture.Width <= Math.Floor((sheet.Column(2).Width * 7d) + 5d));
            Assert.True(picture.Height <= sheet.Row(2).Height * 96d / 72d);
            Assert.InRange(
                Math.Abs(
                    picture.Width / (double)picture.Height -
                    picture.OriginalWidth / (double)picture.OriginalHeight),
                0,
                0.01);
            Assert.True(sheet.Cell("B2").IsMerged());
            Assert.True(sheet.Cell("J2").IsMerged());
            Assert.Equal(180d, sheet.Row(2).Height);
            Assert.True(sheet.Column(7).IsHidden);
            Assert.True(sheet.Column(8).IsHidden);
            Assert.True(sheet.Column(11).IsHidden);
            Assert.False(sheet.ShowGridLines);

            Assert.Equal("Item 1 - Café torrado", sheet.Cell("B4").GetString());
            Assert.True(sheet.Cell("B4").IsMerged());
            Assert.Equal("EMPRESA", sheet.Cell("B5").GetString());
            Assert.Equal("CNPJ", sheet.Cell("C5").GetString());
            Assert.Equal(
                "FONTE DE PESQUISA                                                  IN SEGES Nº 65, ART. 5º",
                sheet.Cell("D5").GetString());
            Assert.Equal("LINK PNCP", sheet.Cell("E5").GetString());
            Assert.Equal("VALOR DA COTAÇÃO", sheet.Cell("F5").GetString());
            Assert.Equal(
                "Fornecedor a (Ribeirão Preto/SP)",
                sheet.Cell("B6").GetString());
            Assert.True(sheet.Row(5).Height >= 64.5d);
            Assert.True(sheet.Row(12).Height >= 64.5d);
            Assert.True(sheet.Cell("F5").Style.Alignment.WrapText);
            Assert.True(sheet.Cell("I12").Style.Alignment.WrapText);
            Assert.Equal(
                "UTILIZAÇÃO DO MÉTODO DE IDENTIFICAÇÃO DE PREÇO EXCESSIVO",
                sheet.Cell("I12").GetString());
            Assert.Equal(
                "UTILIZAÇÃO DO MÉTODO DE IDENTIFICAÇÃO DE PREÇO INEXEQUÍVEL",
                sheet.Cell("J12").GetString());
            Assert.All(sheet.Range("D6:D8").Cells(), cell =>
                Assert.Equal("Inciso II", cell.GetString()));
            Assert.Equal(90m, sheet.Cell("F6").GetValue<decimal>());
            Assert.Equal(
                "IF(F6=\"\",\"\",IFERROR(TRUNC(AVERAGE(F7:F8),2),\"\"))",
                sheet.Cell("G6").FormulaA1);
            Assert.Equal(
                "IF(OR(F6=\"\",G6=\"\"),\"\",F6/G6-1)",
                sheet.Cell("H6").FormulaA1);
            Assert.Equal(
                "IF(F6=\"\",\"\",IF(OR(I6=\"EXCESSIVO\",J6=\"INEXEQUÍVEL\"),\"\",F6))",
                sheet.Cell("K6").FormulaA1);
            Assert.Equal(
                "IFERROR(TRUNC(SUM(K6:K8)/COUNTIF(K6:K8,\">0\"),2),\"\")",
                sheet.Cell("C9").FormulaA1);
            Assert.True(sheet.Cell("C9").IsMerged());
            Assert.True(sheet.Cell("F9").IsMerged());

            Assert.All(sheet.Range("A10:K10").Cells(), cell => Assert.True(cell.IsEmpty()));
            Assert.DoesNotContain(sheet.MergedRanges, range =>
                range.RangeAddress.FirstAddress.RowNumber == 10);
            Assert.Equal("Item 2 - Açúcar", sheet.Cell("B11").GetString());
            Assert.Equal("Preço 1 não obtido", sheet.Cell("B13").GetString());
            Assert.Equal("Preço 2 não obtido", sheet.Cell("B14").GetString());
            Assert.Equal("Preço 3 não obtido", sheet.Cell("B15").GetString());
            Assert.True(sheet.Cell("F13").IsEmpty());
            Assert.Equal(
                "IF(F13=\"\",\"\",IFERROR(TRUNC(AVERAGE(F14:F15),2),\"\"))",
                sheet.Cell("G13").FormulaA1);
            Assert.Equal(
                "IFERROR(TRUNC(SUM(K13:K15)/COUNTIF(K13:K15,\">0\"),2),\"\")",
                sheet.Cell("C16").FormulaA1);
            Assert.True(sheet.Cell("C16").IsMerged());
            Assert.All(sheet.Range("A17:K17").Cells(), cell => Assert.True(cell.IsEmpty()));
            Assert.Equal("Responsável pela cotação:", sheet.Cell("B18").GetString());
            Assert.Equal(
                "Maria de Souza\nAgente de Administração",
                sheet.Cell("C18").GetString());
            Assert.True(sheet.Cell("C18").IsMerged());
            Assert.True(sheet.Cell("I18").IsMerged());
            Assert.Equal(
                XLAlignmentHorizontalValues.Center,
                sheet.Cell("C18").Style.Alignment.Horizontal);
            Assert.Equal(
                XLAlignmentVerticalValues.Center,
                sheet.Cell("C18").Style.Alignment.Vertical);
            Assert.True(sheet.Cell("C18").Style.Alignment.WrapText);
            Assert.True(sheet.Cell("B25").IsEmpty());
            Assert.Equal(
                18,
                sheet.LastCellUsed(XLCellsUsedOptions.Contents)!.Address.RowNumber);
            Assert.Equal(6, sheet.ConditionalFormats.Count());
            Assert.Equal(XLCalculateMode.Auto, workbook.CalculateMode);
            Assert.True(workbook.CalculationOnSave);
            Assert.True(workbook.ForceFullCalculation);
            Assert.True(workbook.FullCalculationOnLoad);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Workbook_RejectsAnEmptyResponsibleNameBeforeCreatingTheFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "PNCPKing.Tests",
            Guid.NewGuid().ToString("N"),
            "invalid-responsible.xlsx");
        var report = new QuotationProjectReport(
            new QuotationProject(
                Guid.NewGuid(),
                "Responsável obrigatório",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            []);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            new QuotationWorkbookService().ExportAsync(path, report, "   "));

        Assert.Equal("responsibleName", exception.ParamName);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Workbook_FormatsSupplierOrBuyerLocationAndExpandsLongSupplierRows()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var project = new QuotationProject(
            Guid.NewGuid(),
            "Localidade do fornecedor",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var line = Line("Café torrado", 100m, "pacote");
        var longSupplierName = string.Join(
            " ",
            Enumerable.Repeat("EMPRESA FORNECEDORA COM RAZÃO SOCIAL EXTENSA", 8));
        var analysis = analyzer.Analyze(line, [
            Reference("complete", "c1", "11222333000181", 90m) with
            {
                SupplierName = longSupplierName,
                SupplierMunicipality = "Ribeirão Preto",
                SupplierUf = "sp"
            },
            Reference("missing", "c2", "60701190000104", 100m) with
            {
                SupplierMunicipality = "Sertãozinho"
            },
            Reference("invalid", "c3", "33000167000101", 110m) with
            {
                SupplierMunicipality = "Franca",
                SupplierUf = "XX",
                Municipality = string.Empty,
                Uf = "XX"
            }
        ]);
        analysis = Confirm(analysis, analysis.Baskets.Single(basket => basket.IsRecommended));
        var path = Path.Combine(
            Path.GetTempPath(),
            "PNCPKing.Tests",
            Guid.NewGuid().ToString("N"),
            "supplier-location.xlsx");
        try
        {
            await new QuotationWorkbookService().ExportAsync(
                path,
                new QuotationProjectReport(project, [analysis]),
                "Maria de Souza");

            using var workbook = new XLWorkbook(path);
            Assert.Equal(2, workbook.Worksheets.Count);
            var sheet = workbook.Worksheet(1);
            var expectedComplete = $"{longSupplierName} (Ribeirão Preto/SP)";
            const string expectedBuyerFallback = "Fornecedor missing (Ribeirão Preto/SP)";
            var supplierCells = sheet.Range("B6:B8").Cells().ToArray();
            Assert.Contains(supplierCells, cell => cell.GetString() == expectedComplete);
            Assert.Contains(supplierCells, cell => cell.GetString() == expectedBuyerFallback);
            Assert.Contains(supplierCells, cell => cell.GetString() == "Fornecedor invalid");
            var longSupplierCell = supplierCells.Single(cell => cell.GetString() == expectedComplete);
            Assert.True(longSupplierCell.Style.Alignment.WrapText);
            Assert.True(sheet.Row(longSupplierCell.Address.RowNumber).Height > 12.75d);
            Assert.All(supplierCells, cell =>
                Assert.DoesNotContain("{{", cell.GetString(), StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Workbook_WritesLinksAndCnpjsAsTextWithoutChangingTemplateColumns()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var project = new QuotationProject(
            Guid.NewGuid(), "CNPJ e URL", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var longUrl = "https://pncp.gov.br/app/editais/teste/2026/1?documento=" +
                      new string('a', 280);
        var analysis = analyzer.Analyze(
            Line("Café", 10m, "pacote"),
            [
                Reference("numeric", "c1", "03370573000103", 40m) with { PortalUrl = longUrl },
                Reference("alpha", "c2", "12ABC34501DE35", 41m),
                Reference("other", "c3", "NI-123", 42m)
            ]);
        analysis = Confirm(analysis, analysis.Baskets.Single(basket => basket.IsRecommended));
        var path = Path.Combine(
            Path.GetTempPath(),
            "PNCPKing.Tests",
            Guid.NewGuid().ToString("N"),
            "cnpj-url.xlsx");
        try
        {
            await new QuotationWorkbookService().ExportAsync(
                path,
                new QuotationProjectReport(project, [analysis]),
                "Maria de Souza");

            using var workbook = new XLWorkbook(path);
            Assert.Equal(2, workbook.Worksheets.Count);
            var sheet = workbook.Worksheet(1);
            var taxIds = sheet.Range("C6:C8").Cells().Select(cell => cell.GetString()).ToArray();
            Assert.Contains("03.370.573/0001-03", taxIds);
            Assert.Contains("12.ABC.345/01DE-35", taxIds);
            Assert.Contains("NI123", taxIds);
            Assert.All(sheet.Range("C6:C8").Cells(), cell => Assert.Equal(XLDataType.Text, cell.DataType));
            Assert.Equal("Inciso II", sheet.Cell("D6").GetString());
            Assert.Equal(longUrl, sheet.Cell("E6").GetString());
            Assert.Equal(XLDataType.Text, sheet.Cell("E6").DataType);
            Assert.False(sheet.Hyperlinks.TryGet(sheet.Cell("E6").Address, out _));
            Assert.InRange(sheet.Column(5).Width, 44.8, 45.0);
            Assert.Contains("0.00", sheet.Cell("F6").Style.NumberFormat.Format, StringComparison.Ordinal);
            Assert.DoesNotContain("0.0000", sheet.Cell("F6").Style.NumberFormat.Format, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Workbook_CompletesAShortBasketToThreePriceRows()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var project = new QuotationProject(Guid.NewGuid(), "Cotação curta", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var analysis = analyzer.Analyze(Line("Café", 10m, "pacote"), [
            Reference("melhor", "c1", "11222333000181", 40m),
            Reference("segunda", "c2", "60701190000104", 42m)
        ]);
        var path = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"), "short.xlsx");
        try
        {
            await new QuotationWorkbookService().ExportAsync(
                path,
                new QuotationProjectReport(project, [analysis]),
                "Maria de Souza");

            using var workbook = new XLWorkbook(path);
            Assert.Equal(2, workbook.Worksheets.Count);
            var sheet = workbook.Worksheet(1);
            Assert.Equal("Item 1 - Café", sheet.Cell("B4").GetString());
            Assert.Equal(
                "Fornecedor melhor (Ribeirão Preto/SP)",
                sheet.Cell("B6").GetString());
            Assert.Equal(
                "Fornecedor segunda (Ribeirão Preto/SP)",
                sheet.Cell("B7").GetString());
            Assert.Equal("Preço 3 não obtido", sheet.Cell("B8").GetString());
            Assert.True(sheet.Cell("C8").IsEmpty());
            Assert.True(sheet.Cell("D8").IsEmpty());
            Assert.True(sheet.Cell("E8").IsEmpty());
            Assert.True(sheet.Cell("F8").IsEmpty());
            Assert.True(sheet.Cell("B8").Style.Font.Italic);
            Assert.Equal(XLColor.DarkRed, sheet.Cell("B8").Style.Font.FontColor);
            Assert.Equal(
                "IF(F8=\"\",\"\",IFERROR(TRUNC(AVERAGE(F6:F7),2),\"\"))",
                sheet.Cell("G8").FormulaA1);
            Assert.Equal(
                "IFERROR(TRUNC(SUM(K6:K8)/COUNTIF(K6:K8,\">0\"),2),\"\")",
                sheet.Cell("C9").FormulaA1);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Workbook_ExpandsAManualBasketBeyondSeventeenPrices()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var project = new QuotationProject(
            Guid.NewGuid(), "Cesta manual extensa", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var line = Line("Item com vinte preços", 10m, "pacote");
        var references = Enumerable.Range(1, 20)
            .Select(number => Reference(
                $"manual-{number:D2}",
                $"contract-{number:D2}",
                "11222333000181",
                80m + number))
            .ToArray();
        var analysis = analyzer.Analyze(
            line,
            references,
            [Manual(line, "Manual extensa", references.Select(reference => reference.Id).ToArray())]);
        analysis = Confirm(analysis, analysis.Baskets.Single(basket => basket.IsManual));

        var path = Path.Combine(
            Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"), "manual.xlsx");
        try
        {
            await new QuotationWorkbookService().ExportAsync(
                path,
                new QuotationProjectReport(project, [analysis]),
                "Maria de Souza");

            using var workbook = new XLWorkbook(path);
            Assert.Equal(2, workbook.Worksheets.Count);
            var sheet = workbook.Worksheet(1);
            Assert.Equal("Item 1 - Item com vinte preços", sheet.Cell("B4").GetString());
            Assert.All(sheet.Range("D6:D25").Cells(), cell =>
                Assert.Equal("Inciso II", cell.GetString()));
            Assert.Equal(81m, sheet.Cell("F6").GetValue<decimal>());
            Assert.Equal(100m, sheet.Cell("F25").GetValue<decimal>());
            Assert.Equal(
                "IF(F6=\"\",\"\",IFERROR(TRUNC(AVERAGE(F7:F25),2),\"\"))",
                sheet.Cell("G6").FormulaA1);
            Assert.Equal(
                "IF(F25=\"\",\"\",IFERROR(TRUNC(AVERAGE(F6:F24),2),\"\"))",
                sheet.Cell("G25").FormulaA1);
            Assert.Equal(
                "IFERROR(TRUNC(SUM(K6:K25)/COUNTIF(K6:K25,\">0\"),2),\"\")",
                sheet.Cell("C26").FormulaA1);
            Assert.True(sheet.Cell("C26").IsMerged());
            Assert.NotEqual("Responsável pela cotação:", sheet.Cell("B25").GetString());
            Assert.All(sheet.Range("A27:K27").Cells(), cell => Assert.True(cell.IsEmpty()));
            Assert.Equal("Responsável pela cotação:", sheet.Cell("B28").GetString());
            Assert.Equal(
                "Maria de Souza\nAgente de Administração",
                sheet.Cell("C28").GetString());
            Assert.True(sheet.Cell("C28").IsMerged());
            Assert.True(sheet.Cell("I28").IsMerged());
            Assert.Equal(
                28,
                sheet.LastCellUsed(XLCellsUsedOptions.Contents)!.Address.RowNumber);
            Assert.Equal(3, sheet.ConditionalFormats.Count());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Workbook_UsesConvertedManualPricesAndMedianWithoutExcludingWarnings()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var project = new QuotationProject(
            Guid.NewGuid(), "Conversão", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var line = Line("Café", 10m, "pacote") with { MaximumUnitPrice = 15m };
        var references = new[]
        {
            Reference("a", "ca", "11222333000181", 10.999m),
            Reference("b", "cb", "60701190000104", 10.02m),
            Reference("c", "cc", "33000167000101", 20.01m)
        };
        var manual = Manual(line, "Convertida", "a", "b", "c") with
        {
            AggregationMethod = QuotationAggregationMethod.Median,
            ConversionFactors = new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["a"] = 1.5m,
                ["b"] = 1m,
                ["c"] = 0.5m
            }
        };
        var analysis = analyzer.Analyze(line, references, [manual]);
        analysis = Confirm(analysis, analysis.Baskets.Single(value => value.IsManual));
        var path = Path.Combine(
            Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"), "converted.xlsx");
        try
        {
            await new QuotationWorkbookService().ExportAsync(
                path,
                new QuotationProjectReport(project, [analysis]),
                "Maria de Souza");

            using var workbook = new XLWorkbook(path);
            Assert.Equal(2, workbook.Worksheets.Count);
            var sheet = workbook.Worksheet(1);
            Assert.All(sheet.Range("D6:D8").Cells(), cell =>
                Assert.Equal("Inciso II", cell.GetString()));
            Assert.Equal([10m, 10.02m, 16.49m],
                sheet.Range("F6:F8").Cells().Select(cell => cell.GetValue<decimal>()).ToArray());
            Assert.Equal("IF(F6=\"\",\"\",F6)", sheet.Cell("K6").FormulaA1);
            Assert.Equal("Mediana dos preços válidos", sheet.Cell("B9").GetString());
            Assert.Equal(
                "IF(COUNTIF(K6:K8,\">0\")=0,\"\",TRUNC(MEDIAN(K6:K8),2))",
                sheet.Cell("C9").FormulaA1);

            var referencesSheet = workbook.Worksheet("Referências");
            Assert.Equal([10m, 10.02m, 16.49m],
                referencesSheet.Range("G2:G4").Cells()
                    .Select(cell => cell.GetValue<decimal>()).ToArray());
            Assert.DoesNotContain(referencesSheet.CellsUsed(), cell =>
                cell.GetString().Contains("1.5", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static QuotationLine Line(string description, decimal quantity, string unit) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Description = description,
        RequestedQuantity = quantity,
        RequestedUnit = unit,
        SampleVersion = 1,
        SampledAt = DateTimeOffset.UtcNow
    };

    private static QuotationManualBasket Manual(
        QuotationLine line,
        string name,
        params string[] referenceIds) => new()
    {
        Id = Guid.NewGuid(),
        LineId = line.Id,
        Name = name,
        ReferenceIds = referenceIds,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static QuotationLineAnalysis Confirm(
        QuotationLineAnalysis analysis,
        QuotationBasket basket) => analysis with
    {
        Line = analysis.Line with
        {
            SelectedBasketKey = basket.Key,
            SelectionConfirmed = true
        }
    };

    private static QuotationReference Reference(
        string id,
        string contractId,
        string cnpj,
        decimal price,
        string description = "Café torrado pacote",
        string unit = "pacote",
        decimal? quantity = 100m,
        DateOnly? date = null) => new()
    {
        Id = id,
        LineId = Guid.NewGuid(),
        ContractId = contractId,
        ItemNumber = 1,
        ResultSequence = 1,
        SupplierName = $"Fornecedor {id}",
        SupplierTaxId = cnpj,
        HomologatedQuantity = quantity,
        UnitPrice = price,
        ResultDate = date ?? Today.AddDays(-20),
        ItemDescription = description,
        ItemUnit = unit,
        ItemRequestedQuantity = quantity,
        Organization = "Órgão de Teste",
        Municipality = "Ribeirão Preto",
        Uf = "SP",
        DistanceFromRibeiraoKilometers = 0,
        PublicationDate = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        PortalUrl = "https://pncp.gov.br/app/editais/teste/2026/1"
    };

    private static ItemSearchRow Row(string contractId, string cnpj, decimal price)
    {
        var contract = RepositorySearchTests.Contract(contractId, "Aquisição de café torrado", "SP", 1) with
        {
            Cnpj = "11222333000181",
            Municipality = "Ribeirão Preto",
            MunicipalityIbgeCode = "3543402"
        };
        var item = new ProcurementItem
        {
            ContractId = contractId,
            ItemNumber = 1,
            Description = "Café torrado pacote",
            Unit = "pacote",
            RequestedQuantityScaled = DecimalScale.ToScaled(100m),
            HasResult = true,
            HydrationStatus = ItemHydrationStatus.Complete
        };
        var result = new HomologationResult
        {
            ContractId = contractId,
            ItemNumber = 1,
            ResultSequence = 1,
            SupplierName = $"Fornecedor {contractId}",
            SupplierTaxId = cnpj,
            SupplierMunicipality = "Sertãozinho",
            SupplierUf = "SP",
            HomologatedQuantityScaled = DecimalScale.ToScaled(100m),
            HomologatedUnitValueScaled = DecimalScale.ToScaled(price),
            ResultDate = Today.AddDays(-20),
            ResultStatusId = 1,
            ResultStatusName = "Informado"
        };
        return new ItemSearchRow(contract, item, result, ItemSearchPriceState.Homologated, "Preço homologado", true);
    }
}
