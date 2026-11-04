using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public sealed class QuotationAiGuidePreviewWindow : Window
{
    public QuotationAiGuidePreviewWindow(QuotationAiGuidePreview preview, bool allowApply = true)
    {
        SetResourceReference(StyleProperty, "AppWindow");
        Title = allowApply ? "Conferir guia da IA" : "Resultado da aplicação do guia";
        Width = 1000;
        Height = 620;
        MinWidth = 500;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MonitorAwareWindowBehavior.Attach(this);
        var panel = new Grid { Margin = new Thickness(16) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock
        {
            Text = allowApply
                ? $"{preview.ReadyCount:N0} item(ns) pronto(s) para aplicar; {preview.PendingCount:N0} com pendências. " +
                  "Confira as mensagens. As pendências permanecem para revisão."
                : "Confira abaixo o resultado de cada item. As cestas existentes que não pertencem a este guia foram preservadas.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        var grid = new DataGrid
        {
            ItemsSource = preview.Items.Select(item => new
            {
                item.Name,
                Status = item.Status switch
                {
                    "ready" => "Pronto",
                    "review" => "Revisar",
                    "insufficient" => "Insuficiente",
                    "applied" => "Aplicado",
                    "unchanged" => "Já aplicado",
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
        grid.Columns.Add(new DataGridTextColumn { Header = "Mensagem", Binding = new Binding("Message"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(grid, 1);
        panel.Children.Add(grid);
        var detail = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65, MaxHeight = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0)
        };
        detail.SetBinding(TextBox.TextProperty, new Binding("SelectedItem.Message") { Source = grid });
        Grid.SetRow(detail, 2);
        panel.Children.Add(detail);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(new Button { Content = allowApply ? "Cancelar" : "Fechar", IsCancel = true });
        if (allowApply)
        {
            var apply = new Button
            {
                Content = "Aplicar itens válidos", IsDefault = true, IsEnabled = preview.ReadyCount > 0
            };
            apply.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(apply);
        }
        Grid.SetRow(buttons, 3);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
