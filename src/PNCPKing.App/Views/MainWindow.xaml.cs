using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DataGridColumnLayoutService _columnLayouts;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;

    public MainWindow(MainViewModel viewModel, DataGridColumnLayoutService columnLayouts)
    {
        _viewModel = viewModel;
        _columnLayouts = columnLayouts;
        InitializeComponent();
        DataContext = viewModel;
        _columnLayouts.Register("item-results", ItemResultsGrid);
        _columnLayouts.Register("quotation-lines", QuotationLinesGrid);
        _columnLayouts.Register("quotation-baskets", QuotationBasketsGrid);
        _columnLayouts.Register("quotation-selected-references", SelectedBasketReferencesGrid);
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

        _columnLayouts.ShowChooser(button, dataGrid);
    }

    private void CatalogDictionary_Click(object sender, RoutedEventArgs e) =>
        _viewModel.OpenCatalogDictionary();

    private void QuotationLinesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        _viewModel.RenameQuotationLineCommand.Execute(null);
        e.Handled = true;
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

    private async void CreateManualBasket_Click(object sender, RoutedEventArgs e)
    {
        var selected = ItemResultsGrid.SelectedItems
            .OfType<ItemSearchDisplayRow>()
            .ToArray();
        await _viewModel.CreateOrAppendManualBasketAsync(selected).ConfigureAwait(true);
    }

    private void QuotationLinesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QuotationLinesGrid.SelectedItem is QuotationLineDisplay)
        {
            _viewModel.OpenSelectedQuotationItem();
        }
    }

    private void MainScopeInBasket_Click(object sender, RoutedEventArgs e) =>
        _viewModel.QuotationReferenceScope = ReferenceViewScope.InBasket;

    private void MainScopeOutside_Click(object sender, RoutedEventArgs e) =>
        _viewModel.QuotationReferenceScope = ReferenceViewScope.EligibleOutsideBasket;

    private void MainScopeRejected_Click(object sender, RoutedEventArgs e) =>
        _viewModel.QuotationReferenceScope = ReferenceViewScope.RejectedOrDuplicate;

    private void MainScopeAll_Click(object sender, RoutedEventArgs e) =>
        _viewModel.QuotationReferenceScope = ReferenceViewScope.All;

    private void MainReferences_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridRow)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        if (source is DataGridRow row)
        {
            SelectedBasketReferencesGrid.SelectedItem = row.Item;
            row.IsSelected = true;
        }
    }

    private void MainReferences_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = _viewModel.SelectedVisibleQuotationReference;
        if (row is not null)
        {
            _viewModel.OpenSelectedQuotationReferenceDocuments(row.Id);
        }
    }

    private void MainReferenceActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: QuotationPriceDisplayRow row } button)
        {
            _viewModel.SelectedVisibleQuotationReference = row;
            MainReferenceContextMenu.PlacementTarget = button;
            MainReferenceContextMenu.Placement =
                System.Windows.Controls.Primitives.PlacementMode.Bottom;
            MainReferenceContextMenu.IsOpen = true;
        }
    }

    private void MainReferenceOpen_Click(object sender, RoutedEventArgs e)
    {
        var row = _viewModel.SelectedVisibleQuotationReference;
        if (row is not null)
        {
            _viewModel.OpenQuotationReferenceCommand.Execute(row.ReferenceDisplay);
        }
    }

    private void MainReferenceDocuments_Click(object sender, RoutedEventArgs e)
    {
        var row = _viewModel.SelectedVisibleQuotationReference;
        if (row is not null)
        {
            _viewModel.AccessQuotationDocumentsCommand.Execute(row.ReferenceDisplay);
        }
    }

    private void MainReferenceRelevant_Click(object sender, RoutedEventArgs e)
    {
        var row = _viewModel.SelectedVisibleQuotationReference;
        if (row is not null)
        {
            _viewModel.OpenSelectedQuotationReferenceDocuments(row.Id);
        }
    }

    private async void MainReferenceToggle_Click(object sender, RoutedEventArgs e)
    {
        var row = _viewModel.SelectedVisibleQuotationReference;
        if (row is not null)
        {
            await ToggleMainReferenceAsync(row, !row.IsInSelectedBasket).ConfigureAwait(true);
        }
    }

    private async void MainMembership_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: QuotationPriceDisplayRow row } checkBox)
        {
            await ToggleMainReferenceAsync(row, checkBox.IsChecked == true).ConfigureAwait(true);
        }
    }

    private async Task ToggleMainReferenceAsync(
        QuotationPriceDisplayRow row,
        bool include)
    {
        if (!include &&
            _viewModel.SelectedQuotationBasket?.Source.IsManual == false &&
            MessageBox.Show(
                this,
                "A cesta automática será preservada e será criada uma cópia manual sem este preço. Continuar?",
                "Editar cesta automática",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            row.IsInSelectedBasket = true;
            return;
        }

        try
        {
            await _viewModel.SetQuotationReferenceMembershipAsync(row, include).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Editar cesta",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void MainReferenceOpenItem_Click(object sender, RoutedEventArgs e) =>
        _viewModel.OpenSelectedQuotationItem();

}
