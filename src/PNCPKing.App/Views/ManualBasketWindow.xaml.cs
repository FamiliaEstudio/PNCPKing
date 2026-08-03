using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class ManualBasketWindow : Window
{
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<QuotationLineAnalysis>> _analysesByProject;
    private readonly string _suggestedDescription;

    public ManualBasketWindow(
        IReadOnlyList<QuotationProject> projects,
        IReadOnlyDictionary<Guid, IReadOnlyList<QuotationLineAnalysis>> analysesByProject,
        Guid? selectedProjectId,
        string suggestedDescription,
        string minimumPrice,
        string maximumPrice)
    {
        InitializeComponent();
        _analysesByProject = analysesByProject;
        _suggestedDescription = suggestedDescription;
        ProjectComboBox.ItemsSource = projects;
        ProjectComboBox.SelectedItem = projects.FirstOrDefault(project => project.Id == selectedProjectId)
                                       ?? projects.FirstOrDefault();
        NewProjectNameTextBox.Text = projects.Count == 0
            ? $"Cotação {DateTime.Now:dd-MM-yyyy HH-mm}"
            : string.Empty;
        DescriptionTextBox.Text = suggestedDescription;
        MinimumPriceTextBox.Text = minimumPrice;
        MaximumPriceTextBox.Text = maximumPrice;
        RefreshLines();
    }

    public Guid? ExistingProjectId { get; private set; }
    public string NewProjectName { get; private set; } = string.Empty;
    public Guid? ExistingLineId { get; private set; }
    public Guid? ExistingBasketId { get; private set; }
    public string BasketName { get; private set; } = string.Empty;
    public QuotationLineInput? Input { get; private set; }

    private void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshLines();

    private void LineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var analysis = (LineComboBox.SelectedItem as LineChoice)?.Analysis;
        var existing = analysis is not null;
        foreach (var control in NewLineControls)
        {
            control.IsEnabled = !existing;
        }

        if (analysis is not null)
        {
            var line = analysis.Line;
            DescriptionTextBox.Text = line.Description;
            QuantityTextBox.Text = line.RequestedQuantity.ToString("N4", CultureInfo.CurrentCulture);
            UnitTextBox.Text = line.RequestedUnit;
            MinimumPriceTextBox.Text = line.MinimumUnitPrice?.ToString("N4", CultureInfo.CurrentCulture) ?? string.Empty;
            MaximumPriceTextBox.Text = line.MaximumUnitPrice?.ToString("N4", CultureInfo.CurrentCulture) ?? string.Empty;
            BasketSizeTextBox.Text = line.RequestedBasketSize.ToString(CultureInfo.CurrentCulture);
        }
        else if (DescriptionTextBox.Text.Length == 0)
        {
            DescriptionTextBox.Text = _suggestedDescription;
        }

        RefreshBaskets();
    }

    private void BasketComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var basket = (BasketComboBox.SelectedItem as BasketChoice)?.Basket;
        BasketNameTextBox.Text = basket?.Name ?? BuildNextManualName();
    }

    private void RefreshLines()
    {
        var project = ProjectComboBox.SelectedItem as QuotationProject;
        var choices = new List<LineChoice> { new("Novo item", null) };
        if (project is not null && _analysesByProject.TryGetValue(project.Id, out var analyses))
        {
            choices.AddRange(analyses.Select(analysis => new LineChoice(analysis.Line.EffectiveDisplayName, analysis)));
        }

        LineComboBox.ItemsSource = choices;
        LineComboBox.SelectedIndex = 0;
        RefreshBaskets();
    }

    private void RefreshBaskets()
    {
        var analysis = (LineComboBox.SelectedItem as LineChoice)?.Analysis;
        var choices = new List<BasketChoice> { new("Nova cesta manual", null) };
        if (analysis is not null)
        {
            choices.AddRange(analysis.Baskets
                .Where(basket => basket.IsManual)
                .Select(basket => new BasketChoice(
                    $"{basket.Name} ({basket.References.Count:N0} preço(s))",
                    basket)));
        }

        BasketComboBox.ItemsSource = choices;
        BasketComboBox.SelectedIndex = 0;
    }

    private string BuildNextManualName()
    {
        var count = (LineComboBox.SelectedItem as LineChoice)?.Analysis?.Baskets.Count(basket => basket.IsManual) ?? 0;
        return $"Manual {count + 1:N0}";
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NewProjectName = NewProjectNameTextBox.Text.Trim();
            ExistingProjectId = NewProjectName.Length > 0
                ? null
                : (ProjectComboBox.SelectedItem as QuotationProject)?.Id;
            if (ExistingProjectId is null && NewProjectName.Length == 0)
            {
                throw new ArgumentException("Selecione um projeto ou informe o nome de um novo.");
            }

            var selectedLine = NewProjectName.Length > 0
                ? null
                : (LineComboBox.SelectedItem as LineChoice)?.Analysis;
            ExistingLineId = selectedLine?.Line.Id;
            ExistingBasketId = (BasketComboBox.SelectedItem as BasketChoice)?.Basket?.ManualBasketId;
            BasketName = BasketNameTextBox.Text.Trim();
            if (BasketName.Length == 0)
            {
                throw new ArgumentException("Informe o nome da cesta manual.");
            }

            if (selectedLine is not null)
            {
                var line = selectedLine.Line;
                Input = new QuotationLineInput(
                    line.Description,
                    line.RequestedQuantity,
                    line.RequestedUnit,
                    line.MinimumUnitPrice,
                    line.MaximumUnitPrice)
                {
                    Weights = line.Weights,
                    RequestedBasketSize = line.RequestedBasketSize
                };
            }
            else
            {
                var description = DescriptionTextBox.Text.Trim();
                var unit = UnitTextBox.Text.Trim();
                if (description.Length == 0 || unit.Length == 0)
                {
                    throw new ArgumentException("Informe a descrição e a unidade do novo item.");
                }

                if (!decimal.TryParse(QuantityTextBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var quantity) ||
                    quantity <= 0)
                {
                    throw new ArgumentException("Informe uma quantidade maior que zero.");
                }

                if (!int.TryParse(BasketSizeTextBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var basketSize) ||
                    basketSize is < 3 or > 10)
                {
                    throw new ArgumentException("Informe de 3 a 10 preços para a cesta automática.");
                }

                var minimum = ParseOptionalPrice(MinimumPriceTextBox.Text, "preço mínimo");
                var maximum = ParseOptionalPrice(MaximumPriceTextBox.Text, "preço máximo");
                if (minimum < 0 || maximum < 0 || minimum is not null && maximum is not null && minimum > maximum)
                {
                    throw new ArgumentException("A faixa de preço é inválida.");
                }

                Input = new QuotationLineInput(description, quantity, unit, minimum, maximum)
                {
                    RequestedBasketSize = basketSize
                };
            }

            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Cesta manual", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static decimal? ParseOptionalPrice(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return decimal.TryParse(
            text,
            NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
            CultureInfo.CurrentCulture,
            out var value)
            ? value
            : throw new ArgumentException($"O {label} não é válido.");
    }

    private Control[] NewLineControls =>
    [
        DescriptionTextBox,
        QuantityTextBox,
        UnitTextBox,
        BasketSizeTextBox,
        MinimumPriceTextBox,
        MaximumPriceTextBox
    ];

    private sealed record LineChoice(string Label, QuotationLineAnalysis? Analysis);
    private sealed record BasketChoice(string Label, QuotationBasket? Basket);
}
