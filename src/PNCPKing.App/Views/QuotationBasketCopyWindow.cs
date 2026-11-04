using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;

namespace PNCPKing.App.Views;

public sealed class QuotationBasketCopyWindow : Window
{
    private readonly ListBox _destinations;
    public string? DestinationKey => (_destinations.SelectedItem as Destination)?.Key;

    public QuotationBasketCopyWindow(IReadOnlyList<QuotationBasketDisplay> baskets)
    {
        SetResourceReference(StyleProperty, "AppWindow");
        Title = "Copiar preço para outra cesta";
        Width = 720; Height = 430; MinWidth = 460; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MonitorAwareWindowBehavior.Attach(this);
        var panel = new DockPanel { Margin = new Thickness(16) };
        var explanation = new TextBlock
        {
            Text = "Escolha uma cesta do mesmo item. O preço e sua conversão serão copiados; a origem será preservada. " +
                   "Se o destino for automático, será criada uma cópia manual para edição. Confira a cesta antes de confirmá-la.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(explanation, Dock.Top); panel.Children.Add(explanation);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        var copy = new Button { Content = "Copiar preço", IsDefault = true, IsEnabled = false };
        copy.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(copy);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        _destinations = new ListBox
        {
            ItemsSource = baskets.Select((basket, index) => new Destination(basket.Key,
                $"{(basket.Source.IsManual ? basket.Name : $"Automática {index + 1:00}")} · {basket.AdoptedPriceText} · " +
                $"{basket.ReferenceCount} preços · {basket.Tags}")).ToArray(),
            DisplayMemberPath = nameof(Destination.Label)
        };
        _destinations.SelectionChanged += (_, _) => copy.IsEnabled = DestinationKey is not null;
        panel.Children.Add(_destinations);
        Content = panel;
    }

    private sealed record Destination(string Key, string Label);
}
