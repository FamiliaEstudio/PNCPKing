using PNCPKing.Core.Search;

namespace PNCPKing.Tests;

public sealed class SearchTextTests
{
    [Fact]
    public void Normalize_RemovesAccentsAndCase()
    {
        Assert.Equal("aquisicao de cafe", SearchText.Normalize("  AQUISIÇÃO   de Café "));
    }

    [Fact]
    public void Normalize_ReplacesInvalidUnicodeWithoutLosingSearchableText()
    {
        var malformed = "Aquisição \uD800 \uFFFE de café \uDC00 \uD83F\uDFFE";

        var sanitized = SearchText.Sanitize(malformed);
        var normalized = SearchText.Normalize(malformed);

        Assert.Equal("Aquisição � � de café � �", sanitized);
        Assert.Equal("aquisicao � � de cafe � �", normalized);
        Assert.True(sanitized.IsNormalized());
    }

    [Fact]
    public void BuildMatchQuery_UsesPrefixesAndQuotedPhrases()
    {
        var query = SearchText.BuildMatchQuery("medic \"saúde pública\"");

        Assert.Equal("(\"medic\"* AND \"saude publica\"*)", query);
    }

    [Theory]
    [InlineData("café filtro", "(\"cafe\"* AND \"filtro\"*)")]
    [InlineData("café + filtro", "(\"cafe\"* AND \"filtro\"*)")]
    [InlineData("café OU chá", "(\"cafe\"* OR \"cha\"*)")]
    [InlineData("café OR chá", "(\"cafe\"* OR \"cha\"*)")]
    [InlineData("café | chá", "(\"cafe\"* OR \"cha\"*)")]
    [InlineData("café OU chá filtro", "(\"cafe\"* OR (\"cha\"* AND \"filtro\"*))")]
    public void Parse_BuildsAndOrGroupsWithAndPrecedence(string text, string expected)
    {
        Assert.Equal(expected, SearchText.Parse(text).ItemMatchQuery);
    }

    [Fact]
    public void Parse_BuildsPhrasesGlobalExclusionsAndBroadCandidateQuery()
    {
        var expression = SearchText.Parse("\"café torrado\" OU chá -cafeteira -\"filtro de papel\"");

        Assert.Equal(
            "(((\"cafe torrado\"* OR \"cha\"*)) NOT \"cafeteira\"*) NOT \"filtro de papel\"*",
            expression.ItemMatchQuery);
        Assert.Equal("\"cafe\"* OR \"torrado\"* OR \"cha\"*", expression.CandidateMatchQuery);
    }

    [Theory]
    [InlineData("-cafeteira")]
    [InlineData("\"café torrado")]
    [InlineData("café +")]
    [InlineData("café OU")]
    [InlineData("OU café")]
    [InlineData("café - filtro")]
    [InlineData("café AND filtro")]
    [InlineData("café & filtro")]
    public void Parse_RejectsInvalidExpressionsBeforeSearch(string text)
    {
        Assert.Throws<SearchQueryException>(() => SearchText.Parse(text));
    }
}
