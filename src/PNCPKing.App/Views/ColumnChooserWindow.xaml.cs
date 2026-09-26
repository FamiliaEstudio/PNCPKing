using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;

namespace PNCPKing.App.Views;

public partial class ColumnChooserWindow : Window
{
    private bool _updatingPosition;

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
        if (from == to) return;
        _updatingPosition = true;
        try
        {
            Rows.Move(from, to);
            ColumnsList.SelectedItem = row;
            PositionComboBox.SelectedIndex = to;
            UpdatePositions();
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
