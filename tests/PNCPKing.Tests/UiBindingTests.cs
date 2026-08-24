using System.Text.Json;
using System.Xml.Linq;
using PNCPKing.App.Services;

namespace PNCPKing.Tests;

public sealed class UiBindingTests
{
    [Fact]
    public void StartupOverlay_BlocksTheWholeWindowWithoutDisablingTheContentTree()
    {
        var document = LoadView("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var rootGrid = Assert.Single(document.Root!.Elements(presentation + "Grid"));
        var contentGrid = Assert.Single(rootGrid.Elements(presentation + "Grid"), element =>
            element.Attribute("Margin")?.Value == "10");
        var overlay = Assert.Single(rootGrid.Elements(presentation + "Grid"), element =>
            element.Attribute("Visibility")?.Value.Contains("IsInitializing", StringComparison.Ordinal) == true);

        Assert.Null(contentGrid.Attribute("IsEnabled"));
        Assert.False(string.IsNullOrWhiteSpace(overlay.Attribute("Background")?.Value));
        Assert.Equal("100", overlay.Attribute("Panel.ZIndex")?.Value);
        Assert.Equal("Cycle", overlay.Attribute("KeyboardNavigation.TabNavigation")?.Value);
        Assert.Equal("True", overlay.Attribute("FocusManager.IsFocusScope")?.Value);
        Assert.Contains(overlay.Descendants(presentation + "Button"), button =>
            button.Attribute("Command")?.Value.Contains("CancelStartupCommand", StringComparison.Ordinal) == true);
    }

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

        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => element.Attribute("Content")?.Value == "Opções ▾");
    }

    [Fact]
    public void MainWindow_CatalogRefreshSelector_IsInsideMaintenancePanel()
    {
        var document = LoadView("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var selector = Assert.Single(
            document.Descendants(presentation + "ComboBox"),
            element => element.Attribute("ItemsSource")?.Value.Contains(
                "CatalogRefreshOptions",
                StringComparison.Ordinal) == true);

        Assert.Contains(
            "SelectedCatalogRefreshOption",
            Assert.IsType<XAttribute>(selector.Attribute("SelectedItem")).Value);
        Assert.Contains(selector.Ancestors(presentation + "Border"), border =>
            border.Attribute("Visibility")?.Value.Contains(
                "IsMaintenancePanelOpen",
                StringComparison.Ordinal) == true);
        Assert.Contains(selector.Ancestors(presentation + "Border").SelectMany(border => border.Descendants()),
            element => element.Name == presentation + "Button" &&
                       element.Attribute("Command")?.Value.Contains(
                           "UpdateCatalogCommand",
                           StringComparison.Ordinal) == true);
    }

    [Fact]
    public void AppSettings_CatalogRefreshDefaultsToWeeklyAndNormalizesInvalidValues()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("""
            {
              "DataFolder": "D:\\PNCP",
              "IsConfigured": true,
              "SettingsVersion": 3
            }
            """);

        Assert.NotNull(legacy);
        Assert.Equal(7, legacy.EffectiveCatalogRefreshIntervalDays);
        Assert.Equal(7, AppSettings.NormalizeCatalogRefreshIntervalDays(-1));
        Assert.Equal(7, AppSettings.NormalizeCatalogRefreshIntervalDays(30));
        Assert.Equal(0, AppSettings.NormalizeCatalogRefreshIntervalDays(0));
        Assert.Equal(2, AppSettings.NormalizeCatalogRefreshIntervalDays(2));
        Assert.Equal(15, AppSettings.NormalizeCatalogRefreshIntervalDays(15));
    }

    [Fact]
    public void AppSettings_DesktopShortcutDefaultsToEnabledAndPersistsDisabled()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("""
            {
              "DataFolder": "D:\\PNCP",
              "IsConfigured": true,
              "SettingsVersion": 4
            }
            """);

        Assert.NotNull(legacy);
        Assert.True(legacy.EffectiveDesktopShortcutEnabled);
        Assert.Equal(5, AppSettings.CurrentVersion);

        var disabledJson = JsonSerializer.Serialize(legacy with
        {
            SettingsVersion = AppSettings.CurrentVersion,
            DesktopShortcutEnabled = false
        });
        var disabled = JsonSerializer.Deserialize<AppSettings>(disabledJson);

        Assert.NotNull(disabled);
        Assert.False(disabled.DesktopShortcutEnabled);
        Assert.False(disabled.EffectiveDesktopShortcutEnabled);
    }

    [Fact]
    public void DesktopShortcutService_DisabledRemovesOnlyManagedShortcut()
    {
        var desktop = Path.Combine(
            Path.GetTempPath(),
            $"pncpking-shortcut-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(desktop);
        try
        {
            var service = new DesktopShortcutService(desktop, () => "ignored.exe");
            var unrelated = Path.Combine(desktop, "Outro atalho.lnk");
            File.WriteAllText(service.ShortcutPath, "atalho gerenciado");
            File.WriteAllText(unrelated, "preservar");

            service.Apply(enabled: false);

            Assert.False(File.Exists(service.ShortcutPath));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(desktop, recursive: true);
        }
    }

    [Fact]
    public void MainWindow_UsesPncpKingBrandingAndSingleOptionsMenu()
    {
        var document = LoadView("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var root = document.Root!;
        const string logoPath = "/PNCPKing;component/Assets/Branding/PNCPKingLogo.png";

        Assert.Equal(logoPath, root.Attribute("Icon")?.Value);
        Assert.Equal(
            2,
            document.Descendants(presentation + "Image")
                .Count(element => element.Attribute("Source")?.Value == logoPath));
        Assert.DoesNotContain(
            document.Descendants(presentation + "TextBlock"),
            element => element.Attribute("Text")?.Value == "PNCP King");

        var optionsButton = Assert.Single(
            document.Descendants(presentation + "Button"),
            element => element.Attribute("Content")?.Value == "Opções ▾");
        Assert.Equal("DropdownMenu_Click", optionsButton.Attribute("Click")?.Value);
        var menu = Assert.Single(optionsButton.Descendants(presentation + "ContextMenu"));
        Assert.Contains(
            "PlacementTarget.DataContext",
            Assert.IsType<XAttribute>(menu.Attribute("DataContext")).Value);

        var expectedGroups = new Dictionary<string, string[]>
        {
            ["_Arquivo"] = [
                "{Binding ExportBackupCommand}",
                "{Binding ImportBackupCommand}"
            ],
            ["_Diagnóstico"] = [
                "{Binding OpenDiagnosticLogsCommand}",
                "{Binding ExportPerformanceReportCommand}",
                "{Binding ComparePerformanceReportCommand}"
            ],
            ["_Limpeza"] = [
                "{Binding ClearCacheCommand}",
                "{Binding ClearDocumentCacheCommand}"
            ]
        };
        foreach (var expectedGroup in expectedGroups)
        {
            var group = Assert.Single(
                menu.Elements(presentation + "MenuItem"),
                element => element.Attribute("Header")?.Value == expectedGroup.Key);
            Assert.Equal(
                expectedGroup.Value,
                group.Elements(presentation + "MenuItem")
                    .Select(element => Assert.IsType<XAttribute>(element.Attribute("Command")).Value)
                    .ToArray());
        }

        var shortcut = Assert.Single(
            menu.Elements(presentation + "MenuItem"),
            element => element.Attribute("Header")?.Value == "Atalho na área de trabalho");
        Assert.Equal("True", shortcut.Attribute("IsCheckable")?.Value);
        Assert.Contains(
            "IsDesktopShortcutEnabled",
            Assert.IsType<XAttribute>(shortcut.Attribute("IsChecked")).Value);
        Assert.Equal(
            "{Binding ToggleDesktopShortcutCommand}",
            shortcut.Attribute("Command")?.Value);
        Assert.Single(menu.Elements(presentation + "Separator"));
    }

    [Fact]
    public void MainWindow_QuotationActions_AreGroupedInCompactMenus()
    {
        var document = LoadView("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var buttons = document.Descendants(presentation + "Button").ToArray();
        var groups = new[]
        {
            new
            {
                Header = "Cotações ▾",
                Separators = 2,
                Commands = new[]
                {
                    "{Binding NewQuotationCommand}",
                    "{Binding RenameQuotationCommand}",
                    "{Binding DeleteQuotationCommand}",
                    "{Binding NewQuotationItemCommand}",
                    "{Binding DeleteQuotationLineCommand}",
                    "{Binding RenameQuotationLineCommand}",
                    "{Binding OpenQuotationItemCommand}",
                    "{Binding ImportQuotationCommand}"
                }
            },
            new
            {
                Header = "Automação ▾",
                Separators = 3,
                Commands = new[]
                {
                    "{Binding AiQuotationCommand}",
                    "{Binding RefineQuotationPromptsCommand}",
                    "{Binding ResumeQuotationAutomationCommand}",
                    "{Binding CancelQuotationAutomationCommand}",
                    "{Binding OpenRestrictiveQuotationSearchCommand}",
                    "{Binding OpenIntermediateQuotationSearchCommand}",
                    "{Binding OpenBroadQuotationSearchCommand}",
                    "{Binding UpdateQuotationSampleCommand}",
                    "{Binding AdjustQuotationWeightsCommand}"
                }
            },
            new
            {
                Header = "Exportar/Importar ▾",
                Separators = 1,
                Commands = new[]
                {
                    "{Binding ExportQuotationCommand}",
                    "{Binding ExportQuotationPackageCommand}",
                    "{Binding ImportQuotationPackageCommand}"
                }
            }
        };

        foreach (var group in groups)
        {
            var button = Assert.Single(
                buttons,
                element => element.Attribute("Content")?.Value == group.Header);
            Assert.Equal("DropdownMenu_Click", button.Attribute("Click")?.Value);

            var menu = Assert.Single(button.Descendants(presentation + "ContextMenu"));
            var dataContext = Assert.IsType<XAttribute>(menu.Attribute("DataContext")).Value;
            Assert.Contains(
                "PlacementTarget.DataContext",
                dataContext);
            Assert.Equal(group.Separators, menu.Descendants(presentation + "Separator").Count());
            Assert.Equal(
                group.Commands,
                menu.Descendants(presentation + "MenuItem")
                    .Select(element => Assert.IsType<XAttribute>(element.Attribute("Command")).Value)
                    .ToArray());
        }

        var chooseColumns = Assert.Single(
            buttons,
            element => element.Attribute("Content")?.Value == "Escolher colunas — itens");
        Assert.Equal("ChooseColumns_Click", chooseColumns.Attribute("Click")?.Value);
        var chooserTag = Assert.IsType<XAttribute>(chooseColumns.Attribute("Tag")).Value;
        Assert.Contains("QuotationLinesGrid", chooserTag);
    }

    [Fact]
    public void NewQuotationItemWindow_CollectsOnlyTheThreeRequiredFields()
    {
        var document = LoadView("NewQuotationItemWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var textBoxes = document.Descendants(presentation + "TextBox").ToArray();

        Assert.Equal(
            ["DescriptionTextBox", "QuantityTextBox", "UnitTextBox"],
            textBoxes.Select(element => Assert.IsType<XAttribute>(element.Attribute(x + "Name")).Value).ToArray());
        Assert.All(textBoxes, element => Assert.Null(element.Attribute("Text")));
        Assert.Contains(document.Descendants(presentation + "Button"), element =>
            element.Attribute("Content")?.Value == "Criar item" &&
            element.Attribute("IsDefault")?.Value == "True");
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
