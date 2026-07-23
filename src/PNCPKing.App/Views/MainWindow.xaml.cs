using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

}
