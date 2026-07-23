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

    [Fact]
    public void Parse_SeparatesObjectTextFromOneOrMoreAcceptedUnits()
    {
        var expression = SearchText.Parse(
            "Café torrado -máquina -cápsula -xicara -hotelaria -copo \"pacote \"unidade");

        Assert.Equal(["pacote", "unidade"], expression.AcceptedUnits);
        Assert.Equal("cafe torrado", expression.PositiveText);
        Assert.Equal("(\"cafe\"* AND \"torrado\"*)", expression.ContractMatchQuery);
        Assert.True(expression.MatchesItem("Café torrado tradicional", "PACOTE (PAC)"));
        Assert.True(expression.MatchesItem("Café torrado tradicional", "Unidade"));
        Assert.False(expression.MatchesItem("Café torrado tradicional", "Caixa"));
        Assert.False(expression.MatchesItem("Café torrado para máquina", "Pacote"));
    }

    [Fact]
    public void Parse_UnmarkedUnitIsTextButQuotePrefixIsAUnitFilter()
    {
        var text = SearchText.Parse("unidade");
        var unit = SearchText.Parse("\"unidade");
        var curlyUnit = SearchText.Parse("“pacote");

        Assert.Empty(text.AcceptedUnits);
        Assert.Equal("unidade", text.PositiveText);
        Assert.Equal(["unidade"], unit.AcceptedUnits);
        Assert.Empty(unit.PositiveGroups);
        Assert.True(unit.MatchesItem("qualquer descrição", "UNIDADE DE FORNECIMENTO"));
        Assert.Equal(["pacote"], curlyUnit.AcceptedUnits);
    }

    [Theory]
    [InlineData("-cafeteira")]
    [InlineData("\"")]
    [InlineData("café +")]
    [InlineData("café OU")]
    [InlineData("OU café")]
    [InlineData("café - filtro")]
    [InlineData("café AND filtro")]
    [InlineData("café & filtro")]
    [InlineData("café + \"pacote")]
    public void Parse_RejectsInvalidExpressionsBeforeSearch(string text)
    {
        Assert.Throws<SearchQueryException>(() => SearchText.Parse(text));
    }
}
