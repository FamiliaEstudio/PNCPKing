using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;

namespace PNCPKing.App.Views;

public partial class ColumnChooserWindow : Window
{
    private bool _updatingPosition;
    private ColumnChooserRow? _pendingDrag;
    private Point _dragStart;
    private ListBoxItem? _dropTarget;
    private bool _dropAfter;
    private long _lastAutoScroll;

    public ColumnChooserWindow(IEnumerable<ColumnChooserRow> rows)
    {
        InitializeComponent();
        MonitorAwareWindowBehavior.Attach(this);
        Rows = new ObservableCollection<ColumnChooserRow>(rows);
        Positions = Enumerable.Range(1, Rows.Count).ToArray();
        UpdatePositions();
        DataContext = this;
    }

    public ObservableCollection<ColumnChooserRow> Rows { get; }
    public IReadOnlyList<int> Positions { get; }

    public event EventHandler<IReadOnlyList<ColumnChooserRow>>? ApplyRequested;
    public event EventHandler? ResetRequested;

    private void ColumnsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _updatingPosition = true;
        try
        {
            PositionComboBox.IsEnabled = ColumnsList.SelectedItem is ColumnChooserRow;
            PositionComboBox.SelectedIndex = ColumnsList.SelectedItem is ColumnChooserRow row
                ? Rows.IndexOf(row)
                : -1;
        }
        finally
        {
            _updatingPosition = false;
        }
    }

    private void PositionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPosition || ColumnsList.SelectedItem is not ColumnChooserRow row ||
            PositionComboBox.SelectedIndex < 0) return;

        var from = Rows.IndexOf(row);
        var to = PositionComboBox.SelectedIndex;
        if (from != to) MoveRow(row, to);
    }

    private void ColumnsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pendingDrag = null;
        if (e.OriginalSource is not DependencyObject source) return;
        for (var current = source; current is not null && !ReferenceEquals(current, ColumnsList);
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is CheckBox) return;
        }

        var item = ItemsControl.ContainerFromElement(ColumnsList, source) as ListBoxItem;
        _pendingDrag = item?.DataContext as ColumnChooserRow;
        _dragStart = e.GetPosition(ColumnsList);
    }

    private void ColumnsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _pendingDrag = null;

    private void ColumnsList_MouseLeave(object sender, MouseEventArgs e) => _pendingDrag = null;

    private void ColumnsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDrag is null || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(ColumnsList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var row = _pendingDrag;
        _pendingDrag = null;
        try
        {
            DragDrop.DoDragDrop(ColumnsList, new DataObject(typeof(ColumnChooserRow), row), DragDropEffects.Move);
        }
        finally
        {
            ClearDropTarget();
        }
        e.Handled = true;
    }

    private void ColumnsList_DragOver(object sender, DragEventArgs e)
    {
        if (DraggedRow(e.Data) is null ||
            ItemsControl.ContainerFromElement(ColumnsList, e.OriginalSource as DependencyObject) is not ListBoxItem target)
        {
            ClearDropTarget();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(ColumnsList);
        if (position.Y < 24 || position.Y > ColumnsList.ActualHeight - 24)
        {
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastAutoScroll).TotalMilliseconds >= 100 &&
                ColumnsList.Template.FindName("PART_ScrollViewer", ColumnsList) is ScrollViewer scroll)
            {
                if (position.Y < 24) scroll.LineUp();
                else scroll.LineDown();
                _lastAutoScroll = now;
            }
        }

        ShowDropTarget(target, e.GetPosition(target).Y >= target.ActualHeight / 2);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ColumnsList_DragLeave(object sender, DragEventArgs e) => ClearDropTarget();

    private void ColumnsList_Drop(object sender, DragEventArgs e)
    {
        var indicatedTarget = _dropTarget;
        var indicatedAfter = _dropAfter;
        ClearDropTarget();
        if (DraggedRow(e.Data) is not { } row ||
            ItemsControl.ContainerFromElement(ColumnsList, e.OriginalSource as DependencyObject) is not ListBoxItem target ||
            target.DataContext is not ColumnChooserRow targetRow) return;

        var after = ReferenceEquals(indicatedTarget, target)
            ? indicatedAfter
            : e.GetPosition(target).Y >= target.ActualHeight / 2;
        var insertAt = Rows.IndexOf(targetRow) + (after ? 1 : 0);
        if (insertAt > Rows.IndexOf(row)) insertAt--;
        MoveRow(row, Math.Clamp(insertAt, 0, Rows.Count - 1));
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private ColumnChooserRow? DraggedRow(IDataObject data) =>
        data.GetDataPresent(typeof(ColumnChooserRow)) &&
        data.GetData(typeof(ColumnChooserRow)) is ColumnChooserRow row && Rows.Contains(row) ? row : null;

    private void ShowDropTarget(ListBoxItem target, bool after)
    {
        if (ReferenceEquals(_dropTarget, target) && _dropAfter == after) return;
        ClearDropTarget();
        _dropTarget = target;
        _dropAfter = after;
        target.BorderBrush = SystemColors.HighlightBrush;
        target.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private void ClearDropTarget()
    {
        if (_dropTarget is null) return;
        _dropTarget.ClearValue(Control.BorderBrushProperty);
        _dropTarget.ClearValue(Control.BorderThicknessProperty);
        _dropTarget = null;
    }

    private void MoveRow(ColumnChooserRow row, int to)
    {
        var from = Rows.IndexOf(row);
        if (from < 0 || from == to) return;
        _updatingPosition = true;
        try
        {
            Rows.Move(from, to);
            ColumnsList.SelectedItem = row;
            PositionComboBox.SelectedIndex = to;
            UpdatePositions();
            ColumnsList.ScrollIntoView(row);
        }
        finally
        {
            _updatingPosition = false;
        }
    }

    private void ColumnCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is CheckBox { DataContext: ColumnChooserRow row }) ColumnsList.SelectedItem = row;
    }

    private void UpdatePositions()
    {
        for (var index = 0; index < Rows.Count; index++) Rows[index].Position = index + 1;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResetRequested?.Invoke(this, EventArgs.Empty);
            Close();
        }
        catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
        {
            AsyncCommandRuntime.Handle(exception);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyRequested?.Invoke(this, Rows.ToArray());
            Close();
        }
        catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
        {
            AsyncCommandRuntime.Handle(exception);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class ColumnChooserRow(
    string key,
    string header,
    bool isVisible) : INotifyPropertyChanged
{
    private bool _isVisible = isVisible;
    private int _position;

    public string Key { get; } = key;
    public string Header { get; } = header;

    public int Position
    {
        get => _position;
        set
        {
            if (_position == value) return;
            _position = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
