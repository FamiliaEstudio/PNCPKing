extern alias AppUnderTest;

using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using MainVm = AppUnderTest::PNCPKing.App.ViewModels.MainViewModel;
using ItemVm = AppUnderTest::PNCPKing.App.ViewModels.QuotationItemViewModel;
using MainView = AppUnderTest::PNCPKing.App.Views.MainWindow;
using BasketView = AppUnderTest::PNCPKing.App.Views.ManualBasketWindow;
using AppServices = AppUnderTest::PNCPKing.App.Services;
using AppModels = AppUnderTest::PNCPKing.App.ViewModels;

internal static partial class Program
{
    private static async Task CheckUsabilityAsync()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.UiChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new SqliteContractRepository(Path.Combine(directory, "test.db"));
            await database.InitializeAsync();
            var repository = new SqliteQuotationRepository(database.DatabasePath);
            var quotations = new QuotationService(repository, new QuotationAnalyzer());
            var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
            foreach (var field in typeof(MainVm).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.FieldType.Name.StartsWith("RangeObservableCollection`", StringComparison.Ordinal) ||
                    field.FieldType.Name.StartsWith("ObservableCollection`", StringComparison.Ordinal))
                    field.SetValue(main, Activator.CreateInstance(field.FieldType));
            SetSearchField(main, "_retainedItemRows", new Dictionary<string, AppModels.ItemSearchDisplayRow>());
            foreach (var field in new[] { "_currentItemResultKeys", "_visibleItemKeys", "_localPendingApplyKeys" })
                SetSearchField(main, field, new HashSet<string>());
            SetSearchField(main, "_quotationService", quotations);
            SetSearchField(main, "_queryText", "café");
            SetSearchField(main, "_minimumPriceText", "");
            SetSearchField(main, "_maximumPriceText", "");
            SetSearchField(main, "<ExportQuotationWordCommand>k__BackingField", new AppModels.AsyncRelayCommand(() => Task.CompletedTask));
            SetSearchField(main, "<ExportQuotationPriceWordCommand>k__BackingField", new AppModels.AsyncRelayCommand(() => Task.CompletedTask));
            await CheckSearchKeyboardAsync(main, directory);
            await CheckManualConfirmationAsync(main, repository, quotations, database.DatabasePath);
            Console.WriteLine("Usability: Enter, insert mode, quantities, manual selection/pagination, persisted edits and validation warnings passed.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static async Task CheckSearchKeyboardAsync(MainVm main, string directory)
    {
        var searches = 0;
        var canSearch = true;
        SetSearchField(main, "<SearchCommand>k__BackingField", new AppModels.RelayCommand(() => searches++, () => canSearch));
        var settings = new AppServices.AppSettingsService();
        typeof(AppServices.AppSettingsService).GetField("_settingsPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, Path.Combine(directory, "settings.json"));
        var layout = new AppServices.DataGridColumnLayoutService(settings, new(directory, true));
        var window = new MainView(main, layout, null!);
        typeof(MainView).GetField("_shutdownComplete", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        var previousMainWindow = Application.Current.MainWindow;
        Application.Current.MainWindow = window;
        try
        {
            window.Show();
            var query = (TextBox)window.FindName("QueryTextBox");
            var grid = (DataGrid)window.FindName("ItemResultsGrid");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            query.Focus();
            KeyEventArgs Enter(UIElement element)
            {
                var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, System.Windows.Input.Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                element.RaiseEvent(e);
                return e;
            }
            Require(Enter(query).Handled && searches == 1, "Enter não executou a pesquisa.");
            canSearch = false;
            Enter(query);
            Require(searches == 1, "Enter ignorou o bloqueio do comando de pesquisa.");
            query.Text = "cafe";
            query.CaretIndex = 2;
            EditingCommands.ToggleInsert.Execute(null, query);
            TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, query, "X"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(query.Text == "caXfe", "A barra entrou em sobrescrita após ToggleInsert.");
            query.Select(2, 1);
            TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, query, "Y"));
            Require(query.Text == "caYfe", "A barra deixou de substituir a seleção intencional.");
            ApplicationCommands.Undo.Execute(null, query);
            Require(query.Text is "caXfe" or "cafe", "Desfazer deixou de funcionar.");
            ApplicationCommands.Redo.Execute(null, query);
            Require(query.Text == "caYfe", "Refazer deixou de funcionar.");

            // The result quantity binding must suppress trailing zeroes without losing fractions.
            var quantity = grid.Columns.OfType<DataGridTextColumn>().Single(column => Equals(column.Header, "Quantidade"));
            foreach (var pair in new[] { (12m, "12"), (12.5m, "12,5"), (0.0001m, "0,0001") })
            {
                var format = ((System.Windows.Data.Binding)quantity.Binding).StringFormat;
                Require(pair.Item1.ToString(format, CultureInfo.CurrentCulture) == pair.Item2, "Quantidade perdeu precisão ou manteve zeros adicionais.");
            }

            // Open the real existing dialog through Enter; cancel it without creating any project.
            main.QueryText = "café";
            var marked = new AppModels.ItemSearchDisplayRow(DisplayRow(1).Source);
            main.ItemSearchRows.Add(marked);
            main.ApplyItemPriceAction([marked], AppModels.ItemPriceAction.MarkForBasket);
            grid.SelectedItem = marked;
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) =>
            {
                var dialogs = Application.Current.Windows.OfType<BasketView>().ToArray();
                if (dialogs.Length == 0) return;
                Require(dialogs.Length == 1, "Enter abriu duas janelas de cesta.");
                timer.Stop();
                dialogs[0].Close();
                opened.TrySetResult();
            };
            timer.Start();
            try
            {
                Require(Enter(grid).Handled, "Enter na grade não foi consumido.");
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(main.QuotationProjects.Count == 0, "O Enter de abertura também salvou a cesta.");
            }
            finally { timer.Stop(); }
        }
        finally
        {
            window.Close();
            Application.Current.MainWindow = previousMainWindow;
        }
    }

    private static async Task CheckManualConfirmationAsync(MainVm main, SqliteQuotationRepository repository, QuotationService quotations, string databasePath)
    {
        var project = await quotations.CreateProjectAsync("Cestas de teste");
        var lineId = Guid.NewGuid();
        var references = Enumerable.Range(1, 3).Select(index => new QuotationReference
        {
            Id = $"p{index}", LineId = lineId, ContractId = $"c{index}", ItemNumber = 1, ResultSequence = 1,
            SupplierTaxId = index.ToString("D14"), UnitPrice = 30m, ItemDescription = "Café", ItemUnit = "un",
            State = QuotationReferenceState.Eligible, ResultDate = DateOnly.FromDateTime(DateTime.Today)
        }).ToArray();
        await repository.SaveSampleAsync(project.Id, lineId, new("Café", 10, "un", null, null), references);
        var first = await quotations.CreateManualBasketAsync(lineId, "Primeira", ["p1", "p2", "p3"]);
        SetSearchField(main, "_selectedQuotationProject", new AppModels.QuotationProjectDisplay(project));
        async Task Reload(string key) => await (Task)CallSearch(main, "ReloadSavedManualBasketAsync", project.Id, lineId, key)!;
        await Reload(first.Key);
        Require((await repository.GetLineAsync(project.Id, lineId))!.SelectionConfirmed, "A primeira cesta manual não foi confirmada.");
        for (var i = 0; i < 100; i++) await quotations.CreateManualBasketAsync(lineId, $"Cesta {i}", ["p1", "p2", "p3"]);
        var last = (await repository.GetManualBasketsAsync(lineId)).Last();
        await Reload(last.Key);
        var savedLine = (await repository.GetLineAsync(project.Id, lineId))!;
        Require(savedLine.SelectedBasketKey == last.Key && savedLine.SelectionConfirmed, "A nova cesta não substituiu a escolha persistida.");
        Require(main.SelectedQuotationBasket?.Key == last.Key && main.QuotationBasketPage > 1, "A cesta escolhida não apareceu na página correta.");
        main.SelectedVisibleQuotationReference = main.VisibleQuotationReferences.First();
        await main.SetSelectedReferencePriceDivisorAsync(1.1m);
        Require((await repository.GetLineAsync(project.Id, lineId))!.SelectionConfirmed, "Editar divisor desconfirmou a cesta manual.");

        // The item window uses the same persisted confirmation flow.
        var store = new InternetEvidenceStore(Path.GetDirectoryName(databasePath)!);
        var internet = new InternetPriceService(repository, quotations, store);
        var item = new ItemVm(main, quotations, null!, null!, internet, null!, null!, null!, "", project.Id, lineId);
        typeof(ItemVm).GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(item, new QuotationItemSearchWorkspace { LineId = lineId });
        try
        {
            await item.LoadAsync(last.Key);
            // Keep three equal effective prices so no warning is expected.
            foreach (var reference in item.VisibleReferences.ToArray())
                await repository.SetManualBasketPriceDivisorAsync(last.Id, reference.Id, 3m);
            await item.LoadAsync(last.Key);
            await item.SetAggregationMethodAsync(QuotationAggregationMethod.Median);
            Require((await repository.GetLineAsync(project.Id, lineId))!.SelectionConfirmed, "A tela do item não reconfirmou o critério salvo.");
            Require(item.Line!.Analysis.SelectedBasket!.AdoptedPrice == 10m, "O total não acompanhou os divisores salvos.");
            item.SelectedBasket = item.Baskets.First(basket => !basket.Source.IsManual);
            var countBefore = (await repository.GetManualBasketsAsync(lineId)).Count;
            try
            {
                await item.SetPriceDivisorAsync(item.VisibleReferences.First(row => row.IsInSelectedBasket), 0m);
                throw new InvalidOperationException("Divisor zero foi aceito.");
            }
            catch (ArgumentOutOfRangeException) { }
            Require((await repository.GetManualBasketsAsync(lineId)).Count == countBefore,
                "Divisor inválido criou uma cópia antes da validação.");

            var image = await store.SavePngAsync(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lxoAAAAASUVORK5CYII="), 1, 1);
            var now = DateTimeOffset.UtcNow;
            var draft = new InternetPriceDraft
            {
                Id = Guid.NewGuid(), LineId = lineId, SourceUrl = "https://loja.exemplo.test/cafe",
                UnitPrice = 10m, Description = "Café", SupplierName = "Loja", SupplierTaxId = "11222333000181",
                PriceImage = image, TaxIdImage = image, CapturedAt = now, CreatedAt = now, UpdatedAt = now
            };
            await item.SaveInternetDraftAsync(draft);
            Require((await repository.GetLineAsync(project.Id, lineId))!.SelectedBasketKey == last.Key,
                "Salvar rascunho alterou a cesta confirmada.");
            await RespondToBasketWarningAsync(true, () => item.CompleteInternetDraftAsync(draft, null, "Internet"));
            savedLine = (await repository.GetLineAsync(project.Id, lineId))!;
            Require(savedLine.SelectionConfirmed && savedLine.SelectedBasketKey == item.SelectedBasket!.Key &&
                    savedLine.SelectedBasketKey != last.Key, "Concluir preço da internet não confirmou sua cesta.");
            var incomplete = await quotations.CreateManualBasketAsync(lineId, "Com ressalva", ["p1"]);
            await RespondToBasketWarningAsync(false, () => Reload(incomplete.Key));
            Require(!(await repository.GetLineAsync(project.Id, lineId))!.SelectionConfirmed, "Recusar ressalva confirmou a cesta.");
            await RespondToBasketWarningAsync(true, () => Reload(incomplete.Key));
            savedLine = (await new SqliteQuotationRepository(databasePath).GetLineAsync(project.Id, lineId))!;
            Require(savedLine.SelectionConfirmed && savedLine.SelectedBasketKey == incomplete.Key, "Aceitar ressalva não persistiu a escolha.");
        }
        finally { await item.DisposeAsync(); }
    }

    private static async Task RespondToBasketWarningAsync(bool accept, Func<Task> action)
    {
        var answered = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
        {
            var title = new StringBuilder(256);
            GetWindowText(handle, title, title.Capacity);
            if (title.ToString() != "Confirmar cesta manual com ressalva") return true;
            answered = true;
            PostMessage(handle, 0x0111, new IntPtr(accept ? 6 : 7), IntPtr.Zero);
            return false;
        }, IntPtr.Zero);
        timer.Start();
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); Require(answered, "A ressalva não foi apresentada."); }
        finally { timer.Stop(); }
    }

    private delegate bool ThreadWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, ThreadWindowCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
