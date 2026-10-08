extern alias AppUnderTest;

using System.IO;
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
        await CheckSearchPriceHighlightsFromXamlAsync();
        await CheckReferencePriceHighlightsAsync();
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
            foreach (var row in rows.Take(2)) grid.SelectedItems.Add(row);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(rows.Take(2).All(row => !row.IsValidInSelection), "Dois preços receberam realce de grupo válido.");
            grid.SelectedItems.Add(rows[2]);
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
            Require(new[] { tiny, one, another }.All(row => !row.IsValidInSelection),
                "Dois preços válidos de medicamentos receberam realce.");
            var cancelled = Price(1005, 1m);
            cancelled = new(cancelled.Source with { PriceState = ItemSearchPriceState.Cancelled });
            rows.Add(cancelled);
            grid.SelectedItems.Add(cancelled);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!tiny.IsValidInSelection && new[] { one, another, cancelled }.All(row => row.IsValidInSelection),
                "O realce dependeu da elegibilidade ou não reconheceu três preços válidos entre quatro.");
        }
        finally { window.Close(); }
        var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
        var markedRows = new AppUnderTest::PNCPKing.App.ViewModels.RangeObservableCollection<AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow>();
        markedRows.AddRange(rows.Take(3).Select(row => new AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow(row.Source)));
        typeof(MainVm).GetField("<ItemSearchRows>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, markedRows);
        typeof(MainVm).GetField("_retainedItemRows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(main, new Dictionary<string, AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow>());
        foreach (var name in new[] { "_currentItemResultKeys", "_visibleItemKeys" })
            typeof(MainVm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main,
                markedRows.Select(row => Key(row.Source)).ToHashSet());
        main.ApplyItemPriceAction(markedRows.Take(2).ToArray(), AppUnderTest::PNCPKing.App.ViewModels.ItemPriceAction.MarkForBasket);
        Require(markedRows.All(row => !row.IsValidInMarkedGroup), "Dois preços marcados receberam realce verde.");
        main.ApplyItemPriceAction([markedRows[2]], AppUnderTest::PNCPKing.App.ViewModels.ItemPriceAction.MarkForBasket);
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

    private static async Task CheckSearchPriceHighlightsFromXamlAsync()
    {
        var rows = new AppUnderTest::PNCPKing.App.ViewModels.RangeObservableCollection<AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow>();
        rows.AddRange(Enumerable.Range(0, 3).Select(index =>
            new AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow(DisplayRow(index).Source)));
        var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
        SetSearchField(main, "<ItemSearchRows>k__BackingField", rows);
        SetSearchField(main, "_retainedItemRows", new Dictionary<string, AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow>());
        foreach (var name in new[] { "_currentItemResultKeys", "_visibleItemKeys" })
            SetSearchField(main, name, new HashSet<string>());
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/PNCPKing.App/Views/MainWindow.xaml"));
        var window = LoadAppearanceWindow(path);
        dynamic data = new System.Dynamic.ExpandoObject();
        data.ItemSearchRows = rows;
        data.SelectedResultsWorkspace = AppUnderTest::PNCPKing.App.ViewModels.ResultsWorkspace.Search;
        data.IsContractsPanelOpen = false;
        window.DataContext = data;
        try
        {
            window.Show();
            var grid = window.FindName("ItemResultsGrid") as DataGrid ??
                throw new InvalidOperationException("A grade de pesquisa não foi encontrada no XAML.");
            foreach (var row in rows) grid.SelectedItems.Add(row);
            grid.ScrollIntoView(rows[0]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(rows.All(row => row.IsValidInSelection), "A pesquisa carregada pelo XAML não calculou os preços selecionados.");
            var container = grid.ItemContainerGenerator.ContainerFromItem(rows[0]) as DataGridRow ??
                throw new InvalidOperationException($"A pesquisa não materializou a linha: visible={grid.IsVisible}, height={grid.ActualHeight}.");
            Require(((SolidColorBrush)container.Background).Color ==
                    ((SolidColorBrush)Application.Current.FindResource("ValidSelectedPriceBrush")).Color,
                "A pesquisa carregada pelo XAML calculou a validade, mas não exibiu o realce azul.");
            main.ApplyItemPriceAction(rows.ToArray(), AppUnderTest::PNCPKing.App.ViewModels.ItemPriceAction.MarkForBasket);
            Require(rows.All(row => row.IsValidInMarkedGroup), "A pesquisa não calculou os três preços marcados.");
            grid.UnselectAll();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(((SolidColorBrush)container.Background).Color ==
                    ((SolidColorBrush)Application.Current.FindResource("ValidMarkedPriceBrush")).Color,
                "A pesquisa carregada pelo XAML não exibiu o realce verde após retirar a seleção.");
            main.ClearItemPriceRetention(rows[2]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(rows.All(row => !row.IsValidInMarkedGroup), "A pesquisa manteve o brilho com apenas dois marcados.");
        }
        finally { window.Close(); }
    }

    private static async Task CheckReferencePriceHighlightsAsync()
    {
        var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
        var line = new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Description = "Realce de preços",
            RequestedQuantity = 1m, RequestedUnit = "unidade"
        };
        var references = new[] { QuotationReferenceState.Eligible, QuotationReferenceState.Rejected, QuotationReferenceState.Duplicate }
            .Select((state, index) => new QuotationReference
            {
                Id = $"highlight-{index}", LineId = line.Id, ContractId = $"contract-{index}",
                ItemNumber = 1, ResultSequence = 1, UnitPrice = index == 0 ? 10m : 100m, State = state
            }).ToArray();
        var basket = new QuotationBasket
        {
            Key = "highlight-basket", References = references, AveragePrice = 100m,
            MinimumPrice = 100m, MaximumPrice = 100m, MaximumDeviationPercent = 0m, Score = 0m,
            PriceEntries = references.Select((reference, index) => new QuotationBasketPrice
            {
                Reference = reference, ConversionFactor = index == 0 ? 10m : 1m, EffectiveUnitPrice = 100m
            }).ToArray()
        };
        var display = new AppUnderTest::PNCPKing.App.ViewModels.QuotationLineDisplay(
            new QuotationLineAnalysis(line, references, [basket], 3, 1, 1, 1, 1));
        var basketDisplay = new AppUnderTest::PNCPKing.App.ViewModels.QuotationBasketDisplay(basket);
        var visible = new AppUnderTest::PNCPKing.App.ViewModels.RangeObservableCollection<AppUnderTest::PNCPKing.App.ViewModels.QuotationPriceDisplayRow>();
        SetSearchField(main, "<VisibleQuotationReferences>k__BackingField", visible);
        SetSearchField(main, "_selectedQuotationLine", display);
        SetSearchField(main, "_selectedQuotationBasket", basketDisplay);
        SetSearchField(main, "_quotationReferenceScope", ReferenceViewScope.All);
        CallSearch(main, "RebuildVisibleQuotationReferences");
        Require(visible.Count == 3 && visible.All(row => row.IsValidInMarkedGroup),
            "Referências da tela principal dependeram da elegibilidade ou ignoraram a conversão.");
        var vm = new ItemVm(main, null!, null!, null!, null!, null!, null!, null!, "", line.ProjectId, line.Id);
        typeof(ItemVm).GetProperty(nameof(ItemVm.Line))!.SetValue(vm, display);
        vm.SelectedBasket = basketDisplay;
        Require(vm.VisibleReferences.Count == 3 && vm.VisibleReferences.All(row => row.IsValidInMarkedGroup),
            "Referências do item dependeram da elegibilidade ou ignoraram a conversão.");
        var grid = new DataGrid { ItemsSource = vm.VisibleReferences, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Extended };
        grid.Columns.Add(new DataGridTextColumn { Header = "Preço", Binding = new Binding("EffectiveUnitPrice") });
        var reader = new AppUnderTest::PNCPKing.App.Controls.GridReader { EnablePriceHighlights = true, Table = grid };
        var window = new Window { Width = 500, Height = 300, Content = reader };
        try
        {
            window.Show();
            foreach (var row in vm.VisibleReferences) grid.SelectedItems.Add(row);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(vm.VisibleReferences.All(row => row.IsValidInSelection),
                "Referências selecionadas dependeram da elegibilidade ou ignoraram a conversão.");
            vm.SelectedBasket = new(basket with { References = references.Take(2).ToArray(), PriceEntries = basket.PriceEntries.Take(2).ToArray() });
            Require(vm.VisibleReferences.Count == 2 && vm.VisibleReferences.All(row => !row.IsValidInMarkedGroup),
                "Duas referências continuaram brilhando ao trocar de cesta.");
        }
        finally { window.Close(); await vm.DisposeAsync(); }
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
