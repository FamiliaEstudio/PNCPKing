using System.Xml.Linq;

namespace PNCPKing.Tests;

public sealed class UiBindingTests
{
    [Fact]
    public void QuotationItemSearchProgress_IsBoundOneWay()
    {
        var xamlPath = Path.Combine(
            AppContext.BaseDirectory,
            "Views",
            "QuotationItemWindow.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var progressBar = Assert.Single(
            document.Descendants(presentation + "ProgressBar"),
            element =>
                element.Attribute("Value")?.Value.Contains(
                    "SearchProgress",
                    StringComparison.Ordinal) == true);
        var binding = Assert.IsType<XAttribute>(progressBar.Attribute("Value")).Value;

        Assert.Contains("Mode=OneWay", binding.Replace(" ", string.Empty));
    }
}
