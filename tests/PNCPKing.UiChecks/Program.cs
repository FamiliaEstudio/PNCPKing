using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--appearance" or "--appearance-baseline", var sourceDirectory, var outputDirectory])
        {
            var appearanceApp = CreateUiApplication();
            if (args[0] == "--appearance-baseline") LoadBaselineResources(sourceDirectory);
            appearanceApp.Startup += async (_, _) =>
            {
                try { await CheckAppearanceAsync(sourceDirectory, outputDirectory); appearanceApp.Shutdown(); }
                catch (Exception error) { Console.Error.WriteLine(error); appearanceApp.Shutdown(1); }
            };
            return appearanceApp.Run();
        }
        if (args is ["--main-search"])
        {
            var searchApp = CreateUiApplication();
            searchApp.Startup += async (_, _) =>
            {
                try { await CheckMainSearchAsync(); searchApp.Shutdown(); }
                catch (Exception error) { Console.Error.WriteLine(error); searchApp.Shutdown(1); }
            };
            return searchApp.Run();
        }
        if (args is ["--reading"] or ["--reading-routed"])
        {
            var readingApp = CreateUiApplication();
            readingApp.Startup += async (_, _) =>
            {
                try
                {
                    await CheckReadingAsync(useNativeMouse: args[0] == "--reading");
                    readingApp.Shutdown();
                }
                catch (Exception error) { Console.Error.WriteLine(error); readingApp.Shutdown(1); }
            };
            return readingApp.Run();
        }
        if (args is ["--layout"])
        {
            var layoutApp = CreateUiApplication();
            layoutApp.Startup += async (_, _) =>
            {
                try
                {
                    await CheckMonitorPlacementAsync();
                    Console.WriteLine("Monitor placement: passed");
                    await CheckQuotationGroupsAsync();
                    Console.WriteLine("Quotation groups dialog: passed");
                    await CheckColumnChooserAsync();
                    Console.WriteLine("Column chooser: passed");
                    await CheckThemeControlsAsync();
                    Console.WriteLine("Theme controls: passed");
                    layoutApp.Shutdown();
                }
                catch (Exception error) { Console.Error.WriteLine(error); layoutApp.Shutdown(1); }
            };
            return layoutApp.Run();
        }

        if (args.Length < 1) throw new ArgumentException("Informe o JSON de saída e, opcionalmente, uma cópia de benchmark.");
        var app = CreateUiApplication();
        app.Startup += async (_, _) =>
        {
            try
            {
                var samples = new List<UiMeasurement>();
                foreach (var count in new[] { 50, 1000, 10000 })
                    foreach (var sorted in new[] { false, true })
                        samples.Add(await MeasureGridAsync(count, sorted));
                var streaming = args.Length > 1 ? await CheckStreamingAsync(args[1]) : null;
                var githubUpdateDialog = CheckGitHubUpdateDialog();
                var report = new { passed = true, samples, streaming, githubUpdateDialog };
                await File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(JsonSerializer.Serialize(report));
                app.Shutdown();
            }
            catch (Exception error) { Console.Error.WriteLine(error); app.Shutdown(1); }
        };
        return app.Run();
    }

    private static async Task CheckMonitorPlacementAsync()
    {
        var window = new Window
        {
            Title = "Validação de janela em monitor pequeno",
            Width = SystemParameters.PrimaryScreenWidth * 2,
            Height = SystemParameters.PrimaryScreenHeight * 2,
            MinWidth = SystemParameters.PrimaryScreenWidth * 1.5,
            MinHeight = SystemParameters.PrimaryScreenHeight * 1.5
        };
        MonitorAwareWindowBehavior.Attach(window);
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var handle = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(handle, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Require(monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info), "Monitor não encontrado.");
            Require(GetWindowRect(handle, out var rect), "Janela não encontrada.");
            Require(rect.Left >= info.WorkArea.Left && rect.Top >= info.WorkArea.Top &&
                    rect.Right <= info.WorkArea.Right && rect.Bottom <= info.WorkArea.Bottom,
                "A janela ultrapassou a área útil do monitor.");
        }
        finally { window.Close(); }
    }

    private static async Task CheckQuotationGroupsAsync()
    {
        var project = new QuotationProject(Guid.NewGuid(), "Grupos", DateTimeOffset.Now, DateTimeOffset.Now);
        int[] quantities = [1, 2, 3, 4, 5, 7, 8, 100];
        decimal[] principal = [1, 2, 3, 3, 4, 6, 6, 75];
        decimal[] reserved = [0, 0, 0, 1, 1, 1, 2, 25];
        string[] numbers = ["1", "2", "3", "4 e 9", "5 e 10", "6 e 11", "7 e 12", "8 e 13"];
        var basket = new QuotationBasket { Key = "chosen", References = [], AveragePrice = 90000.12m,
            MinimumPrice = 90000.12m, MaximumPrice = 90000.12m, MaximumDeviationPercent = 0, Score = 100 };
        var analyses = quantities.Select(quantity => new QuotationLineAnalysis(new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Description = $"Item {quantity:D3}",
            RequestedQuantity = quantity, RequestedUnit = "unidade", SelectedBasketKey = "chosen", SelectionConfirmed = true
        }, [], [basket], 0, 0, 0, 0, 0)).ToArray();
        var window = new PNCPKing.App.Views.QuotationGroupsWindow(new(project, analyses)) { Width = 900, Height = 550 };
        MonitorAwareWindowBehavior.Attach(window);
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var root = (DockPanel)window.Content;
            var grid = root.Children.OfType<DataGrid>().Single();
            Require(grid.ActualHeight > 150, "A lista de membros não tem altura utilizável.");
            Require(grid.Items.Count == quantities.Length && grid.Columns.Count == 10, "Colunas da organização ausentes.");
            var rows = grid.Items.Cast<PNCPKing.App.Views.QuotationGroupsWindow.ItemRow>().ToArray();
            var row = rows.Single(value => value.Quantity == 4);
            Require(row.Numbers == "1 e 2" && row.PrincipalQuantity == 3 && row.ReservedQuantity == 1,
                "A prévia das cotas está incorreta.");
            grid.SelectAll();
            var actions = root.Children.OfType<WrapPanel>().Single();
            var create = actions.Children.OfType<Button>().Single(button => (string)button.Content == "Criar com seleção");
            create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.Groups.Count == 1 && row.ResultGroups == "Grupo 1 — Principal / Grupo 2 — Reservada",
                "O agrupamento não gerou grupos principal e reservado distintos.");
            for (var index = 0; index < rows.Length; index++)
            {
                var item = rows[index];
                Require(item.Numbers == numbers[index] && item.PrincipalQuantity == principal[index] &&
                        item.ReservedQuantity == reserved[index], $"A divisão do item de {item.Quantity} unidades está incorreta.");
                Require(item.PrincipalValue == (principal[index] * 90000.12m).ToString("C2") &&
                        item.ReservedValue == (reserved[index] * 90000.12m).ToString("C2"),
                    "Os valores da prévia não correspondem às quantidades de cada cota.");
                if (item.Quantity < 4)
                    Require(item.ResultGroups == "Grupo 1 — Principal" &&
                            item.Note == "Reserva zero: permanece integralmente na principal",
                        "O item de quantidade pequena foi incluído na reservada ou perdeu sua observação.");
            }
            var remove = actions.Children.OfType<Button>().Single(button => (string)button.Content == "Excluir grupo");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.Groups.Count == 0 && grid.Items.Count == quantities.Length, "Excluir o grupo removeu itens.");
            grid.UnselectAll();
            foreach (var small in rows.Where(value => value.Quantity < 4)) grid.SelectedItems.Add(small);
            create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.Groups.Count == 1, "Não foi possível criar o grupo de pequenas quantidades.");
            foreach (var small in rows.Where(value => value.Quantity < 4))
                Require(small.ResultGroups == "Grupo 1 — Sem cota" && small.ReservedQuantity == 0 &&
                        small.PrincipalQuantity == small.Quantity && small.Numbers == small.Quantity.ToString(),
                    "O grupo de pequenas quantidades gerou uma reserva ou numeração indevida.");
        }
        finally { window.Close(); }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, WorkArea;
        public uint Flags;
    }

    private static object CheckGitHubUpdateDialog()
    {
        var plan = new GitHubUpdatePlan(
            new AppUpdateManifest(1, "1.2.4", "win-x64", SqliteContractRepository.CurrentSchemaVersion,
                new ReleaseFile("PNCPKing.exe", 1024, new string('a', 64))),
            null, "Nova versão disponível.", "Preços em dia.");
        var notes = new GitHubReleaseNotes(
            [new("1.2.4", string.Join('\n', Enumerable.Repeat("Melhoria de cotação em várias telas.", 100)))], null);
        var window = new PNCPKing.App.Views.GitHubUpdateWindow(plan, notes);
        try
        {
            window.Show();
            window.UpdateLayout();
            var panel = (Grid)window.Content;
            var buttons = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
            var notesScroll = panel.Children.OfType<ScrollViewer>()
                .Single(value => Grid.GetRow(value) == 1);
            var update = buttons.Single(b => Equals(b.Content, "Atualizar agora"));
            Require(update.IsEnabled, "A atualização disponível não habilitou o botão.");
            Require(buttons.Any(b => b.IsCancel), "A prévia precisa permitir cancelar.");
            foreach (var (width, height) in new[] { (380d, 280d), (660d, 520d), (900d, 700d) })
            {
                window.Width = width;
                window.Height = height;
                window.UpdateLayout();
                Require(notesScroll.ScrollableHeight > 0, "Notas longas precisam de rolagem própria.");
                foreach (var button in buttons)
                {
                    Require(button.IsVisible, "Botões precisam permanecer visíveis.");
                    var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                    Require(bounds.Bottom <= window.ActualHeight, "Botão fora da janela de atualização.");
                }
            }
            return new { passed = true, appUpdateAvailable = true, cancelAvailable = true, window.ActualHeight };
        }
        finally { window.Close(); }
    }

    private static async Task<UiMeasurement> MeasureGridAsync(int initialCount, bool sorted)
    {
        var initial = Enumerable.Range(0, initialCount).Select(LongRow).ToArray();
        initial[0].IsPinned = true;
        initial[1].IsSelectedForBasket = true;
        var rows = new RangeObservableCollection<ItemSearchDisplayRow>();
        var grid = CreateGrid(rows);
        var window = new Window { Title = "Validação da tabela PNCP King", Width = 1100, Height = 430, Content = new PNCPKing.App.Controls.GridReader { Table = grid } };
        var view = CollectionViewSource.GetDefaultView(rows);
        var batchTimes = new List<double>();
        var inputDelays = new List<double>();
        var pageTimes = new List<double>();
        using var buffer = new UiBatchBuffer<ItemSearchDisplayRow>(batch =>
        {
            var start = Stopwatch.GetTimestamp();
            foreach (var row in batch) rows.Add(row);
            batchTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        });
        window.Show();
        rows.AddRange(initial);
        if (sorted) view.SortDescriptions.Add(new SortDescription(nameof(ItemSearchDisplayRow.PublicationDate), ListSortDirection.Descending));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        // Warm the renderer/JIT before collecting input latency and batch application time.
        buffer.Enqueue(Enumerable.Range(initialCount, 50).Select(LongRow));
        await buffer.FlushAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        batchTimes.Clear();
        for (var round = 0; round < 30; round++)
        {
            rows.ReplaceAll(initial);
            grid.SelectedItem = initial[1];
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var added = Enumerable.Range(initialCount + round * 50, 50).Select(LongRow).ToArray();
            var queued = Stopwatch.GetTimestamp();
            var input = grid.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() => inputDelays.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds)));
            var start = Stopwatch.GetTimestamp();
            buffer.Enqueue(added);
            await buffer.FlushAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await input;
            pageTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Require(rows.Count == initialCount + 50, "A página perdeu linhas.");
            Require(ReferenceEquals(grid.SelectedItem, initial[1]) && initial[0].IsPinned && initial[1].IsSelectedForBasket,
                "A inserção alterou seleção/fixação/cesta.");
            var actual = view.Cast<ItemSearchDisplayRow>().ToArray();
            var expected = sorted ? rows.OrderByDescending(row => row.PublicationDate).ToArray() : rows.ToArray();
            Require(sorted ? actual.Select(row => row.PublicationDate).SequenceEqual(expected.Select(row => row.PublicationDate))
                : actual.SequenceEqual(expected), "A visualização não manteve a ordenação escolhida.");
        }
        window.Close();
        var p95 = Percentile95(inputDelays);
        Require(p95 < 100, $"Dispatcher p95 = {p95:F2} ms para {initialCount} linhas, sorted={sorted}.");
        return new(initialCount, sorted, inputDelays.Count, p95, inputDelays.Max(),
            Percentile95(batchTimes), Percentile95(pageTimes));
    }

    private static async Task<object> CheckStreamingAsync(string path)
    {
        Require(Path.GetDirectoryName(Path.GetFullPath(path))!.Contains("benchmark", StringComparison.OrdinalIgnoreCase),
            "A verificação exige uma cópia de benchmark.");
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        var repository = new SqlitePriceCacheRepository(new SqliteConnectionFactory(path, resourceProbe: new Probe(), readOnly: true));
        var today = DateOnly.FromDateTime(DateTime.Today);
        var query = new SearchQuery("Café -máquina -cápsula -cafeteira \"pacote \"unidade", GeoScope.All,
            DataWindow.Start(today), today);
        var expression = SearchText.Parse(query.Text);
        var rows = new RangeObservableCollection<ItemSearchDisplayRow>();
        var grid = CreateGrid(rows);
        var window = new Window { Title = "Validação da entrega progressiva", Width = 1100, Height = 430, Content = grid };
        var view = CollectionViewSource.GetDefaultView(rows);
        using var firstApplied = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Task<PriceCacheLocalPage>? pending = null;
        var beforeCompletion = false;
        using var buffer = new UiBatchBuffer<ItemSearchDisplayRow>(batch =>
        {
            foreach (var row in batch) rows.Add(row);
            if (!firstApplied.IsSet)
            {
                beforeCompletion = pending is { IsCompleted: false };
                firstApplied.Set();
            }
        });
        window.Show();
        IProgress<PriceCacheLocalProgress> ui = new Progress<PriceCacheLocalProgress>(value =>
            buffer.Enqueue(value.Rows.Select(row => new ItemSearchDisplayRow(row))));
        var paused = false;
        var progress = new InlineProgress(value =>
        {
            ui.Report(value);
            if (!paused && value.Rows.Count > 0)
            {
                paused = true;
                Require(firstApplied.Wait(TimeSpan.FromSeconds(5), timeout.Token), "A tabela esperou o término da página.");
            }
        });
        try
        {
            pending = Task.Run(() => repository.SearchLocalAfterAsync(query, expression, null, null, null, 50,
                PriceCacheLocalReadOrder.Discovery, progress, timeout.Token));
            var first = await pending;
            await buffer.FlushAsync();
            Require(beforeCompletion && rows.Count == 50, "A primeira página não foi entregue progressivamente.");
            Require(rows.Select(row => Key(row.Source)).SequenceEqual(first.Rows!.Select(Key)), "A grade não corresponde à primeira página.");
            var cursor = first.Cursor;
            view.SortDescriptions.Add(new SortDescription(nameof(ItemSearchDisplayRow.PublicationDate), ListSortDirection.Descending));
            Require(first.Cursor == cursor, "Ordenar alterou o cursor.");
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var second = await Task.Run(() => repository.SearchLocalAfterAsync(query, expression, null, null, cursor, 50,
                PriceCacheLocalReadOrder.Discovery, ui, timeout.Token));
            await buffer.FlushAsync();
            Require(rows.Count == 100 && rows.Select(row => Key(row.Source)).Distinct().Count() == 100, "A continuação perdeu ou repetiu preços.");
            Require(rows.Select(row => Key(row.Source)).SequenceEqual(first.Rows!.Concat(second.Rows!).Select(Key)),
                "A ordenação visual alterou a ordem de descoberta.");
            Require(view.Cast<ItemSearchDisplayRow>().Select(row => row.PublicationDate)
                .SequenceEqual(rows.OrderByDescending(row => row.PublicationDate).Select(row => row.PublicationDate)),
                "A segunda página não respeitou a ordenação visual.");
            return new { rows = rows.Count, appliedBeforeCompletion = beforeCompletion, sortedContinuation = true,
                note = "Produtor pausado deliberadamente após a primeira entrega; não é benchmark de velocidade." };
        }
        finally
        {
            window.Close();
            var after = new FileInfo(path);
            Require(after.Length == length && after.LastWriteTimeUtc == modified, "A cópia de benchmark foi alterada.");
        }
    }

    private static DataGrid CreateGrid(object rows)
    {
        var grid = new DataGrid { ItemsSource = (System.Collections.IEnumerable)rows, AutoGenerateColumns = false,
            IsReadOnly = true, EnableRowVirtualization = true, EnableColumnVirtualization = true };
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        foreach (var field in new[] { "PublicationDate", "Uf", "Description", "Unit", "HomologatedQuantity", "Supplier", "HomologatedUnitValue" })
            grid.Columns.Add(new DataGridTextColumn { Header = field, Binding = new Binding(field), Width = 145 });
        return grid;
    }

    private static ItemSearchDisplayRow DisplayRow(int index)
    {
        var contract = new ContractRecord { PncpId = $"check-{index}", Cnpj = "12345678000190", PurchaseYear = 2026,
            PurchaseSequence = index + 1, Object = "Café", Uf = "SP", Municipality = "Ribeirão Preto",
            PublicationDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddDays(-(index * 71 % 333)) };
        var item = new ProcurementItem { ContractId = contract.PncpId, ItemNumber = 1, Description = "Café torrado", Unit = "KG" };
        var price = new HomologationResult { ContractId = contract.PncpId, ItemNumber = 1, ResultSequence = 1,
            SupplierName = "Fornecedor", ResultStatusId = 1, HomologatedUnitValueScaled = DecimalScale.ToScaled(25m) };
        return new(new ItemSearchRow(contract, item, price, ItemSearchPriceState.Homologated, "Preço local", false));
    }

    private static string Key(ItemSearchRow row) => $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}";
    private static double Percentile95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record UiMeasurement(int InitialRows, bool Sorted, int Samples, double DispatcherP95Ms,
        double DispatcherMaxMs, double BatchApplyP95Ms, double PageApplyP95Ms);
    private sealed class Probe : ISystemResourceProbe
    { public SystemResourceSnapshot GetSnapshot() => SystemResourceProbe.CreateSnapshot(6L << 30, 2L << 30, 65, 4); }
    private sealed class InlineProgress(Action<PriceCacheLocalProgress> report) : IProgress<PriceCacheLocalProgress>
    { public void Report(PriceCacheLocalProgress value) => report(value); }
}
