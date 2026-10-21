using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public sealed class QuotationOrganizationWindow : Window
{
    private readonly RadioButton _alphabetical;
    private readonly CheckBox _ignorePrefix;
    private readonly CheckBox _removePrefixes;

    public QuotationOrganizationOptions Options => new(
        _alphabetical.IsChecked == true, _ignorePrefix.IsChecked == true, _removePrefixes.IsChecked == true);

    public QuotationOrganizationWindow(QuotationProject project)
    {
        SetResourceReference(StyleProperty, "AppWindow");
        Title = "Organizar Itens";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MonitorAwareWindowBehavior.Attach(this);

        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var apply = new Button { Content = "Aplicar ao projeto", IsDefault = true };
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(apply);
        buttons.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var panel = new StackPanel();
        panel.Children.Add(Wrapped($"Organizar todos os itens de “{project.Name}”."));
        _alphabetical = new RadioButton
        {
            Content = Wrapped("Somente ordem alfabética — permite quantidade e unidade em branco, sem cestas confirmadas."),
            GroupName = "OrderMode", IsChecked = project.Organization is null, Margin = new Thickness(0, 12, 0, 8)
        };
        panel.Children.Add(_alphabetical);
        panel.Children.Add(new RadioButton
        {
            Content = Wrapped("Organizar por grupos e cotas — exige quantidades inteiras positivas e cestas confirmadas."),
            GroupName = "OrderMode", IsChecked = project.Organization is not null, Margin = new Thickness(0, 0, 0, 16)
        });
        _ignorePrefix = new CheckBox
        {
            Content = Wrapped("Ignorar “Item + número” no início do nome ao ordenar."),
            IsChecked = project.IgnoreItemNumberPrefix, Margin = new Thickness(0, 0, 0, 8)
        };
        panel.Children.Add(_ignorePrefix);
        _removePrefixes = new CheckBox
        {
            Content = Wrapped("Apagar “Item + número” do início dos nomes de TODOS os itens deste projeto."),
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(_removePrefixes);
        panel.Children.Add(Wrapped("Exemplos: “Item 1 Água”, “item 01 Água”, “Item 01 - Água” e “Item 3-Água”. " +
            "Ao apagar, o nome passa a ser “Água”. A numeração automática “Item 1 -”, “Item 2 -” etc. continuará na planilha Excel."));
        panel.Children.Add(new TextBlock
        {
            Text = "A ordem alfabética simples substitui a numeração de cotas aplicada anteriormente. Os grupos cadastrados continuam disponíveis.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
        });
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
}
