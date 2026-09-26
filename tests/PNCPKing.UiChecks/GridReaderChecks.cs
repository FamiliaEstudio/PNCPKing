using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PNCPKing.App.Controls;
using PNCPKing.App.ViewModels;

internal static partial class Program
{
    private static async Task CheckReadingAsync()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        var results = new List<object>();
        var nativeMouse = false;
        foreach (var count in new[] { 50, 1000, 10000 })
        {
            var rows = new RangeObservableCollection<ItemSearchDisplayRow>();
            rows.AddRange(Enumerable.Range(0, count).Select(LongRow));
            var grid = CreateGrid(rows);
            grid.SelectionMode = DataGridSelectionMode.Extended;
            grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            grid.Columns.Add(new DataGridTextColumn { Header = "CNPJ", Binding = new Binding("SupplierTaxId"), Width = 140 });
            var reader = new GridReader { Table = grid };
            var pinCount = 0;
            var basketCount = 0;
            reader.PinRequested = row => { ((ItemSearchDisplayRow)row).TryApplyAction(ItemPriceAction.Pin); pinCount++; };
            reader.BasketSelectionRequested = row => { var price = (ItemSearchDisplayRow)row; price.IsSelectedForBasket = !price.IsSelectedForBasket; basketCount++; };
            reader.ClearRequested = row => ((ItemSearchDisplayRow)row).TryApplyAction(ItemPriceAction.Clear);
            var window = new Window { Title = "PNCP King — teste sintético de leitura", Width = 1050, Height = 600, Content = reader };
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var containers = VisualChildren(grid).OfType<DataGridRow>().ToArray();
                Require(containers.Length < 40, "A grade materializou as linhas fora da tela.");
                Require(containers.All(row => Math.Abs(row.ActualHeight - 32) < 1), "O texto longo aumentou a altura de uma linha.");
                Require(VisualChildren(grid).OfType<TextBlock>().Any(t => t.Text.Contains("descritor 0 ") && !t.Text.Contains('\n')),
                    "A célula não compactou as quebras de linha.");

                reader.OpenRow(rows[0], true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var tabs = reader.Children.OfType<TabControl>().Single();
                var description = (TextBox)((TabItem)tabs.Items[0]).Content;
                Require(description.Text == rows[0].Description, "O leitor truncou o descritivo original.");
                Require(tabs.ActualHeight <= reader.ActualHeight * 0.4 + 1, "O leitor excedeu seu limite de altura.");
                description.Focus();
                description.Select(description.Text.IndexOf("AÇÃO", StringComparison.Ordinal), 4);
                ApplicationCommands.Copy.Execute(null, description);
                await UntilAsync(() => reader.Children.OfType<DockPanel>().Single().Children.OfType<TextBlock>().Any(t => t.Text.StartsWith("Copiado.")));
                Require(Clipboard.GetText() == "AÇÃO", "A cópia do trecho não respeitou o foco do texto.");

                var sourceOrder = rows.ToArray();
                var view = CollectionViewSource.GetDefaultView(rows);
                view.SortDescriptions.Add(new SortDescription("PublicationDate", ListSortDirection.Descending));
                grid.SelectedItems.Clear();
                grid.SelectedItems.Add(rows[0]);
                grid.SelectedItems.Add(rows[2]);
                grid.Columns[^1].DisplayIndex = 0;
                grid.Columns[1].Visibility = Visibility.Collapsed;
                grid.Focus();
                ApplicationCommands.Copy.Execute(null, grid);
                await UntilAsync(() => reader.Children.OfType<DockPanel>().Single().Children.OfType<TextBlock>().Any(t => t.Text.StartsWith("Copiado.")));
                Require(Clipboard.GetText().StartsWith("00123456000190\t", StringComparison.Ordinal), "A cópia não respeitou a ordem das colunas/CNPJ.");
                Require(Clipboard.GetText().Contains(rows[0].Description), "A cópia perdeu o conteúdo integral.");
                Require(((string)Clipboard.GetData(DataFormats.Html)).Contains("mso-number-format"), "CNPJ sem formato textual para planilha.");
                Require(rows.SequenceEqual(sourceOrder), "A ordenação da grade alterou a coleção de descoberta.");
                if (count == 50)
                {
                    Require(OpenClipboard(IntPtr.Zero), "Não foi possível simular área de transferência ocupada.");
                    try
                    {
                        ApplicationCommands.Copy.Execute(null, grid);
                        var queued = Stopwatch.GetTimestamp();
                        await reader.Dispatcher.InvokeAsync(() =>
                            Require(Stopwatch.GetElapsedTime(queued).TotalMilliseconds < 100,
                                "Área de transferência ocupada travou a interface."), DispatcherPriority.Input);
                        await UntilAsync(() => reader.Children.OfType<DockPanel>().Single().Children.OfType<TextBlock>()
                            .Any(t => t.Text.StartsWith("Área de transferência ocupada")));
                    }
                    finally { CloseClipboard(); }
                }

                reader.OpenFind();
                var bar = reader.Children.OfType<WrapPanel>().Single();
                var query = bar.Children.OfType<TextBox>().Single();
                var status = bar.Children.OfType<TextBlock>().Single();
                query.Text = "não existe";
                query.Text = "acao";
                var latencies = new List<double>();
                var elapsed = Stopwatch.StartNew();
                while (status.Text == "Buscando…" && elapsed.Elapsed < TimeSpan.FromSeconds(10))
                {
                    var queued = Stopwatch.GetTimestamp();
                    await reader.Dispatcher.InvokeAsync(() => latencies.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds), DispatcherPriority.Input);
                    await Task.Delay(10);
                }
                Require(status.Text.StartsWith($"{(count + 1) / 2:N0} item"), "A busca ignorou linhas carregadas, acentos ou cancelamento.");
                var next = bar.Children.OfType<Button>().Single(b => (string)b.Content == "Próximo");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(description.SelectedText == "AÇÃO", "A ocorrência não foi destacada no texto original.");
                Require(!rows.Any(r => r.IsPinned || r.IsSelectedForBasket), "Buscar alterou marcações.");
                var orderedMatches = grid.Items.Cast<ItemSearchDisplayRow>().Where(r => r.Description.Contains("AÇÃO")).ToArray();
                Require(ReferenceEquals(grid.SelectedItem, orderedMatches[0]), "A busca não seguiu a ordenação visual.");
                bar.Children.OfType<Button>().Single(b => (string)b.Content == "Anterior").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(ReferenceEquals(grid.SelectedItem, orderedMatches[^1]), "Anterior não retornou ao último resultado.");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(ReferenceEquals(grid.SelectedItem, orderedMatches[0]), "Próximo não retornou ao primeiro resultado.");
                var oldCount = rows.Count;
                rows.Add(LongRow(oldCount + (oldCount % 2)));
                await UntilAsync(() => status.Text.StartsWith($"{(count + 1) / 2 + 1:N0} item"));
                rows.RemoveAt(rows.Count - 1);
                view.Filter = row => ReferenceEquals(row, rows[0]);
                await UntilAsync(() => status.Text.StartsWith("1 item"));
                view.Filter = null;
                query.Text = "ausente";
                await UntilAsync(() => status.Text == "Nenhum item encontrado.");
                query.Text = "";
                Require(status.Text.StartsWith("Digite"), "Busca vazia sem orientação.");
                bar.Children.OfType<Button>().Single(b => (string)b.Content == "Fechar busca").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                if (count == 50)
                {
                    nativeMouse = await CheckPriceMouseAsync(window, grid, rows, reader, () => pinCount, () => basketCount);
                    // A smaller viewport and 150% logical scaling exercise clipping without changing Windows settings.
                    window.Width = 800;
                    window.Height = 520;
                    reader.LayoutTransform = new ScaleTransform(1.5, 1.5);
                    reader.OpenRow(rows[0], true);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Require(grid.ActualHeight > 40 && tabs.ActualHeight <= reader.ActualHeight * 0.4 + 1,
                        "A leitura ocupa a lista inteira em janela pequena/escala aumentada.");
                }
                var p95 = Percentile95(latencies);
                Require(p95 < 100, $"Ctrl+F: dispatcher p95={p95:F2} ms em {count} linhas.");
                results.Add(new { rows = count, realized = containers.Length, findDispatcherP95Ms = p95 });
            }
            finally { window.Close(); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, reading = results, nativeMouse,
            mouseNote = nativeMouse ? "Native mouse passed." : "No interactive foreground desktop; routed gesture checks passed. Native drag/edge scrolling needs an interactive session." }));
    }

    private static ItemSearchDisplayRow LongRow(int index)
    {
        var row = DisplayRow(index).Source;
        return new(row with
        {
            Item = row.Item with { Description = $"descritor {index}\r\n" + new string('x', 2048) + (index % 2 == 0 ? " AÇÃO final" : " outro final") },
            Result = row.Result! with { SupplierTaxId = "00123456000190" }
        });
    }

    private static async Task<bool> CheckPriceMouseAsync(Window window, DataGrid grid,
        RangeObservableCollection<ItemSearchDisplayRow> rows, GridReader reader, Func<int> pins, Func<int> baskets)
    {
        CollectionViewSource.GetDefaultView(rows).SortDescriptions.Clear();
        grid.ScrollIntoView(rows[0]);
        grid.SelectedItem = rows[0];
        // Close the reader so native pointer coordinates match the initial compact list.
        grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid), 0, System.Windows.Input.Key.Escape)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        window.Topmost = true;
        window.Activate();
        SetForegroundWindow(new WindowInteropHelper(window).Handle);
        await Task.Delay(200);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (GetForegroundWindow() != new WindowInteropHelper(window).Handle)
        {
            await CheckRoutedGesturesAsync(grid, rows, reader, pins, baskets);
            return false;
        }
        GetCursorPos(out var previous);
        try
        {
            var first = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(rows[0]);
            var second = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(rows[1]);
            var a = first.PointToScreen(new Point(50, 16));
            var b = second.PointToScreen(new Point(50, 16));
            SetCursorPos((int)a.X, (int)a.Y);
            await ClickAsync();
            Require(ReferenceEquals(grid.SelectedItem, rows[0]) && grid.SelectedItems.Count == 1, "Clique simples não selecionou só uma linha.");
            await Task.Delay(600);
            await ClickAsync();
            await ClickAsync();
            await Task.Delay(600);
            Require(reader.Children.OfType<TabControl>().Single().Visibility == Visibility.Visible && pins() == 0,
                "Duplo clique não abriu a leitura ou fixou o preço.");
            // Close before the next gesture; pointer actions never leave this synthetic window.
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid), 0, System.Windows.Input.Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SetCursorPos((int)a.X, (int)a.Y);
            await ClickAsync(); await ClickAsync(); await ClickAsync();
            await Task.Delay(600);
            Require(pins() == 1 && rows[0].IsPinned, "Triplo clique não fixou exatamente uma vez.");
            Require(reader.Children.OfType<TabControl>().Single().Visibility == Visibility.Collapsed, "Triplo clique também abriu o painel.");
            await ClickAsync(); await ClickAsync(); await ClickAsync();
            await Task.Delay(600);
            Require(rows[0].IsPinned, "Repetir triplo clique removeu a fixação.");
            MouseEvent(0x0008, 0, 0, 0, UIntPtr.Zero); MouseEvent(0x0010, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(80);
            Require(!rows[0].IsRetained, "Clique direito não limpou as marcas.");
            await Task.Delay(600);
            MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(1150);
            MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(60);
            Require(baskets() == 1 && rows[0].IsSelectedForBasket, "Pressão prolongada não marcou uma única vez.");
            await Task.Delay(600);
            MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(60);
            SetCursorPos((int)b.X, (int)b.Y);
            await Task.Delay(1150);
            MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(60);
            Require(grid.SelectedItems.Count >= 2 && baskets() == 1, "Arrastar não selecionou várias linhas ou marcou cesta.");
        }
        finally
        {
            MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(previous.X, previous.Y);
        }
        return true;
    }

    private static async Task CheckRoutedGesturesAsync(DataGrid grid, RangeObservableCollection<ItemSearchDisplayRow> rows,
        GridReader reader, Func<int> pins, Func<int> baskets)
    {
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(rows[0]);
        var cell = VisualChildren(row).OfType<DataGridCell>().First();
        var tabs = reader.Children.OfType<TabControl>().Single();
        void MouseButton(RoutedEvent routed, int count, MouseButton button = System.Windows.Input.MouseButton.Left)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
                { RoutedEvent = routed, Source = cell };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(args, count);
            grid.RaiseEvent(args);
        }
        void Click(int count)
        {
            MouseButton(UIElement.PreviewMouseLeftButtonDownEvent, count);
            MouseButton(UIElement.PreviewMouseLeftButtonUpEvent, count);
        }
        Click(1); Click(2);
        await Task.Delay(650);
        Require(tabs.Visibility == Visibility.Visible && pins() == 0, "Duplo clique roteado não abriu somente a leitura.");
        grid.SelectedItem = rows[1];
        Require(tabs.Visibility == Visibility.Collapsed, "Selecionar outro preço não fechou a leitura anterior.");
        grid.SelectedItem = rows[0];
        Click(1); Click(2); Click(3);
        await Task.Delay(650);
        Require(pins() == 1 && rows[0].IsPinned && tabs.Visibility == Visibility.Collapsed,
            "Triplo clique roteado não cancelou a leitura ou não fixou.");
        Click(1); Click(2); Click(3);
        Require(rows[0].IsPinned, "Triplo clique repetido removeu a fixação.");
        MouseButton(UIElement.PreviewMouseRightButtonDownEvent, 1, System.Windows.Input.MouseButton.Right);
        Require(!rows[0].IsRetained, "Clique direito roteado não limpou as marcas.");
        MouseButton(UIElement.PreviewMouseLeftButtonDownEvent, 1);
        await Task.Delay(1100);
        MouseButton(UIElement.PreviewMouseLeftButtonUpEvent, 1);
        Require(baskets() == 1 && rows[0].IsSelectedForBasket, "Pressão prolongada roteada não marcou uma única vez.");
        MouseButton(UIElement.PreviewMouseLeftButtonDownEvent, 1);
        grid.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.PreviewMouseMoveEvent, Source = cell });
        await Task.Delay(1100);
        MouseButton(UIElement.PreviewMouseLeftButtonUpEvent, 1);
        Require(baskets() == 1, "Mover/soltar o mouse não cancelou a pressão prolongada.");
        MouseButton(UIElement.PreviewMouseLeftButtonDownEvent, 1);
        grid.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseLeaveEvent, Source = grid });
        await Task.Delay(1100);
        Require(baskets() == 1, "Sair da grade não cancelou a pressão prolongada.");
    }

    private static async Task ClickAsync()
    {
        MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(30);
        MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(60);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        var start = Stopwatch.StartNew();
        while (!predicate())
        {
            if (start.Elapsed > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("A operação de interface não terminou em 10 segundos.");
            await Task.Delay(20);
        }
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in VisualChildren(child)) yield return descendant;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
