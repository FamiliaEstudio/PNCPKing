using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int ContractPageSize = 20;
    private const int ItemPageSize = 50;

    private readonly IContractRepository _repository;
    private readonly PreflightService _preflightService;
    private readonly SyncService _syncService;
    private readonly AutoSyncCoordinator _autoSyncCoordinator;
    private readonly ItemHydrationService _hydrationService;
    private readonly ItemSearchSessionService _itemSearchService;
    private readonly BackupService _backupService;
    private readonly IPncpRequestTelemetry _telemetry;
    private readonly string _dataFolder;
    private readonly DispatcherTimer _maintenanceTimer;
    private readonly SemaphoreSlim _itemPageGate = new(1, 1);
    private CancellationTokenSource? _indexCancellation;
    private CancellationTokenSource? _priceCancellation;
    private CancellationTokenSource? _foregroundCancellation;
    private SearchQuery? _activeSearchQuery;
    private PncpRequestTelemetrySnapshot? _searchTelemetryBaseline;
    private string _queryText = string.Empty;
    private SearchGeoFilter _selectedGeoFilter = SearchGeoFilter.All;
    private SearchSortOption _selectedSortOption;
    private DateRangeOption _selectedDateRange;
    private DateTime? _customStartDate;
    private DateTime? _customEndDate;
    private ContractRecord? _selectedContract;
    private ItemSearchDisplayRow? _selectedItemSearchRow;
    private string _statusText = "Pronto";
    private string _datasetSummary = "Nenhuma carga concluída.";
    private string _coverageSummary = "0 de 365 dias completos - 0,0%";
    private string _preflightSummary = "Calcule o tamanho antes de iniciar a primeira carga nacional.";
    private string _itemSummary = "Selecione uma contratação para consultar seu cache permanente.";
    private string _itemSearchSummary = "Digite um objeto para pesquisar também dentro dos itens.";
    private string _minimumPriceText = string.Empty;
    private string _maximumPriceText = string.Empty;
    private bool _isIndexBusy;
    private bool _isIndexPaused;
    private bool _canPauseIndex;
    private bool _isPriceBusy;
    private bool _priceOperationUsesNetwork;
    private bool _isForegroundBusy;
    private bool _isFileBusy;
    private double _operationProgress;
    private int _currentContractPage = 1;
    private long _contractSearchTotal;
    private int _currentItemPage;
    private bool _hasMoreItemCandidates;
    private int _batchCount = 1;
    private int _selectedResultsTabIndex = 1;
    private PreflightEstimate? _preflight;
    private bool _automaticMaintenanceRunning;
    private bool _isItemSearchActive;
    private bool _disposed;

    public MainViewModel(
        IContractRepository repository,
        PreflightService preflightService,
        SyncService syncService,
        AutoSyncCoordinator autoSyncCoordinator,
        ItemHydrationService hydrationService,
        ItemSearchSessionService itemSearchService,
        BackupService backupService,
        QuotationService quotationService,
        IQuotationWorkbookService quotationWorkbookService,
        IPncpRequestTelemetry telemetry,
        string dataFolder)
    {
        _repository = repository;
        _preflightService = preflightService;
        _syncService = syncService;
        _autoSyncCoordinator = autoSyncCoordinator;
        _hydrationService = hydrationService;
        _itemSearchService = itemSearchService;
        _backupService = backupService;
        InitializeQuotation(quotationService, quotationWorkbookService);
        _telemetry = telemetry;
        _dataFolder = dataFolder;

        GeoFilters = BuildGeoFilters();
        SortOptions =
        [
            new SearchSortOption("Relevância", SearchSort.Relevance),
            new SearchSortOption("Mais recentes", SearchSort.Newest),
            new SearchSortOption("Mais próximas", SearchSort.Nearest)
        ];
        _selectedSortOption = SortOptions[2];
        DateRanges =
        [
            new DateRangeOption("Últimos 7 dias", 7),
            new DateRangeOption("Últimos 30 dias", 30),
            new DateRangeOption("Últimos 90 dias", 90),
            new DateRangeOption("Últimos 180 dias", 180),
            new DateRangeOption("Últimos 365 dias", 365),
            new DateRangeOption("Datas personalizadas", null, true)
        ];
        _selectedDateRange = DateRanges[4];
        var today = DateTime.Today;
        _customStartDate = today.AddDays(-364);
        _customEndDate = today;

        SearchCommand = new AsyncRelayCommand(() => SearchAsync(resetSession: true), () => !IsFileBusy);
        PreviousContractPageCommand = new AsyncRelayCommand(
            () => ChangeContractPageAsync(CurrentContractPage - 1),
            () => !IsFileBusy && CurrentContractPage > 1 && _activeSearchQuery is not null);
        NextContractPageCommand = new AsyncRelayCommand(
            () => ChangeContractPageAsync(CurrentContractPage + 1),
            () => !IsFileBusy && CurrentContractPage * ContractPageSize < ContractSearchTotal && _activeSearchQuery is not null);
        LoadNextItemPageCommand = new AsyncRelayCommand(
            LoadNextItemPageAsync,
            () => !IsFileBusy && !IsPriceBusy && HasMoreItemCandidates && _isItemSearchActive);
        FireBatchesCommand = new AsyncRelayCommand(
            FireBatchesAsync,
            () => !IsFileBusy && !IsPriceBusy && _isItemSearchActive);
        ApplyPriceFilterCommand = new AsyncRelayCommand(
            ApplyPriceFilterAsync,
            () => !IsFileBusy && !IsPriceBusy && _itemSearchService.CurrentSession is not null);
        StopItemSearchCommand = new RelayCommand(StopItemSearch, () => _isItemSearchActive);
        CalculatePreflightCommand = new AsyncRelayCommand(CalculatePreflightAsync, () => !IsFileBusy && !IsIndexBusy);
        StartSyncCommand = new AsyncRelayCommand(StartSyncAsync, () => !IsFileBusy && !IsIndexBusy);
        PauseSyncCommand = new RelayCommand(TogglePause, () => _canPauseIndex && _indexCancellation is not null);
        CancelIndexCommand = new RelayCommand(() => _indexCancellation?.Cancel(), () => _indexCancellation is not null);
        HydrateCommand = new AsyncRelayCommand(
            () => HydrateSelectedAsync(true),
            () => !IsFileBusy && !IsForegroundBusy && SelectedContract is not null);
        RetryPendingCommand = new AsyncRelayCommand(
            () => HydrateSelectedAsync(false),
            () => !IsFileBusy && !IsForegroundBusy && SelectedContract is not null);
        OpenPncpCommand = new RelayCommand<ContractRecord>(OpenContract, contract => contract is not null);
        ExportBackupCommand = new AsyncRelayCommand(ExportBackupAsync, () => !IsFileBusy && !IsIndexBusy);
        ImportBackupCommand = new AsyncRelayCommand(
            ImportBackupAsync,
            () => !IsFileBusy && !IsIndexBusy && !IsForegroundBusy && !IsPriceBusy);
        ClearCacheCommand = new AsyncRelayCommand(
            ClearCacheAsync,
            () => !IsFileBusy && !IsForegroundBusy && !IsPriceBusy);

        _maintenanceTimer = new DispatcherTimer { Interval = SyncService.AutomaticRetryDelay };
        _maintenanceTimer.Tick += async (_, _) => await TryRunAutomaticMaintenanceAsync().ConfigureAwait(true);
    }

    public ObservableCollection<ContractRecord> ContractResults { get; } = [];
    public ObservableCollection<ItemSearchDisplayRow> ItemSearchRows { get; } = [];
    public ObservableCollection<ItemDisplayRow> ContractItemRows { get; } = [];
    public ObservableCollection<CoverageDay> CoverageDays { get; } = [];
    public IReadOnlyList<SearchGeoFilter> GeoFilters { get; }
    public IReadOnlyList<SearchSortOption> SortOptions { get; }
    public IReadOnlyList<DateRangeOption> DateRanges { get; }

    public ICommand SearchCommand { get; }
    public ICommand PreviousContractPageCommand { get; }
    public ICommand NextContractPageCommand { get; }
    public ICommand LoadNextItemPageCommand { get; }
    public ICommand FireBatchesCommand { get; }
    public ICommand ApplyPriceFilterCommand { get; }
    public ICommand StopItemSearchCommand { get; }
    public ICommand CalculatePreflightCommand { get; }
    public ICommand StartSyncCommand { get; }
    public ICommand PauseSyncCommand { get; }
    public ICommand CancelIndexCommand { get; }
    public ICommand HydrateCommand { get; }
    public ICommand RetryPendingCommand { get; }
    public ICommand OpenPncpCommand { get; }
    public ICommand ExportBackupCommand { get; }
    public ICommand ImportBackupCommand { get; }
    public ICommand ClearCacheCommand { get; }

    public string QueryText
    {
        get => _queryText;
        set => SetProperty(ref _queryText, value);
    }

    public SearchGeoFilter SelectedGeoFilter
    {
        get => _selectedGeoFilter;
        set => SetProperty(ref _selectedGeoFilter, value);
    }

    public SearchSortOption SelectedSortOption
    {
        get => _selectedSortOption;
        set => SetProperty(ref _selectedSortOption, value);
    }

    public DateRangeOption SelectedDateRange
    {
        get => _selectedDateRange;
        set
        {
            if (SetProperty(ref _selectedDateRange, value))
            {
                OnPropertyChanged(nameof(IsCustomDateRange));
            }
        }
    }

    public DateTime? CustomStartDate
    {
        get => _customStartDate;
        set => SetProperty(ref _customStartDate, value);
    }

    public DateTime? CustomEndDate
    {
        get => _customEndDate;
        set => SetProperty(ref _customEndDate, value);
    }

    public bool IsCustomDateRange => SelectedDateRange.IsCustom;
    public bool CanSortByNearest => true;

    public string MinimumPriceText
    {
        get => _minimumPriceText;
        set => SetProperty(ref _minimumPriceText, value);
    }

    public string MaximumPriceText
    {
        get => _maximumPriceText;
        set => SetProperty(ref _maximumPriceText, value);
    }

    public int BatchCount
    {
        get => _batchCount;
        set => SetProperty(ref _batchCount, Math.Clamp(value, 1, 100));
    }

    public ContractRecord? SelectedContract
    {
        get => _selectedContract;
        set
        {
            if (SetProperty(ref _selectedContract, value))
            {
                NotifyCommands();
                _ = LoadSelectedContractCacheAsync();
            }
        }
    }

    public ItemSearchDisplayRow? SelectedItemSearchRow
    {
        get => _selectedItemSearchRow;
        set
        {
            if (SetProperty(ref _selectedItemSearchRow, value))
            {
                if (value is not null)
                {
                    SelectedContract = value.Contract;
                }

                NotifyCommands();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DatasetSummary
    {
        get => _datasetSummary;
        private set => SetProperty(ref _datasetSummary, value);
    }

    public string CoverageSummary
    {
        get => _coverageSummary;
        private set => SetProperty(ref _coverageSummary, value);
    }

    public string PreflightSummary
    {
        get => _preflightSummary;
        private set => SetProperty(ref _preflightSummary, value);
    }

    public string ItemSummary
    {
        get => _itemSummary;
        private set => SetProperty(ref _itemSummary, value);
    }

    public string ItemSearchSummary
    {
        get => _itemSearchSummary;
        private set => SetProperty(ref _itemSearchSummary, value);
    }

    public bool IsIndexBusy
    {
        get => _isIndexBusy;
        private set
        {
            if (SetProperty(ref _isIndexBusy, value))
            {
                OnPropertyChanged(nameof(IndexActivityText));
                OnPropertyChanged(nameof(IsIndexTransferActive));
                NotifyCommands();
            }
        }
    }

    public bool IsIndexPaused
    {
        get => _isIndexPaused;
        private set
        {
            if (SetProperty(ref _isIndexPaused, value))
            {
                OnPropertyChanged(nameof(IndexActivityText));
                OnPropertyChanged(nameof(IsIndexTransferActive));
                OnPropertyChanged(nameof(PauseIndexButtonText));
            }
        }
    }

    public bool IsIndexTransferActive => IsIndexBusy && !IsIndexPaused;

    public string IndexActivityText => IsIndexPaused
        ? "Índice: pausa solicitada/ativa após a etapa atual"
        : IsIndexBusy
            ? "Índice: consultando o PNCP"
            : "Índice: inativo";

    public string PauseIndexButtonText => IsIndexPaused ? "Retomar índice" : "Pausar índice";

    public bool IsPriceBusy
    {
        get => _isPriceBusy;
        private set
        {
            if (SetProperty(ref _isPriceBusy, value))
            {
                OnPropertyChanged(nameof(PriceActivityText));
                OnPropertyChanged(nameof(IsPriceTransferActive));
                NotifyCommands();
            }
        }
    }

    public bool IsPriceTransferActive => IsPriceBusy || IsForegroundBusy;

    public string PriceActivityText => IsForegroundBusy
        ? "Preços: atualizando a contratação no PNCP"
        : IsPriceBusy
        ? _priceOperationUsesNetwork
            ? "Preços: consultando o PNCP"
            : "Preços: aplicando filtro local"
        : _isItemSearchActive
            ? "Preços: sessão pronta"
            : "Preços: inativos";

    public bool IsForegroundBusy
    {
        get => _isForegroundBusy;
        private set
        {
            if (SetProperty(ref _isForegroundBusy, value))
            {
                OnPropertyChanged(nameof(PriceActivityText));
                OnPropertyChanged(nameof(IsPriceTransferActive));
                NotifyCommands();
            }
        }
    }

    public bool IsFileBusy
    {
        get => _isFileBusy;
        private set
        {
            if (SetProperty(ref _isFileBusy, value))
            {
                NotifyCommands();
            }
        }
    }

    public double OperationProgress
    {
        get => _operationProgress;
        private set => SetProperty(ref _operationProgress, value);
    }

    public int CurrentContractPage
    {
        get => _currentContractPage;
        private set
        {
            if (SetProperty(ref _currentContractPage, value))
            {
                OnPropertyChanged(nameof(ContractPageSummary));
                NotifyCommands();
            }
        }
    }

    public long ContractSearchTotal
    {
        get => _contractSearchTotal;
        private set
        {
            if (SetProperty(ref _contractSearchTotal, value))
            {
                OnPropertyChanged(nameof(ContractPageSummary));
                NotifyCommands();
            }
        }
    }

    public string ContractPageSummary => $"Página {CurrentContractPage} - {ContractSearchTotal:N0} contratação(ões)";

    public int CurrentItemPage
    {
        get => _currentItemPage;
        private set => SetProperty(ref _currentItemPage, value);
    }

    public bool HasMoreItemCandidates
    {
        get => _hasMoreItemCandidates;
        private set
        {
            if (SetProperty(ref _hasMoreItemCandidates, value))
            {
                NotifyCommands();
            }
        }
    }

    public int SelectedResultsTabIndex
    {
        get => _selectedResultsTabIndex;
        set => SetProperty(ref _selectedResultsTabIndex, value);
    }

    public string DataFolder => _dataFolder;

    public PreflightEstimate? Preflight
    {
        get => _preflight;
        private set => SetProperty(ref _preflight, value);
    }

    public async Task InitializeAsync()
    {
        await RefreshQuotationProjectsAsync().ConfigureAwait(true);
        await RefreshDatasetSummaryAsync().ConfigureAwait(true);
        await RefreshCoverageAsync().ConfigureAwait(true);
        await SearchAsync(resetSession: false).ConfigureAwait(true);
        _maintenanceTimer.Start();
        _ = TryRunAutomaticMaintenanceAsync();
    }

    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _maintenanceTimer.Stop();
        _indexCancellation?.Cancel();
        _priceCancellation?.Cancel();
        _foregroundCancellation?.Cancel();
        SetItemSearchActive(false);
        await _itemSearchService.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task SearchAsync(bool resetSession)
    {
        try
        {
            var (startDate, endDate) = ResolveDateRange();
            var sort = SelectedSortOption.Value;
            _activeSearchQuery = new SearchQuery(
                QueryText.Trim(),
                SelectedGeoFilter,
                startDate,
                endDate,
                sort,
                1,
                ContractPageSize);
            await LoadContractPageAsync(1).ConfigureAwait(true);

            if (!resetSession || string.IsNullOrWhiteSpace(_activeSearchQuery.Text))
            {
                if (string.IsNullOrWhiteSpace(_activeSearchQuery.Text))
                {
                    StopItemSearch();
                    ItemSearchRows.Clear();
                    CurrentItemPage = 0;
                    HasMoreItemCandidates = false;
                    ItemSearchSummary = "Pesquisa vazia: nenhuma chamada de itens ou preços foi iniciada.";
                    SelectedResultsTabIndex = 1;
                }

                return;
            }

            _priceCancellation?.Cancel();
            _itemSearchService.Stop();
            SetItemSearchActive(false);
            _priceCancellation?.Dispose();
            _priceCancellation = new CancellationTokenSource();
            ItemSearchRows.Clear();
            CurrentItemPage = 0;
            HasMoreItemCandidates = true;
            SelectedItemSearchRow = null;
            SelectedResultsTabIndex = 0;
            _searchTelemetryBaseline = _telemetry.GetSnapshot();
            await _itemSearchService.StartAsync(
                _activeSearchQuery with { Page = 1, PageSize = 200 },
                _priceCancellation.Token).ConfigureAwait(true);
            SetItemSearchActive(true);
            NotifyCommands();
            ItemSearchSummary = "Camada inicial: 50 municípios próximos. Consultando no máximo 50 listas novas nesta ação…";
            _ = LoadItemPageSafelyAsync(1, append: false, _priceCancellation.Token);
        }
        catch (Exception exception)
        {
            StatusText = $"Não foi possível pesquisar: {exception.Message}";
            ItemSearchSummary = $"Pesquisa rejeitada: {exception.Message}";
        }
    }

    private async Task LoadContractPageAsync(int page)
    {
        if (_activeSearchQuery is null)
        {
            var (startDate, endDate) = ResolveDateRange();
            _activeSearchQuery = new SearchQuery(
                QueryText.Trim(),
                SelectedGeoFilter,
                startDate,
                endDate,
                SelectedSortOption.Value,
                1,
                ContractPageSize);
        }

        StatusText = "Pesquisando no índice local…";
        var result = await _repository.SearchAsync(
            _activeSearchQuery with { Page = Math.Max(1, page), PageSize = ContractPageSize }).ConfigureAwait(true);
        ContractResults.Clear();
        foreach (var contract in result.Results)
        {
            ContractResults.Add(contract);
        }

        CurrentContractPage = result.Page;
        ContractSearchTotal = result.Total;
        StatusText = $"Índice local: {result.Total:N0} contratação(ões).";
    }

    private async Task ChangeContractPageAsync(int page)
    {
        try
        {
            await LoadContractPageAsync(page).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusText = $"Não foi possível mudar a página: {exception.Message}";
        }
    }

    private async Task LoadNextItemPageAsync()
    {
        var cancellation = _priceCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        await LoadItemPageSafelyAsync(CurrentItemPage + 1, append: true, cancellation.Token).ConfigureAwait(true);
    }

    private async Task LoadItemPageSafelyAsync(int page, bool append, CancellationToken cancellationToken)
    {
        if (!await _itemPageGate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        SetPriceBusy(true, usesNetwork: true);
        try
        {
            var (minimum, maximum) = ParsePriceRange();
            using var requestScope = PncpRequestOptions.BeginScope(PncpRequestPriority.VisiblePrices);
            var progress = new Progress<PriceBatchProgress>(UpdateItemSearchProgress);
            var result = await _itemSearchService.LoadPageAsync(
                page,
                minimum,
                maximum,
                progress,
                cancellationToken).ConfigureAwait(true);
            if (!append)
            {
                ItemSearchRows.Clear();
            }

            AppendUniqueRows(result.Rows);
            CurrentItemPage = Math.Max(CurrentItemPage, result.Page);
            HasMoreItemCandidates = result.HasMoreCandidates;
            ItemSearchSummary =
                $"{result.MatchedItemsDiscovered:N0} item(ns) compatível(is) descoberto(s); " +
                $"{ItemSearchRows.Count:N0} linha(s); etapa {result.GeographicStage}; " +
                $"{result.ContractsScanned:N0} contrato(s) examinado(s); " +
                $"{result.CachedItemListsReused:N0} lista(s) do cache reutilizada(s); " +
                $"{result.FreshItemListsUsed:N0}/50 lista(s) nova(s) nesta ação" +
                (result.ItemListBudgetExhausted ? " — limite atingido, use Continuar. " : ". ") +
                BuildSearchTrafficSummary();
            StatusText = "Pesquisa dentro dos itens atualizada.";
        }
        catch (OperationCanceledException)
        {
            ItemSearchSummary = "Consulta automática interrompida; os resultados concluídos permanecem nesta pesquisa.";
        }
        catch (Exception exception)
        {
            ItemSearchSummary = $"Falha na pesquisa de itens: {exception.Message}";
        }
        finally
        {
            SetPriceBusy(false, usesNetwork: false);
            _itemPageGate.Release();
        }
    }

    private async Task FireBatchesAsync()
    {
        var requestedItems = checked(BatchCount * ItemPageSize);
        var largeConfirmed = requestedItems <= 500;
        if (!largeConfirmed)
        {
            var projection = BuildPriceBatchProjection(requestedItems);
            var answer = MessageBox.Show(
                $"Disparar {BatchCount:N0} lotes (até {requestedItems:N0} itens)?\n\n" +
                $"Chamadas de resultados: até {requestedItems:N0}.\n" +
                $"Payload de resultados estimado: {FormatBytes(projection.Bytes)}.\n" +
                $"Duração estimada com até duas chamadas simultâneas: {FormatDuration(projection.Duration)}.\n" +
                $"Média usada por resultado: {FormatBytes(projection.AverageBytes)} em {FormatDuration(projection.AverageDuration)}.\n" +
                "A descoberta é limitada a 50 listas novas nesta ação; use Continuar para avançar.\n" +
                "Uma consulta pode retornar zero, um ou vários resultados.",
                "Confirmar lotes de preços",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            largeConfirmed = true;
        }

        var cancellation = _priceCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            ItemSearchSummary = "Execute novamente a pesquisa para iniciar uma nova sessão de preços.";
            return;
        }

        SetPriceBusy(true, usesNetwork: true);
        try
        {
            using var requestScope = PncpRequestOptions.BeginScope(PncpRequestPriority.AdditionalBatches);
            var progress = new Progress<PriceBatchProgress>(UpdateItemSearchProgress);
            var result = await _itemSearchService.FireBatchesAsync(
                new PriceBatchRequest(BatchCount, largeConfirmed),
                progress,
                cancellation.Token).ConfigureAwait(true);
            await ReloadAllDiscoveredRowsAsync(cancellation.Token).ConfigureAwait(true);
            HasMoreItemCandidates = !result.CandidateSetExhausted;
            ItemSearchSummary =
                $"{result.Message} Etapa {result.GeographicStage}; " +
                $"{result.ContractsScanned:N0} contrato(s) examinado(s); " +
                $"{result.CachedItemListsReused:N0} lista(s) do cache; falhas: {result.FailedItemCalls:N0}. " +
                BuildSearchTrafficSummary();
        }
        catch (OperationCanceledException)
        {
            ItemSearchSummary = "Lotes interrompidos; o que terminou continua disponível até encerrar esta pesquisa.";
        }
        catch (Exception exception)
        {
            ItemSearchSummary = $"Falha ao disparar lotes: {exception.Message}";
        }
        finally
        {
            SetPriceBusy(false, usesNetwork: false);
        }
    }

    private async Task ApplyPriceFilterAsync()
    {
        try
        {
            _ = ParsePriceRange();
            SetPriceBusy(true, usesNetwork: false);
            await ReloadAllDiscoveredRowsAsync(CancellationToken.None).ConfigureAwait(true);
            ItemSearchSummary =
                $"Faixa aplicada aos valores unitários homologados ativos: {ItemSearchRows.Count:N0} linha(s). " +
                BuildSearchTrafficSummary();
        }
        catch (Exception exception)
        {
            ItemSearchSummary = exception.Message;
        }
        finally
        {
            SetPriceBusy(false, usesNetwork: false);
        }
    }

    private async Task ReloadAllDiscoveredRowsAsync(CancellationToken cancellationToken)
    {
        var (minimum, maximum) = ParsePriceRange();
        var rows = await _itemSearchService.GetDiscoveredRowsAsync(
            minimum,
            maximum,
            cancellationToken).ConfigureAwait(true);
        ItemSearchRows.Clear();
        AppendUniqueRows(rows);
    }

    private void StopItemSearch()
    {
        var wasActive = _isItemSearchActive;
        _priceCancellation?.Cancel();
        _itemSearchService.Stop();
        SetItemSearchActive(false);
        HasMoreItemCandidates = false;
        if (wasActive)
        {
            ItemSearchSummary = "Preços automáticos interrompidos; execute outra pesquisa para reiniciar.";
        }

        NotifyCommands();
    }

    private void AppendUniqueRows(IEnumerable<ItemSearchRow> rows)
    {
        var keys = ItemSearchRows.Select(RowKey).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Select(item => new ItemSearchDisplayRow(item)))
        {
            if (keys.Add(RowKey(row)))
            {
                ItemSearchRows.Add(row);
            }
        }
    }

    private static string RowKey(ItemSearchDisplayRow row) =>
        $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result?.ResultSequence.ToString(CultureInfo.InvariantCulture) ?? row.Source.PriceState.ToString()}";

    private void UpdateItemSearchProgress(PriceBatchProgress progress)
    {
        ItemSearchSummary =
            $"{progress.Message} Etapa {progress.GeographicStage}; contratos: {progress.ContractsScanned:N0}; " +
            $"cache reutilizado: {progress.CachedItemListsReused:N0}; " +
            $"listas novas nesta ação: {progress.FreshItemListsUsed:N0}/50; listas da sessão: {progress.ItemListCalls:N0}; " +
            $"falhas: {progress.FailedItemCalls:N0}. {BuildSearchTrafficSummary()}";
    }

    private string BuildSearchTrafficSummary()
    {
        var baseline = _searchTelemetryBaseline;
        var current = _telemetry.GetSnapshot();
        if (baseline is null)
        {
            return $"Rede da sessão: {FormatBytes(current.TotalBytesReceived)}.";
        }

        var listCalls = Math.Max(0, current[PncpRequestCategory.ItemLists].Calls - baseline[PncpRequestCategory.ItemLists].Calls);
        var resultCalls = Math.Max(0, current[PncpRequestCategory.ItemResults].Calls - baseline[PncpRequestCategory.ItemResults].Calls);
        var listBytes = Math.Max(
            0,
            current[PncpRequestCategory.ItemLists].BytesReceived - baseline[PncpRequestCategory.ItemLists].BytesReceived);
        var resultBytes = Math.Max(
            0,
            current[PncpRequestCategory.ItemResults].BytesReceived - baseline[PncpRequestCategory.ItemResults].BytesReceived);
        var listDuration = SubtractNonNegative(
            current[PncpRequestCategory.ItemLists].TotalDuration,
            baseline[PncpRequestCategory.ItemLists].TotalDuration);
        var resultDuration = SubtractNonNegative(
            current[PncpRequestCategory.ItemResults].TotalDuration,
            baseline[PncpRequestCategory.ItemResults].TotalDuration);
        return $"Rede: {FormatBytes(listBytes + resultBytes)} ({listCalls:N0} lista(s), {resultCalls:N0} resultado(s)); " +
               $"médias: lista {FormatCallAverage(listBytes, listDuration, listCalls)}, " +
               $"resultado {FormatCallAverage(resultBytes, resultDuration, resultCalls)}.";
    }

    private (long Bytes, TimeSpan Duration, long AverageBytes, TimeSpan AverageDuration) BuildPriceBatchProjection(
        int requestedItems)
    {
        var current = _telemetry.GetSnapshot()[PncpRequestCategory.ItemResults];
        var baseline = _searchTelemetryBaseline?[PncpRequestCategory.ItemResults];
        var sessionCalls = Math.Max(0, current.Calls - (baseline?.Calls ?? current.Calls));
        var sessionBytes = Math.Max(0, current.BytesReceived - (baseline?.BytesReceived ?? current.BytesReceived));
        var sessionDuration = SubtractNonNegative(
            current.TotalDuration,
            baseline?.TotalDuration ?? current.TotalDuration);
        var averageBytes = sessionCalls > 0
            ? Math.Max(1, sessionBytes / sessionCalls)
            : current.Calls > 0
                ? Math.Max(1, (long)Math.Ceiling(current.AverageBytesPerCall))
                : 1_240L;
        var averageDuration = sessionCalls > 0
            ? TimeSpan.FromTicks(Math.Max(1, sessionDuration.Ticks / sessionCalls))
            : current.Calls > 0 && current.AverageDuration > TimeSpan.Zero
                ? current.AverageDuration
                : TimeSpan.FromSeconds(1);
        var projectedSeconds = Math.Max(1d, averageDuration.TotalSeconds * requestedItems / 2d);
        return (
            checked(averageBytes * requestedItems),
            TimeSpan.FromSeconds(projectedSeconds),
            averageBytes,
            averageDuration);
    }

    private static string FormatCallAverage(long bytes, TimeSpan duration, long calls)
    {
        if (calls <= 0)
        {
            return "ainda não medida";
        }

        return $"{FormatBytes(bytes / calls)}/chamada em {FormatDuration(TimeSpan.FromTicks(duration.Ticks / calls))}";
    }

    private static TimeSpan SubtractNonNegative(TimeSpan value, TimeSpan baseline) =>
        value > baseline ? value - baseline : TimeSpan.Zero;

    private async Task CalculatePreflightAsync()
    {
        await RunIndexOperationAsync(CalculatePreflightCoreAsync, showErrorDialog: true).ConfigureAwait(true);
    }

    private async Task CalculatePreflightCoreAsync(CancellationToken cancellationToken)
    {
        var endDate = DateOnly.FromDateTime(DateTime.Today);
        var startDate = endDate.AddDays(-364);
        var progress = new Progress<string>(message => StatusText = message);
        Preflight = await _preflightService.CalculateAsync(
            startDate,
            endDate,
            GeoScope.All,
            _dataFolder,
            progress,
            cancellationToken).ConfigureAwait(true);
        PreflightSummary = BuildPreflightSummary(Preflight);
        StatusText = "Estimativa concluída. Revise os números antes de baixar.";
    }

    private async Task StartSyncAsync()
    {
        if (IsIndexBusy)
        {
            return;
        }

        Preflight = null;
        await RunIndexOperationAsync(CalculatePreflightCoreAsync, showErrorDialog: true).ConfigureAwait(true);
        var preflight = Preflight;
        if (preflight is null)
        {
            return;
        }

        if (!preflight.HasEnoughSpace)
        {
            MessageBox.Show(
                "Não há espaço livre suficiente para o banco estimado e a margem de segurança.",
                "Espaço insuficiente",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(
            $"CONFIRMAR CARGA DO PNCP\n\n" +
            $"Período: {preflight.StartDate:dd/MM/yyyy} a {preflight.EndDate:dd/MM/yyyy}\n" +
            $"Contratações: {preflight.ExactContractCount:N0}\n" +
            $"Transferência estimada: {FormatBytes(preflight.EstimatedTransferBytes)}\n" +
            $"Banco estimado: {FormatBytes(preflight.EstimatedDatabaseMinBytes)} a {FormatBytes(preflight.EstimatedDatabaseMaxBytes)}\n" +
            "PDFs: 0 bytes\n\n" +
            "Esta confirmação também autoriza retomadas e a manutenção automática enquanto o aplicativo estiver aberto.",
            "Confirmar tamanho e iniciar",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunIndexOperationAsync(
            cancellationToken => SynchronizeManuallyAsync(preflight, cancellationToken),
            showErrorDialog: true,
            supportsPause: true).ConfigureAwait(true);
    }

    private async Task SynchronizeManuallyAsync(PreflightEstimate preflight, CancellationToken cancellationToken)
    {
        var progress = CreateSyncProgress();
        var state = await _repository.GetDatasetStateAsync(cancellationToken).ConfigureAwait(true);
        var currentEnd = DateOnly.FromDateTime(DateTime.Today);
        if (state.LastSuccessfulSync is null)
        {
            var incomplete = await _repository.GetLatestIncompleteSyncAsync(cancellationToken).ConfigureAwait(true);
            var targetStart = currentEnd.AddDays(-364);
            var canResume = incomplete is { Mode: SyncMode.Publication };
            var start = canResume && incomplete!.StartDate > targetStart
                ? incomplete.StartDate
                : targetStart;
            var end = canResume && incomplete!.EndDate < currentEnd
                ? incomplete.EndDate
                : currentEnd;

            // A checkpoint may have fallen completely outside the moving
            // one-year window while the application was closed. In that case
            // start a valid current load; never send an inverted date range.
            if (start > end)
            {
                canResume = false;
                start = preflight.StartDate < targetStart ? targetStart : preflight.StartDate;
                end = preflight.EndDate > currentEnd ? currentEnd : preflight.EndDate;
                if (start > end)
                {
                    start = targetStart;
                    end = currentEnd;
                }
            }

            StatusText = canResume
                ? $"Retomando a carga interrompida de {start:dd/MM/yyyy} a {end:dd/MM/yyyy}…"
                : "Iniciando a carga nacional confirmada…";
            await _syncService.SynchronizeAsync(
                start,
                end,
                GeoScope.All,
                SyncMode.Publication,
                progress,
                cancellationToken).ConfigureAwait(true);
        }

        // Whether the load was new, resumed or already complete, the coordinator
        // now proves every day/modality, fills new dates first, applies the 48 h
        // overlap and only then prunes the expired edge.
        await _autoSyncCoordinator.SynchronizeAsync(progress, cancellationToken).ConfigureAwait(true);
        OperationProgress = 100;
        StatusText = "Sincronização concluída; janela móvel de 365 dias atualizada.";
        await RefreshDatasetSummaryAsync().ConfigureAwait(true);
        await RefreshCoverageAsync().ConfigureAwait(true);
        if (_activeSearchQuery is not null)
        {
            await LoadContractPageAsync(1).ConfigureAwait(true);
        }
    }

    private async Task TryRunAutomaticMaintenanceAsync()
    {
        if (IsIndexBusy || IsFileBusy || _automaticMaintenanceRunning || _disposed)
        {
            return;
        }

        var state = await _repository.GetDatasetStateAsync().ConfigureAwait(true);
        var incomplete = await _repository.GetLatestIncompleteSyncAsync().ConfigureAwait(true);
        if (state.LastSuccessfulSync is null && incomplete is null)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var targetStart = today.AddDays(-364);
        var coverageComplete = _repository is ICoverageRepository coverage &&
                               await coverage.IsCoverageCompleteAsync(targetStart, today).ConfigureAwait(true);
        if (state.EndDate >= today && state.LastSuccessfulSync?.LocalDateTime.Date >= DateTime.Today && coverageComplete)
        {
            return;
        }

        _automaticMaintenanceRunning = true;
        try
        {
            await RunIndexOperationAsync(async cancellationToken =>
            {
                StatusText = "Manutenção automática: recuperando os dias ausentes mais recentes…";
                await _autoSyncCoordinator.SynchronizeAsync(CreateSyncProgress(), cancellationToken).ConfigureAwait(true);
                OperationProgress = 100;
                StatusText = "Manutenção automática concluída.";
                await RefreshDatasetSummaryAsync().ConfigureAwait(true);
                await RefreshCoverageAsync().ConfigureAwait(true);
            }, showErrorDialog: false, supportsPause: true).ConfigureAwait(true);
        }
        finally
        {
            _automaticMaintenanceRunning = false;
        }
    }

    private IProgress<SyncProgress> CreateSyncProgress() => new Progress<SyncProgress>(item =>
    {
        OperationProgress = item.Percentage;
        StatusText = $"{item.Message} - {item.ContractsSaved:N0} registro(s) gravado(s)";
        if (item.CompletedPartitions == item.TotalPartitions || item.CompletedPartitions % 10 == 0)
        {
            _ = RefreshCoverageAsync();
        }
    });

    private async Task RunIndexOperationAsync(
        Func<CancellationToken, Task> action,
        bool showErrorDialog,
        bool supportsPause = false)
    {
        if (IsIndexBusy)
        {
            return;
        }

        IsIndexBusy = true;
        IsIndexPaused = false;
        _canPauseIndex = supportsPause;
        OperationProgress = 0;
        _indexCancellation = new CancellationTokenSource();
        NotifyCommands();
        try
        {
            using var requestScope = PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance);
            await action(_indexCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Sincronização interrompida; os checkpoints foram preservados.";
        }
        catch (Exception exception)
        {
            var message = GetSynchronizationErrorMessage(exception);
            StatusText = $"Atualização pendente: {message}";
            if (IsGatewayTimeout(exception) && !_disposed)
            {
                // DispatcherTimer keeps its original cadence after a long HTTP
                // attempt. Restart it so the next tick agrees with NextRetryAt
                // instead of hammering PNCP seconds after a 504.
                _maintenanceTimer.Stop();
                _maintenanceTimer.Start();
            }

            if (showErrorDialog)
            {
                MessageBox.Show(message, "PNCP King", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (_syncService.IsPaused)
            {
                _syncService.Resume();
            }

            IsIndexPaused = false;
            _canPauseIndex = false;
            _indexCancellation.Dispose();
            _indexCancellation = null;
            IsIndexBusy = false;
            NotifyCommands();
        }
    }

    private async Task HydrateSelectedAsync(bool forceRefresh)
    {
        var contract = SelectedContract;
        if (contract is null || IsForegroundBusy)
        {
            return;
        }

        IsForegroundBusy = true;
        _foregroundCancellation = new CancellationTokenSource();
        try
        {
            using var requestScope = PncpRequestOptions.BeginScope(PncpRequestPriority.UserSelectedItem);
            StatusText = "Carregando a lista completa da contratação…";
            var preparation = await _hydrationService.PrepareAsync(
                contract,
                forceRefresh,
                _foregroundCancellation.Token).ConfigureAwait(true);
            await RefreshContractItemRowsAsync().ConfigureAwait(true);
            if (preparation.ItemsToConsult == 0)
            {
                ItemSummary = preparation.ItemsWithResult == 0
                    ? $"{preparation.TotalItems:N0} item(ns); nenhum marcado com resultado homologado."
                    : "Todos os resultados desta contratação já estão no cache permanente.";
                return;
            }

            if (MessageBox.Show(
                    $"Itens: {preparation.TotalItems:N0}\n" +
                    $"Consultas de resultado necessárias: {preparation.ItemsToConsult:N0}\n" +
                    $"Tempo estimado: {FormatDurationRange(preparation.EstimatedMinimumDuration, preparation.EstimatedMaximumDuration)}.\n\n" +
                    "Guardar estes resultados no cache permanente?",
                    "Atualizar contratação",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            var progress = new Progress<HydrationProgress>(item =>
            {
                ItemSummary = $"{item.Message}. Falhas: {item.FailedItems:N0}.";
                if (item.CompletedItems == item.TotalItemsWithResult || item.CompletedItems % 10 == 0)
                {
                    _ = RefreshContractItemRowsAsync();
                }
            });
            await _hydrationService.HydratePreparedAsync(
                contract,
                forceRefresh,
                progress,
                _foregroundCancellation.Token).ConfigureAwait(true);
            await RefreshContractItemRowsAsync().ConfigureAwait(true);
            StatusText = "Cache permanente da contratação atualizado.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Consulta manual interrompida; itens pendentes foram preservados.";
        }
        catch (Exception exception)
        {
            StatusText = $"Falha ao atualizar a contratação: {exception.Message}";
        }
        finally
        {
            _foregroundCancellation.Dispose();
            _foregroundCancellation = null;
            IsForegroundBusy = false;
        }
    }

    private async Task LoadSelectedContractCacheAsync()
    {
        try
        {
            await RefreshContractItemRowsAsync().ConfigureAwait(true);
            ItemSummary = SelectedContract is null
                ? "Selecione uma contratação."
                : ContractItemRows.Count == 0
                    ? "Itens ainda não estão no cache permanente."
                    : $"{ContractItemRows.Count:N0} linha(s) no cache permanente.";
        }
        catch (Exception exception)
        {
            ItemSummary = $"Não foi possível ler o cache: {exception.Message}";
        }
    }

    private async Task RefreshContractItemRowsAsync()
    {
        var contract = SelectedContract;
        if (contract is null)
        {
            ContractItemRows.Clear();
            return;
        }

        var rows = await _repository.GetItemDisplayRowsAsync(contract.PncpId).ConfigureAwait(true);
        if (!string.Equals(SelectedContract?.PncpId, contract.PncpId, StringComparison.Ordinal))
        {
            return;
        }

        ContractItemRows.Clear();
        foreach (var row in rows)
        {
            ContractItemRows.Add(row);
        }
    }

    private async Task RefreshDatasetSummaryAsync()
    {
        var state = await _repository.GetDatasetStateAsync().ConfigureAwait(true);
        var cache = await _repository.GetCacheSizeBytesAsync().ConfigureAwait(true);
        DatasetSummary = state.LastSuccessfulSync is null
            ? state.ContractCount == 0
                ? $"Índice vazio. Pasta: {_dataFolder}"
                : $"Carga parcial: {state.ContractCount:N0} contratações prontas para retomar | Pasta: {_dataFolder}"
            : $"{state.ContractCount:N0} contratações | {state.StartDate:dd/MM/yyyy}-{state.EndDate:dd/MM/yyyy} | " +
              $"Atualizado em {state.LastSuccessfulSync:dd/MM/yyyy HH:mm} | " +
              $"{state.CachedItemCount:N0} itens e {state.CachedResultCount:N0} resultados permanentes ({FormatBytes(cache)})";
    }

    private async Task RefreshCoverageAsync()
    {
        var end = DateOnly.FromDateTime(DateTime.Today);
        var start = end.AddDays(-364);
        IReadOnlyList<CoverageDay> stored = _repository is ICoverageRepository coverageRepository
            ? await coverageRepository.GetCoverageDaysAsync(start, end).ConfigureAwait(true)
            : [];
        var byDate = stored.ToDictionary(day => day.Date);
        CoverageDays.Clear();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            CoverageDays.Add(byDate.TryGetValue(date, out var day)
                ? day
                : new CoverageDay
                {
                    Date = date,
                    Status = CoverageStatus.Missing,
                    ExpectedModalities = 0,
                    CompletedModalities = 0
                });
        }

        var complete = CoverageDays.Count(day => day.IsComplete);
        CoverageSummary = new CoverageSummary(CoverageDays, complete, CoverageDays.Count).Display;
    }

    private async Task ExportBackupAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar backup do PNCP King",
            Filter = "Backup PNCP King (*.pncpking)|*.pncpking",
            DefaultExt = ".pncpking",
            FileName = $"PNCPKing-{DateTime.Today:yyyyMMdd}.pncpking"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunFileOperationAsync(async cancellationToken =>
        {
            StatusText = "Criando snapshot do banco permanente…";
            await _backupService.ExportAsync(dialog.FileName, cancellationToken).ConfigureAwait(true);
            StatusText = $"Backup criado em {dialog.FileName}";
        }).ConfigureAwait(true);
    }

    private async Task ImportBackupAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar backup do PNCP King",
            Filter = "Backup PNCP King (*.pncpking)|*.pncpking",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true ||
            MessageBox.Show(
                "O banco atual será substituído somente após validação e migração. Uma cópia recuperável será preservada. Continuar?",
                "Importar backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        StopItemSearch();
        await RunFileOperationAsync(async cancellationToken =>
        {
            StatusText = "Validando, migrando e importando o backup…";
            var recovery = await _backupService.ImportAsync(dialog.FileName, cancellationToken).ConfigureAwait(true);
            StatusText = $"Backup importado. Base anterior preservada em {recovery}";
            Preflight = null;
            await RefreshDatasetSummaryAsync().ConfigureAwait(true);
            await RefreshCoverageAsync().ConfigureAwait(true);
            await SearchAsync(resetSession: true).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task ClearCacheAsync()
    {
        var size = await _repository.GetCacheSizeBytesAsync().ConfigureAwait(true);
        if (MessageBox.Show(
                $"Limpar {FormatBytes(size)} do cache permanente de itens e resultados? O índice de contratações será preservado.",
                "Limpar cache",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunFileOperationAsync(async cancellationToken =>
        {
            await _repository.ClearItemCacheAsync(cancellationToken).ConfigureAwait(true);
            ContractItemRows.Clear();
            ItemSummary = "Cache permanente removido.";
            await RefreshDatasetSummaryAsync().ConfigureAwait(true);
            StatusText = "Cache permanente limpo; preços temporários da pesquisa atual não foram alterados.";
        }).ConfigureAwait(true);
    }

    private async Task RunFileOperationAsync(Func<CancellationToken, Task> action)
    {
        if (IsFileBusy)
        {
            return;
        }

        IsFileBusy = true;
        using var cancellation = new CancellationTokenSource();
        try
        {
            await action(cancellation.Token).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusText = $"Falha: {exception.Message}";
            MessageBox.Show(exception.Message, "PNCP King", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileBusy = false;
        }
    }

    private void OpenContract(ContractRecord? contract)
    {
        if (contract is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = contract.PortalUri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusText = $"Não foi possível abrir a contratação no navegador: {exception.Message}";
        }
    }

    private void TogglePause()
    {
        if (_syncService.IsPaused)
        {
            _syncService.Resume();
            IsIndexPaused = false;
            StatusText = "Sincronização retomada.";
        }
        else
        {
            _syncService.Pause();
            IsIndexPaused = true;
            StatusText = "Pausa solicitada; a requisição atual terminará antes de o índice parar. Use 'Retomar índice' para continuar.";
        }

        NotifyCommands();
    }

    private void SetItemSearchActive(bool value)
    {
        if (_isItemSearchActive == value)
        {
            return;
        }

        _isItemSearchActive = value;
        OnPropertyChanged(nameof(PriceActivityText));
        NotifyCommands();
    }

    private void SetPriceBusy(bool value, bool usesNetwork)
    {
        _priceOperationUsesNetwork = value && usesNetwork;
        if (IsPriceBusy == value)
        {
            OnPropertyChanged(nameof(PriceActivityText));
            OnPropertyChanged(nameof(IsPriceTransferActive));
            return;
        }

        IsPriceBusy = value;
    }

    private (DateOnly Start, DateOnly End) ResolveDateRange()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (!SelectedDateRange.IsCustom)
        {
            var days = SelectedDateRange.Days ?? 365;
            return (today.AddDays(-(days - 1)), today);
        }

        if (CustomStartDate is null || CustomEndDate is null)
        {
            throw new ArgumentException("Informe as duas datas do período personalizado.");
        }

        var start = DateOnly.FromDateTime(CustomStartDate.Value.Date);
        var end = DateOnly.FromDateTime(CustomEndDate.Value.Date);
        if (start > end)
        {
            throw new ArgumentException("A data inicial deve ser anterior ou igual à data final.");
        }

        return (start, end);
    }

    private (decimal? Minimum, decimal? Maximum) ParsePriceRange()
    {
        var minimum = ParseOptionalDecimal(MinimumPriceText, "preço mínimo");
        var maximum = ParseOptionalDecimal(MaximumPriceText, "preço máximo");
        if (minimum < 0 || maximum < 0)
        {
            throw new ArgumentException("A faixa de preço não aceita valores negativos.");
        }

        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            throw new ArgumentException("O preço mínimo deve ser menor ou igual ao preço máximo.");
        }

        return (minimum, maximum);
    }

    private static decimal? ParseOptionalDecimal(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.CurrentCulture, out var value))
        {
            return value;
        }

        throw new ArgumentException($"O {label} não é válido.");
    }

    private void NotifyCommands()
    {
        foreach (var command in new ICommand[]
                 {
                     SearchCommand, PreviousContractPageCommand, NextContractPageCommand,
                     LoadNextItemPageCommand, FireBatchesCommand, ApplyPriceFilterCommand,
                     StopItemSearchCommand, CalculatePreflightCommand, StartSyncCommand,
                     PauseSyncCommand, CancelIndexCommand, HydrateCommand, RetryPendingCommand,
                     OpenPncpCommand, ExportBackupCommand, ImportBackupCommand, ClearCacheCommand,
                     UseQuotationSampleCommand, UpdateQuotationSampleCommand,
                     AdjustQuotationWeightsCommand,
                     ConfirmQuotationBasketCommand, ExportQuotationCommand,
                     PreviousQuotationBasketPageCommand, NextQuotationBasketPageCommand
                 })
        {
            switch (command)
            {
                case AsyncRelayCommand asyncCommand:
                    asyncCommand.NotifyCanExecuteChanged();
                    break;
                case RelayCommand relayCommand:
                    relayCommand.NotifyCanExecuteChanged();
                    break;
                case RelayCommand<ContractRecord> contractCommand:
                    contractCommand.NotifyCanExecuteChanged();
                    break;
                case RelayCommand<QuotationReferenceDisplay> referenceCommand:
                    referenceCommand.NotifyCanExecuteChanged();
                    break;
            }
        }
    }

    private static bool IsGatewayTimeout(Exception exception) =>
        exception is HttpRequestException { StatusCode: HttpStatusCode.GatewayTimeout } ||
        exception.InnerException is not null && IsGatewayTimeout(exception.InnerException);

    private static string GetSynchronizationErrorMessage(Exception exception)
    {
        if (!IsGatewayTimeout(exception))
        {
            return exception.Message;
        }

        return "O servidor do PNCP demorou para responder (erro 504). " +
               "O checkpoint foi preservado e a próxima tentativa será automática " +
               $"em aproximadamente {SyncService.AutomaticRetryDelay.TotalMinutes:N0} minutos.";
    }

    private static IReadOnlyList<SearchGeoFilter> BuildGeoFilters()
    {
        var filters = new List<SearchGeoFilter>
        {
            SearchGeoFilter.All,
            SearchGeoFilter.NearRibeirao,
            SearchGeoFilter.Southeast
        };
        filters.AddRange(new[]
        {
            "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS",
            "MG", "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC",
            "SP", "SE", "TO"
        }.Select(SearchGeoFilter.State));
        return filters;
    }

    private static string BuildPreflightSummary(PreflightEstimate estimate) =>
        $"{estimate.ExactContractCount:N0} contratações | " +
        $"rede {FormatBytes(estimate.EstimatedTransferBytes)} | " +
        $"banco {FormatBytes(estimate.EstimatedDatabaseMinBytes)}-{FormatBytes(estimate.EstimatedDatabaseMaxBytes)} | " +
        $"cache total potencial {FormatBytes(estimate.EstimatedFullCacheMinBytes)}-{FormatBytes(estimate.EstimatedFullCacheMaxBytes)} | " +
        $"livre {FormatBytes(estimate.AvailableFreeBytes)} | " +
        $"~{estimate.EstimatedRequests:N0} requisições / {FormatDuration(estimate.EstimatedDuration)} | PDFs: 0 bytes";

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return $"{value:N1} {suffixes[suffix]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{duration.TotalDays:N1} dias";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:N1} horas";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.TotalMinutes:N0} minutos";
        }

        return $"{Math.Max(1, duration.TotalSeconds):N0} segundos";
    }

    private static string FormatDurationRange(TimeSpan minimum, TimeSpan maximum) =>
        $"{FormatDuration(minimum)} a {FormatDuration(maximum)}";
}
