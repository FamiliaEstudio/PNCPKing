using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.ViewModels;

namespace PNCPKing.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private int _prefetchedFromPage;
    private int _highestLoadedRowIndex = -1;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
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

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        base.OnClosed(e);
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentItemPage) && _viewModel.CurrentItemPage == 0)
        {
            _prefetchedFromPage = 0;
            _highestLoadedRowIndex = -1;
        }

        if (e.PropertyName == nameof(MainViewModel.IsPriceBusy) && !_viewModel.IsPriceBusy)
        {
            TryPrefetchNextPage();
        }
    }

    private void ItemResultsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        _highestLoadedRowIndex = Math.Max(_highestLoadedRowIndex, e.Row.GetIndex());
        TryPrefetchNextPage();
    }

    private void TryPrefetchNextPage()
    {
        if (_viewModel.CurrentItemPage <= 0 ||
            _highestLoadedRowIndex < Math.Max(0, _viewModel.ItemSearchRows.Count - 10) ||
            _prefetchedFromPage >= _viewModel.CurrentItemPage ||
            !_viewModel.LoadNextItemPageCommand.CanExecute(null))
        {
            return;
        }

        _prefetchedFromPage = _viewModel.CurrentItemPage;
        _viewModel.LoadNextItemPageCommand.Execute(null);
    }
}
