using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class QuotationWeightsWindow : Window
{
    private AdequacyWeights _weights;
    private bool _changingWeights;

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

    public AdequacyWeights Weights => _weights;

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
        _weights.Validate();
        DialogResult = true;
    }
}
