extern alias AppUnderTest;

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using AppModels = AppUnderTest::PNCPKing.App.ViewModels;
using AppServices = AppUnderTest::PNCPKing.App.Services;
using MainView = AppUnderTest::PNCPKing.App.Views.MainWindow;
using BasketWindow = AppUnderTest::PNCPKing.App.Views.ManualBasketWindow;
using DuplicateWindow = AppUnderTest::PNCPKing.App.Views.QuotationDuplicatePricesWindow;
using MainVm = AppUnderTest::PNCPKing.App.ViewModels.MainViewModel;

internal static partial class Program
{
    private static async Task CheckManualBasketLoadingAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var firstProject = new QuotationProject(Guid.NewGuid(), "Primeiro", now, now);
        var secondProject = new QuotationProject(Guid.NewGuid(), "Segundo", now, now);
        QuotationLine Line(QuotationProject project, string description) => new()
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Description = description,
            RequestedQuantity = 25m, RequestedUnit = "un"
        };
        var firstLine = Line(firstProject, "Item antigo");
        var secondLine = Line(secondProject, "Item escolhido");
        var thirdLine = Line(secondProject, "Outro item");
        var firstLoad = new TaskCompletionSource<IReadOnlyList<QuotationLine>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoad = new TaskCompletionSource<IReadOnlyList<QuotationLine>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var basketLoad = new TaskCompletionSource<IReadOnlyList<QuotationManualBasket>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedProjects = new List<Guid>();
        var requestedItems = new List<Guid>();
        var basket = new QuotationManualBasket
        {
            Id = Guid.NewGuid(), LineId = secondLine.Id, Name = "Minha cesta", ReferenceIds = ["p1", "p2", "p3"]
        };
        var window = new BasketWindow([firstProject, secondProject],
            (id, _) => { requestedProjects.Add(id); return id == firstProject.Id ? firstLoad.Task : secondLoad.Task; },
            (id, _) =>
            {
                requestedItems.Add(id);
                return id == secondLine.Id ? basketLoad.Task : Task.FromResult<IReadOnlyList<QuotationManualBasket>>([]);
            }, (_, _) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), firstProject.Id, "Café", "", "");
        var projects = (ComboBox)window.FindName("ProjectComboBox");
        var lines = (ComboBox)window.FindName("LineComboBox");
        var baskets = (ComboBox)window.FindName("BasketComboBox");
        var save = (Button)window.FindName("SaveButton");
        try
        {
            window.Show();
            await WaitForBasketUiAsync(() => requestedProjects.Count == 1);
            Require(requestedProjects.Single() == firstProject.Id && requestedItems.Count == 0 && !save.IsEnabled,
                "Abrir a janela carregou outros projetos/cestas ou liberou o salvamento prematuramente.");
            projects.SelectedItem = secondProject;
            secondLoad.SetResult([secondLine, thirdLine]);
            await WaitForBasketUiAsync(() => save.IsEnabled && lines.Items.Count == 3);
            firstLoad.SetResult([firstLine]);
            await Task.Delay(30);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(lines.Items.Count == 3 && requestedProjects.Count == 2 && requestedItems.Count == 0,
                "Uma resposta antiga substituiu os itens do projeto escolhido.");
            lines.SelectedIndex = 1;
            Require(!save.IsEnabled && requestedItems.SequenceEqual([secondLine.Id]),
                "A janela não aguardou as cestas do item escolhido.");
            lines.SelectedIndex = 2;
            await WaitForBasketUiAsync(() => save.IsEnabled);
            basketLoad.SetResult([basket]);
            await Task.Delay(30);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(baskets.Items.Count == 1 && ((TextBox)window.FindName("DescriptionTextBox")).Text == thirdLine.Description,
                "Uma resposta antiga misturou cestas de itens diferentes.");
            lines.SelectedIndex = 1;
            await WaitForBasketUiAsync(() => save.IsEnabled && baskets.Items.Count == 2);
            Require(((TextBox)window.FindName("BasketNameTextBox")).Text == "Manual 2",
                "A sugestão do nome perdeu a contagem das cestas manuais.");
            baskets.SelectedIndex = 1;
            window.Hide();
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Require(window.ShowDialog() == true && window.ExistingProjectId == secondProject.Id &&
                    window.ExistingLineId == secondLine.Id && window.ExistingBasketId == basket.Id &&
                    window.Input?.RequestedQuantity == 25m,
                "Salvar não preservou o projeto, item, cesta ou quantidade escolhidos.");
        }
        finally { window.Close(); }

        var pending = new TaskCompletionSource<IReadOnlyList<QuotationLine>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken closingToken = default;
        var closing = new BasketWindow([firstProject], (_, token) => { closingToken = token; return pending.Task; },
            (_, _) => throw new InvalidOperationException("Não deve carregar cestas ao fechar."),
            (_, _) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), firstProject.Id, "Café", "", "");
        closing.Show();
        await WaitForBasketUiAsync(() => closingToken.CanBeCanceled);
        closing.Close();
        Require(closingToken.IsCancellationRequested, "Fechar a janela não cancelou o carregamento pendente.");
        pending.SetResult([firstLine]);
        await Task.Delay(30);
        Console.WriteLine("Manual basket loading: chosen project/item only, stale responses, save destination and close cancellation passed.");
    }

    private static async Task CheckBasketPickerPositionAsync()
    {
        var project = new QuotationProject(Guid.NewGuid(), "Seletor", default, default) { SortItemsAlphabetically = true };
        var empty = project with { Id = Guid.NewGuid(), Name = "Sem cestas" };
        var items = Enumerable.Range(1, 451).Select(i => new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Description = $"Produto {i:D3}", RequestedQuantity = 1, RequestedUnit = "un"
        }).ToArray();
        var marked = items.Where((_, i) => i < 427 && (i + 1) % 7 == 0).Select(row => row.Id).ToHashSet();
        var basketReads = 0;
        var window = new BasketWindow([project, empty],
            (id, _) => Task.FromResult<IReadOnlyList<QuotationLine>>(items.Reverse().Select(line => line with { ProjectId = id }).ToArray()),
            (_, _) => { basketReads++; return Task.FromResult<IReadOnlyList<QuotationManualBasket>>([]); },
            (id, _) => Task.FromResult<IReadOnlySet<Guid>>(id == project.Id ? marked : new HashSet<Guid>()), project.Id, "Café", "", "");
        try
        {
            window.Show();
            var combo = (ComboBox)window.FindName("LineComboBox");
            combo.MaxDropDownHeight = 240;
            await WaitForBasketUiAsync(() => combo.IsEnabled && combo.Items.Count == 452);

            async Task<ScrollViewer> OpenAsync()
            {
                combo.IsDropDownOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
                return VisualChildren(popup.Child).OfType<ScrollViewer>().First();
            }

            var scroll = await OpenAsync();
            Require(scroll.VerticalOffset <= 427 && scroll.VerticalOffset + scroll.ViewportHeight >= 429,
                "O último item com cesta (427) e o seguinte não estão juntos na área visível.");
            Require(combo.SelectedIndex == 0 && basketReads == 0 && window.Input is null, "Abrir a lista selecionou ou criou um item.");
            var green = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(427);
            var next = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(428);
            Require(Equals(green.Background, window.FindResource("ValidMarkedPriceBrush")) &&
                    Equals(green.Foreground, window.FindResource("ValidMarkedPriceTextBrush")) &&
                    !Equals(next.Background, green.Background), "O contraste ou a marcação dos itens intercalados está incorreto.");
            combo.SelectedIndex = 428;
            combo.IsDropDownOpen = false;
            combo.SelectedIndex = 7;
            await OpenAsync();
            Require(combo.SelectedIndex == 7 && basketReads > 0, "Reabrir o seletor substituiu a escolha do usuário.");
            combo.IsDropDownOpen = false;
            ((ComboBox)window.FindName("ProjectComboBox")).SelectedItem = empty;
            await WaitForBasketUiAsync(() => combo.IsEnabled && combo.Items.Count == 452);
            scroll = await OpenAsync();
            Require(scroll.VerticalOffset == 0 && combo.SelectedIndex == 0 && window.Input is null, "Projeto sem cestas não abriu no começo.");
        }
        finally { window.Close(); }
        Console.WriteLine("Manual basket picker: actual dropdown, 451 items, interleaved marks, scroll, free selection and empty project passed.");
    }

    private static async Task CheckDuplicatePricesCommandAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.UiChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainView? owner = null;
        var previousMain = Application.Current.MainWindow;
        try
        {
            var database = new SqliteContractRepository(Path.Combine(directory, "synthetic.db"));
            await database.InitializeAsync();
            var repo = new SqliteQuotationRepository(database.DatabasePath);
            var service = new QuotationService(repo, new QuotationAnalyzer());
            var project = await repo.CreateProjectAsync("Revisão sintética");
            var line = Guid.NewGuid();
            var first = new QuotationReference
            {
                LineId = line, Id = "origem-1", ContractId = "11222333000181-1-000123/2026", ItemNumber = 1,
                ResultSequence = 1, SupplierTaxId = "08236940000196", SupplierName = "Fornecedor", ItemDescription = "Café",
                UnitPrice = 10, ResultDate = new(2025, 12, 1)
            };
            var second = first with { Id = "origem-2", ResultSequence = 2, UnitPrice = 12, ResultDate = new(2026, 11, 1) };
            await repo.SaveSampleAsync(project.Id, line, new("Café", 10, "kg", null, null), [first, second]);
            var basket = await repo.SaveManualBasketAsync(line, null, "Manual", [first.Id, second.Id]);
            var initial = (await service.GetAnalysisAsync(project.Id, line))!;
            var automatic = initial.Baskets.Single(b => !b.IsManual);
            await service.ConfirmBasketAsync(initial, automatic.Key);
            var other = await repo.CreateLineAsync(project.Id, new("Item intacto", 1, "un", null, null));
            var report = await service.GetReportAsync(project.Id);
            var main = CreateBasketTestMain(service, report);
            main.QuotationProjects.Add(main.SelectedQuotationProject!);
            SetSearchField(main, "_quotationsInitialized", true);
            main.SelectedResultsWorkspace = AppModels.ResultsWorkspace.Quotations;
            CallSearch(main, "ApplyQuotationReport", report, line);
            var intactRow = main.QuotationLines.Single(item => item.Line.Id == other.Id);
            var rowChanges = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            main.QuotationLines.CollectionChanged += (_, e) => rowChanges.Add(e.Action);
            await using var spectator = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
            await spectator.OpenAsync();
            async Task<long> DataVersionAsync()
            {
                await using var query = spectator.CreateCommand();
                query.CommandText = "PRAGMA data_version";
                return Convert.ToInt64(await query.ExecuteScalarAsync());
            }
            var beforeVersion = await DataVersionAsync();
            var settings = new AppServices.AppSettingsService();
            typeof(AppServices.AppSettingsService).GetField("_settingsPath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(settings, Path.Combine(directory, "settings.json"));
            owner = new MainView(main, new AppServices.DataGridColumnLayoutService(settings, new(directory, true)), null!);
            typeof(MainView).GetField("_shutdownComplete", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, true);
            Application.Current.MainWindow = owner;
            owner.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var menuButton = VisualChildren(owner).OfType<Button>().Single(b => Equals(b.Content, "Cotações ▾"));
            Require(main.SelectedQuotationBasket?.Key == automatic.Key, "A cotação de teste não abriu com a automática confirmada.");
            var command = (AppModels.AsyncRelayCommand)main.RemoveDuplicateQuotationPricesCommand;
            async Task ClickPreviewAsync(string label)
            {
                using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!Application.Current.Windows.OfType<DuplicateWindow>().Any()) await Task.Delay(10, timer.Token);
                var preview = Application.Current.Windows.OfType<DuplicateWindow>().Single();
                try
                {
                    var details = string.Join("\\n", VisualChildren(preview).OfType<TextBox>().Select(box => box.Text));
                    if (label == "Fechar")
                        Require(details.Length == 0, "Repetir a verificação apresentou exclusões.");
                    else
                    {
                        Require(details.Contains(first.Id) && details.Contains(second.Id) && details.Contains("Manter") &&
                                details.Contains("Automática confirmada") && details.Contains("Manual") &&
                                details.Contains("continuará selecionada e confirmada") && details.Contains("mínimo 3"),
                            "A prévia omitiu uma das cestas, o mínimo ou o destino da seleção.");
                        var consents = VisualChildren(preview).OfType<CheckBox>().ToArray();
                        Require(consents.Length == 2 && consents.All(check => check.IsChecked == false),
                            "Cada cesta incompleta exige aceite individual.");
                        var apply = VisualChildren(preview).OfType<Button>().Single(b => Equals(b.Content, "Remover"));
                        Require(!apply.IsEnabled, "A retirada foi habilitada sem aceite de ressalva.");
                        if (label == "Remover")
                            foreach (var check in consents) check.IsChecked = true;
                        else consents[0].IsChecked = true; // Cancel must discard even a checked warning.
                    }
                    var button = VisualChildren(preview).OfType<Button>().Single(b => Equals(b.Content, label));
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
                    ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
                }
                catch { preview.Close(); throw; }
            }

            foreach (var action in new[] { "Cancelar", "Remover", "Fechar" })
            {
                var click = ClickPreviewAsync(action);
                menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var menu = menuButton.ContextMenu!;
                var actionItem = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Remover preços duplicados"));
                Require(menu.IsOpen && actionItem.IsEnabled && actionItem.Command == command &&
                    menu.Items[menu.Items.IndexOf(actionItem) - 1] is MenuItem { Header: "Organizar Itens" },
                    "A ação da tela de Cotações não está vinculada ou na posição solicitada.");
                var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(actionItem);
                ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
                await click;
                await command.ExecutionTask!;
                Require(ReferenceEquals(intactRow, main.QuotationLines.Single(row => row.Line.Id == other.Id)),
                    "A atualização substituiu um item intacto.");
                Require(rowChanges.Count == (action == "Cancelar" ? 0 : 1) &&
                    rowChanges.All(change => change == System.Collections.Specialized.NotifyCollectionChangedAction.Replace),
                    "O comando recarregou a lista de itens ou repetiu a atualização.");
                if (action == "Cancelar") Require(beforeVersion == await DataVersionAsync(), "Cancelar gravou na base.");
                var fresh = new QuotationService(new SqliteQuotationRepository(database.DatabasePath), new QuotationAnalyzer());
                var reread = (await fresh.GetAnalysisAsync(project.Id, line))!;
                var manuals = reread.Baskets.Where(b => b.IsManual).ToArray();
                Require(reread.Line.SelectionConfirmed && reread.Baskets.Any(b => b.Key == automatic.Key && b.References.Count == 2),
                    "A revisão mudou a sugestão ou perdeu a confirmação.");
                if (action == "Cancelar")
                    Require(manuals.Length == 1 && manuals[0].References.Count == 2 && reread.SelectedBasket?.Key == automatic.Key,
                        "Cancelar alterou a composição ou a escolha.");
                else
                    Require(manuals.Length == 2 && manuals.All(b => b.References.Single().Id == first.Id) &&
                        manuals.Any(b => b.ManualBasketId == basket.Id) && reread.SelectedBasket!.IsManual &&
                        main.SelectedQuotationBasket?.Key == reread.SelectedBasket.Key &&
                        main.SelectedBasketReferences.Single().Source.Id == first.Id,
                        "A tela ou a cotação reaberta não apresentou a composição salva.");
            }
        }
        finally
        {
            owner?.Close();
            Application.Current.MainWindow = previousMain;
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
        Console.WriteLine("Quotation duplicate command: manual and confirmed automatic baskets, cancel, apply, active selection, persisted reload and repeat passed.");
    }

    private static MainVm CreateBasketTestMain(QuotationService service, QuotationProjectReport report)
    {
        var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
        foreach (var field in typeof(MainVm).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            if (field.FieldType.Name.StartsWith("RangeObservableCollection`", StringComparison.Ordinal) ||
                field.FieldType.Name.StartsWith("ObservableCollection`", StringComparison.Ordinal))
                field.SetValue(main, Activator.CreateInstance(field.FieldType));
        SetSearchField(main, "_retainedItemRows", new Dictionary<string, AppModels.ItemSearchDisplayRow>());
        CallSearch(main, "InitializeQuotation", service, null, null, null, null);
        SetSearchField(main, "_selectedQuotationProject", new AppModels.QuotationProjectDisplay(report.Project));
        SetSearchField(main, "_quotationWordReport", report);
        return main;
    }

    private static async Task WaitForBasketUiAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Require(timer.Elapsed < TimeSpan.FromSeconds(10), "O carregamento da janela de cesta não terminou.");
            await Task.Delay(10);
        }
    }

    private static async Task CheckSavedManualBasketRefreshAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.UiChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new SqliteContractRepository(Path.Combine(directory, "test.db"));
            await database.InitializeAsync();
            var connections = new BasketCountingConnections(database.DatabasePath);
            var repository = new SqliteQuotationRepository(connections);
            var quotations = new QuotationService(repository, new QuotationAnalyzer());
            var project = await quotations.CreateProjectAsync("Atualização local");
            var input = new QuotationLineInput("Café", 25m, "KG", null, null);
            var rows = Enumerable.Range(1, 3).Select(index => DisplayRow(index).Source).ToArray();
            var saved = await quotations.SaveManualBasketAsync(project.Id, null, input, null, "Manual", rows);
            var other = await quotations.SaveManualBasketAsync(project.Id, null, input with { Description = "Outro item" },
                null, "Outra cesta", rows);
            var report = await quotations.GetReportAsync(project.Id);
            var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
            foreach (var field in typeof(MainVm).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.FieldType.Name.StartsWith("RangeObservableCollection`", StringComparison.Ordinal) ||
                    field.FieldType.Name.StartsWith("ObservableCollection`", StringComparison.Ordinal))
                    field.SetValue(main, Activator.CreateInstance(field.FieldType));
            SetSearchField(main, "_retainedItemRows", new Dictionary<string, AppModels.ItemSearchDisplayRow>());
            SetSearchField(main, "_quotationService", quotations);
            SetSearchField(main, "_selectedQuotationProject", new AppModels.QuotationProjectDisplay(report.Project));
            SetSearchField(main, "_quotationWordReport", report);
            SetSearchField(main, "<ExportQuotationWordCommand>k__BackingField", new AppModels.AsyncRelayCommand(() => Task.CompletedTask));
            SetSearchField(main, "<ExportQuotationPriceWordCommand>k__BackingField", new AppModels.AsyncRelayCommand(() => Task.CompletedTask));
            saved = await quotations.SaveManualBasketAsync(project.Id, saved.Analysis.Line.Id, input, saved.Basket.Id,
                saved.Basket.Name, [DisplayRow(4).Source]);
            connections.OpenCount = 0;
            await (Task)CallSearch(main, "ShowSavedManualBasketAsync", saved.Analysis, saved.Basket.Key)!;
            Require(connections.OpenCount <= 2, "Salvar uma cesta releu ou recalculou o projeto inteiro.");
            Require(main.QuotationLines.Select(value => value.Line.Id).SequenceEqual(report.Lines.Select(value => value.Line.Id)),
                "Atualizar uma cesta mudou a ordem dos itens.");
            Require(ReferenceEquals(main.QuotationLines.Single(value => value.Line.Id == other.Analysis.Line.Id).Analysis,
                    report.Lines.Single(value => value.Line.Id == other.Analysis.Line.Id)),
                "Atualizar uma cesta recalculou outro item.");
            Require(main.SelectedQuotationBasket?.Key == saved.Basket.Key &&
                    main.SelectedQuotationLine?.Analysis.Line.SelectionConfirmed == true &&
                    (await repository.GetLineAsync(project.Id, saved.Analysis.Line.Id))!.SelectionConfirmed,
                "Reaproveitar a análise perdeu a seleção ou a confirmação persistida.");
            Console.WriteLine("Manual basket refresh: preserved order, persisted confirmation and no unrelated item reads passed.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private sealed class BasketCountingConnections(string path) : ISqliteConnectionFactory
    {
        private readonly SqliteConnectionFactory _inner = new(path);
        public int OpenCount { get; set; }
        public string DatabasePath => _inner.DatabasePath;
        public ISqliteWorkCoordinator WorkCoordinator => _inner.WorkCoordinator;
        public int MigrationCacheKib => _inner.MigrationCacheKib;
        public long MmapBytes => _inner.MmapBytes;
        public int WorkerThreads => _inner.WorkerThreads;
        public string ProfileName => _inner.ProfileName;
        public SqliteSearchTuning SearchTuning => _inner.SearchTuning;
        public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return _inner.OpenAsync(cancellationToken);
        }
    }
}
