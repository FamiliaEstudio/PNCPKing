extern alias AppUnderTest;

using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using PNCPKing.Core.Models;
using ItemVm = AppUnderTest::PNCPKing.App.ViewModels.QuotationItemViewModel;
using MainVm = AppUnderTest::PNCPKing.App.ViewModels.MainViewModel;
using ItemWindow = AppUnderTest::PNCPKing.App.Views.QuotationItemWindow;
using AppSettings = AppUnderTest::PNCPKing.App.Services.AppSettings;
using AppSettingsService = AppUnderTest::PNCPKing.App.Services.AppSettingsService;
using AppColumnLayouts = AppUnderTest::PNCPKing.App.Services.DataGridColumnLayoutService;

internal static partial class Program
{
    private static async Task CheckQuotationItemProgressBindingsAsync()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, $"item-binding-ui-{Guid.NewGuid():N}.json");
        var settings = new AppSettingsService();
        typeof(AppSettingsService).GetField("_settingsPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, settingsPath);
        var layouts = new AppColumnLayouts(settings, new AppSettings("synthetic", true));
        var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
        var line = new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Description = "Item com candidatos importados",
            RequestedQuantity = 10, RequestedUnit = "unidade"
        };
        var references = new[]
        {
            new QuotationReference { Id = "adaptador", LineId = line.Id, ContractId = "a", ItemNumber = 1,
                ResultSequence = 1, ItemDescription = "Adaptador PVC soldável 25 mm", ItemUnit = "un", UnitPrice = 100,
                State = QuotationReferenceState.Rejected },
            new QuotationReference { Id = "conexao", LineId = line.Id, ContractId = "b", ItemNumber = 1,
                ResultSequence = 1, ItemDescription = "Conexão metálica galvanizada", ItemUnit = "un", UnitPrice = 100 },
            new QuotationReference { Id = "tubo", LineId = line.Id, ContractId = "c", ItemNumber = 1,
                ResultSequence = 1, ItemDescription = "Tubo PVC soldável 25 mm", ItemUnit = "m", UnitPrice = 300 },
            new QuotationReference { Id = "joelho", LineId = line.Id, ContractId = "d", ItemNumber = 1,
                ResultSequence = 1, ItemDescription = "Joelho PVC roscável 25 mm", ItemUnit = "un", UnitPrice = 110,
                State = QuotationReferenceState.Rejected }
        };
        var basket = new QuotationBasket
        {
            Key = "candidate-search-basket", References = references.Take(3).ToArray(), AveragePrice = 100,
            MinimumPrice = 100, MaximumPrice = 100, MaximumDeviationPercent = 0, Score = 0,
            PriceEntries = references.Take(3).Select(reference => new QuotationBasketPrice
            {
                Reference = reference, EffectiveUnitPrice = 100, PriceDivisor = reference.Id == "tubo" ? 3m : null
            }).ToArray()
        };
        var analysis = new QuotationLineAnalysis(line, references, [basket], 4, 2, 0, 2, 2);
        var vm = new ItemVm(main, null!, null!, null!, null!, null!, null!, null!, "", line.ProjectId, line.Id);
        SetItemBindingProperty(vm, nameof(ItemVm.Line),
            new AppUnderTest::PNCPKing.App.ViewModels.QuotationLineDisplay(analysis));
        vm.SelectedBasket = new(basket);
        // Exercise the production Window and real VM properties, while holding the
        // initial LoadAsync guard so this binding check never calls null services.
        SetItemBindingProperty(vm, nameof(ItemVm.IsBusy), true);
        SetItemBindingProperty(vm, nameof(ItemVm.IsAnalysisBusy), true);
        SetItemBindingProperty(vm, nameof(ItemVm.AnalysisStatus), "Preparando o cálculo de cestas…");
        ItemWindow? window = null;
        try
        {
            window = new ItemWindow(vm, null!, null!, layouts);
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(window.IsVisible && window.IsLoaded && window.DataContext == vm,
                "A janela real do item não abriu com seu ViewModel.");
            var progress = VisualChildren(window).OfType<ProgressBar>().Single(control =>
                BindingOperations.GetBinding(control, ProgressBar.ValueProperty)?.Path?.Path == nameof(ItemVm.AnalysisProgress));
            var status = VisualChildren(window).OfType<TextBlock>().Single(control =>
                BindingOperations.GetBinding(control, TextBlock.TextProperty)?.Path?.Path == nameof(ItemVm.AnalysisStatus));
            var stop = VisualChildren(window).OfType<Button>().Single(button => button.Content?.ToString() == "Parar cálculo");
            Require(progress.IsVisible && progress.ActualWidth > 0 && progress.ActualHeight > 0,
                "O progresso de cálculo não está visível na janela do item.");
            Require(stop.IsEnabled, "O cálculo ativo não habilitou o botão de interromper.");
            foreach (var value in new[] { 0d, 37.5d, 100d })
            {
                var message = $"Calculando cestas: {value:0.0}%";
                SetItemBindingProperty(vm, nameof(ItemVm.AnalysisProgress), value);
                SetItemBindingProperty(vm, nameof(ItemVm.AnalysisStatus), message);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(Math.Abs(progress.Value - value) < 0.001 && status.Text == message,
                    "As propriedades de leitura do cálculo não atualizaram a barra e a mensagem da janela.");
            }
            SetItemBindingProperty(vm, nameof(ItemVm.IsAnalysisBusy), false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!stop.IsEnabled, "O término do cálculo não desabilitou a interrupção.");
            SetItemBindingProperty(vm, nameof(ItemVm.IsBusy), false);
            await CheckQuotationCandidateFilteringAsync(window, vm);
        }
        finally
        {
            if (window is not null)
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await vm.DisposeAsync();
            await layouts.FlushAsync();
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    private static void SetItemBindingProperty(ItemVm vm, string name, object value) =>
        typeof(ItemVm).GetProperty(name)!.SetValue(vm, value);

    private static async Task CheckQuotationCandidateFilteringAsync(ItemWindow window, ItemVm vm)
    {
        var input = (TextBox)window.FindName("CandidateSearchBox");
        var search = (Button)window.FindName("CandidateSearchButton");
        var clear = (Button)window.FindName("ClearCandidateSearchButton");
        var grid = (DataGrid)window.FindName("PriceGrid");
        Require(((TabItem)window.FindName("LegacyDatabaseSearchTab")).Visibility == Visibility.Collapsed,
            "A janela voltou a oferecer uma busca concorrente em todo o banco.");
        Require(vm.ReferenceScope == ReferenceViewScope.All && vm.VisibleReferences.Count == 4,
            "O item não apresentou todos os candidatos coletados por padrão.");
        window.Height = 720;
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(grid.ActualHeight > 100 && input.IsVisible,
            "O painel de pesquisa deixou a tabela de preços sem altura utilizável.");

        async Task Filter(string text, bool enter = false)
        {
            input.Text = text;
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            if (enter)
                input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), 0, System.Windows.Input.Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            else search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !vm.IsCandidateSearchBusy && !vm.IsInteracting);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        var originalBasket = vm.SelectedBasket!.Source;
        vm.ReferenceScope = ReferenceViewScope.InBasket;
        await Filter("adaptador + joelho", enter: true);
        Require(vm.ReferenceScope == ReferenceViewScope.All &&
                vm.VisibleReferences.Select(reference => reference.Id).SequenceEqual(["adaptador", "joelho"]),
            "Enter/+ não encontraram candidatos rejeitados e fora da cesta no próprio item.");
        Require(grid.Items.Count == 2 && !vm.VisibleReferences.Single(reference => reference.Id == "joelho").IsInSelectedBasket,
            "Filtrar preços alterou a composição da cesta.");
        Require(vm.VisibleReferences.Single(reference => reference.Id == "adaptador").IsValidInMarkedGroup,
            "Ocultar preços pelo filtro modificou a validação da cesta completa.");
        vm.ReferenceScope = ReferenceViewScope.InBasket;
        Require(vm.VisibleReferences.Count == 1 && vm.VisibleReferences[0].Id == "adaptador",
            "O filtro de cesta não foi combinado com a pesquisa aplicada.");
        await Filter("tubo");
        Require(vm.VisibleReferences.Count == 1 && vm.VisibleReferences[0].PriceDivisor == 3m &&
                vm.VisibleReferences[0].EffectiveUnitPrice == 100m && vm.VisibleReferences[0].IsValidInMarkedGroup,
            "Pesquisar um preço perdeu a divisão ou a validação dos três membros da cesta.");
        await Filter("C:(tubo)");
        Require(vm.VisibleReferences.Count == 1 && vm.VisibleReferences[0].Id == "tubo" &&
                vm.CandidateSearchSummary.Contains("Pesquisa não aplicada", StringComparison.Ordinal),
            "Consulta inválida apagou o último resultado válido ou não mostrou o motivo.");
        var originalAnalysis = vm.Line!.Analysis;
        using (var gate = new ManualResetEventSlim())
        {
            SetItemBindingProperty(vm, nameof(ItemVm.Line),
                new AppUnderTest::PNCPKing.App.ViewModels.QuotationLineDisplay(originalAnalysis with
                { References = new GatedCandidateList(originalAnalysis.References, gate) }));
            vm.CandidateSearchText = "joelho";
            var pending = vm.SearchCandidatesAsync();
            vm.StopCandidateSearch();
            gate.Set();
            await pending;
        }
        SetItemBindingProperty(vm, nameof(ItemVm.Line),
            new AppUnderTest::PNCPKing.App.ViewModels.QuotationLineDisplay(originalAnalysis));
        Require(vm.VisibleReferences.Count == 1 && vm.VisibleReferences[0].Id == "tubo",
            "Cancelar uma pesquisa substituiu os resultados anteriores.");
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(vm.ReferenceScope == ReferenceViewScope.All && vm.VisibleReferences.Count == 4 &&
                vm.CandidateSearchText.Length == 0 && grid.Items.Count == 4,
            "Limpar não restaurou todos os candidatos do item.");
        Require(ReferenceEquals(originalBasket, vm.SelectedBasket.Source) &&
                vm.SelectedBasket.Source.References.Select(reference => reference.Id).SequenceEqual(["adaptador", "conexao", "tubo"]),
            "Pesquisar ou limpar modificou os membros da cesta selecionada.");
    }

    private sealed class GatedCandidateList(IReadOnlyList<QuotationReference> references, ManualResetEventSlim gate)
        : IReadOnlyList<QuotationReference>
    {
        public int Count => references.Count;
        public QuotationReference this[int index] => references[index];
        public IEnumerator<QuotationReference> GetEnumerator()
        {
            if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("O teste não liberou a pesquisa cancelada.");
            foreach (var reference in references) yield return reference;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
