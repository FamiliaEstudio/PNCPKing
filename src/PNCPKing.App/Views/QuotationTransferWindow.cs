using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public sealed class QuotationTransferWindow : Window
{
    private readonly ComboBox _projects;
    public QuotationProject? Destination => _projects.SelectedItem as QuotationProject;
    public bool Copy { get; private set; }

    public QuotationTransferWindow(string itemName, IReadOnlyList<QuotationProject> projects)
    {
        SetResourceReference(StyleProperty, "AppWindow");
        Title = "Transferir ou copiar item para outro projeto";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MonitorAwareWindowBehavior.Attach(this);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Transferir ou copiar “{itemName}” com todos os preços, cestas e evidências.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new TextBlock { Text = "Projeto de destino:" });
        _projects = new ComboBox
        {
            ItemsSource = projects, DisplayMemberPath = nameof(QuotationProject.Name),
            Margin = new Thickness(0, 6, 0, 12)
        };
        panel.Children.Add(_projects);
        panel.Children.Add(new TextBlock
        {
            Text = "Transferir remove o item da origem. Copiar mantém o item e seus dados na origem. " +
                "Em ambos os casos, o item entra solto no destino, sem vínculo com a automação de origem. " +
                "Se os projetos já estiverem organizados por grupos e cotas, use Organizar Itens novamente. " +
                "Ao mudar entre projeto comum e de medicamentos, confirme a cesta novamente no destino.",
            TextWrapping = TextWrapping.Wrap
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var transfer = new Button { Content = "Transferir", IsDefault = true, IsEnabled = false };
        var copy = new Button { Content = "Copiar", IsEnabled = false };
        _projects.SelectionChanged += (_, _) => transfer.IsEnabled = copy.IsEnabled = Destination is not null;
        transfer.Click += (_, _) => { if (Destination is not null) DialogResult = true; };
        copy.Click += (_, _) => { if (Destination is not null) { Copy = true; DialogResult = true; } };
        buttons.Children.Add(transfer);
        buttons.Children.Add(copy);
        buttons.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;
    }
}
