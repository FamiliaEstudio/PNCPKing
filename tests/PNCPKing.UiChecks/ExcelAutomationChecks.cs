extern alias AppUnderTest;

using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using ExcelMainVm = AppUnderTest::PNCPKing.App.ViewModels.MainViewModel;
using ExcelItemVm = AppUnderTest::PNCPKing.App.ViewModels.QuotationItemViewModel;
using ExcelItemWindow = AppUnderTest::PNCPKing.App.Views.QuotationItemWindow;
using ExcelMainWindow = AppUnderTest::PNCPKing.App.Views.MainWindow;
using ExcelServices = AppUnderTest::PNCPKing.App.Services;

internal static partial class Program
{
    private static async Task CheckExcelAutomationUiAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.UiChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new SqliteContractRepository(Path.Combine(directory, "test.db"));
            await database.InitializeAsync();
            var repository = new SqliteQuotationRepository(database.DatabasePath);
            var quotations = new QuotationService(repository, new QuotationAnalyzer());
            var project = await quotations.CreateProjectAsync("Itens do Excel");
            var line = await repository.CreateLineAsync(project.Id, new("Café", 100, "pacote", null, null));
            var references = Enumerable.Range(0, 120).Select(index => new QuotationReference
            {
                Id = $"candidate-{index}", LineId = line.Id, ContractId = $"contract-{index}",
                ItemNumber = 1, ResultSequence = 1, UnitPrice = 100m,
                ItemDescription = index < 60 ? "Café torrado" : "Açúcar cristal", ItemUnit = "pacote",
                SupplierTaxId = "11222333000181", ResultDate = DateOnly.FromDateTime(DateTime.Today)
            }).ToArray();
            await repository.SaveSampleAsync(project.Id, line.Id, new("Café", 100, "pacote", null, null), references);
            var main = (ExcelMainVm)RuntimeHelpers.GetUninitializedObject(typeof(ExcelMainVm));
            foreach (var field in typeof(ExcelMainVm).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.FieldType.Name.StartsWith("RangeObservableCollection`", StringComparison.Ordinal) ||
                    field.FieldType.Name.StartsWith("ObservableCollection`", StringComparison.Ordinal))
                    field.SetValue(main, Activator.CreateInstance(field.FieldType));
            var settings = new ExcelServices.AppSettingsService();
            typeof(ExcelServices.AppSettingsService).GetField("_settingsPath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(settings, Path.Combine(directory, "settings.json"));
            var layouts = new ExcelServices.DataGridColumnLayoutService(settings, new(directory, true));
            var internet = DispatchProxy.Create<IInternetPriceService, EmptyInternetDrafts>();
            var vm = new ExcelItemVm(main, quotations, null!, null!, internet, null!, null!, null!, directory, project.Id, line.Id);
            var itemWindow = new ExcelItemWindow(vm, null!, null!, layouts);
            try
            {
                itemWindow.Show();
                var initialLoad = (Task)typeof(ExcelItemWindow).GetField("_initialLoadTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(itemWindow)!;
                await initialLoad.WaitAsync(TimeSpan.FromSeconds(45));
                Require(vm.Line?.Analysis.References.Count == 120, "A janela não abriu todos os preços coletados.");
                await CheckCompiledItemPriceHighlightsAsync(itemWindow, vm);
                var basketKey = vm.SelectedBasket?.Key;
                vm.CandidateSearchText = "açúcar";
                await vm.SearchCandidatesAsync();
                Require(vm.ReferenceScope == ReferenceViewScope.All && vm.VisibleReferences.Count == 60,
                    "A pesquisa ficou limitada à cesta em vez dos preços coletados.");
                Require(vm.SelectedBasket?.Key == basketKey, "Filtrar alterou a cesta selecionada.");
                vm.CandidateSearchText = "café + açúcar";
                await vm.SearchCandidatesAsync();
                Require(vm.VisibleReferences.Count == 120, "O + não reuniu os candidatos.");
                vm.CandidateSearchText = "café +";
                await vm.SearchCandidatesAsync();
                Require(vm.VisibleReferences.Count == 120 && vm.CandidateSearchSummary.Contains("não aplicada"),
                    "Uma pesquisa inválida apagou o resultado anterior.");
                vm.ClearCandidateSearch();
                Require(vm.VisibleReferences.Count == 120 && !vm.IsCandidateSearchBusy, "Limpar não restaurou os candidatos.");
                var menu = (ContextMenu)itemWindow.FindName("PriceContextMenu");
                var copy = menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString()?.StartsWith("Copiar preço(s)") == true);
                typeof(ExcelItemWindow).GetMethod("CopyPriceToBasket_Opened", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(itemWindow, [copy, new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, copy)]);
                Require(copy.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "Nova cesta manual"),
                    "A ação da linha não permite copiar para uma nova cesta.");

                var recalculate = (Button)itemWindow.FindName("RecalculateBasketsButton");
                Require(recalculate.IsEnabled, "O recálculo não ficou disponível ao carregar o item.");
                var snapshot = vm.Line!.Line;
                CheckBasketRecalculationDialog(vm.Line.Analysis, itemWindow);
                await vm.RecalculateBasketsAsync(new(0, 0, 0, 0, 100), 200m, 300m);
                Require(vm.Baskets.Count == 0 && vm.References.Count == 120 &&
                    vm.ProgressText.Contains("Nenhuma cesta automática"),
                    "Uma faixa vazia descartou candidatos ou deixou de informar a ausência de cestas.");
                Require(vm.ItemSummary.Contains("200,00") && vm.ItemSummary.Contains("300,00"),
                    "O resumo do item não exibe sua faixa atual.");
                await vm.RecalculateBasketsAsync(AdequacyWeights.Default, null, null);
                Require(vm.Baskets.Count > 0 && vm.References.Count == 120 &&
                    vm.Line!.Line.SampleVersion == snapshot.SampleVersion && !vm.Line.Line.SelectionConfirmed,
                    "Limpar a faixa não restaurou as cestas locais ou modificou a coleta.");
                typeof(ExcelMainVm).GetField("_quotationAutomationCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(main, new CancellationTokenSource());
                Require(!vm.CanEditRequestedDetails, "O recálculo ficou disponível durante a automação.");
                var rejected = false;
                try { await vm.RecalculateBasketsAsync(AdequacyWeights.Default, null, null); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "O recálculo não recusou uma coleta em andamento.");
                ((CancellationTokenSource)typeof(ExcelMainVm).GetField("_quotationAutomationCancellation",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).Dispose();
                typeof(ExcelMainVm).GetField("_quotationAutomationCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(main, null);
            }
            finally { itemWindow.Close(); await vm.DisposeAsync(); }

            foreach (var count in new[] { 1, 3 })
            {
                var description = count == 1 ? "Café" : "Tubo PVC rígido DN 100 milímetros";
                var input = new QuotationLineInput(description, 100, "pacote", null, null);
                var withoutBasket = await repository.CreateLineAsync(project.Id, input);
                await repository.SaveSampleAsync(project.Id, withoutBasket.Id, input,
                    references.Take(count).Select(reference => reference with { LineId = withoutBasket.Id }).ToArray());
                var withoutBasketVm = new ExcelItemVm(main, quotations, null!, null!, internet,
                    null!, null!, null!, directory, project.Id, withoutBasket.Id);
                try
                {
                    await withoutBasketVm.LoadAsync();
                    Require(withoutBasketVm.Baskets.Count == 0, "O cenário deveria estar sem cesta automática.");
                    Require(withoutBasketVm.ReferenceScope == ReferenceViewScope.All &&
                        withoutBasketVm.VisibleReferences.Count == count,
                        "Um item sem cesta ocultou os candidatos salvos ao abrir a janela.");
                    withoutBasketVm.ReferenceScope = ReferenceViewScope.InBasket;
                    Require(withoutBasketVm.ReferenceScope == ReferenceViewScope.All &&
                        withoutBasketVm.VisibleReferences.Count == count,
                        "O filtro de uma cesta inexistente ocultou os candidatos.");

                    var mainType = typeof(ExcelMainVm);
                    mainType.GetField("_selectedQuotationLine", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(main, new AppUnderTest::PNCPKing.App.ViewModels.QuotationLineDisplay(withoutBasketVm.Line!.Analysis));
                    mainType.GetField("_selectedQuotationBasket", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(main, null);
                    mainType.GetField("_quotationReferenceScope", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(main, ReferenceViewScope.InBasket);
                    mainType.GetMethod("RebuildVisibleQuotationReferences", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(main, null);
                    Require(main.QuotationReferenceScope == ReferenceViewScope.All &&
                        main.VisibleQuotationReferences.Count == count,
                        "A tela principal ocultou os candidatos de um item sem cesta.");
                }
                finally { await withoutBasketVm.DisposeAsync(); }
            }

            var window = new ExcelMainWindow(main, layouts, null!);
            typeof(ExcelMainWindow).GetField("_shutdownComplete", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            try
            {
                window.Show();
                var panel = (StackPanel)window.FindName("ExcelAutomationPanel");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(panel.Visibility == Visibility.Collapsed, "O progresso apareceu sem automação.");
                typeof(ExcelMainVm).GetProperty(nameof(ExcelMainVm.IsExcelAutomationRunning))!.SetValue(main, true);
                typeof(ExcelMainVm).GetProperty(nameof(ExcelMainVm.ExcelAutomationProgress))!.SetValue(main, 50d);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(panel.Visibility == Visibility.Visible, "O progresso não apareceu durante a automação.");
                var progress = panel.Children.OfType<ProgressBar>().Single();
                Require(progress.Value == 50, "O progresso não acompanhou o valor informado.");
                typeof(ExcelMainVm).GetProperty(nameof(ExcelMainVm.IsExcelAutomationRunning))!.SetValue(main, false);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(panel.Visibility == Visibility.Collapsed, "O progresso permaneceu depois do término.");
            }
            finally { window.Close(); }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static void CheckBasketRecalculationDialog(QuotationLineAnalysis analysis, Window owner)
    {
        foreach (var decimals in new[] { 2, 4 })
        {
            var dialog = new AppUnderTest::PNCPKing.App.Views.QuotationWeightsWindow(analysis with
            {
                PriceDecimalPlaces = decimals,
                Line = analysis.Line with { MinimumUnitPrice = 1234.56m, MaximumUnitPrice = null }
            }) { Owner = owner };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                dialog.Width = 540;
                dialog.Height = 430;
                dialog.UpdateLayout();
                var minimum = (TextBox)dialog.FindName("MinimumPriceTextBox");
                var maximum = (TextBox)dialog.FindName("MaximumPriceTextBox");
                var accept = (Button)dialog.FindName("AcceptButton");
                Require(minimum.Text.StartsWith("1.234,56", StringComparison.Ordinal) && maximum.Text == "",
                    "O diálogo não carregou a faixa salva em formato brasileiro.");
                var point = accept.TranslatePoint(new Point(), dialog);
                Require(accept.IsVisible && accept.ActualHeight > 0 && point.Y >= 0 &&
                    point.Y + accept.ActualHeight <= dialog.ActualHeight,
                    "O botão de recalcular ficou inacessível na janela reduzida.");
                foreach (var invalid in new[] { "abc", "-1", "99999999999999999999999999", "5" })
                {
                    minimum.Text = invalid;
                    maximum.Text = "2";
                    accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(dialog.IsVisible && ((TextBlock)dialog.FindName("ValidationText")).Visibility == Visibility.Visible,
                        "O diálogo aceitou uma faixa inválida.");
                }
                minimum.Text = "1,23999";
                maximum.Text = "";
                ((Slider)dialog.FindName("RecencyWeightSlider")).Value = 100;
                accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Require(dialog.ShowDialog() == true && dialog.MinimumUnitPrice == (decimals == 2 ? 1.23m : 1.2399m) &&
                dialog.MaximumUnitPrice is null && dialog.Weights == new AdequacyWeights(0, 0, 0, 0, 100),
                "O diálogo não aplicou os pesos e limites com truncamento.");
        }

        var cancelled = new AppUnderTest::PNCPKing.App.Views.QuotationWeightsWindow(analysis) { Owner = owner };
        cancelled.Loaded += (_, _) => cancelled.Dispatcher.BeginInvoke(new Action(() =>
        {
            ((TextBox)cancelled.FindName("MinimumPriceTextBox")).Text = "500";
            cancelled.Close();
        }));
        Require(cancelled.ShowDialog() != true, "Fechar o diálogo aplicou os critérios.");
    }

    public class EmptyInternetDrafts : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Require(method?.Name == nameof(IInternetPriceService.GetDraftsAsync), "O filtro tentou acessar outra fonte.");
            return Task.FromResult<IReadOnlyList<InternetPriceDraft>>([]);
        }
    }
}
