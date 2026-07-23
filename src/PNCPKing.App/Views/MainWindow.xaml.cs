using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PNCPKing.App.ViewModels;

namespace PNCPKing.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        IsEnabled = false;
        try
        {
            await _viewModel.ShutdownAsync().ConfigureAwait(true);
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private void ChooseItemColumns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Top,
            StaysOpen = true
        };
        foreach (var column in ItemResultsGrid.Columns)
        {
            var item = new MenuItem
            {
                Header = column.Header?.ToString() ?? "Coluna",
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
                Tag = column
            };
            item.Click += (_, _) =>
            {
                if (item.Tag is DataGridColumn selectedColumn)
                {
                    selectedColumn.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                }
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

}
