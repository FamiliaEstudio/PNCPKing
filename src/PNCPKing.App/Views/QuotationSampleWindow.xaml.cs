using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class QuotationSampleWindow : Window
{
    private AdequacyWeights _weights = AdequacyWeights.Default;
    private bool _changingWeights;

    public QuotationSampleWindow(
        IReadOnlyList<QuotationProject> projects,
        string description,
        string minimumPrice,
        string maximumPrice)
    {
        InitializeComponent();
        Projects = projects;
        DataContext = this;
        ProjectComboBox.SelectedIndex = projects.Count > 0 ? 0 : -1;
        NewProjectNameTextBox.Text = projects.Count == 0 ? $"Cotação {DateTime.Now:dd-MM-yyyy HH-mm}" : string.Empty;
        DescriptionTextBox.Text = description;
        MinimumPriceTextBox.Text = minimumPrice;
        MaximumPriceTextBox.Text = maximumPrice;
        ApplyWeights(_weights);
        foreach (var slider in WeightSliders)
        {
            slider.ValueChanged += WeightSlider_ValueChanged;
        }

        QuantityTextBox.Focus();
    }

    public IReadOnlyList<QuotationProject> Projects { get; }
    public Guid? ExistingProjectId { get; private set; }
    public string NewProjectName { get; private set; } = string.Empty;
    public QuotationLineInput? Input { get; private set; }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var description = DescriptionTextBox.Text.Trim();
            var unit = UnitTextBox.Text.Trim();
            if (description.Length == 0 || unit.Length == 0)
            {
                throw new ArgumentException("Informe a descrição e a unidade do item.");
            }

            if (!decimal.TryParse(QuantityTextBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var quantity) || quantity <= 0)
            {
                throw new ArgumentException("Informe uma quantidade maior que zero.");
            }

            var minimum = ParseOptionalPrice(MinimumPriceTextBox.Text, "preço mínimo");
            var maximum = ParseOptionalPrice(MaximumPriceTextBox.Text, "preço máximo");
            if (minimum < 0 || maximum < 0 || minimum is not null && maximum is not null && minimum > maximum)
            {
                throw new ArgumentException("A faixa de preço é inválida.");
            }

            NewProjectName = NewProjectNameTextBox.Text.Trim();
            ExistingProjectId = NewProjectName.Length > 0
                ? null
                : (ProjectComboBox.SelectedItem as QuotationProject)?.Id;
            if (ExistingProjectId is null && NewProjectName.Length == 0)
            {
                throw new ArgumentException("Selecione um projeto ou informe o nome de um novo projeto.");
            }

            Input = new QuotationLineInput(description, quantity, unit, minimum, maximum)
            {
                Weights = _weights
            };
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Cotação", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private Slider[] WeightSliders =>
    [
        DescriptionWeightSlider,
        UnitWeightSlider,
        QuantityWeightSlider,
        ProximityWeightSlider,
        RecencyWeightSlider
    ];

    private void WeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_changingWeights || sender is not Slider { Tag: string tag } ||
            !int.TryParse(tag, CultureInfo.InvariantCulture, out var component))
        {
            return;
        }

        var requestedValue = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
        ApplyWeights(_weights.Rebalance((AdequacyWeightComponent)component, requestedValue));
    }

    private void ApplyWeights(AdequacyWeights weights)
    {
        _changingWeights = true;
        try
        {
            _weights = weights;
            DescriptionWeightSlider.Value = weights.Description;
            UnitWeightSlider.Value = weights.Unit;
            QuantityWeightSlider.Value = weights.Quantity;
            ProximityWeightSlider.Value = weights.Proximity;
            RecencyWeightSlider.Value = weights.Recency;
        }
        finally
        {
            _changingWeights = false;
        }
    }
}
