using System.Globalization;
using System.Text;
using PNCPKing.App.Services;

namespace PNCPKing.Tests;

public sealed class GridTextTests
{
    [Theory]
    [InlineData("Sem alteração", "Sem alteração")]
    [InlineData("a\r\nb\nc\td\u2028e\u2029f", "a b c d e f")]
    public void CompactDisplay_PreservesTheOriginal(string original, string expected)
    {
        var row = new { Description = original };
        Assert.Equal(expected, GridText.SingleLine(original));
        Assert.Equal(original, GridText.Read(row, "Description"));
    }

    [Theory]
    [InlineData("início\nAÇÃO final", "acao", 7, 4)]
    [InlineData("início\nac\u0327a\u0303o final", "AÇÃO", 7, 6)]
    [InlineData("agulha e agulha", "agulha", 0, 6)]
    [InlineData("a.b literal", "a.b", 0, 3)]
    [InlineData("descrição", "ausente", -1, 0)]
    [InlineData("descrição", "  ", -1, 0)]
    public void Find_ReturnsTheOriginalSpanWithoutChangingAccents(string text, string term, int start, int length)
    {
        var match = GridText.Find(text, term);
        Assert.Equal(start, match.Start);
        Assert.Equal(length, match.Length);
    }

    [Fact]
    public void Clipboard_PreservesIdentifiersNewlinesAndUnicodeByteOffsets()
    {
        string[][] rows = [["00123456000190", "ação\r\ncom \"aspas\"\te tabulação"], ["0001", "<café>"]];
        var tsv = GridText.Tabular(rows);
        Assert.StartsWith("00123456000190\t\"ação\r\ncom \"\"aspas\"\"\te tabulação\"", tsv);
        Assert.EndsWith("\r\n0001\t<café>", tsv);
        var html = GridText.ClipboardHtml(rows, [true, false]);
        var header = html.Split("\r\n").Take(5).Select(line => line.Split(':')).ToDictionary(p => p[0], p => p[1]);
        var bytes = Encoding.UTF8.GetBytes(html);
        var start = int.Parse(header["StartFragment"], CultureInfo.InvariantCulture);
        var end = int.Parse(header["EndFragment"], CultureInfo.InvariantCulture);
        var fragment = Encoding.UTF8.GetString(bytes[start..end]);
        Assert.StartsWith("<table>", fragment);
        Assert.EndsWith("</table>", fragment);
        Assert.Contains("mso-number-format:'\\@'", fragment);
        Assert.Contains("&lt;caf", fragment);
        Assert.Equal(bytes.Length, int.Parse(header["EndHTML"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ReadAndFormat_RespectNestedFieldsNullsAndDisplayCulture()
    {
        var row = new { Supplier = new { Cnpj = "00123456000190" }, Price = 12.34m };
        Assert.Equal("00123456000190", GridText.Read(row, "Supplier.Cnpj"));
        Assert.Equal("12,3400", GridText.Format(GridText.Read(row, "Price"), "N4", CultureInfo.GetCultureInfo("pt-BR")));
        Assert.Null(GridText.Read(row, "Unknown"));
        Assert.Empty(GridText.Format(null, "C4", CultureInfo.InvariantCulture));
    }
}
