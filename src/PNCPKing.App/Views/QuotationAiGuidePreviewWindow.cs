using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public sealed class QuotationAiGuidePreviewWindow : Window
{
    public bool AllowPriceRangeExpansion { get; private set; }
    public bool AllowTechnicalReview { get; private set; }

    public QuotationAiGuidePreviewWindow(QuotationAiGuidePreview preview, bool allowApply = true,
        bool allowPriceRangeExpansion = false, bool allowTechnicalReview = false)
    {
        AllowPriceRangeExpansion = allowPriceRangeExpansion;
        AllowTechnicalReview = allowTechnicalReview;
        SetResourceReference(StyleProperty, "AppWindow");
        Title = allowApply ? "Conferir guia da IA" : "Resultado da aplicação do guia";
        Width = 1000;
        Height = 620;
        MinWidth = 500;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MonitorAwareWindowBehavior.Attach(this);
        var panel = new Grid { Margin = new Thickness(16) };
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bodyPanel = new StackPanel();
        var body = new ScrollViewer
        {
            Name = "GuideBody", Content = bodyPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        panel.Children.Add(body);
        bodyPanel.Children.Add(new TextBlock
        {
            Name = "GuideSummary",
            Text = allowApply
                ? (preview.ReadyCount == 0
                    ? "Nenhuma cesta da IA será aplicada. Confira no guia os itens marcados como revisar ou insuficiente. "
                    : $"{preview.ReadyCount:N0} cesta(s) pronta(s); {preview.PendingCount:N0} item(ns) com pendências. ") +
                  $"Os candidatos de {preview.ImportableCount:N0} item(ns) poderão ser importados para análise no programa. " +
                  "Confira as mensagens e as faixas antes de aplicar."
                : "Confira abaixo o resultado de cada item. As cestas existentes que não pertencem a este guia foram preservadas.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        var grid = new DataGrid
        {
            Name = "GuideItems", Height = 180,
            ItemsSource = preview.Items.Select(item => new
            {
                item.Name,
                item.CandidateCount,
                Status = item.Status switch
                {
                    "ready" => "Pronto",
                    "review" => "Revisar",
                    "insufficient" => "Insuficiente",
                    "applied" => "Aplicado",
                    "unchanged" => "Já aplicado",
                    "candidates" => "Preços importados",
                    _ => item.Status
                },
                item.Message
            }).ToArray(), AutoGenerateColumns = false, IsReadOnly = true,
            EnableRowVirtualization = true, EnableColumnVirtualization = true,
            CanUserAddRows = false, CanUserDeleteRows = false
        };
        VirtualizingPanel.SetIsVirtualizing(grid, true);
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        grid.Columns.Add(new DataGridTextColumn { Header = "Item", Binding = new Binding("Name"), Width = 230 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Situação", Binding = new Binding("Status"), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Candidatos", Binding = new Binding("CandidateCount"), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Mensagem", Binding = new Binding("Message"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        bodyPanel.Children.Add(grid);
        // Keep a bounded viewport for row virtualization inside the scrolling body.
        // Larger windows still devote most of that viewport to the item list.
        body.SizeChanged += (_, args) => grid.Height = Math.Max(180, args.NewSize.Height * 0.6);
        var detail = new TextBox
        {
            Name = "GuideDetails",
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65, MaxHeight = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0)
        };
        detail.SetBinding(TextBox.TextProperty, new Binding("SelectedItem.Message") { Source = grid, Mode = BindingMode.OneWay });
        bodyPanel.Children.Add(detail);
        var buttons = new WrapPanel
        {
            Name = "GuideFooter",
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(new Button { Content = allowApply ? "Cancelar" : "Fechar", IsCancel = true });
        if (allowApply)
        {
            var applyLabel = preview.ReadyCount == 0 && preview.ImportableCount > 0
                ? "Importar somente candidatos"
                : "Importar preços e aplicar cestas válidas";
            var applyText = new TextBlock { Text = applyLabel, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            var apply = new Button
            {
                Content = applyText, IsDefault = true,
                IsEnabled = preview.ImportableCount > 0 || preview.ReadyCount > 0
            };
            var range = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = "Permitir ampliar a faixa para os preços escolhidos no guia. O desvio de 25% continua obrigatório.",
                    TextWrapping = TextWrapping.Wrap
                },
                IsChecked = allowPriceRangeExpansion, Margin = new Thickness(0, 8, 0, 0)
            };
            var technical = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = "Aceitar revisão técnica documentada no guia quando a pontuação textual for baixa. " +
                           "A justificativa e o trecho de origem permanecem para conferência; as outras validações continuam.",
                    TextWrapping = TextWrapping.Wrap
                },
                IsChecked = allowTechnicalReview, Margin = new Thickness(0, 8, 0, 0)
            };
            void UpdateRangeOption(object sender, RoutedEventArgs args)
            {
                AllowPriceRangeExpansion = range.IsChecked == true;
                AllowTechnicalReview = technical.IsChecked == true;
                var changed = AllowPriceRangeExpansion != allowPriceRangeExpansion || AllowTechnicalReview != allowTechnicalReview;
                apply.IsEnabled = changed || preview.ImportableCount > 0 || preview.ReadyCount > 0;
                applyText.Text = changed
                    ? "Revalidar e mostrar alterações"
                    : applyLabel;
            }
            range.Checked += UpdateRangeOption;
            range.Unchecked += UpdateRangeOption;
            technical.Checked += UpdateRangeOption;
            technical.Unchecked += UpdateRangeOption;
            var options = new StackPanel { Name = "GuideOptions" };
            options.Children.Add(range);
            options.Children.Add(technical);
            bodyPanel.Children.Add(options);
            apply.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(apply);
        }
        Grid.SetRow(buttons, 1);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
