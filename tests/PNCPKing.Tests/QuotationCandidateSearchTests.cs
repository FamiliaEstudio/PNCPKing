using System.Collections;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Core.Search;

namespace PNCPKing.Tests;

public sealed class QuotationCandidateSearchTests
{
    private static readonly Guid LineId = Guid.Parse("8a82206f-19bd-4a29-8538-8d5a14fa249a");

    [Fact]
    public void PlusUnionsAlternativesWithoutDuplicatingCandidatesOrChangingSourceOrder()
    {
        var first = Reference("first", "Café torrado e chá");
        var second = Reference("second", "Chá preto");
        var third = Reference("third", "Açúcar cristal");
        QuotationReference[] candidates = [first, second, third, first];
        var result = QuotationCandidateSearch.Filter(candidates, "café + chá + café");
        Assert.Equal(new[] { "first", "second" }, result.Select(value => value.Id));
        Assert.Same(first, result[0]);
        Assert.Same(second, result[1]);
        Assert.Equal(4, candidates.Length);
    }

    [Theory]
    [InlineData("café torrado OU chá -solúvel", "torrado", "cha")]
    [InlineData("café -solúvel + açúcar cristal", "torrado", "acucar")]
    [InlineData("café OR chá -solúvel", "torrado", "cha")]
    [InlineData("café | chá -solúvel", "torrado", "cha")]
    public void TermsAndAlternativesKeepMainSearchPrecedenceAndExclusions(string query, string first, string second)
    {
        QuotationReference[] candidates =
        [
            Reference("torrado", "Café torrado"),
            Reference("soluvel", "Café solúvel"),
            Reference("cha", "Chá preto"),
            Reference("acucar", "Açúcar cristal solúvel")
        ];
        Assert.Equal(new[] { first, second }, QuotationCandidateSearch.Filter(candidates, query).Select(value => value.Id));
    }

    [Fact]
    public void PhrasesPrefixesAndAccentsUseTheExistingMatcher()
    {
        QuotationReference[] candidates =
        [
            Reference("phrase", "MATERIAIS PARA SAÚDE PÚBLICA"),
            Reference("separated", "Saúde e assistência pública"),
            Reference("prefix", "Café torrado"),
            Reference("other", "Café solúvel")
        ];
        var result = QuotationCandidateSearch.Filter(candidates, "\"saude publica\" + \"cafe torrad\"");
        Assert.Equal(new[] { "phrase", "prefix" }, result.Select(value => value.Id));
    }

    [Fact]
    public void ApproximateMeasurementsAndUnitFilterRemainScopedToEachAlternative()
    {
        QuotationReference[] candidates =
        [
            Reference("halfLiter", "Frasco de 0,5 litro", "Unidade"),
            Reference("large", "Frasco de 900 ml", "Unidade"),
            Reference("box", "Frasco de 500 ml", "Caixa"),
            Reference("cup", "Copo 200 ml", "Pacote")
        ];
        var result = QuotationCandidateSearch.Filter(candidates, "frasco %500 ml \"un + copo \"pacote");
        Assert.Equal(new[] { "halfLiter", "cup" }, result.Select(value => value.Id));
    }

    [Fact]
    public void SearchDoesNotHideRejectedLowScoreOrOutlyingPriceCandidates()
    {
        var cheap = Reference("cheap", "Tê PVC", "UN") with
            { UnitPrice = 0.01m, State = QuotationReferenceState.Rejected, StateReason = "Pontuação textual baixa" };
        var expensive = Reference("expensive", "Tê PVC", "UN") with
            { UnitPrice = 99999m, State = QuotationReferenceState.Duplicate };
        var normal = Reference("normal", "Tê PVC", "UN") with
            { UnitPrice = 10m, State = QuotationReferenceState.Eligible };
        var result = QuotationCandidateSearch.Filter([cheap, expensive, normal], "te pvc");
        Assert.Equal(new[] { "cheap", "expensive", "normal" }, result.Select(value => value.Id));
        Assert.Equal(0m, cheap.Adequacy.Total);
        Assert.Equal(QuotationReferenceState.Rejected, cheap.State);
        Assert.Equal(0.01m, cheap.UnitPrice);
    }

    [Fact]
    public void TextSearchUsesSameDescriptionAndUnitFieldsAsMainSearch()
    {
        var reference = Reference("item", "Copo descartável", "Unidade") with
        {
            ItemAdditionalInformation = "Café torrado",
            SupplierName = "Açúcar Fornecedor",
            ItemCategory = "Chá",
            UnitPrice = 99999m
        };
        Assert.Single(QuotationCandidateSearch.Filter([reference], "copo \"unidade"));
        Assert.Empty(QuotationCandidateSearch.Filter([reference], "café + açúcar + chá + 99999"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void EmptyQueryRestoresAllCandidatesWithoutCopyingOrReclassifying(string? query)
    {
        QuotationReference[] candidates = [Reference("first", ""), Reference("second", "Qualquer item")];
        Assert.Same(candidates, QuotationCandidateSearch.Filter(candidates, query));
    }

    [Theory]
    [InlineData("café++chá")]
    [InlineData("café OU")]
    [InlineData("-solúvel")]
    [InlineData("%zero")]
    [InlineData("café AND chá")]
    public void InvalidQueryIsRejectedEvenWhenNoCandidatesExist(string query)
    {
        Assert.Throws<SearchQueryException>(() => QuotationCandidateSearch.Filter([], query));
    }

    [Fact]
    public void ContractPriorityCannotBeSilentlyAppliedWithoutContractMetadata()
    {
        var exception = Assert.Throws<SearchQueryException>(() => QuotationCandidateSearch.Filter(
            [Reference("first", "Café torrado")], "café C:(gêneros alimentícios)"));
        Assert.Contains("Remova o bloco C:", exception.Message);
    }

    [Fact]
    public void AlreadyCancelledQueryDoesNotReadCandidatesEvenForEmptyText()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => QuotationCandidateSearch.Filter(
            [Reference("first", "Café")], "", cancellation.Token));
    }

    [Fact]
    public void CancellationDuringEnumerationDoesNotReturnAPartialResult()
    {
        using var cancellation = new CancellationTokenSource();
        var candidates = new CancellingCandidates(cancellation);
        Assert.Throws<OperationCanceledException>(() => QuotationCandidateSearch.Filter(
            candidates, "café", cancellation.Token));
        Assert.Equal(2, candidates.ReadCount);
    }

    private static QuotationReference Reference(string id, string description, string unit = "kg") => new()
    {
        Id = id, LineId = LineId, ContractId = id, ItemNumber = 1, ResultSequence = 1,
        ItemDescription = description, ItemUnit = unit
    };

    private sealed class CancellingCandidates(CancellationTokenSource cancellation) : IReadOnlyList<QuotationReference>
    {
        public int ReadCount { get; private set; }
        public int Count => 3;
        public QuotationReference this[int index] => Reference(index.ToString(), "Café");
        public IEnumerator<QuotationReference> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                ReadCount++;
                if (index == 1) cancellation.Cancel();
                yield return this[index];
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
