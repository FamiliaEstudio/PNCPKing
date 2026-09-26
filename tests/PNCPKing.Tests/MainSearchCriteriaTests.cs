using PNCPKing.Core.Search;

namespace PNCPKing.Tests;

public sealed class MainSearchCriteriaTests
{
    [Theory]
    [InlineData("café torrado+açúcar cristal+chá")]
    [InlineData("  café torrado + açúcar cristal + chá  ")]
    public void PlusSeparatesIndependentCriteria(string text)
    {
        var parts = SearchText.ParseMainCriteria(text);
        Assert.Equal(3, parts.Count);
        Assert.True(parts[0].MatchesItem("CAFÉ TORRADO", "kg"));
        Assert.False(parts[0].MatchesItem("café solúvel", "kg"));
        Assert.True(parts[1].MatchesItem("açúcar cristal", "pacote"));
        Assert.True(parts[2].MatchesItem("chá preto", "caixa"));
        Assert.False(parts[2].MatchesItem("café", "caixa"));
    }

    [Fact]
    public void EachPartKeepsItsOwnUnitsExclusionsAlternativesAndApproximateNumbers()
    {
        var parts = SearchText.ParseMainCriteria("café OU chá -solúvel \"pacote + açúcar %500 g \"kg");
        Assert.Equal(2, parts.Count);
        Assert.True(parts[0].MatchesItem("chá preto", "pacote"));
        Assert.False(parts[0].MatchesItem("café solúvel", "pacote"));
        Assert.False(parts[0].MatchesItem("café torrado", "kg"));
        Assert.True(parts[1].MatchesItem("açúcar solúvel 500 gramas", "kg"));
        Assert.False(parts[1].MatchesItem("açúcar 900 gramas", "kg"));
        Assert.False(parts[1].MatchesItem("açúcar 500 gramas", "pacote"));
    }

    [Theory]
    [InlineData("\"café + torrado\" + açúcar", 2)]
    [InlineData("café C:(alimentos + bebidas) + açúcar C:(mercearia)", 2)]
    [InlineData("café C:(\"alimentos + bebidas\") + açúcar", 2)]
    [InlineData("café \"pacote + açúcar \"kg + chá “caixa", 3)]
    [InlineData("\"café + torrado\"", 1)]
    [InlineData("café C:(alimentos + bebidas)", 1)]
    [InlineData("café -\"açúcar + chá\" + chocolate", 2)]
    public void SeparatorsRespectExistingPhraseAndContractBlockSyntax(string text, int count)
    {
        Assert.Equal(count, SearchText.ParseMainCriteria(text).Count);
    }

    [Theory]
    [InlineData("+café")]
    [InlineData("café+")]
    [InlineData("café++chá")]
    [InlineData("café +  + chá")]
    [InlineData("café + -solúvel")]
    [InlineData("café + açúcar OU")]
    [InlineData("café C:(alimentos + açúcar")]
    public void InvalidPartsAreRejectedBeforeSearch(string text) =>
        Assert.Throws<SearchQueryException>(() => SearchText.ParseMainCriteria(text));

    [Theory]
    [InlineData("café -solúvel \"pacote")]
    [InlineData("café OU chá -bebida")]
    [InlineData("%500 g")]
    [InlineData("")]
    public void SinglePartKeepsExistingExpression(string text)
    {
        var before = SearchText.Parse(text);
        var after = Assert.Single(SearchText.ParseMainCriteria(text));
        Assert.Equal(before.ItemMatchQuery, after.ItemMatchQuery);
        Assert.Equal(before.AcceptedUnits, after.AcceptedUnits);
        Assert.Equal(before.OriginalText, after.OriginalText);
    }

    [Fact]
    public void SavedQuotationCriteriaKeepAndSemanticsWhenTransferred()
    {
        const string legacy = "café+torrado+moído -solúvel C:(alimentos + bebidas)";
        var converted = SearchText.ToMainCriteria(legacy);
        var part = Assert.Single(SearchText.ParseMainCriteria(converted));
        Assert.Equal(SearchText.Parse(legacy).ItemMatchQuery, part.ItemMatchQuery);
        Assert.Equal(SearchText.Parse(legacy).ExplicitContractMatchQuery, part.ExplicitContractMatchQuery);
        Assert.False(part.MatchesItem("café", "kg"));
        Assert.Contains("alimentos + bebidas", converted);
        Assert.Equal("\"café + torrado\" moído", SearchText.ToMainCriteria("\"café + torrado\"+moído"));
        // The shared parser and the catalog still accept their original AND operator.
        Assert.Equal(SearchText.Parse("café torrado").ItemMatchQuery, SearchText.Parse("café+torrado").ItemMatchQuery);
        Assert.Equal(2, Assert.Single(CatalogSearchExpression.Parse("café+torrado").Alternatives).Terms.Count);
    }
}
