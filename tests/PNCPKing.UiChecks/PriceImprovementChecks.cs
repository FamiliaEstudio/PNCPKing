extern alias AppUnderTest;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using PNCPKing.App.Controls;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;
using ItemVm = AppUnderTest::PNCPKing.App.ViewModels.QuotationItemViewModel;
using MainVm = AppUnderTest::PNCPKing.App.ViewModels.MainViewModel;

internal static partial class Program
{
    private static async Task CheckPriceHighlightsAsync()
    {
        ItemSearchDisplayRow Price(int index, decimal value)
        {
            var source = DisplayRow(index).Source;
            return new(source with { Result = source.Result! with { HomologatedUnitValueScaled = DecimalScale.ToScaled(value) } });
        }
        var rows = new RangeObservableCollection<ItemSearchDisplayRow>();
        rows.AddRange(Enumerable.Range(0, 1000).Select(index => Price(index, index == 0 ? 125m : 100m)));
        rows[0].IsSelectedForBasket = true;
        rows[0].IsValidInMarkedGroup = true;
        var grid = CreateGrid(rows);
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        var reader = new GridReader { EnablePriceHighlights = true, Table = grid };
        var window = new Window { Width = 1100, Height = 460, Content = reader };
        Color BrushColor(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;
        DataGridRow Container(ItemSearchDisplayRow row) => (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
        try
        {
            window.Show();
            grid.UnselectAll();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(((SolidColorBrush)Container(rows[0]).Background).Color == BrushColor("ValidMarkedPriceBrush"),
                "Preço marcado válido não recebeu o realce verde.");
            foreach (var row in rows.Take(3)) grid.SelectedItems.Add(row);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(rows.Take(3).All(row => row.IsValidInSelection), "O limite inclusivo de 25% não foi aplicado.");
            Require(((SolidColorBrush)Container(rows[0]).Background).Color == BrushColor("ValidSelectedPriceBrush"),
                "O realce azul não prevaleceu sobre o verde.");
            Require(VisualChildren(Container(rows[0])).OfType<DataGridCell>().All(cell =>
                    ((SolidColorBrush)cell.Background).Color == Colors.Transparent),
                "As células ocultaram a cor da linha.");
            grid.SelectedItems.Remove(rows[1]);
            grid.SelectedItems.Remove(rows[2]);
            var expensive = Price(1001, 250m);
            rows.Add(expensive);
            grid.SelectedItems.Add(expensive);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!rows[0].IsValidInSelection && !expensive.IsValidInSelection && rows[0].IsValidInMarkedGroup,
                "A seleção azul foi confundida com a cesta verde.");
            grid.UnselectAll();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(((SolidColorBrush)Container(rows[0]).Background).Color == BrushColor("ValidMarkedPriceBrush"),
                "Desselecionar não restaurou o realce verde.");
            grid.ScrollIntoView(rows[900]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(VisualChildren(grid).OfType<DataGridRow>().Count() < 100, "O realce desativou a virtualização.");
            Require(Container(rows[900]).FontWeight == FontWeights.Normal, "O realce vazou para uma linha reciclada.");
            var tiny = Price(1002, 1.2501m);
            var one = Price(1003, 1m);
            var another = Price(1004, 1m);
            rows.AddRange([tiny, one, another]);
            foreach (var row in new[] { tiny, one, another }) grid.SelectedItems.Add(row);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(tiny.IsValidInSelection, "Seleção normal deixou de truncar em duas casas.");
            reader.PriceDecimalPlaces = 4;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!tiny.IsValidInSelection, "Seleção de medicamentos não usou quatro casas.");
            var cancelled = Price(1005, 1m);
            cancelled = new(cancelled.Source with { PriceState = ItemSearchPriceState.Cancelled });
            rows.Add(cancelled);
            grid.SelectedItems.Add(cancelled);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!cancelled.IsValidInSelection, "Preço inelegível recebeu realce.");
        }
        finally { window.Close(); }
        var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
        var markedRows = new AppUnderTest::PNCPKing.App.ViewModels.RangeObservableCollection<AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow>();
        markedRows.AddRange(rows.Take(3).Select(row => new AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow(row.Source)));
        typeof(MainVm).GetField("<ItemSearchRows>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, markedRows);
        typeof(MainVm).GetField("_retainedItemRows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(main, new Dictionary<string, AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow>());
        foreach (var name in new[] { "_currentItemResultKeys", "_visibleItemKeys" })
            typeof(MainVm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, new HashSet<string>());
        main.ApplyItemPriceAction(markedRows.ToArray(), AppUnderTest::PNCPKing.App.ViewModels.ItemPriceAction.MarkForBasket);
        Require(markedRows.All(row => row.IsValidInMarkedGroup), "Marcar preços não recalculou os crivos do grupo verde.");
        var outlier = new AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow(Price(1006, 250m).Source);
        markedRows.Add(outlier);
        main.ApplyItemPriceAction([outlier], AppUnderTest::PNCPKing.App.ViewModels.ItemPriceAction.MarkForBasket);
        Require(!outlier.IsValidInMarkedGroup, "Preço excessivo marcado recebeu realce verde.");
        main.ClearItemPriceRetention(outlier);
        Require(!outlier.IsValidInMarkedGroup && markedRows.All(row => row.IsValidInMarkedGroup),
            "Desmarcar não recalculou o conjunto restante.");
        Console.WriteLine("Price highlights: inclusive checks, blue/green precedence, precision and virtualization passed.");
    }

    private static async Task CheckImportedItemEditorAsync()
    {
        var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
        var line = new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Description = "Item Excel",
            RequestedQuantity = 10, RequestedUnit = "pacote", AutomationRunId = Guid.NewGuid(),
            AutomationState = QuotationAutomationItemState.Completed
        };
        var vm = new ItemVm(main, null!, null!, null!, null!, null!, null!, null!, "", line.ProjectId, line.Id);
        var analysis = new QuotationLineAnalysis(line, [], [], 0, 0, 0, 0, 0);
        typeof(ItemVm).GetProperty(nameof(ItemVm.Line))!.SetValue(vm,
            new AppUnderTest::PNCPKing.App.ViewModels.QuotationLineDisplay(analysis));
        var button = new Button { DataContext = vm, Content = "Editar dados" };
        button.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(ItemVm.CanEditRequestedDetails)));
        var window = new Window { Width = 400, Height = 200, Content = button };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(button.IsEnabled, "Item importado concluído continua bloqueado para edição.");
            foreach (var property in new[] { nameof(ItemVm.IsBusy), nameof(ItemVm.IsSearchBusy) })
            {
                typeof(ItemVm).GetProperty(property)!.SetValue(vm, true);
                Require(!button.IsEnabled, "Edição liberada durante carregamento ou pesquisa.");
                typeof(ItemVm).GetProperty(property)!.SetValue(vm, false);
            }
            using var cancellation = new CancellationTokenSource();
            typeof(MainVm).GetField("_quotationAutomationCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(main, cancellation);
            void NotifyAutomation() => typeof(AppUnderTest::PNCPKing.App.ViewModels.ObservableObject)
                .GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, [nameof(MainVm.IsQuotationAutomationRunning)]);
            NotifyAutomation();
            Require(!button.IsEnabled, "Edição liberada durante automação.");
            typeof(MainVm).GetField("_quotationAutomationCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, null);
            NotifyAutomation();
            Require(button.IsEnabled, "Pausar a automação não liberou a edição.");
            var editor = new AppUnderTest::PNCPKing.App.Views.NewQuotationItemWindow(line) { Owner = window };
            editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(new Action(() =>
            {
                ((TextBox)editor.FindName("QuantityTextBox")).Text = "25";
                ((TextBox)editor.FindName("UnitTextBox")).Text = "caixa";
                ((Button)editor.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Require(editor.ShowDialog() == true && editor.Input is { RequestedQuantity: 25, RequestedUnit: "caixa" },
                "O formulário não salvou os dados do item importado.");
        }
        finally { window.Close(); await vm.DisposeAsync(); }
        Console.WriteLine("Imported item editor: availability, research/automation guards and quantity/unit form passed.");
    }

    private static async Task CheckItemOwnerFocusAsync()
    {
        var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
        var owner = new Window { Width = 500, Height = 300 };
        var other = new Window { Width = 400, Height = 240 };
        void Return() => typeof(MainVm).GetMethod("ReturnToMainWindowAfterItemClose", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, [owner]);
        try
        {
            owner.Show();
            owner.WindowState = WindowState.Maximized;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            owner.WindowState = WindowState.Minimized;
            other.Show();
            Return();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(owner.WindowState == WindowState.Maximized && owner.IsActive,
                "Retornar do item não restaurou a janela principal maximizada com foco.");
            other.Activate();
            owner.IsEnabled = false;
            Return();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!owner.IsActive, "O fechamento principal roubou o foco.");
            owner.IsEnabled = true;
            var replacement = RuntimeHelpers.GetUninitializedObject(typeof(AppUnderTest::PNCPKing.App.Views.QuotationItemWindow));
            typeof(MainVm).GetField("_quotationItemWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, replacement);
            Return();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!owner.IsActive, "Trocar de item reativou a janela principal.");
            typeof(MainVm).GetField("_quotationItemWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, null);
            typeof(MainVm).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
            Return();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!owner.IsActive, "Encerrar o aplicativo reativou a janela principal.");
        }
        finally { other.Close(); owner.Close(); }
        Console.WriteLine("Item owner focus: restore/maximization and shutdown guards passed.");
    }
}
