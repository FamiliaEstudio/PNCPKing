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

    [Theory]
    [InlineData("%1", "produto 1 unidade", true)]
    [InlineData("%1", "produto 2 unidades", true)]
    [InlineData("%1", "produto 3 unidades", false)]
    [InlineData("%3", "produto 2 unidades", true)]
    [InlineData("%3", "produto 4 unidades", true)]
    [InlineData("%3", "produto 5 unidades", false)]
    [InlineData("%4", "produto 1 unidade", true)]
    [InlineData("%4", "produto 7 unidades", true)]
    [InlineData("%4", "produto 8 unidades", false)]
    [InlineData("%19", "produto 16 unidades", true)]
    [InlineData("%19", "produto 22 unidades", true)]
    [InlineData("%19", "produto 23 unidades", false)]
    [InlineData("%20", "produto 15 unidades", true)]
    [InlineData("%20", "produto 25 unidades", true)]
    [InlineData("%20", "produto 26 unidades", false)]
    [InlineData("%2,5", "produto 3.5 unidades", true)]
    public void ApproximateNumber_UsesGraduatedInclusiveTolerance(
        string query,
        string description,
        bool expected)
    {
        var expression = SearchText.Parse(query);

        Assert.Equal(string.Empty, expression.ItemMatchQuery);
        Assert.Equal(expected, expression.MatchesItem(description, string.Empty));
    }

    [Theory]
    [InlineData("%600 g", "pacote com 0,6 kg", "pacote", true)]
    [InlineData("%600g", "pacote com 750 gramas", "pacote", true)]
    [InlineData("%600 g", "pacote com 0,8 kg", "pacote", false)]
    [InlineData("%2 m", "rolo de 200 cm", "rolo", true)]
    [InlineData("%500 ml", "frasco de 0,5 litro", "frasco", true)]
    [InlineData("%500 ml", "frasco de 500 g", "frasco", false)]
    public void ApproximateNumber_NormalizesCompatibleMeasurementUnits(
        string query,
        string description,
        string unit,
        bool expected)
    {
        Assert.Equal(expected, SearchText.Parse(query).MatchesItem(description, unit));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("%zero")]
    [InlineData("%0")]
    [InlineData("%-1")]
    [InlineData("%1,")]
    [InlineData("%1.2.3")]
    [InlineData("%10xyz")]
    public void ApproximateNumber_RejectsInvalidSyntax(string query)
    {
        Assert.Throws<SearchQueryException>(() => SearchText.Parse(query));
    }

    [Fact]
    public void ApproximateNumber_IsExcludedFromFtsAndAppliedAfterTextMatch()
    {
        var expression = SearchText.Parse("pincel %20 cm -parede");

        Assert.Equal("(\"pincel\"*) NOT \"parede\"*", expression.ItemMatchQuery);
        Assert.DoesNotContain("20", expression.CandidateMatchQuery, StringComparison.Ordinal);
        Assert.True(expression.MatchesItem("Pincel artístico de 19 cm", "unidade"));
        Assert.False(expression.MatchesItem("Pincel de parede com 19 cm", "unidade"));
    }
}
