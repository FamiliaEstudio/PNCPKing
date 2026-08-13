using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using PNCPKing.App.ViewModels;

namespace PNCPKing.App.Views;

public partial class ColumnChooserWindow : Window
{
    public ColumnChooserWindow(IEnumerable<ColumnChooserRow> rows)
    {
        InitializeComponent();
        Rows = new ObservableCollection<ColumnChooserRow>(rows);
        DataContext = this;
    }

    public ObservableCollection<ColumnChooserRow> Rows { get; }

    public event EventHandler<IReadOnlyDictionary<string, bool>>? ApplyRequested;

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows)
        {
            row.IsVisible = row.IsDefaultVisible;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyRequested?.Invoke(
                this,
                Rows.ToDictionary(row => row.Key, row => row.IsVisible, StringComparer.Ordinal));
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
    bool isVisible,
    bool isDefaultVisible) : INotifyPropertyChanged
{
    private bool _isVisible = isVisible;

    public string Key { get; } = key;
    public string Header { get; } = header;
    public bool IsDefaultVisible { get; } = isDefaultVisible;

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
