using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public sealed class QuotationDuplicatePricesWindow : Window
{
    private static readonly CultureInfo Brazilian = CultureInfo.GetCultureInfo("pt-BR");
    private readonly HashSet<(Guid LineId, string BasketKey)> _accepted = [];
    public IReadOnlySet<(Guid LineId, string BasketKey)> AcceptedWarnings => _accepted;

    public QuotationDuplicatePricesWindow(QuotationProjectReport report, IReadOnlyList<QuotationDuplicatePrice> preview)
    {
        SetResourceReference(StyleProperty, "AppWindow");
        Title = "Remover preços duplicados";
        Width = 760;
        MinWidth = 400;
        Height = preview.Count > 0 ? 520 : 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MonitorAwareWindowBehavior.Attach(this);

        var panel = new DockPanel { Margin = new Thickness(16) };
        Content = panel;
        var summary = new TextBlock
        {
            Text = preview.Count == 0 ? $"Nenhuma duplicata identificada nas cestas salvas ou confirmadas de “{report.Project.Name}”."
                : $"“{report.Project.Name}” · {preview.Count:N0} entrada(s) repetida(s). Confira a ordem e o efeito em cada cesta. " +
                  "As cestas com ressalva ficam intactas até você marcar o aceite correspondente.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = preview.Count > 0 ? "Cancelar" : "Fechar", IsCancel = true, IsDefault = true };
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        Loaded += (_, _) => cancel.Focus();
        if (preview.Count == 0) return;

        var remove = new Button { Content = "Remover", MinWidth = 90, IsEnabled = preview.Any(row => row.CanRemove(_accepted)) };
        remove.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(remove);
        var list = new StackPanel();
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var order = report.Lines.Select((line, index) => (line.Line.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        var baskets = preview.GroupBy(row => (row.LineId, row.BasketKey)).ToArray();
        var repeatedNames = baskets.Select(group => group.First()).GroupBy(row => (row.LineId, row.BasketName))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        foreach (var basket in baskets.OrderBy(group => order.GetValueOrDefault(group.Key.LineId, int.MaxValue)))
        {
            var row = basket.First();
            var impact = row.Impact;
            var number = order.TryGetValue(row.LineId, out var index) ? report.ItemNumbers(index) : row.LineId.ToString("N");
            var name = repeatedNames.Contains((row.LineId, row.BasketName)) ? $"{row.BasketName} ({row.BasketKey})" : row.BasketName;
            var details = new StringBuilder();
            details.AppendLine($"Item {number} · {row.LineName} — {name}");
            details.AppendLine($"{impact.Before} → {impact.After} preços · mínimo {impact.Minimum} · " +
                $"{(impact.Method == QuotationAggregationMethod.Median ? "mediana" : "média")} após a retirada {Money(impact.AdoptedPrice, impact.DecimalPlaces)}");
            details.AppendLine(impact.Validation);
            if (impact.BlockReason is { } reason) details.AppendLine(reason);
            else if (row.IsAutomatic)
                details.AppendLine("Ao aplicar, a composição será salva como manual e continuará selecionada e confirmada para exportação.");
            else if (impact.SelectionConfirmed && impact.SelectedKey == row.BasketKey)
                details.AppendLine("A mesma cesta manual continuará selecionada e confirmada para exportação.");
            if (impact.BlockReason is null && impact.SelectedKey == row.BasketKey && report.Project.Organization is not null)
                details.AppendLine("Os grupos e as cotas deste projeto dependem dos preços escolhidos. Após a retirada, use Organizar Itens antes de exportar.");
            details.AppendLine(row.IsAutomatic ? "Ordem da automática por preço efetivo e, no empate, identificador."
                : "Ordem salva da cesta manual. A primeira referência de cada grupo permanece.");
            foreach (var group in basket.GroupBy(entry => entry.Kept))
            {
                var pair = group.First();
                details.AppendLine($"CNPJ {pair.SupplierCnpj} · Compra PNCP {pair.PurchaseId}");
                details.AppendLine($"Manter  {Describe(pair.Kept, impact.DecimalPlaces)}");
                foreach (var duplicate in group)
                    details.AppendLine($"Remover se aplicada  {Describe(duplicate.Removed, impact.DecimalPlaces)}");
            }
            list.Children.Add(new TextBox { Text = details.ToString().TrimEnd(), IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
            if (!impact.NeedsAcceptance || impact.BlockReason is not null) continue;
            var consent = new CheckBox { Content = new TextBlock { Text = $"Aceito a ressalva do item {number}, cesta “{name}”, " +
                "e autorizo a retirada indicada.", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(2, 2, 0, 12) };
            consent.Checked += (_, _) => { _accepted.Add(basket.Key); remove.IsEnabled = true; };
            consent.Unchecked += (_, _) => { _accepted.Remove(basket.Key); remove.IsEnabled = preview.Any(entry => entry.CanRemove(_accepted)); };
            list.Children.Add(consent);
        }
    }

    private static string Money(decimal value, int places) => value.ToString($"C{places}", Brazilian);

    private static string Describe(QuotationPricePreview entry, int places)
    {
        var date = entry.ResultDate?.ToString("dd/MM/yyyy", Brazilian) ?? "sem data";
        var adjustment = entry.PriceDivisor is { } divisor ? $"÷ {divisor.ToString("G29", Brazilian)}" : $"× {entry.ConversionFactor.ToString("G29", Brazilian)}";
        var effective = QuotationMoney.EffectivePrice(entry.UnitPrice, entry.ConversionFactor, entry.PriceDivisor, places);
        return $"{entry.Position}ª · {entry.SupplierName} · {entry.ReferenceId} · {Money(entry.UnitPrice, 4)} na origem {adjustment} = {Money(effective, places)} · {date}";
    }
}
