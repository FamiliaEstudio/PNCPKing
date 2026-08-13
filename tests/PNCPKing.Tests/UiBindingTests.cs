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

    [Fact]
    public void ContractExactCount_IsExposedAsExplicitCancelableCommands()
    {
        var xamlPath = Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var buttons = document.Descendants(presentation + "Button").ToArray();

        Assert.Contains(buttons, element =>
            element.Attribute("Command")?.Value.Contains(
                "CalculateExactContractCountCommand",
                StringComparison.Ordinal) == true);
        Assert.Contains(buttons, element =>
            element.Attribute("Command")?.Value.Contains(
                "CancelExactContractCountCommand",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CatalogDefaultFlag_IsReadOnlyOneWay()
    {
        var document = LoadView("CatalogDictionaryWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var column = Assert.Single(
            document.Descendants(presentation + "DataGridCheckBoxColumn"),
            element => element.Attribute("Header")?.Value == "Padrão");

        Assert.Equal("True", column.Attribute("IsReadOnly")?.Value);
        Assert.Contains("Mode=OneWay", column.Attribute("Binding")?.Value.Replace(" ", string.Empty));
    }

    [Fact]
    public void QuotationCapturedFlags_AreReadOnlyOneWay()
    {
        var document = LoadView("QuotationItemWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var columns = document.Descendants(presentation + "DataGridCheckBoxColumn")
            .Where(element => element.Attribute("Header")?.Value.EndsWith("capturado", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(2, columns.Length);
        Assert.All(columns, column =>
        {
            Assert.Equal("True", column.Attribute("IsReadOnly")?.Value);
            Assert.Contains("Mode=OneWay", column.Attribute("Binding")?.Value.Replace(" ", string.Empty));
        });
    }

    [Fact]
    public void MainWindow_UsesContextualContractsPanelAndCompactMaintenanceMenus()
    {
        var document = LoadView("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var root = document.Root!;

        Assert.Equal("800", root.Attribute("MinWidth")?.Value);
        Assert.Equal("520", root.Attribute("MinHeight")?.Value);
        Assert.DoesNotContain(
            document.Descendants(presentation + "TabItem"),
            element => element.Attribute("Header")?.Value == "Contratações e cache permanente");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => element.Attribute("Command")?.Value.Contains(
                "ToggleContractsPanelCommand",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => element.Attribute("Command")?.Value.Contains(
                "ToggleMaintenancePanelCommand",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants(presentation + "TabControl"),
            element => element.Attribute("SelectedValue")?.Value.Contains(
                "SelectedResultsWorkspace",
                StringComparison.Ordinal) == true);

        var menuHeaders = document.Descendants(presentation + "MenuItem")
            .Select(element => element.Attribute("Header")?.Value)
            .ToArray();
        Assert.Contains("_Arquivo", menuHeaders);
        Assert.Contains("_Diagnóstico", menuHeaders);
        Assert.Contains("_Limpeza", menuHeaders);
    }

    [Fact]
    public void ColumnChooser_AppliesOrCancelsDraftInsteadOfChangingLiveGrid()
    {
        var document = LoadView("ColumnChooserWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var buttons = document.Descendants(presentation + "Button").ToArray();

        Assert.Contains(buttons, element =>
            element.Attribute("Content")?.Value == "Aplicar" &&
            element.Attribute("Click")?.Value == "Apply_Click");
        Assert.Contains(buttons, element =>
            element.Attribute("Content")?.Value == "Cancelar" &&
            element.Attribute("Click")?.Value == "Cancel_Click");
        Assert.Contains(buttons, element =>
            element.Attribute("Content")?.Value == "Restaurar padrão" &&
            element.Attribute("Click")?.Value == "Reset_Click");
    }

    private static XDocument LoadView(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Views", fileName));
}
