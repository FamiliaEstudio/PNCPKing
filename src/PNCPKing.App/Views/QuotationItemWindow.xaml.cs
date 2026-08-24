using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class QuotationItemWindow : Window
{
    private readonly IWindowCaptureService _capture;
    private readonly IInternetEvidenceStore _evidenceStore;
    private readonly DataGridColumnLayoutService _columnLayouts;
    private readonly SemaphoreSlim _interactionGate = new(1, 1);
    private Task _initialLoadTask = Task.CompletedTask;
    private bool _closing;
    private bool _closedCleanly;

    public QuotationItemWindow(
        QuotationItemViewModel viewModel,
        IWindowCaptureService capture,
        IInternetEvidenceStore evidenceStore,
        DataGridColumnLayoutService columnLayouts)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _capture = capture;
        _evidenceStore = evidenceStore;
        _columnLayouts = columnLayouts;
        DataContext = viewModel;
        _columnLayouts.Register("quotation-item-prices", PriceGrid);
        _columnLayouts.Register("quotation-item-search-results", SearchResultsGrid);
        Loaded += (_, _) =>
            _initialLoadTask = RunAsync(() => ViewModel.LoadAsync());
    }

    public QuotationItemViewModel ViewModel { get; }

    public void ShowReferenceDocuments(string referenceId)
    {
        async void Show()
        {
            await _initialLoadTask.ConfigureAwait(true);
            WorkspaceTabs.SelectedIndex = 2;
            await RunAsync(() => ViewModel.PrepareReferenceDocumentsAsync(referenceId))
                .ConfigureAwait(true);
        }

        if (IsLoaded)
        {
            Show();
            return;
        }

        RoutedEventHandler? handler = null;
        handler = (_, _) =>
        {
            Loaded -= handler;
            Show();
        };
        Loaded += handler;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closedCleanly)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        if (_closing)
        {
            return;
        }

        _closing = true;
        IsEnabled = false;
        ViewModel.StopSearch();
        try
        {
            await ViewModel.DisposeAsync().ConfigureAwait(true);
            _columnLayouts.Unregister(PriceGrid);
            _columnLayouts.Unregister(SearchResultsGrid);
            await _columnLayouts.FlushAsync().ConfigureAwait(true);
        }
        finally
        {
            _closedCleanly = true;
            Close();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => ViewModel.LoadAsync()).ConfigureAwait(true);

    private async void RenameItem_Click(object sender, RoutedEventArgs e) =>
        await PromptRenameItemAsync().ConfigureAwait(true);

    private async Task PromptRenameItemAsync()
    {
        var current = ViewModel.Line?.Line.EffectiveDisplayName;
        if (string.IsNullOrWhiteSpace(current)) return;
        var window = new TextPromptWindow(
            "Editar nome do item",
            "Nome visível nas telas e exportações (o descritor técnico continuará sendo usado nas pesquisas):",
            current)
        {
            Owner = this
        };
        if (window.ShowDialog() == true)
        {
            await RunAsync(() => ViewModel.RenameItemAsync(window.Value)).ConfigureAwait(true);
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || Keyboard.FocusedElement is TextBox) return;
        e.Handled = true;
        await PromptRenameItemAsync().ConfigureAwait(true);
    }

    private async void CatalogSearch_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => ViewModel.SearchCatalogAsync()).ConfigureAwait(true);

    private async void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, WorkspaceTabs) ||
            !ReferenceEquals(WorkspaceTabs.SelectedItem, CatalogTab))
        {
            return;
        }

        await RunAsync(ViewModel.EnsureCatalogAreaLoadedAsync).ConfigureAwait(true);
    }

    private async void CatalogQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunAsync(() => ViewModel.SearchCatalogAsync()).ConfigureAwait(true);
    }

    private void CatalogHierarchy_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        ViewModel.ApplyCatalogHierarchy(e.NewValue as CatalogHierarchyNode);

    private async void CatalogHierarchy_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: CatalogHierarchyNode node })
        {
            await RunAsync(() => ViewModel.LoadCatalogHierarchyChildrenAsync(node)).ConfigureAwait(true);
        }
    }

    private async void CatalogPrevious_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.PreviousCatalogPageAsync).ConfigureAwait(true);

    private async void CatalogNext_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.NextCatalogPageAsync).ConfigureAwait(true);

    private async void CatalogAssign_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.AssignSelectedCatalogAsync).ConfigureAwait(true);

    private async void CatalogRemove_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.RemoveCatalogSelectionAsync).ConfigureAwait(true);

    private void CatalogCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCatalogResult is { } selected)
        {
            Clipboard.SetText($"{selected.Kind} {selected.Code}");
        }
    }

    private void CatalogDictionary_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Main.OpenCatalogDictionary(this);

    private void Pause_Click(object sender, RoutedEventArgs e) => ViewModel.PauseAutomation();
    private void Resume_Click(object sender, RoutedEventArgs e) => ViewModel.ResumeAutomation();

    private async void RestrictivePrompt_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => ViewModel.LoadPromptSlotAsync(ItemSearchPromptSlot.Restrictive)).ConfigureAwait(true);

    private async void IntermediatePrompt_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => ViewModel.LoadPromptSlotAsync(ItemSearchPromptSlot.Intermediate)).ConfigureAwait(true);

    private async void BroadPrompt_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => ViewModel.LoadPromptSlotAsync(ItemSearchPromptSlot.Broad)).ConfigureAwait(true);

    private async void CustomPrompt_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => ViewModel.LoadPromptSlotAsync(ItemSearchPromptSlot.Custom)).ConfigureAwait(true);

    private void InsertSweetCode_Click(object sender, RoutedEventArgs e) =>
        ViewModel.InsertSelectedSweetCode();

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var restart = ViewModel.SearchDefinitionChanged;
        if (restart &&
            MessageBox.Show(
                this,
                "A expressão, geografia, período ou ordenação mudou. Reiniciar somente esta área de prompt? " +
                "As referências já adicionadas às cestas serão preservadas.",
                "Reiniciar pesquisa do item",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!ConfirmLargeBatch())
        {
            return;
        }

        await RunAsync(() => ViewModel.RunSearchAsync(restart)).ConfigureAwait(true);
    }

    private async void ContinueSearch_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SearchDefinitionChanged)
        {
            MessageBox.Show(
                this,
                "Os critérios principais foram alterados. Use Pesquisar para confirmar o reinício.",
                "Continuar pesquisa",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ConfirmLargeBatch())
        {
            return;
        }

        await RunAsync(() => ViewModel.RunSearchAsync(false)).ConfigureAwait(true);
    }

    private bool ConfirmLargeBatch() =>
        ViewModel.BatchCount <= 10 ||
        MessageBox.Show(
            this,
            $"Examinar as próximas {ViewModel.BatchCount * ItemSearchDefaults.ContractsPerBatch:N0} " +
            "contratações ainda não resolvidas nesta pesquisa individual? " +
            "Candidatas já cobertas pelo cache serão avançadas sem consumir essa cota.",
            "Confirmar lotes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void StopSearch_Click(object sender, RoutedEventArgs e) => ViewModel.StopSearch();

    private async void ApplyPriceFilter_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.ApplyPriceFilterAsync).ConfigureAwait(true);

    private async void AddSearchResults_Click(object sender, RoutedEventArgs e)
    {
        var rows = SearchResultsGrid.SelectedItems.OfType<ItemSearchDisplayRow>().ToArray();
        await RunAsync(() => ViewModel.AddSearchRowsAsync(rows)).ConfigureAwait(true);
    }

    private async void Membership_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: QuotationPriceDisplayRow row } checkBox)
        {
            return;
        }

        var include = checkBox.IsChecked == true;
        if (!include &&
            ViewModel.SelectedBasket?.Source.IsManual == false &&
            MessageBox.Show(
                this,
                "A cesta automática será preservada e será criada uma cópia manual sem este preço. Continuar?",
                "Editar cesta automática",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            checkBox.IsChecked = true;
            return;
        }

        await RunAsync(() => ViewModel.SetReferenceMembershipAsync(row, include)).ConfigureAwait(true);
    }

    private async void ConfirmBasket_Click(object sender, RoutedEventArgs e)
    {
        var basket = ViewModel.SelectedBasket?.Source;
        if (basket is null)
        {
            return;
        }

        if (basket.IsManual && !basket.IsValid &&
            MessageBox.Show(
                this,
                $"{basket.ValidationMessage}\n\nConfirmar mesmo com esta ressalva?",
                "Confirmar cesta",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(ViewModel.ConfirmSelectedBasketAsync).ConfigureAwait(true);
    }

    private void ScopeInBasket_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ReferenceScope = ReferenceViewScope.InBasket;
    private void ScopeOutside_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ReferenceScope = ReferenceViewScope.EligibleOutsideBasket;
    private void ScopeRejected_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ReferenceScope = ReferenceViewScope.RejectedOrDuplicate;
    private void ScopeAll_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ReferenceScope = ReferenceViewScope.All;

    private void PriceGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) =>
        SelectRowUnderPointer(PriceGrid, e.OriginalSource as DependencyObject);

    private void SearchGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) =>
        SelectRowUnderPointer(SearchResultsGrid, e.OriginalSource as DependencyObject);

    private async void PriceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedPrice is null)
        {
            return;
        }

        WorkspaceTabs.SelectedIndex = 2;
        await RunAsync(() => ViewModel.PreparePriceDocumentsAsync(ViewModel.SelectedPrice)).ConfigureAwait(true);
    }

    private async void SearchGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedSearchRow is null)
        {
            return;
        }

        WorkspaceTabs.SelectedIndex = 2;
        await RunAsync(() => ViewModel.PrepareSearchDocumentsAsync(ViewModel.SelectedSearchRow)).ConfigureAwait(true);
    }

    private void PriceActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: QuotationPriceDisplayRow row } button)
        {
            ViewModel.SelectedPrice = row;
            OpenMenu(PriceContextMenu, button);
        }
    }

    private void SearchActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ItemSearchDisplayRow row } button)
        {
            ViewModel.SelectedSearchRow = row;
            SearchResultsGrid.SelectedItem = row;
            OpenMenu(SearchContextMenu, button);
        }
    }

    private void PriceOpenSource_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenSource(ViewModel.SelectedPrice);

    private void SearchOpenSource_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenSource(search: ViewModel.SelectedSearchRow);

    private void PriceFullDocuments_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPrice?.Source.Source == QuotationReferenceSource.InternetIncisoIII)
        {
            _ = RunAsync(() => ViewModel.PreparePriceDocumentsAsync(ViewModel.SelectedPrice));
            WorkspaceTabs.SelectedIndex = 2;
            return;
        }

        ViewModel.AccessFullDocuments(ViewModel.SelectedPrice);
    }

    private void SearchFullDocuments_Click(object sender, RoutedEventArgs e) =>
        ViewModel.AccessFullDocuments(search: ViewModel.SelectedSearchRow);

    private async void PriceRelevantPages_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPrice is null)
        {
            return;
        }

        WorkspaceTabs.SelectedIndex = 2;
        await RunAsync(() => ViewModel.PreparePriceDocumentsAsync(ViewModel.SelectedPrice)).ConfigureAwait(true);
    }

    private async void SearchRelevantPages_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedSearchRow is null)
        {
            return;
        }

        WorkspaceTabs.SelectedIndex = 2;
        await RunAsync(() => ViewModel.PrepareSearchDocumentsAsync(ViewModel.SelectedSearchRow)).ConfigureAwait(true);
    }

    private async void PriceToggleBasket_Click(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.SelectedPrice;
        if (row is null)
        {
            return;
        }

        await RunAsync(() => ViewModel.SetReferenceMembershipAsync(
            row,
            !row.IsInSelectedBasket)).ConfigureAwait(true);
    }

    private async void SearchAddToBasket_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedSearchRow is not null)
        {
            await RunAsync(() => ViewModel.AddSearchRowsAsync([ViewModel.SelectedSearchRow])).ConfigureAwait(true);
        }
    }

    private async void PrepareDocuments_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.PrepareSelectedDocumentsAsync).ConfigureAwait(true);

    private void OpenRelevantPdf_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenRelevantPdf();

    private async void NewInternetPrice_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var draft = new InternetPriceDraft
        {
            Id = Guid.NewGuid(),
            LineId = ViewModel.Line!.Line.Id,
            BasketId = ViewModel.SelectedBasket?.Source.IsManual == true
                ? ViewModel.SelectedBasket.Source.ManualBasketId
                : null,
            CapturedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        await EditDraftAsync(draft).ConfigureAwait(true);
    }

    private async void EditDraft_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDraft is not null)
        {
            await EditDraftAsync(ViewModel.SelectedDraft).ConfigureAwait(true);
        }
    }

    private async void DeleteDraft_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDraft is not null &&
            MessageBox.Show(
                this,
                "Excluir este rascunho?",
                "Preço da internet",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await RunAsync(ViewModel.DeleteSelectedDraftAsync).ConfigureAwait(true);
        }
    }

    private async void EditInternetReference_Click(object sender, RoutedEventArgs e)
    {
        var reference = ViewModel.SelectedPrice?.Source;
        if (reference?.Source != QuotationReferenceSource.InternetIncisoIII)
        {
            MessageBox.Show(this, "Selecione um preço da internet.", "Editar preço");
            return;
        }

        try
        {
            var evidence = await ViewModel.GetSelectedInternetEvidenceAsync().ConfigureAwait(true);
            var suffix = reference.Id["internet:".Length..];
            var draft = new InternetPriceDraft
            {
                Id = Guid.ParseExact(suffix, "N"),
                LineId = reference.LineId,
                BasketId = ViewModel.SelectedBasket?.Source.ManualBasketId,
                SourceUrl = reference.PortalUrl,
                UnitPrice = reference.UnitPrice,
                Description = reference.ItemDescription,
                SupplierName = reference.SupplierName,
                SupplierTaxId = reference.SupplierTaxId,
                PriceImage = evidence.PriceImage,
                TaxIdImage = evidence.TaxIdImage,
                CapturedAt = evidence.CapturedAt,
                CreatedAt = evidence.CapturedAt,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await EditDraftAsync(draft).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void DeleteInternetReference_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPrice?.Source.Source != QuotationReferenceSource.InternetIncisoIII)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                "Excluir este preço da internet de todas as cestas deste item? Os dados permanecerão apenas nos backups já criados.",
                "Excluir preço da internet",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await RunAsync(ViewModel.DeleteSelectedInternetReferenceAsync).ConfigureAwait(true);
        }
    }

    private async Task EditDraftAsync(InternetPriceDraft draft)
    {
        var prices = ViewModel.SelectedBasket?.Source.References
            .Where(reference => reference.Id != $"internet:{draft.Id:N}")
            .Select(reference => reference.UnitPrice)
            .ToArray() ?? [];
        var window = new InternetPriceWindow(draft, prices, _capture, _evidenceStore)
        {
            Owner = this
        };
        if (window.ShowDialog() != true || window.ResultDraft is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (!window.CompleteRequested)
            {
                await ViewModel.SaveInternetDraftAsync(window.ResultDraft).ConfigureAwait(true);
                return;
            }

            var basket = await ViewModel.EnsureManualBasketAsync(copyAutomatic: true).ConfigureAwait(true);
            await ViewModel.CompleteInternetDraftAsync(
                window.ResultDraft with { BasketId = basket?.Id },
                basket?.Id,
                basket?.Name ?? "Manual 1").ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private void ChooseColumns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DataGrid dataGrid } button)
        {
            _columnLayouts.ShowChooser(this, dataGrid);
        }
    }

    private static void SelectRowUnderPointer(DataGrid grid, DependencyObject? source)
    {
        while (source is not null && source is not DataGridRow)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        if (source is DataGridRow row)
        {
            grid.SelectedItem = row.Item;
            row.IsSelected = true;
        }
    }

    private static void OpenMenu(ContextMenu menu, Button target)
    {
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (!await _interactionGate.WaitAsync(0).ConfigureAwait(true))
        {
            AsyncCommandRuntime.ReportRejected();
            return;
        }

        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            _interactionGate.Release();
        }
    }

    private void ShowError(Exception exception) =>
        MessageBox.Show(
            this,
            exception.Message,
            "Item da cotação",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}
