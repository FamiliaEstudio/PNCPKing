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
    public void BuildMatchQuery_UsesPrefixesAndQuotedPhrases()
    {
        var query = SearchText.BuildMatchQuery("medic \"saúde pública\"");

        Assert.Equal("\"medic\"* AND \"saude publica\"", query);
    }
}
