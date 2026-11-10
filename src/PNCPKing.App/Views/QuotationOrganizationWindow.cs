using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public sealed class QuotationOrganizationWindow : Window
{
    private readonly RadioButton _alphabetical;
    private readonly RadioButton _manual;
    private readonly CheckBox _ignorePrefix;
    private readonly CheckBox _removePrefixes;
    private readonly ObservableCollection<ItemRow> _rows;
    private readonly DataGrid _items = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserSortColumns = false,
        CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = false,
        SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, AllowDrop = true
    };
    private readonly TextBox _position = new() { Width = 70, Margin = new(4), ToolTip = "Posição final do item selecionado (1 até o total de itens)" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
    private bool _manualEdited;
    private ItemRow? _pendingDrag;
    private Point _dragStart;
    private DataGridRow? _dropTarget;
    private bool _dropAfter;
    private long _lastAutoScroll;
    private ScrollViewer? _gridScroll;

    public QuotationOrganizationOptions Options => new(
        _alphabetical.IsChecked == true, _ignorePrefix.IsChecked == true, _removePrefixes.IsChecked == true,
        ManualOnly: _manual.IsChecked == true,
        ItemOrder: _manual.IsChecked == true || (_manualEdited && _alphabetical.IsChecked != true)
            ? _rows.Select(row => row.Line.Id).ToArray() : null);

    public QuotationOrganizationWindow(QuotationProject project, IReadOnlyList<QuotationLine>? lines = null)
    {
        _rows = new((lines ?? []).Select((line, index) => new ItemRow(line, index + 1)));
        SetResourceReference(StyleProperty, "AppWindow");
        Title = "Organizar Itens";
        Width = 800; MinWidth = 360; MinHeight = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MonitorAwareWindowBehavior.Attach(this);

        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var apply = new Button { Content = "Aplicar ao projeto", IsDefault = true };
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(apply);
        buttons.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var panel = new StackPanel();
        panel.Children.Add(Wrapped($"Organizar todos os itens de “{project.Name}”."));
        var manualSelected = project.Organization is null && project.ManualItemOrder && !project.SortItemsAlphabetically;
        _alphabetical = new RadioButton
        {
            Content = Wrapped("Somente ordem alfabética — permite quantidade e unidade em branco, sem cestas confirmadas."),
            GroupName = "OrderMode", IsChecked = project.Organization is null && !manualSelected, Margin = new(0, 10, 0, 8)
        };
        panel.Children.Add(_alphabetical);
        var groups = new RadioButton
        {
            Content = Wrapped("Organizar por grupos e cotas — usa a sequência atual; exige quantidades inteiras positivas e cestas confirmadas."),
            GroupName = "OrderMode", IsChecked = project.Organization is not null, Margin = new(0, 0, 0, 8)
        };
        panel.Children.Add(groups);
        _manual = new RadioButton
        {
            Content = Wrapped("Organizar manualmente — arraste um item ou digite sua posição final, sem exigir cestas confirmadas."),
            GroupName = "OrderMode", IsChecked = manualSelected, Margin = new(0, 0, 0, 12)
        };
        panel.Children.Add(_manual);
        _ignorePrefix = new CheckBox
        {
            Content = Wrapped("Ignorar “Item + número” no início do nome ao ordenar alfabeticamente."),
            IsChecked = project.IgnoreItemNumberPrefix, Margin = new(0, 0, 0, 8)
        };
        panel.Children.Add(_ignorePrefix);
        _removePrefixes = new CheckBox
        {
            Content = Wrapped("Apagar “Item + número” do início dos nomes de TODOS os itens deste projeto."),
            Margin = new(0, 0, 0, 8)
        };
        panel.Children.Add(_removePrefixes);
        panel.Children.Add(Wrapped("Exemplos: “Item 1 Água”, “item 01 Água”, “Item 01 - Água” e “Item 3-Água”. " +
            "Ao apagar, o nome passa a ser “Água”. A numeração automática continuará na planilha Excel."));
        panel.Children.Add(new TextBlock
        {
            Text = "A ordem simples (alfabética ou manual) substitui a numeração de cotas anterior. Os grupos cadastrados continuam disponíveis. " +
                "Grupos e cotas respeitam a sequência dos itens dentro de cada grupo e categoria.",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8)
        });
        var optionsScroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(optionsScroll, Dock.Top);
        root.Children.Add(optionsScroll);

        var manualPanel = new DockPanel();
        var footer = new StackPanel { Margin = new(0, 6, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        manualPanel.Children.Add(footer);
        var actions = new WrapPanel();
        actions.Children.Add(new TextBlock { Text = "Mover para a posição:", VerticalAlignment = VerticalAlignment.Center });
        actions.Children.Add(_position);
        AddButton(actions, "Mover", MoveToPosition);
        AddButton(actions, "Subir", () => MoveSelected(-1));
        AddButton(actions, "Descer", () => MoveSelected(1));
        footer.Children.Add(actions);
        footer.Children.Add(_status);
        _items.ItemsSource = _rows;
        VirtualizingPanel.SetVirtualizationMode(_items, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_items, true);
        _items.Columns.Add(new DataGridTextColumn { Header = "Posição", Binding = new Binding(nameof(ItemRow.Position)), Width = 75 });
        var nameStyle = new Style(typeof(TextBlock));
        nameStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        nameStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(ItemRow.Name))));
        _items.Columns.Add(new DataGridTextColumn { Header = "Item", Binding = new Binding(nameof(ItemRow.Name)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = nameStyle });
        _items.SelectionChanged += (_, _) =>
        {
            if (_items.SelectedItem is ItemRow row) _position.Text = row.Position.ToString();
        };
        _position.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true; // Enter moves the item; it must not apply/close the entire dialog.
            MoveToPosition();
        };
        _items.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pendingDrag = RowAt(e.OriginalSource)?.Item as ItemRow;
            _dragStart = e.GetPosition(_items);
        };
        _items.PreviewMouseLeftButtonUp += (_, _) => _pendingDrag = null;
        _items.PreviewMouseMove += OnMouseMove;
        _items.DragOver += OnDragOver;
        _items.DragLeave += (_, _) => ClearDropTarget();
        _items.Drop += OnDrop;
        manualPanel.Children.Add(_items);
        root.Children.Add(manualPanel);
        Content = root;

        void ResizeOptions() => optionsScroll.MaxHeight = _manual.IsChecked == true
            ? Math.Max(100, ActualHeight * 0.4) : double.PositiveInfinity;
        void UpdateMode()
        {
            var manual = _manual.IsChecked == true;
            manualPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
            _ignorePrefix.IsEnabled = _alphabetical.IsChecked == true;
            if (manual)
            {
                SizeToContent = SizeToContent.Manual;
                Height = Math.Min(720, SystemParameters.WorkArea.Height);
                _status.Text = $"{_rows.Count} itens. As alterações só serão salvas ao aplicar ao projeto.";
            }
            else SizeToContent = SizeToContent.Height;
            ResizeOptions();
        }
        _manual.Checked += (_, _) => UpdateMode();
        _alphabetical.Checked += (_, _) => UpdateMode();
        groups.Checked += (_, _) => UpdateMode();
        SizeChanged += (_, _) => ResizeOptions();
        UpdateMode();
    }

    private void MoveToPosition()
    {
        if (!int.TryParse(_position.Text.Trim(), out var position) || position < 1 || position > _rows.Count)
        {
            _status.Text = $"Informe uma posição inteira de 1 a {_rows.Count}.";
            return;
        }
        if (_items.SelectedItem is ItemRow row) MoveRow(row, position - 1);
        else _status.Text = "Selecione o item que deseja mover.";
    }

    private void MoveSelected(int offset)
    {
        if (_items.SelectedItem is ItemRow row) MoveRow(row, Math.Clamp(row.Position - 1 + offset, 0, _rows.Count - 1));
        else _status.Text = "Selecione o item que deseja mover.";
    }

    private void MoveRow(ItemRow row, int to)
    {
        var from = _rows.IndexOf(row);
        if (from < 0 || from == to) return;
        _rows.Move(from, to);
        for (var index = Math.Min(from, to); index <= Math.Max(from, to); index++) _rows[index].Position = index + 1;
        _manualEdited = true;
        _items.SelectedItem = row;
        _items.ScrollIntoView(row);
        _position.Text = row.Position.ToString();
        _status.Text = $"Item movido da posição {from + 1} para {to + 1}. Clique em Aplicar ao projeto para salvar.";
    }

    private DataGridRow? RowAt(object? source) => source is DependencyObject element
        ? ItemsControl.ContainerFromElement(_items, element) as DataGridRow : null;

    private ItemRow? DraggedRow(IDataObject data) => data.GetDataPresent(typeof(ItemRow)) &&
        data.GetData(typeof(ItemRow)) is ItemRow row && _rows.Contains(row) ? row : null;

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDrag is not { } row || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(_items);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pendingDrag = null;
        try { DragDrop.DoDragDrop(_items, new DataObject(typeof(ItemRow), row), DragDropEffects.Move); }
        finally { ClearDropTarget(); }
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DraggedRow(e.Data) is null || RowAt(e.OriginalSource) is not { } target)
        {
            ClearDropTarget(); e.Effects = DragDropEffects.None; return;
        }
        ClearDropTarget();
        _dropTarget = target;
        _dropAfter = e.GetPosition(target).Y >= target.ActualHeight / 2;
        target.BorderBrush = SystemColors.HighlightBrush;
        target.BorderThickness = _dropAfter ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
        e.Effects = DragDropEffects.Move;
        var position = e.GetPosition(_items);
        if ((position.Y < 40 || position.Y > _items.ActualHeight - 30) &&
            Stopwatch.GetElapsedTime(_lastAutoScroll).TotalMilliseconds >= 100)
        {
            _gridScroll ??= FindScrollViewer(_items);
            if (position.Y < 40) _gridScroll?.LineUp(); else _gridScroll?.LineDown();
            _lastAutoScroll = Stopwatch.GetTimestamp();
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var indicated = _dropTarget;
        var after = _dropAfter;
        ClearDropTarget();
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (DraggedRow(e.Data) is not { } row || RowAt(e.OriginalSource) is not { Item: ItemRow targetRow } target) return;
        if (!ReferenceEquals(indicated, target)) after = e.GetPosition(target).Y >= target.ActualHeight / 2;
        var insertAt = _rows.IndexOf(targetRow) + (after ? 1 : 0);
        if (insertAt > _rows.IndexOf(row)) insertAt--;
        MoveRow(row, Math.Clamp(insertAt, 0, _rows.Count - 1));
        e.Effects = DragDropEffects.Move;
    }

    private void ClearDropTarget()
    {
        _dropTarget?.ClearValue(BorderBrushProperty);
        _dropTarget?.ClearValue(BorderThicknessProperty);
        _dropTarget = null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        return null;
    }

    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new(4) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    public sealed class ItemRow(QuotationLine line, int position) : ObservableObject
    {
        public QuotationLine Line { get; } = line;
        public string Name => Line.EffectiveDisplayName;
        private int _position = position;
        public int Position { get => _position; set => SetProperty(ref _position, value); }
    }
}
