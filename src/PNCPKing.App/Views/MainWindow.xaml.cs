using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private readonly MainViewModel _viewModel;
    private readonly DataGridColumnLayoutService _columnLayouts;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;
    private int _manualBasketInteraction;

    public MainWindow(MainViewModel viewModel, DataGridColumnLayoutService columnLayouts)
    {
        _viewModel = viewModel;
        _columnLayouts = columnLayouts;
        InitializeComponent();
        DataContext = viewModel;
        ClampInitialSizeToWorkArea();
        SourceInitialized += MainWindow_SourceInitialized;
        _columnLayouts.Register("item-results", ItemResultsGrid);
        _columnLayouts.Register("quotation-lines", QuotationLinesGrid);
        _columnLayouts.Register("quotation-baskets", QuotationBasketsGrid);
        _columnLayouts.Register("quotation-selected-references", SelectedBasketReferencesGrid);
    }

    private void ClampInitialSizeToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(500, workArea.Width - 12);
        var availableHeight = Math.Max(360, workArea.Height - 12);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Max(MinWidth, Math.Min(Width, availableWidth));
        Height = Math.Max(MinHeight, Math.Min(Height, availableHeight));
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WindowMessageHook);
    }

    private static IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition.X = Math.Abs(monitorInfo.WorkArea.Left - monitorInfo.Monitor.Left);
        info.MaxPosition.Y = Math.Abs(monitorInfo.WorkArea.Top - monitorInfo.Monitor.Top);
        info.MaxSize.X = Math.Abs(monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left);
        info.MaxSize.Y = Math.Abs(monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top);
        Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
        handled = true;
        return IntPtr.Zero;
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
        if (sender is not Button { Tag: DataGrid dataGrid })
        {
            return;
        }

        _columnLayouts.ShowChooser(this, dataGrid);
    }

    private void QuotationActionsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    private void CatalogDictionary_Click(object sender, RoutedEventArgs e) =>
        _viewModel.OpenCatalogDictionary(this);

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
        if (Interlocked.CompareExchange(ref _manualBasketInteraction, 1, 0) != 0)
        {
            AsyncCommandRuntime.ReportRejected();
            return;
        }

        try
        {
            var selected = ItemResultsGrid.SelectedItems
                .OfType<ItemSearchDisplayRow>()
                .ToArray();
            await _viewModel.CreateOrAppendManualBasketAsync(selected).ConfigureAwait(true);
        }
        catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
        {
            AsyncCommandRuntime.Handle(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _manualBasketInteraction, 0);
        }
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
