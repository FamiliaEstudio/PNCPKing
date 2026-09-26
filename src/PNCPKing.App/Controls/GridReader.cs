using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using PNCPKing.App.Services;

namespace PNCPKing.App.Controls;

/// <summary>A virtualized table with on-demand reading, local find and clipboard support.</summary>
[ContentProperty(nameof(Table))]
public sealed class GridReader : Grid
{
    private readonly WrapPanel _findBar = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _query = new() { Width = 210, ToolTip = "Buscar nos descritivos já carregados (Ctrl+F)" };
    private readonly TextBlock _findStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _selectionCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TabControl _reader = new() { Visibility = Visibility.Collapsed, Height = 190 };
    private readonly TextBox _description = ReadOnlyText();
    private readonly TextBox _details = ReadOnlyText();
    private readonly DispatcherTimer _findTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _readTimer = new() { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<Match> _matches = [];
    private DataGrid _table = null!;
    private CancellationTokenSource? _findCancellation;
    private Task _findTask = Task.CompletedTask;
    private object? _openedRow;
    private object? _pendingRead;
    private object? _pressedRow;
    private object? _lastClickedRow;
    private Point _pressOrigin;
    private int _clickCount;
    private int _matchIndex = -1;
    private long _searchVersion;
    private long _lastFocus;
    private bool _held;
    private bool _loaded;
    private bool _copying;
    private Window? _window;

    public string DescriptionPath { get; set; } = "Description";
    public bool EnableFind { get; set; } = true;
    public bool OpenOnDoubleClick { get; set; } = true;
    public Action<object>? PinRequested { get; set; }
    public Action<object>? BasketSelectionRequested { get; set; }
    public Action<object>? ClearRequested { get; set; }

    public GridReader()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        _findBar.Children.Add(_query);
        _findBar.Children.Add(Button("Anterior", () => Navigate(-1)));
        _findBar.Children.Add(Button("Próximo", () => Navigate(1)));
        _findBar.Children.Add(_findStatus);
        _findBar.Children.Add(Button("Fechar busca", CloseFind));
        Children.Add(_findBar);
        var footer = new DockPanel();
        var read = Button("Ler/copiar dados", () => OpenRow(Table.SelectedItem, false));
        DockPanel.SetDock(read, Dock.Left);
        footer.Children.Add(read);
        DockPanel.SetDock(_selectionCount, Dock.Right);
        footer.Children.Add(_selectionCount);
        footer.Children.Add(_status);
        SetRow(footer, 2);
        Children.Add(footer);
        _reader.Items.Add(new TabItem { Header = "Descrição", Content = _description });
        _reader.Items.Add(new TabItem { Header = "Dados da linha", Content = _details });
        SetRow(_reader, 3);
        Children.Add(_reader);
        _query.TextChanged += (_, _) => ScheduleFind(true);
        _findTimer.Tick += async (_, _) => { _findTimer.Stop(); await (_findTask = FindAsync()); };
        _readTimer.Tick += (_, _) =>
        {
            var row = _pendingRead;
            CancelRead();
            OpenRow(row, true);
        };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            // The press is cleared on release, drag, leave, unload and window deactivation.
            if (_pressedRow is null) return;
            _held = true;
            BasketSelectionRequested?.Invoke(_pressedRow);
        };
        PreviewKeyDown += OnKeyDown;
        GotKeyboardFocus += (_, _) => _lastFocus = Stopwatch.GetTimestamp();
        AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(OnCopy));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => _reader.MaxHeight = Math.Max(0, ActualHeight * 0.4);
    }

    public DataGrid Table
    {
        get => _table;
        set
        {
            if (_table is not null) throw new InvalidOperationException("A grade já foi atribuída.");
            _table = value;
            SetRow(value, 1);
            value.RowHeight = 32;
            value.CanUserResizeRows = false;
            value.EnableRowVirtualization = true;
            value.EnableColumnVirtualization = true;
            value.ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader;
            VirtualizingPanel.SetIsVirtualizing(value, true);
            VirtualizingPanel.SetVirtualizationMode(value, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(value, true);
            var buttonStyle = new Style(typeof(Button), value.TryFindResource(typeof(Button)) as Style);
            buttonStyle.Setters.Add(new Setter(Control.MinHeightProperty, 24d));
            buttonStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2)));
            buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 2, 5, 2)));
            value.Resources[typeof(Button)] = buttonStyle;
            foreach (var column in value.Columns.OfType<DataGridTextColumn>())
            {
                var style = new Style(typeof(TextBlock), column.ElementStyle);
                style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
                style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
                column.ElementStyle = style;
                if (column.Binding is not Binding binding || binding.Converter is not null) continue;
                column.ClipboardContentBinding = binding;
                if (string.IsNullOrEmpty(column.SortMemberPath)) column.SortMemberPath = binding.Path.Path;
                column.Binding = new Binding(binding.Path.Path)
                {
                    Mode = BindingMode.OneWay, StringFormat = binding.StringFormat,
                    ConverterCulture = binding.ConverterCulture, Converter = SingleLineConverter.Instance
                };
            }
            var rowStyle = new Style(typeof(DataGridRow), value.RowStyle);
            var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.HighlightBrush));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.HighlightTextBrush));
            rowStyle.Triggers.Add(selected);
            value.RowStyle = rowStyle;
            value.PreviewMouseLeftButtonDown += OnLeftDown;
            value.PreviewMouseLeftButtonUp += OnLeftUp;
            value.PreviewMouseMove += OnMouseMove;
            value.PreviewMouseRightButtonDown += OnRightDown;
            value.MouseLeave += (_, _) => CancelGestures();
            value.LostMouseCapture += (_, _) =>
            {
                if (Mouse.LeftButton == MouseButtonState.Pressed) CancelGestures();
            };
            value.SelectionChanged += (_, _) =>
            {
                _selectionCount.Text = $"Selecionados: {value.SelectedItems.Count:N0}";
                if (_openedRow is not null && !ReferenceEquals(_openedRow, value.SelectedItem)) CloseReader();
            };
            Children.Add(value);
        }
    }

    public void OpenFind()
    {
        if (!EnableFind) return;
        _findBar.Visibility = Visibility.Visible;
        _query.Focus();
        _query.SelectAll();
        ScheduleFind(true);
    }

    public static void OpenFindIn(Window window)
    {
        var readers = Descendants(window).OfType<GridReader>().Where(r => r.IsVisible && r.EnableFind).ToArray();
        (readers.FirstOrDefault(r => r.IsKeyboardFocusWithin) ?? readers.MaxBy(r => r._lastFocus))?.OpenFind();
    }

    public void OpenRow(object? row, bool description)
    {
        if (row is null || !Table.Items.Contains(row)) return;
        _openedRow = row;
        _description.Text = Description(row);
        _details.Text = string.Join(Environment.NewLine, Columns(false)
            .Where(c => c.Path != DescriptionPath).Select(c => $"{c.Header}: {c.Text(row)}"));
        _reader.SelectedIndex = description && _description.Text.Length > 0 ? 0 : 1;
        _reader.Visibility = Visibility.Visible;
        _description.ScrollToHome();
        _details.ScrollToHome();
        _status.Text = "Esc fecha a leitura. Selecione um trecho e use Ctrl+C.";
    }

    private void CloseReader()
    {
        _reader.Visibility = Visibility.Collapsed;
        _openedRow = null;
        _description.Clear();
        _details.Clear();
        _status.Text = string.Empty;
    }

    private void CloseFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        CancelFind();
        _matches.Clear();
        _matchIndex = -1;
        Table.Focus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        ((INotifyCollectionChanged)Table.Items).CollectionChanged += OnItemsChanged;
        _window = Window.GetWindow(this);
        if (_window is not null) _window.Deactivated += OnDeactivated;
        ScheduleFind(false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _loaded = false;
        ((INotifyCollectionChanged)Table.Items).CollectionChanged -= OnItemsChanged;
        if (_window is not null) _window.Deactivated -= OnDeactivated;
        _window = null;
        CancelGestures();
        CancelFind();
        CloseReader();
    }

    private void OnDeactivated(object? sender, EventArgs e) => CancelGestures();

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_openedRow is not null && !Table.Items.Contains(_openedRow)) CloseReader();
        if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
            CancelGestures();
        ScheduleFind(false);
    }

    private string Description(object row) => GridText.Read(row, DescriptionPath)?.ToString() ?? string.Empty;

    private void ScheduleFind(bool restart)
    {
        if (_findBar.Visibility != Visibility.Visible) return;
        _searchVersion++;
        _findCancellation?.Cancel();
        if (restart) _findTimer.Stop();
        _findTimer.Start();
        _findStatus.Text = string.IsNullOrWhiteSpace(_query.Text) ? "Digite para buscar nesta lista." : "Buscando…";
    }

    private void CancelFind()
    {
        _searchVersion++;
        _findTimer.Stop();
        _findCancellation?.Cancel();
    }

    private async Task FindAsync()
    {
        var term = _query.Text.Trim();
        var version = ++_searchVersion;
        var previous = _matchIndex >= 0 && _matchIndex < _matches.Count ? _matches[_matchIndex].Row : null;
        _matches.Clear();
        _matchIndex = -1;
        if (term.Length == 0) return;
        _findCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _findCancellation = cancellation;
        var token = cancellation.Token;
        // Only immutable string references and row identities cross the worker boundary.
        var rows = Table.Items.Cast<object>().Select(row => (Row: row, Text: Description(row))).ToArray();
        try
        {
            var found = await Task.Run(() =>
            {
                var matches = new List<Match>();
                foreach (var (row, text) in rows)
                {
                    token.ThrowIfCancellationRequested();
                    var (start, length) = GridText.Find(text, term);
                    if (start >= 0) matches.Add(new(row, start, length));
                }
                return matches;
            }, token);
            if (version != _searchVersion || !_loaded) return;
            _matches.AddRange(found);
            _matchIndex = previous is null ? -1 : _matches.FindIndex(m => ReferenceEquals(m.Row, previous));
            _findStatus.Text = _matches.Count == 0 ? "Nenhum item encontrado." : $"{_matches.Count:N0} item(ns) encontrado(s). Enter para navegar.";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_findCancellation, cancellation)) _findCancellation = null;
        }
    }

    private async void Navigate(int direction)
    {
        if (_findTimer.IsEnabled)
        {
            _findTimer.Stop();
            _findTask = FindAsync();
        }
        await _findTask;
        if (_matches.Count == 0 || _findCancellation is not null) return;
        _matchIndex = _matchIndex < 0 ? (direction > 0 ? 0 : _matches.Count - 1)
            : (_matchIndex + direction + _matches.Count) % _matches.Count;
        var match = _matches[_matchIndex];
        Table.SelectedItem = match.Row;
        OpenRow(match.Row, true);
        Table.ScrollIntoView(match.Row);
        _description.Select(match.Start, match.Length);
        await Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_openedRow, match.Row)) return;
            _description.ScrollToLine(Math.Max(0, _description.GetLineIndexFromCharacterIndex(match.Start)));
        }, DispatcherPriority.Loaded);
        _findStatus.Text = $"{_matchIndex + 1:N0} de {_matches.Count:N0} item(ns)";
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && EnableFind)
        { OpenFind(); e.Handled = true; }
        else if (EnableFind && (e.Key == Key.F3 || e.Key == Key.Enter && _query.IsKeyboardFocused))
        {
            if (_findBar.Visibility != Visibility.Visible) OpenFind();
            else Navigate(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelGestures();
            if (_findBar.Visibility == Visibility.Visible) CloseFind();
            else { CloseReader(); Table.Focus(); }
            e.Handled = true;
        }
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        CancelGestures();
        var row = RowUnderPointer(e.OriginalSource as DependencyObject);
        if (row is null) return;
        if (!ReferenceEquals(_openedRow, row)) CloseReader();
        _pressedRow = row;
        _pressOrigin = e.GetPosition(Table);
        _clickCount = e.ClickCount == 1 || !ReferenceEquals(_lastClickedRow, row) ? 1 : _clickCount + 1;
        _lastClickedRow = row;
        _held = false;
        if (_clickCount == 1 && BasketSelectionRequested is not null) _holdTimer.Start();
        if (_clickCount >= 2 && OpenOnDoubleClick) e.Handled = true;
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        _holdTimer.Stop();
        var row = _pressedRow;
        _pressedRow = null;
        if (row is null) return;
        if (_held) { e.Handled = true; return; }
        if (_clickCount == 3 && PinRequested is not null)
        { PinRequested(row); e.Handled = true; }
        else if (_clickCount == 2 && OpenOnDoubleClick)
        {
            _pendingRead = row;
            _readTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
            _readTimer.Start();
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedRow is null) return;
        var position = e.GetPosition(Table);
        if (e.LeftButton != MouseButtonState.Pressed ||
            Math.Abs(position.X - _pressOrigin.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(position.Y - _pressOrigin.Y) > SystemParameters.MinimumVerticalDragDistance)
            CancelGestures(); // Leave WPF's native drag selection and edge scrolling in control.
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        CancelGestures();
        if (ClearRequested is null) return;
        var row = RowUnderPointer(e.OriginalSource as DependencyObject);
        if (row is null) return;
        ClearRequested(row);
        e.Handled = true;
    }

    private void CancelRead() { _readTimer.Stop(); _pendingRead = null; }
    private void CancelGestures() { _holdTimer.Stop(); _pressedRow = null; CancelRead(); }

    internal static object? RowUnderPointer(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or ScrollBar or DataGridColumnHeader or GridReader) return null;
            if (source is DataGridRow row) return row.Item;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private Column[] Columns(bool visibleOnly) => Table.Columns
        .Where(c => !visibleOnly || c.Visibility == Visibility.Visible)
        .OrderBy(c => c.DisplayIndex)
        .Select(c => (Column: c, Binding: c.ClipboardContentBinding as Binding))
        .Where(c => c.Binding?.Path is not null)
        .Select(c => new Column(c.Column.Header?.ToString() ?? string.Empty, c.Binding!.Path.Path,
            c.Binding.StringFormat, c.Binding.ConverterCulture ?? CultureInfo.CurrentCulture)).ToArray();

    private async void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Copy) return;
        e.Handled = true;
        if (_copying) return;
        try
        {
            _copying = true;
            string tabular;
            string html;
            if (Keyboard.FocusedElement is TextBox text)
            {
                if (text.SelectionLength == 0) return;
                tabular = text.SelectedText;
                html = GridText.ClipboardHtml([[tabular]], [true]);
            }
            else
            {
                var selected = Table.SelectedItems.Cast<object>().ToHashSet(ReferenceEqualityComparer.Instance);
                if (selected.Count == 0) return;
                var rows = Table.Items.Cast<object>().Where(selected.Contains).ToArray();
                var columns = Columns(true);
                _status.Text = "Preparando cópia…";
                (tabular, html) = await Task.Run(() =>
                {
                    var values = rows.Select(row => columns.Select(c => c.Text(row)).ToArray()).ToArray();
                    return (GridText.Tabular(values), GridText.ClipboardHtml(values,
                        columns.Select(c => c.Path.Contains("TaxId", StringComparison.OrdinalIgnoreCase) ||
                                            c.Path.Contains("Cnpj", StringComparison.OrdinalIgnoreCase)).ToArray()));
                });
            }
            // OLE retries when another application owns the clipboard. Keep those waits off the dispatcher.
            _status.Text = "Preparando cópia…";
            await SetClipboardAsync(tabular, html);
            _status.Text = "Copiado. Use Ctrl+V no aplicativo de destino.";
        }
        catch (ExternalException)
        { _status.Text = "Área de transferência ocupada. Tente copiar novamente."; }
        finally { _copying = false; }
    }

    private static Task SetClipboardAsync(string text, string html)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                data.SetData(DataFormats.Html, html);
                Clipboard.SetDataObject(data, true);
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "PNCP King — copiar texto" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static TextBox ReadOnlyText() => new()
    {
        IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true,
        IsUndoEnabled = false, IsInactiveSelectionHighlightEnabled = true
    };

    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, MinHeight = 24, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(2) };
        button.Click += (_, _) => action();
        return button;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            if (child is GridReader) continue;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
    private sealed record Match(object Row, int Start, int Length);
    private sealed record Column(string Header, string Path, string? Format, CultureInfo Culture)
    { public string Text(object row) => GridText.Format(GridText.Read(row, Path), Format, Culture); }
    private sealed class SingleLineConverter : IValueConverter
    {
        public static readonly SingleLineConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string text ? GridText.SingleLine(text) : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
