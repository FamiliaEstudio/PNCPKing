using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class QuotationWeightsWindow : Window
{
    private AdequacyWeights _weights;
    private bool _changingWeights;
    private bool _editPriceRange;
    private int _priceDecimalPlaces = 2;

    public QuotationWeightsWindow(AdequacyWeights weights)
    {
        weights.Validate();
        _weights = weights;
        InitializeComponent();
        ApplyWeights(weights);
        foreach (var slider in WeightSliders)
        {
            slider.ValueChanged += WeightSlider_ValueChanged;
        }
    }

    public QuotationWeightsWindow(QuotationLineAnalysis analysis) : this(analysis.Line.Weights)
    {
        _editPriceRange = true;
        _priceDecimalPlaces = analysis.PriceDecimalPlaces;
        Title = "Recalcular cestas";
        Height = 660;
        PriceRangePanel.Visibility = Visibility.Visible;
        AcceptButton.Content = "Recalcular cestas";
        var format = $"N{_priceDecimalPlaces}";
        MinimumPriceTextBox.Text = analysis.Line.MinimumUnitPrice is { } minimum
            ? QuotationMoney.Truncate(minimum, _priceDecimalPlaces).ToString(format, CultureInfo.GetCultureInfo("pt-BR")) : "";
        MaximumPriceTextBox.Text = analysis.Line.MaximumUnitPrice is { } maximum
            ? QuotationMoney.Truncate(maximum, _priceDecimalPlaces).ToString(format, CultureInfo.GetCultureInfo("pt-BR")) : "";
        RangeHelpText.Text = $"Usa os {analysis.CollectedCount:N0} preços já coletados deste item, sem nova pesquisa. " +
            "Deixe um limite vazio para não restringir esse lado da faixa. " +
            $"Valores com {_priceDecimalPlaces} casas decimais; excedentes são truncados. " +
            "Preços fora da faixa e cestas manuais são preservados. Confirme a cesta desejada após o recálculo.";
        MonitorAwareWindowBehavior.Attach(this);
    }

    public AdequacyWeights Weights => _weights;
    public decimal? MinimumUnitPrice { get; private set; }
    public decimal? MaximumUnitPrice { get; private set; }

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

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _weights.Validate();
            if (_editPriceRange)
            {
                var minimum = ParseOptionalPrice(MinimumPriceTextBox.Text, "mínimo");
                var maximum = ParseOptionalPrice(MaximumPriceTextBox.Text, "máximo");
                if (minimum < 0 || maximum < 0 || minimum > maximum)
                    throw new ArgumentException("Use limites não negativos e preço mínimo menor ou igual ao máximo.");
                MinimumUnitPrice = minimum is { } min ? QuotationMoney.Truncate(min, _priceDecimalPlaces) : null;
                MaximumUnitPrice = maximum is { } max ? QuotationMoney.Truncate(max, _priceDecimalPlaces) : null;
            }
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
            ValidationText.Visibility = Visibility.Visible;
        }
    }

    private static decimal? ParseOptionalPrice(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
                CultureInfo.GetCultureInfo("pt-BR"), out var value) || value > long.MaxValue / DecimalScale.Scale)
            throw new ArgumentException($"O preço {label} não é válido. Use o formato 1.234,56.");
        return value;
    }
}
