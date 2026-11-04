using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class QuotationAiCandidatePersistenceTests
{
    [Fact]
    public async Task StreamedCandidates_AreAddedOnce_WithoutDuplicatingBasketsOrChangingSchema()
    {
        await using var fixture = await Fixture.CreateAsync();
        var references = Enumerable.Range(1, 237).Select(fixture.Reference).ToArray();
        var progress = new List<QuotationAiProgress>();
        var first = await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false,
            Stream(references.Concat([references[0], references[236]])), new InlineProgress(progress.Add));
        Assert.Equal(237, first.AddedCount);
        Assert.Equal(fixture.Line.SampleVersion + 1, first.Line.SampleVersion);
        Assert.Equal(237, (await fixture.Repository.GetReferencesAsync(fixture.Line.Id)).Count);
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        Assert.Contains(progress, update => update.CandidateCount == 100);
        Assert.Contains(progress, update => update.CandidateCount == 200);
        var repeated = await fixture.Repository.ImportAiCandidatesAsync(first.Line, false, Stream(references));
        Assert.Equal(0, repeated.AddedCount);
        Assert.Equal(first.Line.SampleVersion, repeated.Line.SampleVersion);
        Assert.Equal(first.Line.SampledAt, repeated.Line.SampledAt);
        Assert.Equal(33, SqliteContractRepository.CurrentSchemaVersion);
    }

    [Fact]
    public async Task CancellationAfterFlushedBatch_RollsBackItemAndPreservesExistingReferences()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Reference(1);
        var initial = await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false, Stream([original]));
        using var cancellation = new CancellationTokenSource();
        async IAsyncEnumerable<QuotationReference> Interrupted([EnumeratorCancellation] CancellationToken token = default)
        {
            for (var index = 2; index < 240; index++)
            {
                if (index == 126) cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                yield return fixture.Reference(index);
            }
            await Task.CompletedTask;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Repository.ImportAiCandidatesAsync(
            initial.Line, false, Interrupted(), cancellationToken: cancellation.Token));
        Assert.Equal(original.Id, Assert.Single(await fixture.Repository.GetReferencesAsync(fixture.Line.Id)).Id);
        Assert.Equal(initial.Line.SampleVersion, (await fixture.CurrentAsync()).SampleVersion);
    }

    [Fact]
    public async Task CollisionAfterFlushedBatch_RollsBackAllNewReferencesAndKeepsOriginalPrice()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Reference(1);
        var initial = await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false, Stream([original]));
        var candidates = Enumerable.Range(2, 115).Select(fixture.Reference)
            .Append(original with { UnitPrice = original.UnitPrice + 10 });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.ImportAiCandidatesAsync(
            initial.Line, false, Stream(candidates)));
        var remaining = Assert.Single(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Equal(original.UnitPrice, remaining.UnitPrice);
        Assert.Equal(initial.Line.SampleVersion, (await fixture.CurrentAsync()).SampleVersion);
    }

    [Fact]
    public async Task ConflictingDuplicateWithinPendingBatch_AndForeignItem_AreRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Reference(1);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.ImportAiCandidatesAsync(
            fixture.Line, false, Stream([original, original with { ItemDescription = "outro objeto" }])));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.ImportAiCandidatesAsync(
            fixture.Line, false, Stream([original, fixture.Reference(2) with { LineId = Guid.NewGuid() }])));
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Equal(fixture.Line.SampleVersion, (await fixture.CurrentAsync()).SampleVersion);
    }

    [Fact]
    public async Task StaleSampleOrChangedPrecision_DoesNotImportCandidates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initial = await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false, Stream([fixture.Reference(1)]));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.ImportAiCandidatesAsync(
            fixture.Line, false, Stream([fixture.Reference(2)])));
        await fixture.Repository.SetProjectMedicationAsync(fixture.Line.ProjectId, true);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.ImportAiCandidatesAsync(
            initial.Line, false, Stream([fixture.Reference(2)])));
        Assert.Single(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task NewCandidates_PreserveManualChoice_AndInvalidateAutomaticConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var references = Enumerable.Range(1, 3).Select(fixture.Reference).ToArray();
        await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false, Stream(references));
        var manual = await fixture.Repository.SaveManualBasketAsync(fixture.Line.Id, null, "Escolha do usuário",
            references.Select(reference => reference.Id).ToArray());
        await fixture.Repository.ConfirmBasketAsync(fixture.Line.Id, manual.Key);
        var imported = await fixture.Repository.ImportAiCandidatesAsync(await fixture.CurrentAsync(), false,
            Stream([fixture.Reference(4)]));
        Assert.Equal(manual.Key, imported.Line.SelectedBasketKey);
        Assert.True(imported.Line.SelectionConfirmed);
        Assert.Equal(references.Select(reference => reference.Id),
            Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id)).ReferenceIds);
        await fixture.Repository.ConfirmBasketAsync(fixture.Line.Id, "automatic:test");
        imported = await fixture.Repository.ImportAiCandidatesAsync(await fixture.CurrentAsync(), false,
            Stream([fixture.Reference(5)]));
        Assert.Equal("automatic:test", imported.Line.SelectedBasketKey);
        Assert.False(imported.Line.SelectionConfirmed);
    }

    [Fact]
    public async Task ExpandedRangeAndBasket_AreAppliedTogether_AndRepeatedApplicationIsUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync(1m, 2m);
        var application = fixture.Application(new(0.5m, 3m, "Candidatos tecnicamente equivalentes fora da estimativa."));
        Assert.True(await fixture.Repository.SaveAiGuideBasketAsync(application));
        var current = await fixture.CurrentAsync();
        Assert.Equal(0.5m, current.MinimumUnitPrice);
        Assert.Equal(3m, current.MaximumUnitPrice);
        Assert.Equal($"manual:{application.BasketId:N}", current.SelectedBasketKey);
        Assert.True(current.SelectionConfirmed);
        Assert.False(await fixture.Repository.SaveAiGuideBasketAsync(application with { ExpectedLine = current }));
        Assert.Equal(current.SampleVersion, (await fixture.CurrentAsync()).SampleVersion);
        Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task RangeChange_PreservesEarlierManualChoiceButRequiresItsReconfirmation()
    {
        await using var fixture = await Fixture.CreateAsync(1m, 2m);
        var references = Enumerable.Range(11, 3).Select(fixture.Reference).ToArray();
        await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false, Stream(references));
        var manual = await fixture.Repository.SaveManualBasketAsync(fixture.Line.Id, null, "Cesta anterior",
            references.Select(reference => reference.Id).ToArray());
        await fixture.Repository.ConfirmBasketAsync(fixture.Line.Id, manual.Key);
        var application = fixture.Application(new(1m, 3m, "Preços mais atuais do mesmo objeto.")) with
        { ExpectedLine = await fixture.CurrentAsync() };
        Assert.True(await fixture.Repository.SaveAiGuideBasketAsync(application));
        var current = await fixture.CurrentAsync();
        Assert.Equal(manual.Key, current.SelectedBasketKey);
        Assert.False(current.SelectionConfirmed);
        Assert.Equal(2, (await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id)).Count);
    }

    [Fact]
    public async Task BasketWriteFailure_RollsBackRangeReferencesAndBasket()
    {
        await using var fixture = await Fixture.CreateAsync(1m, 2m);
        await using (var connection = new SqliteConnection($"Data Source={fixture.Database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER refuse_test_members BEFORE INSERT ON quotation_manual_basket_references
                BEGIN SELECT RAISE(ABORT, 'simulated basket failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Repository.SaveAiGuideBasketAsync(
            fixture.Application(new(0.5m, 3m, "Ampliação justificada."))));
        var current = await fixture.CurrentAsync();
        Assert.Equal(1m, current.MinimumUnitPrice);
        Assert.Equal(2m, current.MaximumUnitPrice);
        Assert.Equal(fixture.Line.SampleVersion, current.SampleVersion);
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task InvalidRangeAdjustment_DoesNotWrite()
    {
        await using var fixture = await Fixture.CreateAsync(1m, 2m);
        foreach (var range in new QuotationAiPriceRangeAdjustment[]
                 {
                     new(1.1m, 3m, "Restrição indevida"), new(0.5m, 1.9m, "Restrição indevida"),
                     new(-1m, 3m, "Negativo"), new(0.5m, 3m, ""),
                     new(0.12345m, 3m, "Precisão não armazenável"), new(null, 3m, "Limite removido")
                 })
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.SaveAiGuideBasketAsync(fixture.Application(range)));
        Assert.Equal(fixture.Line.SampleVersion, (await fixture.CurrentAsync()).SampleVersion);
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task TechnicalReview_IsSavedForAlreadyImportedCandidates_AndPreservedByReimport()
    {
        await using var fixture = await Fixture.CreateAsync();
        var references = Enumerable.Range(1, 3).Select(fixture.Reference).ToArray();
        var imported = await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false, Stream(references));
        var application = AddReviews(fixture.Application(null) with { ExpectedLine = imported.Line },
            "Correspondência técnica conferida pelo responsável.");
        Assert.True(await fixture.Repository.SaveAiGuideBasketAsync(application));
        var current = await fixture.CurrentAsync();
        foreach (var stored in await fixture.Repository.GetReferencesAsync(fixture.Line.Id))
        {
            Assert.NotNull(stored.TechnicalReview);
            Assert.True(stored.TechnicalReview.Matches(current, stored));
            Assert.Equal(string.Empty, stored.Adequacy.Explanation);
        }
        imported = await fixture.Repository.ImportAiCandidatesAsync(current, false, Stream(references));
        Assert.Equal(0, imported.AddedCount);
        Assert.All(await fixture.Repository.GetReferencesAsync(fixture.Line.Id), reference => Assert.NotNull(reference.TechnicalReview));
        Assert.False(await fixture.Repository.SaveAiGuideBasketAsync(application with { ExpectedLine = imported.Line }));
        Assert.Equal(current.SampleVersion, (await fixture.CurrentAsync()).SampleVersion);

        // A guide without the opt-in must not remove an existing accepted review.
        var withoutReviews = application with
        {
            ExpectedLine = imported.Line,
            Selections = application.Selections.Select(selection => selection with
                { Reference = selection.Reference with { TechnicalReview = null } }).ToArray()
        };
        Assert.False(await fixture.Repository.SaveAiGuideBasketAsync(withoutReviews));
        Assert.All(await fixture.Repository.GetReferencesAsync(fixture.Line.Id), reference => Assert.NotNull(reference.TechnicalReview));
    }

    [Fact]
    public async Task ExistingGuidedBasket_CanReceiveNewTechnicalEvidence_WithoutDuplicatingItsSelections()
    {
        await using var fixture = await Fixture.CreateAsync();
        var application = fixture.Application(null);
        Assert.True(await fixture.Repository.SaveAiGuideBasketAsync(application));
        var before = await fixture.CurrentAsync();
        application = AddReviews(application with { ExpectedLine = before }, "Equivalência material documentada.");
        Assert.True(await fixture.Repository.SaveAiGuideBasketAsync(application));
        Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        Assert.Equal(before.SampleVersion + 1, (await fixture.CurrentAsync()).SampleVersion);
        Assert.All(await fixture.Repository.GetReferencesAsync(fixture.Line.Id), reference => Assert.NotNull(reference.TechnicalReview));
    }

    [Fact]
    public async Task TechnicalReviewFromDifferentSpecification_IsRejectedWithoutWrites()
    {
        await using var fixture = await Fixture.CreateAsync();
        var application = AddReviews(fixture.Application(null), "Equivalência documentada.");
        application = application with
        {
            Selections = application.Selections.Select(selection => selection with
            {
                Reference = selection.Reference with
                {
                    TechnicalReview = QuotationAiTechnicalReview.Create(
                        fixture.Line with { Description = "Especificação diferente" }, selection.Reference,
                        "Equivalência documentada.", selection.Reference.ItemDescription)
                }
            }).ToArray()
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.SaveAiGuideBasketAsync(application));
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task BasketFailure_RollsBackTechnicalReviewChangesOnExistingCandidates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var references = Enumerable.Range(1, 3).Select(fixture.Reference).ToArray();
        var imported = await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false, Stream(references));
        await using (var connection = new SqliteConnection($"Data Source={fixture.Database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER refuse_review_test BEFORE INSERT ON quotation_manual_basket_references
                BEGIN SELECT RAISE(ABORT, 'simulated review rollback'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var application = AddReviews(fixture.Application(null) with { ExpectedLine = imported.Line }, "Equivalência documentada.");
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Repository.SaveAiGuideBasketAsync(application));
        Assert.Equal(imported.Line.SampleVersion, (await fixture.CurrentAsync()).SampleVersion);
        Assert.All(await fixture.Repository.GetReferencesAsync(fixture.Line.Id), reference => Assert.Null(reference.TechnicalReview));
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
    }

    private static QuotationAiBasketApplication AddReviews(QuotationAiBasketApplication application, string reason) =>
        application with
        {
            Selections = application.Selections.Select(selection => selection with
            {
                Reference = selection.Reference with
                {
                    TechnicalReview = QuotationAiTechnicalReview.Create(application.ExpectedLine, selection.Reference,
                        reason, selection.Reference.ItemDescription)
                }
            }).ToArray()
        };

    private static async IAsyncEnumerable<QuotationReference> Stream(IEnumerable<QuotationReference> references,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return reference;
        }
        await Task.CompletedTask;
    }

    private sealed class InlineProgress(Action<QuotationAiProgress> report) : IProgress<QuotationAiProgress>
    {
        public void Report(QuotationAiProgress value) => report(value);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required SqliteQuotationRepository Repository { get; init; }
        public required QuotationLine Line { get; init; }

        public static async Task<Fixture> CreateAsync(decimal? minimum = null, decimal? maximum = null)
        {
            var database = await TestDatabase.CreateAsync();
            var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
            var project = await repository.CreateProjectAsync("Candidatos de IA");
            var line = await repository.CreateLineAsync(project.Id,
                new("Copo descartável de polipropileno 180 ml", 100, "unidade", minimum, maximum));
            return new() { Database = database, Repository = repository, Line = line };
        }

        public QuotationReference Reference(int index) => new()
        {
            Id = $"contrato{index}|1|1", LineId = Line.Id, ContractId = $"contrato{index}",
            ItemNumber = 1, ResultSequence = 1, ItemDescription = Line.Description, ItemUnit = "unidade",
            UnitPrice = 2.5m + index / 1000m, SupplierName = "Fornecedor de teste", SupplierTaxId = "11222333000181",
            HomologatedQuantity = 100, ResultDate = DateOnly.FromDateTime(DateTime.Today), Municipality = "Ribeirão Preto", Uf = "SP"
        };

        public QuotationAiBasketApplication Application(QuotationAiPriceRangeAdjustment? range) => new(
            Line, false, Guid.NewGuid(), "IA externa teste", QuotationAggregationMethod.Mean,
            Enumerable.Range(1, 3).Select(index => new QuotationAiBasketSelection(Reference(index), 1m,
                "Mesmo material e capacidade.", string.Empty)).ToArray()) { PriceRangeAdjustment = range };

        public async Task<QuotationLine> CurrentAsync() =>
            (await Repository.GetLineAsync(Line.ProjectId, Line.Id))!;

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
