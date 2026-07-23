using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private void ChooseColumns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DataGrid dataGrid } button)
        {
            return;
        }

        DataGridColumnChooser.Show(button, dataGrid);
    }

    private void QueryTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SweetCodePopup.IsOpen || SweetCodeList.Items.Count == 0)
        {
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            var direction = e.Key == Key.Down ? 1 : -1;
            var current = SweetCodeList.SelectedIndex < 0 ? 0 : SweetCodeList.SelectedIndex;
            SweetCodeList.SelectedIndex = Math.Clamp(current + direction, 0, SweetCodeList.Items.Count - 1);
            SweetCodeList.ScrollIntoView(SweetCodeList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            _viewModel.ApplySelectedSweetCode();
            QueryTextBox.CaretIndex = QueryTextBox.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.DismissSweetCodeSuggestions();
            e.Handled = true;
        }
    }

    private void SweetCodeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ApplySelectedSweetCode();
        QueryTextBox.Focus();
        QueryTextBox.CaretIndex = QueryTextBox.Text.Length;
    }

}
