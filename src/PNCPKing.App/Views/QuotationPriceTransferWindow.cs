using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public sealed class QuotationPriceTransferWindow : Window
{
    private readonly ComboBox _baskets;
    public QuotationBasket? Destination => (_baskets.SelectedItem as ComboBoxItem)?.Tag as QuotationBasket;

    public QuotationPriceTransferWindow(QuotationBasket source, IReadOnlyList<QuotationBasket> destinations,
        int priceCount, int priceDecimalPlaces = 2)
    {
        SetResourceReference(StyleProperty, "AppWindow");
        Title = "Transferir preços entre cestas";
        Width = 560;
        MinWidth = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MonitorAwareWindowBehavior.Attach(this);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Transferir {priceCount:N0} preço(s) da cesta {(source.IsManual ? source.Name : "automática selecionada")} para outra cesta deste item.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new TextBlock { Text = "Cesta de destino:" });
        _baskets = new ComboBox { Margin = new Thickness(0, 6, 0, 12) };
        for (var index = 0; index < destinations.Count; index++)
        {
            var basket = destinations[index];
            var name = basket.IsManual ? basket.Name : $"Automática {index + 1}";
            var price = basket.AdoptedPrice.ToString($"C{priceDecimalPlaces}", CultureInfo.GetCultureInfo("pt-BR"));
            _baskets.Items.Add(new ComboBoxItem
            {
                Content = $"{name} · {basket.References.Count:N0} preços · {price} · " +
                    (basket.AggregationMethod == QuotationAggregationMethod.Median ? "mediana" : "média") +
                    $" · desvio {basket.MaximumDeviationPercent:N2}%", Tag = basket
            });
        }
        panel.Children.Add(_baskets);
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Escolha o destino para conferir os preços que já pertencem a ele." };
        panel.Children.Add(new ScrollViewer
        {
            Content = preview, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Os preços saem da origem e entram no destino com seus divisores e conversões. " +
                "Se algum já estiver no destino, a transferência será recusada sem alterar as cestas. " +
                "Cestas automáticas serão ajustadas em cópias manuais; as sugestões originais serão mantidas. " +
                "A cesta de origem vazia será removida. Depois, revise os valores e confirme a cesta desejada.",
            TextWrapping = TextWrapping.Wrap
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var transfer = new Button { Content = "Transferir", IsDefault = true, IsEnabled = false };
        _baskets.SelectionChanged += (_, _) =>
        {
            transfer.IsEnabled = Destination is not null;
            if (Destination is { } destination)
                preview.Text = "Preços atuais do destino:\n" + string.Join("\n", destination.References.Select(reference =>
                    $"• {reference.SupplierName} · {reference.UnitPrice.ToString("C4", CultureInfo.GetCultureInfo("pt-BR"))}" +
                    $" · {reference.ContractId} · item {reference.ItemNumber}"));
        };
        transfer.Click += (_, _) => { if (Destination is not null) DialogResult = true; };
        buttons.Children.Add(transfer);
        buttons.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;
    }
}
