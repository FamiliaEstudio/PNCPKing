using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PNCPKing.App.Services;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Api;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public enum ResultsWorkspace
{
    Search,
    Quotations
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int ContractPageSize = 20;

    private readonly IContractRepository _repository;
    private readonly PreflightService _preflightService;
    private readonly SyncService _syncService;
    private readonly AutoSyncCoordinator _autoSyncCoordinator;
    private readonly PncpRequestScheduler _requestScheduler;
    private readonly IPriceCacheRepository _priceCacheRepository;
    private readonly PriceCacheService _priceCacheService;
    private readonly ItemHydrationService _hydrationService;
    private readonly ItemSearchSessionService _itemSearchService;
    private readonly ItemSearchSessionService _transientItemSearchService;
    private readonly IQuotationItemSearchService _quotationItemSearchService;
    private readonly BackupService _backupService;
    private readonly IPncpRequestTelemetry _telemetry;
    private readonly ISweetCodeRepository _sweetCodeRepository;
    private readonly IContractDocumentService _documentService;
    private readonly IContractRelevantPageService _relevantPageService;
    private readonly IQuotationEvidenceExportService _evidenceService;
    private readonly IInternetPriceService _internetPriceService;
    private readonly IInternetEvidenceStore _internetEvidenceStore;
    private readonly IWindowCaptureService _windowCaptureService;
    private readonly IPdfPageRasterizer _pdfPageRasterizer;
    private readonly IDisposable _documentResources;
    private readonly DataGridColumnLayoutService _columnLayouts;
    private readonly string _dataFolder;
    private readonly IAiQuotationDraftService _aiDraftService;
    private readonly IAiCostEstimator _aiCostEstimator;
    private readonly IAiCredentialStore _aiCredentialStore;
    private readonly IAiDraftCache _aiDraftCache;
    private readonly IAiPromptRefinementService _aiPromptRefinementService;
    private readonly ITimedQuotationAutomationService _timedQuotationAutomation;
    private readonly AppSettingsService _settingsService;
    private readonly DesktopShortcutService _desktopShortcutService;
    private readonly AppDiagnosticLog _diagnosticLog;
    private readonly AppPerformanceTelemetry _performanceTelemetry;
    private readonly AdaptiveMaintenanceCoordinator _maintenanceCoordinator;
    private readonly DispatcherTimer _maintenanceTimer;
    private readonly DispatcherTimer _healthTimer;
    private readonly HashSet<string> _visibleItemKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentItemResultKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemSearchDisplayRow> _retainedItemRows = new(StringComparer.Ordinal);
    private CancellationTokenSource? _indexCancellation;
    private CancellationTokenSource? _contractSearchCancellation;
    private CancellationTokenSource? _contractCountCancellation;
    private CancellationTokenSource? _selectedContractCacheCancellation;
    private CancellationTokenSource? _visibleIdleResumeCancellation;
    private CancellationTokenSource? _priceCancellation;
    private CancellationTokenSource? _foregroundCancellation;
    private CancellationTokenSource? _documentCancellation;
    private IDisposable? _backgroundCacheSuppression;
    private SearchQuery? _activeSearchQuery;
    private ItemSearchLocalSummary? _localItemSearchSummary;
    private SearchExpression? _activeItemSearchExpression;
    private int _localPricePage;
    private PriceCacheLocalCursor? _localPriceCursor;
    private bool _hasMoreLocalPriceRows;
    private int _localPriceRowsLoaded;
    private bool _remotePriceExpansionStarted;
    private int _activeExpansionRemaining;
    private bool _activeExpansionExhaustive;
    private int _priceRunGeneration;
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
    private bool _isDocumentBusy;
    private double _operationProgress;
    private string _fileOperationProgressText = "Operação de arquivo inativa";
    private double _priceSearchProgress;
    private double _documentProgress;
    private string _documentProgressText = "Documentos: inativos";
    private int _currentContractPage = 1;
    private long _contractSearchTotal;
    private bool _contractSearchCountPending;
    private bool _contractSearchCountExact;
    private bool _contractPageHasMore;
    private int _contractSearchGeneration;
    private int _currentItemPage;
    private bool _hasMoreItemCandidates;
    private int _batchCount = ItemSearchDefaults.InitialBatchCount;
    private bool _searchUntilCandidateSetExhausted;
    private ResultsWorkspace _selectedResultsWorkspace = ResultsWorkspace.Search;
    private bool _isContractsPanelOpen;
    private int _contractPanelViewIndex;
    private int _selectedContractCacheGeneration;
    private bool _isMaintenancePanelOpen;
    private bool _isDesktopShortcutEnabled;
    private readonly HashSet<string> _openedMaintenanceIssues = new(StringComparer.Ordinal);
    private PreflightEstimate? _preflight;
    private bool _automaticMaintenanceRunning;
    private DateTimeOffset _nextMaintenanceAllowedAt;
    private DateOnly? _lastOptimizeDate;
    private bool _preferPriceCacheMaintenance;
    private string _maintenanceActivityText = "Manutenção: aguardando ociosidade";
    private string _resourceStatusText = "RAM física: verificando…";
    private string _interfaceIndicatorText = "Interface: medindo";
    private string _interfaceIndicatorBrush = "#6B7280";
    private string _interfaceIndicatorDetails = "Coletando os primeiros dados da interface.";
    private string _pncpIndicatorText = "PNCP: aguardando";
    private string _pncpIndicatorBrush = "#6B7280";
    private string _pncpIndicatorDetails = "Aguardando chamadas concluídas ao PNCP.";
    private bool _isItemSearchActive;
    private bool _sweetCodeEnabled;
    private bool _isApplicationReady;
    private bool _isInitializing = true;
    private string _startupStatus = "Abrindo o PNCP King…";
    private double _startupProgress;
    private readonly CancellationTokenSource _startupCancellation = new();
    private bool _quotationsInitialized;
    private bool _indexPausedForVisibleActivity;
    private bool _catalogPausedForVisibleActivity;
    private string? _selectedSweetCodeSuggestion;
    private bool _disposed;
    private SweetCodeLibrary _sweetCodeLibrary = new(true, []);

    public MainViewModel(
        IContractRepository repository,
        PreflightService preflightService,
        SyncService syncService,
        AutoSyncCoordinator autoSyncCoordinator,
        PncpRequestScheduler requestScheduler,
        IPriceCacheRepository priceCacheRepository,
        PriceCacheService priceCacheService,
        ItemHydrationService hydrationService,
        ItemSearchSessionService itemSearchService,
        ItemSearchSessionService transientItemSearchService,
        IQuotationItemSearchService quotationItemSearchService,
        BackupService backupService,
        QuotationService quotationService,
        IQuotationWorkbookService quotationWorkbookService,
        IQuotationWorkbookImportService quotationWorkbookImportService,
        IQuotationPackageService quotationPackageService,
        ICatalogRepository catalogRepository,
        CatalogSyncService catalogSyncService,
        ICatalogSearchService catalogSearchService,
        IPncpRequestTelemetry telemetry,
        ISweetCodeRepository sweetCodeRepository,
        IContractDocumentService documentService,
        IContractRelevantPageService relevantPageService,
        IQuotationEvidenceExportService evidenceService,
        IInternetPriceService internetPriceService,
        IInternetEvidenceStore internetEvidenceStore,
        IWindowCaptureService windowCaptureService,
        IPdfPageRasterizer pdfPageRasterizer,
        IDisposable documentResources,
        DataGridColumnLayoutService columnLayouts,
        IAiQuotationDraftService aiDraftService,
        IAiCostEstimator aiCostEstimator,
        IAiCredentialStore aiCredentialStore,
        IAiDraftCache aiDraftCache,
        IAiPromptRefinementService aiPromptRefinementService,
        ITimedQuotationAutomationService timedQuotationAutomation,
        AppSettingsService settingsService,
        DesktopShortcutService desktopShortcutService,
        bool desktopShortcutEnabled,
        int catalogRefreshIntervalDays,
        string dataFolder,
        AppDiagnosticLog diagnosticLog,
        AppPerformanceTelemetry performanceTelemetry,
        AdaptiveMaintenanceCoordinator maintenanceCoordinator)
    {
        _repository = repository;
        _preflightService = preflightService;
        _syncService = syncService;
        _autoSyncCoordinator = autoSyncCoordinator;
        _requestScheduler = requestScheduler;
        _priceCacheRepository = priceCacheRepository;
        _priceCacheService = priceCacheService;
        _hydrationService = hydrationService;
        _itemSearchService = itemSearchService;
        _transientItemSearchService = transientItemSearchService;
        _quotationItemSearchService = quotationItemSearchService;
        _backupService = backupService;
        _settingsService = settingsService;
        _desktopShortcutService = desktopShortcutService;
        _isDesktopShortcutEnabled = desktopShortcutEnabled;
        InitializeQuotation(
            quotationService,
            quotationWorkbookService,
            quotationWorkbookImportService,
            quotationPackageService);
        InitializeCatalog(
            catalogRepository,
            catalogSyncService,
            catalogSearchService,
            catalogRefreshIntervalDays);
        InitializePriceCache();
        _telemetry = telemetry;
        _sweetCodeRepository = sweetCodeRepository;
        _documentService = documentService;
        _relevantPageService = relevantPageService;
        _evidenceService = evidenceService;
        _internetPriceService = internetPriceService;
        _internetEvidenceStore = internetEvidenceStore;
        _windowCaptureService = windowCaptureService;
        _pdfPageRasterizer = pdfPageRasterizer;
        _documentResources = documentResources;
        _columnLayouts = columnLayouts;
        _aiDraftService = aiDraftService;
        _aiCostEstimator = aiCostEstimator;
        _aiCredentialStore = aiCredentialStore;
        _aiDraftCache = aiDraftCache;
        _aiPromptRefinementService = aiPromptRefinementService;
        _timedQuotationAutomation = timedQuotationAutomation;
        _dataFolder = dataFolder;
        _diagnosticLog = diagnosticLog;
        _performanceTelemetry = performanceTelemetry;
        _maintenanceCoordinator = maintenanceCoordinator;

        GeoFilters = BuildGeoFilters();
        SortOptions =
        [
            new SearchSortOption("Relevância", SearchSort.Relevance),
            new SearchSortOption("Mais recentes", SearchSort.Newest),
            new SearchSortOption("Mais próximas", SearchSort.Nearest)
        ];
        _selectedSortOption = SortOptions[1];
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

        SearchCommand = new AsyncRelayCommand(
            () => SearchAsync(resetSession: true, restartPriceSession: false),
            () => !IsFileBusy,
            allowConcurrentExecutions: true);
        RestartItemSearchCommand = new AsyncRelayCommand(
            RestartItemSearchAsync,
            () => !IsFileBusy && !IsPriceBusy && !string.IsNullOrWhiteSpace(QueryText));
        PreviousContractPageCommand = new AsyncRelayCommand(
            () => ChangeContractPageAsync(CurrentContractPage - 1),
            () => !IsFileBusy && CurrentContractPage > 1 && _activeSearchQuery is not null);
        NextContractPageCommand = new AsyncRelayCommand(
            () => ChangeContractPageAsync(CurrentContractPage + 1),
            () => !IsFileBusy && _contractPageHasMore && _activeSearchQuery is not null);
        CalculateExactContractCountCommand = new AsyncRelayCommand(
            CalculateExactContractCountAsync,
            () => !IsFileBusy && _activeSearchQuery is not null &&
                  !_contractSearchCountExact && !_contractSearchCountPending);
        CancelExactContractCountCommand = new RelayCommand(
            () => _contractCountCancellation?.Cancel(),
            () => _contractSearchCountPending && _contractCountCancellation is not null);
        LoadNextItemPageCommand = new AsyncRelayCommand(
            LoadNextItemPageAsync,
            () => !IsFileBusy && !IsPriceBusy && _hasMoreLocalPriceRows && _isItemSearchActive);
        FireBatchesCommand = new AsyncRelayCommand(
            FireBatchesAsync,
            () => !IsFileBusy && !IsPriceBusy && _isItemSearchActive);
        ApplyPriceFilterCommand = new AsyncRelayCommand(
            ApplyPriceFilterAsync,
            () => !IsFileBusy && !IsPriceBusy && _itemSearchService.CurrentSession is not null);
        StopItemSearchCommand = new RelayCommand(StopItemSearch, () => _isItemSearchActive);
        CalculatePreflightCommand = new AsyncRelayCommand(CalculatePreflightAsync, () => !IsFileBusy && !IsIndexBusy);
        StartSyncCommand = new AsyncRelayCommand(
            StartSyncAsync,
            () => !IsFileBusy && !IsIndexBusy && !IsCatalogBusy);
        PauseSyncCommand = new RelayCommand(TogglePause, () => _canPauseIndex && _indexCancellation is not null);
        CancelIndexCommand = new RelayCommand(() => _indexCancellation?.Cancel(), () => _indexCancellation is not null);
        HydrateCommand = new AsyncRelayCommand(
            () => HydrateSelectedAsync(true),
            () => !IsFileBusy && !IsForegroundBusy && SelectedContract is not null);
        RetryPendingCommand = new AsyncRelayCommand(
            () => HydrateSelectedAsync(false),
            () => !IsFileBusy && !IsForegroundBusy && SelectedContract is not null);
        OpenPncpCommand = new RelayCommand<ContractRecord>(OpenContract, contract => contract is not null);
        AccessDocumentsCommand = new AsyncRelayCommand<ContractRecord>(
            AccessDocumentsAsync,
            contract => contract is not null && !IsDocumentBusy && !IsFileBusy);
        AccessItemDocumentsCommand = new AsyncRelayCommand<ItemSearchDisplayRow>(
            AccessItemDocumentsAsync,
            row => row is not null && !IsDocumentBusy && !IsFileBusy);
        ClearDocumentCacheCommand = new AsyncRelayCommand(
            ClearDocumentCacheAsync,
            () => !IsDocumentBusy && !IsFileBusy);
        CancelDocumentOperationCommand = new RelayCommand(
            () => _documentCancellation?.Cancel(),
            () => _documentCancellation is not null);
        ExportBackupCommand = new AsyncRelayCommand(ExportBackupAsync, () => !IsFileBusy && !IsIndexBusy);
        ImportBackupCommand = new AsyncRelayCommand(
            ImportBackupAsync,
            () => !IsFileBusy && !IsIndexBusy && !IsForegroundBusy && !IsPriceBusy &&
                  !IsCatalogBusy && !IsPriceCacheBusy);
        OpenDiagnosticLogsCommand = new RelayCommand(OpenDiagnosticLogs);
        ExportPerformanceReportCommand = new AsyncRelayCommand(
            ExportPerformanceReportAsync,
            () => !IsFileBusy);
        ComparePerformanceReportCommand = new AsyncRelayCommand(
            ComparePerformanceReportAsync,
            () => !IsFileBusy);
        ClearCacheCommand = new AsyncRelayCommand(
            ClearCacheAsync,
            () => !IsFileBusy && !IsForegroundBusy && !IsPriceBusy);
        ManageSweetCodesCommand = new AsyncRelayCommand(ManageSweetCodesAsync, () => !IsFileBusy);
        ToggleContractsPanelCommand = new RelayCommand(ToggleContractsPanel);
        CloseContractsPanelCommand = new RelayCommand(() => IsContractsPanelOpen = false);
        OpenSelectedContractCacheCommand = new RelayCommand(
            OpenSelectedContractCache,
            () => SelectedContract is not null);
        ToggleMaintenancePanelCommand = new RelayCommand(
            () => IsMaintenancePanelOpen = !IsMaintenancePanelOpen);
        ToggleDesktopShortcutCommand = new AsyncRelayCommand(ToggleDesktopShortcutAsync);
        CancelStartupCommand = new RelayCommand(() => _startupCancellation.Cancel());

        _maintenanceTimer = new DispatcherTimer { Interval = SyncService.AutomaticRetryDelay };
        _maintenanceTimer.Tick += OnMaintenanceTimerTick;
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _healthTimer.Tick += (_, _) => RefreshPerformanceHealth();
    }

    public RangeObservableCollection<ContractRecord> ContractResults { get; } = [];
    public RangeObservableCollection<ItemSearchDisplayRow> ItemSearchRows { get; } = [];
    public RangeObservableCollection<ItemDisplayRow> ContractItemRows { get; } = [];
    public RangeObservableCollection<CoverageDay> CoverageDays { get; } = [];
    public RangeObservableCollection<string> SweetCodeSuggestions { get; } = [];
    public IReadOnlyList<SearchGeoFilter> GeoFilters { get; }
    public IReadOnlyList<SearchSortOption> SortOptions { get; }
    public IReadOnlyList<DateRangeOption> DateRanges { get; }

    public int SelectedBasketPriceCount =>
        _retainedItemRows.Values.Count(row => row.IsSelectedForBasket);

    public bool HasSelectedBasketPrices => SelectedBasketPriceCount > 0;

    public string ManualBasketButtonText =>
        $"Criar/adicionar à cesta ({SelectedBasketPriceCount:N0})";

    public ICommand SearchCommand { get; }
    public ICommand PreviousContractPageCommand { get; }
    public ICommand NextContractPageCommand { get; }
    public ICommand CalculateExactContractCountCommand { get; }
    public ICommand CancelExactContractCountCommand { get; }
    public ICommand LoadNextItemPageCommand { get; }
    public ICommand FireBatchesCommand { get; }
    public ICommand RestartItemSearchCommand { get; }
    public ICommand ApplyPriceFilterCommand { get; }
    public ICommand StopItemSearchCommand { get; }
    public ICommand CalculatePreflightCommand { get; }
    public ICommand StartSyncCommand { get; }
    public ICommand PauseSyncCommand { get; }
    public ICommand CancelIndexCommand { get; }
    public ICommand HydrateCommand { get; }
    public ICommand RetryPendingCommand { get; }
    public ICommand OpenPncpCommand { get; }
    public ICommand AccessDocumentsCommand { get; }
    public ICommand AccessItemDocumentsCommand { get; }
    public ICommand ClearDocumentCacheCommand { get; }
    public ICommand CancelDocumentOperationCommand { get; }
    public ICommand ExportBackupCommand { get; }
    public ICommand ImportBackupCommand { get; }
    public ICommand OpenDiagnosticLogsCommand { get; }
    public ICommand ExportPerformanceReportCommand { get; }
    public ICommand ComparePerformanceReportCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand ManageSweetCodesCommand { get; }
    public ICommand ToggleContractsPanelCommand { get; }
    public ICommand CloseContractsPanelCommand { get; }
    public ICommand OpenSelectedContractCacheCommand { get; }
    public ICommand ToggleMaintenancePanelCommand { get; }
    public ICommand ToggleDesktopShortcutCommand { get; }
    public ICommand CancelStartupCommand { get; }

    public bool IsApplicationReady
    {
        get => _isApplicationReady;
        private set => SetProperty(ref _isApplicationReady, value);
    }

    public bool IsInitializing
    {
        get => _isInitializing;
        private set => SetProperty(ref _isInitializing, value);
    }

    public string StartupStatus
    {
        get => _startupStatus;
        private set => SetProperty(ref _startupStatus, value);
    }

    public string MaintenanceActivityText
    {
        get => _maintenanceActivityText;
        private set
        {
            if (SetProperty(ref _maintenanceActivityText, value))
            {
                OnPropertyChanged(nameof(MaintenancePanelSummary));
            }
        }
    }

    public string ResourceStatusText
    {
        get => _resourceStatusText;
        private set
        {
            if (SetProperty(ref _resourceStatusText, value))
            {
                OnPropertyChanged(nameof(MaintenancePanelSummary));
            }
        }
    }

    public string MaintenancePanelSummary => $"{MaintenanceActivityText} · {ResourceStatusText}";

    public string InterfaceIndicatorText
    {
        get => _interfaceIndicatorText;
        private set => SetProperty(ref _interfaceIndicatorText, value);
    }

    public string InterfaceIndicatorBrush
    {
        get => _interfaceIndicatorBrush;
        private set => SetProperty(ref _interfaceIndicatorBrush, value);
    }

    public string InterfaceIndicatorDetails
    {
        get => _interfaceIndicatorDetails;
        private set => SetProperty(ref _interfaceIndicatorDetails, value);
    }

    public string PncpIndicatorText
    {
        get => _pncpIndicatorText;
        private set => SetProperty(ref _pncpIndicatorText, value);
    }

    public string PncpIndicatorBrush
    {
        get => _pncpIndicatorBrush;
        private set => SetProperty(ref _pncpIndicatorBrush, value);
    }

    public string PncpIndicatorDetails
    {
        get => _pncpIndicatorDetails;
        private set => SetProperty(ref _pncpIndicatorDetails, value);
    }

    public bool IsMaintenancePanelOpen
    {
        get => _isMaintenancePanelOpen;
        set => SetProperty(ref _isMaintenancePanelOpen, value);
    }

    public bool IsDesktopShortcutEnabled
    {
        get => _isDesktopShortcutEnabled;
        private set => SetProperty(ref _isDesktopShortcutEnabled, value);
    }

    public double StartupProgress
    {
        get => _startupProgress;
        private set => SetProperty(ref _startupProgress, Math.Clamp(value, 0d, 100d));
    }

    public CancellationToken StartupCancellationToken => _startupCancellation.Token;

    public void SetStartupPhase(string phase)
    {
        StartupStatus = phase;
        StatusText = phase;
    }

    public void SetStartupProgress(DatabaseInitializationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        StartupProgress = progress.Percentage;
        StartupStatus = $"{progress.Phase} · banco v{progress.PreviousVersion} → v{progress.TargetVersion}\n" +
                        progress.Message;
        StatusText = progress.Message;
    }

    public void CompleteStartup()
    {
        IsApplicationReady = true;
        IsInitializing = false;
        StartupProgress = 100;
        StartupStatus = "Inicialização concluída.";
        StatusText = "Pronto";
        StartPerformanceHealthMonitoring();
        NotifyCommands();
    }

    private void StartPerformanceHealthMonitoring()
    {
        RefreshPerformanceHealth();
        _healthTimer.Start();
    }

    private void RefreshPerformanceHealth()
    {
        try
        {
            var live = _performanceTelemetry.GetLiveSnapshot(PerformanceHealthEvaluator.RollingWindow);
            var recentPncp = _telemetry.GetRecentSnapshot(PerformanceHealthEvaluator.RollingWindow);
            var evaluation = PerformanceHealthEvaluator.Evaluate(live, recentPncp);
            InterfaceIndicatorText = $"Interface: {evaluation.InterfaceLabel}";
            InterfaceIndicatorBrush = IndicatorBrush(evaluation.Interface);
            PncpIndicatorText = $"PNCP: {evaluation.PncpLabel}";
            PncpIndicatorBrush = IndicatorBrush(evaluation.Pncp);
            ResourceStatusText =
                $"RAM livre: {FormatBytes(live.Resources.AvailablePhysicalMemoryBytes)} de " +
                $"{FormatBytes(live.Resources.TotalPhysicalMemoryBytes)} " +
                $"({live.Resources.MemoryLoadPercent}% em uso)";
            InterfaceIndicatorDetails = BuildInterfaceIndicatorDetails(live, evaluation);
            PncpIndicatorDetails = BuildPncpIndicatorDetails(live, recentPncp, evaluation);
        }
        catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
        {
            InterfaceIndicatorText = "Interface: indisponível";
            InterfaceIndicatorBrush = IndicatorBrush(PerformanceIndicatorLevel.Measuring);
            PncpIndicatorText = "PNCP: indisponível";
            PncpIndicatorBrush = IndicatorBrush(PerformanceIndicatorLevel.Measuring);
            InterfaceIndicatorDetails = $"Não foi possível medir a interface: {exception.Message}";
            PncpIndicatorDetails = $"Não foi possível medir o PNCP: {exception.Message}";
        }
    }

    private static string BuildInterfaceIndicatorDetails(
        LivePerformanceSnapshot live,
        PerformanceHealthEvaluation evaluation)
    {
        var dispatcher = live.DispatcherDelaySamples == 0
            ? "Nenhum atraso ≥ 25 ms foi observado no último minuto"
            : $"Atrasos da interface: p95 {live.DispatcherDelayP95.TotalMilliseconds:N0} ms; " +
              $"máximo {live.DispatcherDelayMaximum.TotalMilliseconds:N0} ms; " +
              $"amostras {live.DispatcherDelaySamples:N0}";
        return
            $"{evaluation.InterfaceReason}\n" +
            $"{dispatcher}.\n" +
            $"RAM: {FormatBytes(live.Resources.AvailablePhysicalMemoryBytes)} livre de " +
            $"{FormatBytes(live.Resources.TotalPhysicalMemoryBytes)}; " +
            $"{live.Resources.MemoryLoadPercent}% em uso; perfil {live.Resources.Pressure}.";
    }

    private static string BuildPncpIndicatorDetails(
        LivePerformanceSnapshot live,
        PncpRecentRequestSnapshot recentPncp,
        PerformanceHealthEvaluation evaluation)
    {
        var scheduler = live.Scheduler;
        var network = scheduler is null
            ? "Fila e concorrência: aguardando medições"
            : $"Concorrência {scheduler.ActiveRequests:N0}/{scheduler.EffectiveConcurrency:N0}/" +
              $"{scheduler.MaximumConcurrency:N0} (ativas/efetiva/máxima); fila {scheduler.TotalQueued:N0}";
        var recovery = scheduler?.GrowthBlockedUntil is { } blockedUntil && blockedUntil > live.CapturedAt
            ? $"recuperação até {blockedUntil.ToLocalTime():HH:mm:ss}"
            : "sem recuperação ativa";
        var lastReduction = string.IsNullOrWhiteSpace(scheduler?.LastReductionReason)
            ? "sem motivo de recuo registrado"
            : $"último recuo: {scheduler.LastReductionReason}";
        return
            $"{evaluation.PncpReason}\n" +
            $"Último minuto: {recentPncp.Succeeded:N0} sucesso(s), " +
            $"{recentPncp.Failed:N0} falha(s) real(is), {recentPncp.Canceled:N0} cancelamento(s); " +
            $"p50 {FormatOptionalDuration(recentPncp.P50)}, " +
            $"p95 {FormatOptionalDuration(recentPncp.P95)}.\n" +
            $"{network}; {lastReduction}; {recovery}. " +
            "Cancelamentos são apenas diagnósticos e não alteram o estado do PNCP.";
    }

    private static string FormatOptionalDuration(TimeSpan? duration) =>
        duration is { } value ? $"{value.TotalSeconds:N1}s" : "aguardando";

    private static string IndicatorBrush(PerformanceIndicatorLevel level) => level switch
    {
        PerformanceIndicatorLevel.Good => "#2E7D32",
        PerformanceIndicatorLevel.Warning => "#B26A00",
        PerformanceIndicatorLevel.Critical => "#B3261E",
        _ => "#6B7280"
    };

    public string QueryText
    {
        get => _queryText;
        set
        {
            if (SetProperty(ref _queryText, value))
            {
                RefreshSweetCodeSuggestions();
            }
        }
    }

    public bool SweetCodeEnabled
    {
        get => _sweetCodeEnabled;
        set
        {
            if (SetProperty(ref _sweetCodeEnabled, value))
            {
                _ = _sweetCodeRepository.SetEnabledAsync(value);
                RefreshSweetCodeSuggestions();
            }
        }
    }

    public string? SelectedSweetCodeSuggestion
    {
        get => _selectedSweetCodeSuggestion;
        set => SetProperty(ref _selectedSweetCodeSuggestion, value);
    }

    public bool HasSweetCodeSuggestions => SweetCodeEnabled && SweetCodeSuggestions.Count > 0;

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

    public bool SearchUntilCandidateSetExhausted
    {
        get => _searchUntilCandidateSetExhausted;
        set => SetProperty(ref _searchUntilCandidateSetExhausted, value);
    }

    public ContractRecord? SelectedContract
    {
        get => _selectedContract;
        set
        {
            if (SetProperty(ref _selectedContract, value))
            {
                CancelSelectedContractCacheLoad(clearRows: true);
                NotifyCommands();
                if (IsContractsPanelOpen && ContractPanelViewIndex == 1)
                {
                    QueueSelectedContractCacheLoad();
                }
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

    public void ToggleItemPricePin(ItemSearchDisplayRow? row)
    {
        if (row is null)
        {
            return;
        }

        row.IsPinned = !row.IsPinned;
        SynchronizeRetainedItemRow(row);
        StatusText = row.IsPinned
            ? "Preço fixado; ele continuará visível ao mudar os critérios."
            : "Fixação removida.";
    }

    public void ToggleItemPriceBasketSelection(ItemSearchDisplayRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.IsBasketEligible)
        {
            StatusText = "Somente preços homologados ativos e positivos podem ser selecionados para a cesta.";
            return;
        }

        row.IsSelectedForBasket = !row.IsSelectedForBasket;
        SynchronizeRetainedItemRow(row);
        StatusText = row.IsSelectedForBasket
            ? $"Preço selecionado para a cesta ({SelectedBasketPriceCount:N0} marcado(s))."
            : $"Preço retirado da seleção da cesta ({SelectedBasketPriceCount:N0} marcado(s)).";
    }

    public IReadOnlyList<ItemSearchDisplayRow> GetSelectedBasketPrices() =>
        _retainedItemRows.Values
            .Where(row => row.IsSelectedForBasket)
            .ToArray();

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

    public string FileOperationProgressText
    {
        get => _fileOperationProgressText;
        private set => SetProperty(ref _fileOperationProgressText, value);
    }

    public double PriceSearchProgress
    {
        get => _priceSearchProgress;
        private set => SetProperty(ref _priceSearchProgress, Math.Clamp(value, 0d, 100d));
    }

    public bool IsDocumentBusy
    {
        get => _isDocumentBusy;
        private set
        {
            if (SetProperty(ref _isDocumentBusy, value))
            {
                NotifyCommands();
            }
        }
    }

    public double DocumentProgress
    {
        get => _documentProgress;
        private set => SetProperty(ref _documentProgress, Math.Clamp(value, 0d, 100d));
    }

    public string DocumentProgressText
    {
        get => _documentProgressText;
        private set => SetProperty(ref _documentProgressText, value);
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

    public string ContractPageSummary => _contractSearchCountPending
        ? $"Página {CurrentContractPage} - calculando o total exato…"
        : _contractSearchCountExact
            ? $"Página {CurrentContractPage} - {ContractSearchTotal:N0} contratação(ões)"
            : $"Página {CurrentContractPage} - pelo menos {ContractSearchTotal:N0} contratação(ões)";

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

    public ResultsWorkspace SelectedResultsWorkspace
    {
        get => _selectedResultsWorkspace;
        set
        {
            if (SetProperty(ref _selectedResultsWorkspace, value) &&
                value == ResultsWorkspace.Quotations && !_quotationsInitialized)
            {
                _ = InitializeQuotationsOnDemandAsync();
            }
        }
    }

    public bool IsContractsPanelOpen
    {
        get => _isContractsPanelOpen;
        set
        {
            if (!SetProperty(ref _isContractsPanelOpen, value))
            {
                return;
            }

            if (!value)
            {
                CancelSelectedContractCacheLoad(clearRows: false);
            }
            else if (ContractPanelViewIndex == 1)
            {
                QueueSelectedContractCacheLoad();
            }
        }
    }

    public int ContractPanelViewIndex
    {
        get => _contractPanelViewIndex;
        set
        {
            if (!SetProperty(ref _contractPanelViewIndex, Math.Clamp(value, 0, 1)))
            {
                return;
            }

            if (_contractPanelViewIndex == 1 && IsContractsPanelOpen)
            {
                QueueSelectedContractCacheLoad();
            }
            else
            {
                CancelSelectedContractCacheLoad(clearRows: false);
            }
        }
    }

    public string DataFolder => _dataFolder;

    public PreflightEstimate? Preflight
    {
        get => _preflight;
        private set => SetProperty(ref _preflight, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await LoadSweetCodesAsync().ConfigureAwait(true);
    }

    public async Task InitializeDeferredAsync(CancellationToken cancellationToken = default)
    {
        using var span = _performanceTelemetry.Begin("startup", "deferred-initialization");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SearchAsync(resetSession: false).ConfigureAwait(true);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshDatasetSummaryAsync().ConfigureAwait(true);
            await RefreshCoverageAsync().ConfigureAwait(true);
            await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
            await RefreshCatalogCoverageAsync().ConfigureAwait(true);
            span.Complete();
        }
        catch (OperationCanceledException exception)
        {
            span.Fail(exception);
            throw;
        }
        catch (Exception exception)
        {
            span.Fail(exception);
            _diagnosticLog.Warning("startup", $"Carga secundária adiada: {exception.Message}");
        }
    }

    public void StartBackgroundMaintenance()
    {
        if (_disposed)
        {
            return;
        }

        _maintenanceCoordinator.NotifyVisibleActivity();
        _ = RunAutomaticMaintenanceCycleAsync();
    }

    public void NotifyMaintenancePausedForVisibleActivity()
    {
        if (!_disposed)
        {
            MaintenanceActivityText =
                "Manutenção: pausada para priorizar sua atividade; retomada após 30 s";
        }
    }

    private async Task InitializeQuotationsOnDemandAsync()
    {
        if (_quotationsInitialized || _disposed)
        {
            return;
        }

        _quotationsInitialized = true;
        try
        {
            await _quotationService.RecoverInterruptedAutomationAsync().ConfigureAwait(true);
            await RefreshQuotationProjectsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _quotationsInitialized = false;
            _diagnosticLog.Warning("quotations", $"Carga sob demanda adiada: {exception.Message}");
        }
    }

    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _startupCancellation.Cancel();
        _visibleIdleResumeCancellation?.Cancel();
        _maintenanceTimer.Stop();
        _healthTimer.Stop();
        _indexCancellation?.Cancel();
        _contractSearchCancellation?.Cancel();
        _contractCountCancellation?.Cancel();
        _selectedContractCacheCancellation?.Cancel();
        _priceCancellation?.Cancel();
        _foregroundCancellation?.Cancel();
        _documentCancellation?.Cancel();
        _catalogCancellation?.Cancel();
        _priceCacheCycleCancellation?.Cancel();
        _quotationAutomationCancellation?.Cancel();
        _backgroundCacheSuppression?.Dispose();
        _backgroundCacheSuppression = null;
        await _columnLayouts.FlushAsync().ConfigureAwait(true);
        if (_quotationAutomationCompletion is { } quotationCompletion)
        {
            await quotationCompletion.Task.ConfigureAwait(true);
        }

        if (_priceCacheCycleTask is { } priceCacheTask)
        {
            try
            {
                await priceCacheTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Normal application shutdown.
            }
        }

        SetItemSearchActive(false);
        await _itemSearchService.DisposeAsync().ConfigureAwait(false);
        await _transientItemSearchService.DisposeAsync().ConfigureAwait(false);
        _documentResources.Dispose();
        _visibleIdleResumeCancellation?.Dispose();
        _selectedContractCacheCancellation?.Dispose();
        _startupCancellation.Dispose();
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    public void ApplySelectedSweetCode()
    {
        if (SelectedSweetCodeSuggestion is not { Length: > 0 } suggestion)
        {
            return;
        }

        QueryText = suggestion;
        SweetCodeSuggestions.Clear();
        SelectedSweetCodeSuggestion = null;
        OnPropertyChanged(nameof(HasSweetCodeSuggestions));
    }

    public void DismissSweetCodeSuggestions()
    {
        SweetCodeSuggestions.Clear();
        SelectedSweetCodeSuggestion = null;
        OnPropertyChanged(nameof(HasSweetCodeSuggestions));
    }

    private async Task LoadSweetCodesAsync()
    {
        _sweetCodeLibrary = await _sweetCodeRepository.LoadAsync().ConfigureAwait(true);
        _sweetCodeEnabled = _sweetCodeLibrary.Enabled;
        OnPropertyChanged(nameof(SweetCodeEnabled));
        RefreshSweetCodeSuggestions();
    }

    private async Task ManageSweetCodesAsync()
    {
        var window = new Views.SweetCodeWindow(_sweetCodeLibrary.Enabled, _sweetCodeLibrary.Expressions)
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        IsFileBusy = true;
        try
        {
            await _sweetCodeRepository.SaveAsync(window.SweetCodesEnabled, window.Expressions).ConfigureAwait(true);
            _sweetCodeLibrary = new SweetCodeLibrary(window.SweetCodesEnabled, window.Expressions);
            _sweetCodeEnabled = window.SweetCodesEnabled;
            OnPropertyChanged(nameof(SweetCodeEnabled));
            RefreshSweetCodeSuggestions();
            StatusText = $"Sweet Code atualizado: {window.Expressions.Count:N0} expressão(ões).";
        }
        finally
        {
            IsFileBusy = false;
        }
    }

    private void RefreshSweetCodeSuggestions()
    {
        SweetCodeSuggestions.Clear();
        SelectedSweetCodeSuggestion = null;
        if (!SweetCodeEnabled)
        {
            OnPropertyChanged(nameof(HasSweetCodeSuggestions));
            return;
        }

        var prefix = SearchText.Normalize(QueryText);
        if (prefix.Length == 0)
        {
            OnPropertyChanged(nameof(HasSweetCodeSuggestions));
            return;
        }

        foreach (var expression in _sweetCodeLibrary.Expressions
                     .Where(expression => SearchText.Normalize(expression)
                         .StartsWith(prefix, StringComparison.Ordinal))
                     .Take(12))
        {
            SweetCodeSuggestions.Add(expression);
        }

        SelectedSweetCodeSuggestion = SweetCodeSuggestions.FirstOrDefault();
        OnPropertyChanged(nameof(HasSweetCodeSuggestions));
    }

    private async Task SearchAsync(
        bool resetSession,
        bool restartPriceSession = false)
    {
        var exhaustCandidateSet = SearchUntilCandidateSetExhausted;
        var previousSessionId = _itemSearchService.CurrentSession?.Id;
        var interruptedBatchRemainder = resetSession && !restartPriceSession && IsPriceBusy &&
                                        !_activeExpansionExhaustive
            ? Math.Max(0, _activeExpansionRemaining)
            : 0;
        CancellationTokenSource? priceCancellation = null;
        if (resetSession)
        {
            Interlocked.Increment(ref _priceRunGeneration);
            var previousPriceCancellation = _priceCancellation;
            priceCancellation = new CancellationTokenSource();
            _priceCancellation = priceCancellation;
            previousPriceCancellation?.Cancel();
            _itemSearchService.Stop();
            previousPriceCancellation?.Dispose();
        }

        using var visibleActivity = _requestScheduler.SuppressBackgroundRequests();
        _visibleIdleResumeCancellation?.Cancel();
        _visibleIdleResumeCancellation?.Dispose();
        _visibleIdleResumeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _startupCancellation.Token);
        var visibleIdleToken = _visibleIdleResumeCancellation.Token;
        _priceCacheService.PauseForVisibleActivity();
        if (IsIndexBusy && !_syncService.IsPaused)
        {
            _syncService.Pause();
            IsIndexPaused = true;
            _indexPausedForVisibleActivity = true;
        }

        if (IsCatalogBusy && !IsCatalogPaused)
        {
            _catalogSyncService.Pause();
            IsCatalogPaused = true;
            _catalogPausedForVisibleActivity = true;
        }

        _contractSearchCancellation?.Cancel();
        _contractSearchCancellation?.Dispose();
        _contractCountCancellation?.Cancel();
        _contractCountCancellation?.Dispose();
        _contractCountCancellation = null;
        _contractSearchCountPending = false;
        _contractSearchCountExact = false;
        _contractPageHasMore = false;
        ContractSearchTotal = 0;
        _contractSearchCancellation = new CancellationTokenSource();
        var contractSearchToken = _contractSearchCancellation.Token;
        var searchGeneration = Interlocked.Increment(ref _contractSearchGeneration);
        try
        {
            var (startDate, endDate) = ResolveDateRange();
            var sort = SelectedSortOption.Value;
            var activeSearchQuery = new SearchQuery(
                QueryText.Trim(),
                SelectedGeoFilter,
                startDate,
                endDate,
                sort,
                1,
                ContractPageSize);
            _activeSearchQuery = activeSearchQuery;
            await LoadContractPageAsync(1, searchGeneration, contractSearchToken).ConfigureAwait(true);
            if (searchGeneration != Volatile.Read(ref _contractSearchGeneration))
            {
                return;
            }

            if (!resetSession || string.IsNullOrWhiteSpace(activeSearchQuery.Text))
            {
                if (string.IsNullOrWhiteSpace(activeSearchQuery.Text))
                {
                    StopItemSearch();
                    ResetCurrentItemRows();
                    CurrentItemPage = 0;
                    HasMoreItemCandidates = false;
                    ItemSearchSummary = "Pesquisa vazia: nenhuma chamada de itens ou preços foi iniciada.";
                    SelectedResultsWorkspace = ResultsWorkspace.Search;
                    IsContractsPanelOpen = false;
                }

                return;
            }

            var activePriceCancellation = priceCancellation ??
                throw new InvalidOperationException("A pesquisa de itens não possui token de cancelamento.");
            SetItemSearchActive(false);
            ResetCurrentItemRows();
            CurrentItemPage = 0;
            HasMoreItemCandidates = true;
            SelectedItemSearchRow = null;
            SelectedResultsWorkspace = ResultsWorkspace.Search;
            _searchTelemetryBaseline = _telemetry.GetSnapshot();
            PriceSearchProgress = 0;
            _activeItemSearchExpression = SearchText.Parse(activeSearchQuery.Text);
            _localPricePage = 0;
            _localPriceCursor = null;
            _hasMoreLocalPriceRows = false;
            _localPriceRowsLoaded = 0;
            _remotePriceExpansionStarted = false;
            _localItemSearchSummary = null;
            ItemSearchSummary = "Buscando a primeira página no cache local…";
            var localSummaryTask = RefreshLocalItemSearchSummaryAsync(
                activeSearchQuery,
                _activeItemSearchExpression,
                searchGeneration,
                activePriceCancellation.Token);
            var itemSessionTask = Task.Run(
                () => _itemSearchService.StartAsync(
                    activeSearchQuery with { Page = 1, PageSize = 200 },
                    restartPriceSession,
                    activePriceCancellation.Token),
                activePriceCancellation.Token);
            var localRows = await LoadLocalPricePageAsync(activePriceCancellation.Token).ConfigureAwait(true);
            await Task.Yield();
            var itemSession = await itemSessionTask.ConfigureAwait(true);
            if (searchGeneration != Volatile.Read(ref _contractSearchGeneration))
            {
                return;
            }

            var (minimum, maximum) = ParsePriceRange();
            var restoredRows = await _itemSearchService.GetDiscoveredRowsAsync(
                    minimum,
                    maximum,
                    activePriceCancellation.Token)
                .ConfigureAwait(true);
            AppendUniqueRows(restoredRows);
            SetItemSearchActive(true);
            NotifyCommands();
            if (exhaustCandidateSet)
            {
                ItemSearchSummary = "Calculando o total de contratações candidatas antes da busca completa…";
                await localSummaryTask.ConfigureAwait(true);
                if (!ConfirmExhaustiveItemSearch())
                {
                    HasMoreItemCandidates = true;
                    ItemSearchSummary =
                        $"{BuildLocalSearchSummary()} Busca completa não iniciada; " +
                        "os resultados locais foram preservados.";
                    StatusText = "Busca completa cancelada antes das chamadas de preços.";
                    return;
                }
            }
            else
            {
                _ = localSummaryTask;
            }

            HasMoreItemCandidates = true;
            ItemSearchSummary =
                $"{BuildLocalSearchSummary()} {restoredRows.Count:N0} linha(s) da sessão retomável; " +
                (exhaustCandidateSet
                    ? "iniciando busca até esgotar as contratações candidatas."
                    : "iniciando ampliação da cobertura.");
            StatusText = localRows > 0
                ? "Resultados locais entregues; ampliando com contratações ainda não resolvidas."
                : "Ampliando com contratações ainda não resolvidas.";
            _remotePriceExpansionStarted = true;
            int? exactRemainder = previousSessionId == itemSession.Id && interruptedBatchRemainder > 0
                ? interruptedBatchRemainder
                : null;
            await RunSelectedBatchesAsync(
                    activePriceCancellation.Token,
                    exactRemainder,
                    exhaustCandidateSet)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Pesquisa anterior interrompida.";
        }
        catch (Exception exception)
        {
            StatusText = $"Não foi possível pesquisar: {exception.Message}";
            ItemSearchSummary = $"Pesquisa rejeitada: {exception.Message}";
        }
        finally
        {
            if (_indexPausedForVisibleActivity || _catalogPausedForVisibleActivity)
            {
                _ = ResumeVisiblePausedWorkAfterIdleAsync(
                    _indexPausedForVisibleActivity,
                    _catalogPausedForVisibleActivity,
                    resumePriceCache: true,
                    cancellationToken: visibleIdleToken);
            }
        }
    }

    private async Task ResumeVisiblePausedWorkAfterIdleAsync(
        bool resumeIndex,
        bool resumeCatalog,
        bool resumePriceCache,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                    AdaptiveMaintenanceCoordinator.VisibleIdleDelay,
                    cancellationToken)
                .ConfigureAwait(true);
            if (resumeIndex && IsIndexBusy && _syncService.IsPaused)
            {
                _syncService.Resume();
                IsIndexPaused = false;
                _indexPausedForVisibleActivity = false;
            }

            if (resumeCatalog && IsCatalogBusy && IsCatalogPaused)
            {
                _catalogSyncService.Resume();
                IsCatalogPaused = false;
                _catalogPausedForVisibleActivity = false;
            }

            if (resumePriceCache)
            {
                _priceCacheService.ResumeAfterVisibleActivity();
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento durante a janela de ociosidade.
        }
    }

    private async Task LoadContractPageAsync(
        int page,
        int? expectedGeneration = null,
        CancellationToken cancellationToken = default)
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
        var generation = expectedGeneration ?? Volatile.Read(ref _contractSearchGeneration);
        var query = _activeSearchQuery with
        {
            Page = Math.Max(1, page),
            PageSize = ContractPageSize
        };
        var result = await Task.Run(
                () => _repository.SearchPageAsync(query, cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);
        if (generation != Volatile.Read(ref _contractSearchGeneration))
        {
            return;
        }

        using var applySpan = _performanceTelemetry.Begin("ui", "contract-results-apply");
        ContractResults.ReplaceAll(result.Results);
        CurrentContractPage = result.Page;
        _contractPageHasMore = result.MayHaveMore;
        var observedTotal = Math.Max(
            result.Results.Count,
            (result.Page - 1L) * result.PageSize + result.Results.Count +
            (result.MayHaveMore ? 1 : 0));
        if (!_contractSearchCountExact)
        {
            ContractSearchTotal = Math.Max(ContractSearchTotal, observedTotal);
            if (!result.MayHaveMore)
            {
                _contractSearchCountExact = true;
                ContractSearchTotal = (result.Page - 1L) * result.PageSize + result.Results.Count;
            }
        }
        OnPropertyChanged(nameof(ContractPageSummary));
        StatusText = _contractSearchCountExact
            ? $"Índice local: {ContractSearchTotal:N0} contratação(ões)."
            : $"Índice local: {result.Results.Count:N0} linha(s) exibida(s); total exato sob demanda.";
        NotifyCommands();
        applySpan.Complete(result.Results.Count);
    }

    private async Task CalculateExactContractCountAsync()
    {
        var query = _activeSearchQuery;
        var searchCancellation = _contractSearchCancellation;
        if (query is null || searchCancellation is null)
        {
            return;
        }

        _contractCountCancellation?.Cancel();
        _contractCountCancellation?.Dispose();
        _contractCountCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            searchCancellation.Token,
            _startupCancellation.Token);
        _contractSearchCountPending = true;
        OnPropertyChanged(nameof(ContractPageSummary));
        NotifyCommands();
        await CompleteContractCountAsync(
                query,
                Volatile.Read(ref _contractSearchGeneration),
                _contractCountCancellation.Token)
            .ConfigureAwait(true);
    }

    private async Task CompleteContractCountAsync(
        SearchQuery query,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var total = await Task.Run(
                    () => _repository.CountSearchAsync(query, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);
            if (generation != Volatile.Read(ref _contractSearchGeneration))
            {
                return;
            }

            ContractSearchTotal = total;
            _contractSearchCountPending = false;
            _contractSearchCountExact = true;
            OnPropertyChanged(nameof(ContractPageSummary));
            StatusText = $"Índice local: {total:N0} contratação(ões).";
        }
        catch (OperationCanceledException)
        {
            if (generation == Volatile.Read(ref _contractSearchGeneration))
            {
                _contractSearchCountPending = false;
                OnPropertyChanged(nameof(ContractPageSummary));
            }
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _contractSearchGeneration))
            {
                _contractSearchCountPending = false;
                OnPropertyChanged(nameof(ContractPageSummary));
                _diagnosticLog.Warning("performance", $"Contagem exata adiada: {exception.Message}");
            }
        }
        finally
        {
            NotifyCommands();
        }
    }

    private async Task RefreshLocalItemSearchSummaryAsync(
        SearchQuery query,
        SearchExpression expression,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var summary = await Task.Run(
                    () => _repository.GetItemSearchLocalSummaryAsync(query, expression, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);
            if (generation == Volatile.Read(ref _contractSearchGeneration))
            {
                _localItemSearchSummary = summary;
                if (!_remotePriceExpansionStarted)
                {
                    ItemSearchSummary = BuildLocalSearchSummary();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer search superseded this summary.
        }
        catch (Exception exception)
        {
            _diagnosticLog.Warning("performance", $"Resumo local adiado: {exception.Message}");
        }
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

    private async Task RestartItemSearchAsync()
    {
        if (MessageBox.Show(
                "Reiniciar esta pesquisa cria uma nova rotação e descarta apenas o progresso " +
                "retomável da pesquisa geral atual. O cache permanente não será alterado. Continuar?",
                "Reiniciar pesquisa",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await SearchAsync(resetSession: true, restartPriceSession: true).ConfigureAwait(true);
    }

    private async Task LoadNextItemPageAsync()
    {
        var cancellation = _priceCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        if (!_hasMoreLocalPriceRows)
        {
            return;
        }

        SetPriceBusy(true, usesNetwork: false);
        try
        {
            var localRows = await LoadLocalPricePageAsync(cancellation.Token).ConfigureAwait(true);
            ItemSearchSummary =
                $"{BuildLocalSearchSummary()} Mais {localRows:N0} preço(s) entregue(s) do cache local; " +
                "nenhuma chamada de rede foi feita.";
        }
        finally
        {
            SetPriceBusy(false, usesNetwork: false);
        }
    }

    private async Task FireBatchesAsync()
    {
        var cancellation = _priceCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            ItemSearchSummary = "Execute novamente a mesma pesquisa para retomar a sessão de preços.";
            return;
        }

        await RunSelectedBatchesAsync(cancellation.Token).ConfigureAwait(true);
    }

    private async Task RunSelectedBatchesAsync(
        CancellationToken cancellationToken,
        int? exactContractCount = null,
        bool exhaustCandidateSet = false)
    {
        var effectiveBatchCount = BatchCount;
        var estimatedCandidateContracts = _localItemSearchSummary?.CandidateContracts;
        var exhaustiveProgressCount = estimatedCandidateContracts is > 0
            ? checked((int)Math.Min(int.MaxValue, estimatedCandidateContracts.Value))
            : (int?)null;
        var requestedContracts = exhaustCandidateSet
            ? exhaustiveProgressCount ?? ItemSearchDefaults.ContractsPerBatch
            : exactContractCount is > 0
            ? exactContractCount.Value
            : checked(effectiveBatchCount * ItemSearchDefaults.ContractsPerBatch);
        var largeConfirmed = exhaustCandidateSet || requestedContracts <= 500;
        if (!largeConfirmed)
        {
            var answer = MessageBox.Show(
                $"Examinar as próximas {requestedContracts:N0} contratações " +
                $"({effectiveBatchCount:N0} lote(s) de 50)?\n\n" +
                "Todos os itens compatíveis encontrados serão consultados. Por isso, a quantidade de " +
                "chamadas de preço pode ser maior que o número de contratações.",
                "Confirmar lotes de preços",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            largeConfirmed = true;
        }

        SetPriceBusy(true, usesNetwork: true);
        var runGeneration = Interlocked.Increment(ref _priceRunGeneration);
        _activeExpansionExhaustive = exhaustCandidateSet;
        _activeExpansionRemaining = exhaustCandidateSet ? 0 : requestedContracts;
        try
        {
            _remotePriceExpansionStarted = true;
            PriceSearchProgress = 0;
            var (minimum, maximum) = ParsePriceRange();
            using var requestScope = PncpRequestOptions.BeginScope(PncpRequestPriority.AdditionalBatches);
            var progress = new Progress<PriceBatchProgress>(value =>
                UpdateItemSearchProgress(value, runGeneration));
            var rowProgress = new Progress<IReadOnlyList<ItemSearchRow>>(rows =>
            {
                if (runGeneration == Volatile.Read(ref _priceRunGeneration))
                {
                    AppendUniqueRows(rows);
                }
            });
            var result = await _itemSearchService.RunContinuousAsync(
                new PriceBatchRequest(
                    effectiveBatchCount,
                    largeConfirmed,
                    exhaustCandidateSet
                        ? PriceBatchBudgetMode.CandidateContracts
                        : PriceBatchBudgetMode.UnresolvedContracts,
                    exhaustCandidateSet ? exhaustiveProgressCount : exactContractCount)
                {
                    ExhaustCandidateSet = exhaustCandidateSet
                },
                minimum,
                maximum,
                progress,
                rowProgress,
                cancellationToken).ConfigureAwait(true);
            HasMoreItemCandidates = !result.CandidateSetExhausted;
            PriceSearchProgress = 100;
            if (runGeneration == Volatile.Read(ref _priceRunGeneration))
            {
                _activeExpansionRemaining = 0;
                _activeExpansionExhaustive = false;
            }
            ItemSearchSummary =
                $"{BuildLocalSearchSummary()} {result.Message} Etapa {result.GeographicStage}; " +
                $"{result.ContractsScanned:N0} contratação(ões) examinada(s) na sessão; " +
                $"{result.ExpandedContracts:N0} ampliada(s) pela API; " +
                $"{result.FullyResolvedContracts:N0} resolvida(s) pelo cache; " +
                $"{BuildRemainingCandidates(result.ContractsScanned)}; " +
                $"{result.MatchedItems:N0} item(ns) compatível(is); " +
                $"{result.RevealedPrices:N0} preço(s) homologado(s) revelado(s); " +
                $"chamadas de listas/resultados: {result.ItemListCalls:N0}/{result.ItemResultCalls:N0}; " +
                $"falhas na sessão: {result.TotalFailedCalls:N0}. " +
                BuildSearchTrafficSummary();
        }
        catch (OperationCanceledException)
        {
            if (runGeneration == Volatile.Read(ref _priceRunGeneration))
            {
                ItemSearchSummary = "Lotes interrompidos; o que terminou foi preservado no checkpoint retomável.";
            }
        }
        catch (Exception exception)
        {
            if (runGeneration == Volatile.Read(ref _priceRunGeneration))
            {
                ItemSearchSummary = $"Falha ao disparar lotes: {exception.Message}";
            }
        }
        finally
        {
            if (runGeneration == Volatile.Read(ref _priceRunGeneration))
            {
                _activeExpansionExhaustive = false;
                SetPriceBusy(false, usesNetwork: false);
            }
        }
    }

    private bool ConfirmExhaustiveItemSearch()
    {
        var candidateContracts = _localItemSearchSummary?.CandidateContracts;
        if (candidateContracts is <= 500)
        {
            return true;
        }

        var amount = candidateContracts is { } known
            ? $"{known:N0} contratações candidatas"
            : "uma quantidade ainda desconhecida de contratações candidatas";
        return MessageBox.Show(
                   $"Esta busca percorrerá {amount} até esgotar o conjunto.\n\n" +
                   "O cache será reutilizado, mas a operação pode fazer muitas chamadas ao PNCP. " +
                   "Você poderá interromper e retomar pelo checkpoint. Continuar?",
                   "Confirmar busca completa de preços",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Question) == MessageBoxResult.Yes;
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
        var localPagesToRestore = Math.Max(1, _localPricePage);
        _localPricePage = 0;
        _localPriceCursor = null;
        _hasMoreLocalPriceRows = false;
        _localPriceRowsLoaded = 0;
        ResetCurrentItemRows();
        for (var index = 0; index < localPagesToRestore; index++)
        {
            await LoadLocalPricePageAsync(cancellationToken, minimum, maximum).ConfigureAwait(true);
            if (!_hasMoreLocalPriceRows)
            {
                break;
            }
        }

        var rows = await _itemSearchService.GetDiscoveredRowsAsync(
            minimum,
            maximum,
            cancellationToken).ConfigureAwait(true);
        AppendUniqueRows(rows);
    }

    private async Task<int> LoadLocalPricePageAsync(
        CancellationToken cancellationToken,
        decimal? minimum = null,
        decimal? maximum = null)
    {
        if (_activeSearchQuery is null || _activeItemSearchExpression is null)
        {
            return 0;
        }

        if (minimum is null && maximum is null)
        {
            (minimum, maximum) = ParsePriceRange();
        }

        var page = await Task.Run(
                () => _priceCacheRepository.SearchLocalAfterAsync(
                    _activeSearchQuery,
                    _activeItemSearchExpression,
                    minimum,
                    maximum,
                    _localPriceCursor,
                    ItemSearchSessionService.DefaultPageSize,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);
        var rows = page.Rows ?? [];

        AppendUniqueRows(rows);
        _localPricePage = page.Page;
        _localPriceCursor = page.Cursor;
        _hasMoreLocalPriceRows = page.HasMore;
        _localPriceRowsLoaded += rows.Count;
        HasMoreItemCandidates = _hasMoreLocalPriceRows || !_remotePriceExpansionStarted;
        NotifyCommands();
        return rows.Count;
    }

    private void StopItemSearch()
    {
        var wasActive = _isItemSearchActive;
        Interlocked.Increment(ref _priceRunGeneration);
        _activeExpansionRemaining = 0;
        _activeExpansionExhaustive = false;
        _priceCancellation?.Cancel();
        _itemSearchService.Stop();
        SetPriceBusy(false, usesNetwork: false);
        SetItemSearchActive(false);
        HasMoreItemCandidates = false;
        if (wasActive)
        {
            ItemSearchSummary = "Ampliação interrompida; execute a mesma pesquisa para retomar do checkpoint.";
        }

        NotifyCommands();
    }

    private void AppendUniqueRows(IEnumerable<ItemSearchRow> rows)
    {
        using var span = _performanceTelemetry.Begin("ui", "item-results-apply");
        var pending = new List<ItemSearchDisplayRow>();
        var expression = _activeItemSearchExpression;
        foreach (var row in rows
                     .Where(item => expression is null ||
                                    expression.MatchesItem(item.Item.Description, item.Item.Unit))
                     .Select(item => new ItemSearchDisplayRow(item)))
        {
            var key = RowKey(row);
            _currentItemResultKeys.Add(key);
            if (_visibleItemKeys.Add(key))
            {
                pending.Add(_retainedItemRows.TryGetValue(key, out var retained) ? retained : row);
            }
        }

        ItemSearchRows.AddRange(pending);
        span.Complete(pending.Count);
    }

    private static string RowKey(ItemSearchDisplayRow row) =>
        $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result?.ResultSequence.ToString(CultureInfo.InvariantCulture) ?? row.Source.PriceState.ToString()}";

    private void ResetCurrentItemRows()
    {
        ItemSearchRows.Clear();
        _visibleItemKeys.Clear();
        _currentItemResultKeys.Clear();
        foreach (var (key, row) in _retainedItemRows)
        {
            _visibleItemKeys.Add(key);
            ItemSearchRows.Add(row);
        }
    }

    private void SynchronizeRetainedItemRow(ItemSearchDisplayRow row)
    {
        var key = RowKey(row);
        if (row.IsRetained)
        {
            _retainedItemRows[key] = row;
            var currentIndex = ItemSearchRows.IndexOf(row);
            if (currentIndex > 0)
            {
                ItemSearchRows.Move(currentIndex, 0);
            }
        }
        else
        {
            _retainedItemRows.Remove(key);
            if (!_currentItemResultKeys.Contains(key))
            {
                ItemSearchRows.Remove(row);
                _visibleItemKeys.Remove(key);
                if (ReferenceEquals(SelectedItemSearchRow, row))
                {
                    SelectedItemSearchRow = null;
                }
            }
        }

        OnPropertyChanged(nameof(SelectedBasketPriceCount));
        OnPropertyChanged(nameof(HasSelectedBasketPrices));
        OnPropertyChanged(nameof(ManualBasketButtonText));
    }

    private void UpdateItemSearchProgress(PriceBatchProgress progress, int? runGeneration = null)
    {
        if (runGeneration is { } generation &&
            generation != Volatile.Read(ref _priceRunGeneration))
        {
            return;
        }

        if (runGeneration is not null && !_activeExpansionExhaustive)
        {
            _activeExpansionRemaining = Math.Max(
                0,
                progress.RequestedContracts - progress.ProcessedContracts);
        }
        var remaining = _localItemSearchSummary is null
            ? string.Empty
            : $"; restantes estimadas: {Math.Max(0, _localItemSearchSummary.CandidateContracts - progress.ContractsScanned):N0}";
        PriceSearchProgress = progress.CandidateSetExhausted
            ? 100d
            : progress.RequestedContracts <= 0
                ? 0d
                : Math.Min(100d, progress.ProcessedContracts * 100d / progress.RequestedContracts);
        ItemSearchSummary =
            $"{BuildLocalSearchSummary()} {progress.Message} Etapa {progress.GeographicStage}; " +
            $"itens: {progress.MatchedItems:N0}; preços revelados: {progress.RevealedPrices:N0}; " +
            $"cobertura API: {progress.ExpandedContracts:N0}; " +
            $"resolvidas pelo cache: {progress.FullyResolvedContracts:N0}; " +
            $"candidatas examinadas: {progress.ContractsScanned:N0}{remaining}; " +
            $"listas reutilizadas: {progress.CachedItemListsReused:N0}; " +
            $"chamadas de listas/resultados: {progress.ItemListCalls:N0}/{progress.ItemResultCalls:N0}; " +
            $"falhas na sessão: {progress.TotalFailedCalls:N0}. {BuildSearchTrafficSummary()}";
    }

    private string BuildRemainingCandidates(int examined) => _localItemSearchSummary is null
        ? "restantes ainda não estimadas"
        : $"{Math.Max(0, _localItemSearchSummary.CandidateContracts - examined):N0} candidata(s) restante(s) estimada(s)";

    private string BuildLocalSearchSummary() => _localItemSearchSummary is null
        ? "Contagem local parcial indisponível."
        : $"Banco local: {_localItemSearchSummary.CandidateContracts:N0} contratação(ões) candidata(s) (total exato); " +
          $"{_localItemSearchSummary.CachedMatchingItems:N0} item(ns) compatível(is) no cache (parcial); " +
          $"{_localItemSearchSummary.CachedItemsWithActivePrices:N0} item(ns) com preço ativo salvo (parcial); " +
          $"{_localPriceRowsLoaded:N0} preço(s) entregue(s) primeiro pelo cache.";

    private string BuildSearchTrafficSummary()
    {
        var baseline = _searchTelemetryBaseline;
        var current = _telemetry.GetSnapshot();
        var scheduler = _requestScheduler.GetSnapshot();
        var cooldown = scheduler.GrowthBlockedUntil is { } blockedUntil && blockedUntil > DateTimeOffset.UtcNow
            ? $"cooldown por {Math.Ceiling((blockedUntil - DateTimeOffset.UtcNow).TotalSeconds):N0}s"
            : "sem cooldown";
        var latency = scheduler.RollingP50 is { } p50 && scheduler.RollingP95 is { } p95
            ? $"p50 {p50.TotalSeconds:N1}s; p95 {p95.TotalSeconds:N1}s; " +
              $"vazão {scheduler.RollingThroughput:N2} req/s"
            : "latência móvel aguardando amostra";
        var lastReduction = string.IsNullOrWhiteSpace(scheduler.LastReductionReason)
            ? "sem recuo registrado"
            : $"último recuo: {scheduler.LastReductionReason}";
        var lastChange = scheduler.LastConcurrencyChangeAt is { } changedAt
            ? $"mudança às {changedAt.ToLocalTime():HH:mm:ss}"
            : "mudança ainda não registrada";
        var concurrency =
            $"Concorrência: {scheduler.EffectiveConcurrency}/{scheduler.MaximumConcurrency}; " +
            $"ativas/fila: {scheduler.ActiveRequests:N0}/{scheduler.TotalQueued:N0}; " +
            $"sequência 2xx: {scheduler.ConsecutiveSuccesses:N0}; " +
            $"reduções: {scheduler.ConcurrencyReductions:N0}; {latency}; " +
            $"{lastReduction}; {lastChange}; {cooldown}";
        if (baseline is null)
        {
            return $"Rede da sessão: {FormatBytes(current.TotalBytesReceived)}. {concurrency}.";
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
               $"resultado {FormatCallAverage(resultBytes, resultDuration, resultCalls)}. {concurrency}.";
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

    private async Task<bool> TryRunAutomaticMaintenanceAsync(
        TimeSpan sliceDuration,
        CancellationToken visibleActivityCancellation)
    {
        if (IsIndexBusy || IsCatalogBusy || IsFileBusy || _automaticMaintenanceRunning || _disposed)
        {
            return false;
        }

        var inspection = await Task.Run(async () =>
        {
            var state = await _repository.GetDatasetStateAsync(visibleActivityCancellation)
                .ConfigureAwait(false);
            var incomplete = await _repository.GetLatestIncompleteSyncAsync(visibleActivityCancellation)
                .ConfigureAwait(false);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var targetStart = today.AddDays(-364);
            var coverageComplete = _repository is ICoverageRepository coverage &&
                                   await coverage.IsCoverageCompleteAsync(
                                           targetStart,
                                           today,
                                           visibleActivityCancellation)
                                       .ConfigureAwait(false);
            return (State: state, Incomplete: incomplete, Today: today, CoverageComplete: coverageComplete);
        }, visibleActivityCancellation).ConfigureAwait(true);
        var state = inspection.State;
        var incomplete = inspection.Incomplete;
        if (state.LastSuccessfulSync is null && incomplete is null)
        {
            return false;
        }

        if (state.EndDate >= inspection.Today &&
            state.LastSuccessfulSync?.LocalDateTime.Date >= DateTime.Today &&
            inspection.CoverageComplete)
        {
            return false;
        }

        _automaticMaintenanceRunning = true;
        using var phaseSpan = _performanceTelemetry.Begin("maintenance", "coverage");
        try
        {
            await RunIndexOperationAsync(async cancellationToken =>
            {
                StatusText = "Manutenção automática: recuperando os dias ausentes mais recentes…";
                using var sliceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    visibleActivityCancellation);
                sliceCancellation.CancelAfter(sliceDuration);
                try
                {
                    var progress = CreateSyncProgress();
                    await Task.Run(
                            () => _autoSyncCoordinator.SynchronizeAsync(
                                progress,
                                sliceCancellation.Token),
                            sliceCancellation.Token)
                        .ConfigureAwait(true);
                    OperationProgress = 100;
                    StatusText = "Manutenção automática concluída.";
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && sliceCancellation.IsCancellationRequested)
                {
                    StatusText = "Fatia da cobertura concluída; checkpoints preservados.";
                }

                await RefreshDatasetSummaryAsync().ConfigureAwait(true);
                await RefreshCoverageAsync().ConfigureAwait(true);
            }, showErrorDialog: false, supportsPause: true).ConfigureAwait(true);
            phaseSpan.Complete();
            return true;
        }
        catch (OperationCanceledException)
        {
            phaseSpan.Complete();
            throw;
        }
        catch (Exception exception)
        {
            phaseSpan.Fail(exception);
            throw;
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
            using var backgroundSuppression = _requestScheduler.SuppressBackgroundRequests();
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
                if (!_automaticMaintenanceRunning)
                {
                    ScheduleNextMaintenance(SyncService.AutomaticRetryDelay);
                }
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
            using var backgroundSuppression = _requestScheduler.SuppressBackgroundRequests();
            await _priceCacheRepository.MarkContractPinnedAsync(
                    contract.PncpId,
                    _foregroundCancellation.Token)
                .ConfigureAwait(true);
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

    private void ToggleContractsPanel()
    {
        SelectedResultsWorkspace = ResultsWorkspace.Search;
        IsContractsPanelOpen = !IsContractsPanelOpen;
    }

    private void OpenMaintenanceForIssue(string issueKey)
    {
        if (_openedMaintenanceIssues.Add(issueKey))
        {
            IsMaintenancePanelOpen = true;
        }
    }

    private void OpenSelectedContractCache()
    {
        if (SelectedContract is null)
        {
            return;
        }

        SelectedResultsWorkspace = ResultsWorkspace.Search;
        IsContractsPanelOpen = true;
        ContractPanelViewIndex = 1;
    }

    private void QueueSelectedContractCacheLoad()
    {
        CancelSelectedContractCacheLoad(clearRows: false);
        if (!IsContractsPanelOpen || ContractPanelViewIndex != 1 || SelectedContract is null)
        {
            return;
        }

        _selectedContractCacheCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _startupCancellation.Token);
        var generation = Interlocked.Increment(ref _selectedContractCacheGeneration);
        _ = LoadSelectedContractCacheAsync(generation, _selectedContractCacheCancellation.Token);
    }

    private void CancelSelectedContractCacheLoad(bool clearRows)
    {
        Interlocked.Increment(ref _selectedContractCacheGeneration);
        _selectedContractCacheCancellation?.Cancel();
        _selectedContractCacheCancellation?.Dispose();
        _selectedContractCacheCancellation = null;
        if (clearRows)
        {
            ContractItemRows.Clear();
            ItemSummary = SelectedContract is null
                ? "Selecione uma contratação."
                : "Abra a visão de cache para consultar os itens desta contratação.";
        }
    }

    private async Task LoadSelectedContractCacheAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(true);
            var contract = SelectedContract;
            if (contract is null)
            {
                ContractItemRows.Clear();
                ItemSummary = "Selecione uma contratação.";
                return;
            }

            var rows = await _repository.GetItemDisplayRowsAsync(contract.PncpId, cancellationToken)
                .ConfigureAwait(true);
            if (generation != Volatile.Read(ref _selectedContractCacheGeneration) ||
                !IsContractsPanelOpen || ContractPanelViewIndex != 1 ||
                !string.Equals(SelectedContract?.PncpId, contract.PncpId, StringComparison.Ordinal))
            {
                return;
            }

            ContractItemRows.ReplaceAll(rows);
            ItemSummary = rows.Count == 0
                ? "Itens ainda não estão no cache permanente."
                : $"{rows.Count:N0} linha(s) no cache permanente.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Uma seleção mais recente substituiu esta leitura.
        }
        catch (Exception exception)
        {
            ItemSummary = $"Não foi possível ler o cache: {exception.Message}";
        }
    }

    private async Task RefreshContractItemRowsAsync(CancellationToken cancellationToken = default)
    {
        var contract = SelectedContract;
        if (contract is null)
        {
            ContractItemRows.Clear();
            return;
        }

        var rows = await _repository.GetItemDisplayRowsAsync(contract.PncpId, cancellationToken)
            .ConfigureAwait(true);
        if (!string.Equals(SelectedContract?.PncpId, contract.PncpId, StringComparison.Ordinal))
        {
            return;
        }

        ContractItemRows.ReplaceAll(rows);
    }

    private async Task RefreshDatasetSummaryAsync()
    {
        var state = await Task.Run(() => _repository.GetDatasetStateAsync()).ConfigureAwait(true);
        var databasePath = Path.Combine(_dataFolder, "pncpking.db");
        var databaseBytes = GetFileLength(databasePath) + GetFileLength(databasePath + "-wal");
        DatasetSummary = state.LastSuccessfulSync is null
            ? state.ContractCount == 0
                ? $"Índice vazio | Banco: {FormatBytes(databaseBytes)} | Pasta: {_dataFolder}"
                : $"Carga parcial: {state.ContractCount:N0} contratações prontas para retomar | " +
                  $"Banco: {FormatBytes(databaseBytes)} | Pasta: {_dataFolder}"
            : $"{state.ContractCount:N0} contratações | {state.StartDate:dd/MM/yyyy}-{state.EndDate:dd/MM/yyyy} | " +
              $"Atualizado em {state.LastSuccessfulSync:dd/MM/yyyy HH:mm} | " +
              $"{state.CachedItemCount:N0} itens e {state.CachedResultCount:N0} resultados permanentes | " +
              $"Banco: {FormatBytes(databaseBytes)}";
        if (state.LastSuccessfulSync is null)
        {
            OpenMaintenanceForIssue("first-load");
        }
    }

    private static long GetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task RefreshCoverageAsync()
    {
        var end = DateOnly.FromDateTime(DateTime.Today);
        var start = end.AddDays(-364);
        IReadOnlyList<CoverageDay> stored = _repository is ICoverageRepository coverageRepository
            ? await Task.Run(() => coverageRepository.GetCoverageDaysAsync(start, end)).ConfigureAwait(true)
            : [];
        var byDate = stored.ToDictionary(day => day.Date);
        var displayDays = new List<CoverageDay>(365);
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            displayDays.Add(byDate.TryGetValue(date, out var day)
                ? day
                : new CoverageDay
                {
                    Date = date,
                    Status = CoverageStatus.Missing,
                    ExpectedModalities = 0,
                    CompletedModalities = 0
                });
        }

        CoverageDays.ReplaceAll(displayDays);

        var complete = CoverageDays.Count(day => day.IsComplete);
        CoverageSummary = new CoverageSummary(CoverageDays, complete, CoverageDays.Count).Display;
    }

    private async Task ExportBackupAsync()
    {
        var profileChoice = MessageBox.Show(
            "Escolha o perfil do backup:\n\n" +
            "SIM — Compacto (recomendado): preserva índice, cotações, evidências e CATMAT/CATSER, sem o cache reconstruível de itens/preços.\n\n" +
            "NÃO — Completo: inclui também todo o cache de itens/preços e seus checkpoints.\n\n" +
            "CANCELAR — não exportar.",
            "Perfil do backup .pncpking",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (profileChoice == MessageBoxResult.Cancel)
        {
            return;
        }

        var profile = profileChoice == MessageBoxResult.Yes
            ? BackupProfile.Compact
            : BackupProfile.Full;
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
            var progressMessage = profile == BackupProfile.Compact
                ? "Criando e compactando uma cópia temporária do banco…"
                : "Criando snapshot completo do banco permanente…";
            StatusText = progressMessage;
            FileOperationProgressText = progressMessage;
            _diagnosticLog.Info(
                "backup-export",
                $"Exportação iniciada em segundo plano. perfil={profile}; destino={dialog.FileName}");
            await Task.Run(
                    () => _backupService.ExportAsync(dialog.FileName, profile, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);
            OperationProgress = 100;
            FileOperationProgressText = "Backup concluído.";
            _diagnosticLog.Info(
                "backup-export",
                $"Exportação concluída. perfil={profile}; destino={dialog.FileName}");
            StatusText = $"Backup {profile.ToString().ToLowerInvariant()} criado em {dialog.FileName}";
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
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        BackupInspection inspection;
        try
        {
            StatusText = "Inspecionando o backup e o espaço disponível…";
            inspection = await Task.Run(() => _backupService.InspectAsync(dialog.FileName))
                .ConfigureAwait(true);
            _diagnosticLog.Info(
                "backup-import",
                $"Backup inspecionado. arquivo={inspection.SourcePath}; esquema={inspection.SchemaVersion}; " +
                $"arquivo_bytes={inspection.ArchiveBytes}; banco_bytes={inspection.DatabaseBytes}; " +
                $"temporario_livre={inspection.TemporaryAvailableBytes}; dados_livre={inspection.DataAvailableBytes}.");
        }
        catch (Exception exception)
        {
            _diagnosticLog.Error("backup-import", "Falha ao inspecionar o backup.", exception);
            MessageBox.Show(
                $"Não foi possível inspecionar o backup.\n\n{exception.Message}\n\n" +
                $"Log: {_diagnosticLog.FilePath}",
                "Importar backup",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (!inspection.HasEnoughSpace)
        {
            var spaceMessage =
                "Não há espaço livre suficiente para uma importação recuperável.\n\n" +
                $"Banco descompactado: {FormatBytes(inspection.DatabaseBytes)}\n" +
                $"Temporários ({inspection.TemporaryRoot}): " +
                $"{FormatBytes(inspection.TemporaryAvailableBytes)} livres / " +
                $"{FormatBytes(inspection.TemporaryRequiredBytes)} necessários\n" +
                $"Dados ({inspection.DataRoot}): " +
                $"{FormatBytes(inspection.DataAvailableBytes)} livres / " +
                $"{FormatBytes(inspection.DataRequiredBytes)} necessários\n\n" +
                "Libere espaço e tente novamente; nenhum banco foi alterado.";
            _diagnosticLog.Warning("backup-import", spaceMessage.ReplaceLineEndings(" | "));
            MessageBox.Show(
                spaceMessage,
                "Espaço insuficiente",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var profile = inspection.Profile?.ToString() ?? "legado";
        if (MessageBox.Show(
                "IMPORTAR BACKUP VALIDADO\n\n" +
                $"Arquivo: {FormatBytes(inspection.ArchiveBytes)}\n" +
                $"Banco descompactado: {FormatBytes(inspection.DatabaseBytes)}\n" +
                $"Esquema: {inspection.SchemaVersion} → {SqliteContractRepository.CurrentSchemaVersion}\n" +
                $"Perfil: {profile}\n" +
                $"Espaço temporário livre: {FormatBytes(inspection.TemporaryAvailableBytes)}\n\n" +
                "A operação pode levar vários minutos em computadores lentos. O progresso será mostrado e " +
                "uma cópia recuperável do banco atual será preservada. Não encerre o programa durante a instalação final. Continuar?",
                "Confirmar importação",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            StatusText = "Importação cancelada antes de alterar o banco.";
            return;
        }

        StopItemSearch();
        _maintenanceTimer.Stop();
        try
        {
            await RunFileOperationAsync(async cancellationToken =>
            {
                BackupImportStage? lastLoggedStage = null;
                var lastLoggedBucket = -1;
                var importProgress = new Progress<BackupImportProgress>(item =>
                {
                    OperationProgress = item.Percentage;
                    FileOperationProgressText = item.Message;
                    StatusText = item.Message;
                    var bucket = (int)(item.Percentage / 10d);
                    if (lastLoggedStage != item.Stage || bucket != lastLoggedBucket)
                    {
                        _diagnosticLog.Info(
                            "backup-import",
                            $"fase={item.Stage}; progresso={item.Percentage:N1}%; " +
                            $"bytes={item.BytesProcessed}/{item.TotalBytes}; mensagem={item.Message}");
                        lastLoggedStage = item.Stage;
                        lastLoggedBucket = bucket;
                    }
                });
                _diagnosticLog.Info("backup-import", "Importação autorizada pelo usuário e iniciada.");
                var recovery = await Task.Run(
                        () => _backupService.ImportAsync(
                            dialog.FileName,
                            importProgress,
                            cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(true);
                _diagnosticLog.Info(
                    "backup-import",
                    $"Importação concluída. banco_anterior={recovery}");
                StatusText = $"Backup importado. Base anterior preservada em {recovery}";
                FileOperationProgressText = "Importação concluída; atualizando a tela…";
                Preflight = null;
                await RefreshDatasetSummaryAsync().ConfigureAwait(true);
                await RefreshCoverageAsync().ConfigureAwait(true);
                await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
                await SearchAsync(resetSession: true, restartPriceSession: true).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        finally
        {
            if (!_disposed)
            {
                _maintenanceTimer.Start();
            }
        }
    }

    private async Task ToggleDesktopShortcutAsync()
    {
        var enabled = !IsDesktopShortcutEnabled;
        var updatedSettings = await _settingsService.UpdateAsync(settings => settings with
        {
            SettingsVersion = Math.Max(AppSettings.CurrentVersion, settings.SettingsVersion),
            DesktopShortcutEnabled = enabled
        }).ConfigureAwait(true);

        IsDesktopShortcutEnabled = updatedSettings.EffectiveDesktopShortcutEnabled;
        try
        {
            _desktopShortcutService.Apply(IsDesktopShortcutEnabled);
        }
        catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
        {
            throw new InvalidOperationException(
                "A preferência do atalho foi salva, mas o Windows não conseguiu aplicá-la. " +
                "O PNCP King tentará novamente na próxima abertura.",
                exception);
        }

        StatusText = IsDesktopShortcutEnabled
            ? "Atalho do PNCP King criado ou atualizado na área de trabalho."
            : "Atalho do PNCP King removido da área de trabalho.";
    }

    private async Task ClearCacheAsync()
    {
        var size = await Task.Run(() => _repository.GetCacheSizeBytesAsync()).ConfigureAwait(true);
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
            await Task.Run(
                    () => _repository.ClearItemCacheAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);
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
        OperationProgress = 0;
        FileOperationProgressText = "Preparando operação de arquivo…";
        using var cancellation = new CancellationTokenSource();
        try
        {
            await action(cancellation.Token).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _diagnosticLog.Error("file-operation", "Falha em operação de arquivo.", exception);
            StatusText = $"Falha: {exception.Message}";
            MessageBox.Show(
                $"{exception.Message}\n\nLog para diagnóstico:\n{_diagnosticLog.FilePath}",
                "PNCP King",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsFileBusy = false;
        }
    }

    private void OpenDiagnosticLogs()
    {
        try
        {
            Directory.CreateDirectory(_diagnosticLog.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = _diagnosticLog.DirectoryPath,
                UseShellExecute = true
            });
            StatusText = $"Pasta de logs aberta: {_diagnosticLog.DirectoryPath}";
        }
        catch (Exception exception)
        {
            _diagnosticLog.Error("diagnostics", "Não foi possível abrir a pasta de logs.", exception);
            MessageBox.Show(
                $"Não foi possível abrir a pasta de logs.\n\n{_diagnosticLog.DirectoryPath}\n\n{exception.Message}",
                "Logs de diagnóstico",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task ExportPerformanceReportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar relatório de desempenho",
            Filter = "Relatório JSON (*.json)|*.json",
            DefaultExt = ".json",
            FileName = $"PNCPKing-performance-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsFileBusy = true;
        try
        {
            await _performanceTelemetry.ExportAsync(dialog.FileName).ConfigureAwait(true);
            StatusText = $"Relatórios JSON e TXT exportados ao lado de {dialog.FileName}.";
        }
        catch (Exception exception)
        {
            _diagnosticLog.Error("performance", "Não foi possível exportar o relatório.", exception);
            MessageBox.Show(
                exception.Message,
                "Relatório de desempenho",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsFileBusy = false;
        }
    }

    private async Task ComparePerformanceReportAsync()
    {
        var baselineDialog = new OpenFileDialog
        {
            Title = "Selecionar o relatório-base (antes)",
            Filter = "Relatório JSON (*.json)|*.json",
            CheckFileExists = true
        };
        if (baselineDialog.ShowDialog() != true)
        {
            return;
        }

        var outputDialog = new SaveFileDialog
        {
            Title = "Salvar comparação de desempenho",
            Filter = "Relatório JSON (*.json)|*.json",
            DefaultExt = ".json",
            FileName = $"PNCPKing-performance-comparison-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (outputDialog.ShowDialog() != true)
        {
            return;
        }

        IsFileBusy = true;
        try
        {
            await _performanceTelemetry.ExportAsync(
                    outputDialog.FileName,
                    baselineDialog.FileName)
                .ConfigureAwait(true);
            StatusText = "Comparação antes × depois exportada em JSON e TXT.";
        }
        catch (Exception exception)
        {
            _diagnosticLog.Error("performance", "Não foi possível comparar os relatórios.", exception);
            MessageBox.Show(
                exception.Message,
                "Comparar desempenho",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

    private Task AccessDocumentsAsync(ContractRecord? contract) =>
        contract is null
            ? Task.CompletedTask
            : AccessDocumentsAsync(PncpContractKey.FromContract(contract));

    private Task AccessItemDocumentsAsync(ItemSearchDisplayRow? row) =>
        row is null
            ? Task.CompletedTask
            : AccessDocumentsAsync(
                PncpContractKey.FromContract(row.Contract),
                row.Item.Description);

    private async Task AccessDocumentsAsync(
        PncpContractKey contract,
        string? suggestedReference = null)
    {
        if (IsDocumentBusy)
        {
            return;
        }

        var accessWindow = new Views.DocumentAccessWindow(suggestedReference)
        {
            Owner = Application.Current.MainWindow
        };
        if (accessWindow.ShowDialog() != true)
        {
            return;
        }

        var downloadsFolder = GetDownloadsFolder();
        Directory.CreateDirectory(downloadsFolder);
        var relevantPages = accessWindow.SelectedMode == Views.DocumentAccessMode.RelevantPages;
        var fileSuffix = relevantPages ? "paginas_relevantes" : "documentos";
        var destinationPath = GetUniqueFilePath(
            Path.Combine(
                downloadsFolder,
                $"PNCPKing_{SanitizeFileName(contract.PncpId)}_{fileSuffix}.pdf"));

        IsDocumentBusy = true;
        _documentCancellation = new CancellationTokenSource();
        DocumentProgress = 0;
        DocumentProgressText = relevantPages
            ? "Documentos: procurando páginas relevantes…"
            : "Documentos: iniciando…";
        try
        {
            string outputPath;
            string heading;
            string summary;
            IReadOnlyList<string> warnings;
            if (relevantPages)
            {
                var result = await _relevantPageService.CreateAsync(
                    contract,
                    accessWindow.Expressions,
                    destinationPath,
                    CreateDocumentProgress(),
                    _documentCancellation.Token).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(result.OutputPath))
                {
                    var warning = result.Warnings.Count == 0
                        ? "Nenhuma página relevante foi encontrada."
                        : string.Join("\n", result.Warnings.Take(8));
                    MessageBox.Show(
                        warning,
                        "Páginas relevantes",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    DocumentProgressText = "Documentos: nenhuma página relevante encontrada";
                    return;
                }

                outputPath = result.OutputPath;
                heading = "Páginas relevantes concluídas";
                var foundExpressions = result.Expressions
                    .Where(expression => expression.OccurrenceCount > 0)
                    .ToArray();
                summary =
                    $"Expressões pesquisadas: {string.Join("; ", result.Expressions.Select(expression => expression.Expression))}\n" +
                    $"Expressões encontradas: {string.Join("; ", foundExpressions.Select(expression => expression.Expression))}\n" +
                    $"{foundExpressions.Length:N0} de {result.Expressions.Count:N0} expressão(ões); " +
                    $"{result.MatchedPageCount:N0} página(s) única(s), " +
                    $"{result.OccurrenceCount:N0} ocorrência(s) " +
                    $"em {result.MatchedPdfCount:N0} PDF(s); " +
                    $"{result.Bundle.DownloadedFiles:N0} arquivo(s) baixado(s) e " +
                    $"{result.Bundle.ReusedFiles:N0} reutilizado(s) do cache.";
                warnings = result.Warnings;
                DocumentProgressText =
                    $"Documentos: {result.MatchedPageCount:N0} página(s) relevante(s), " +
                    $"{result.OccurrenceCount:N0} ocorrência(s)";
            }
            else
            {
                var result = await _documentService.CreateConsolidatedPdfAsync(
                    contract,
                    destinationPath,
                    CreateDocumentProgress(),
                    _documentCancellation.Token).ConfigureAwait(true);
                if (result.Pdfs.Count == 0 || string.IsNullOrWhiteSpace(result.ConsolidatedPath))
                {
                    var warning = result.Warnings.Count == 0
                        ? "A contratação não possui arquivos PDF ativos."
                        : string.Join("\n", result.Warnings.Take(8));
                    MessageBox.Show(
                        warning,
                        "Documentos PNCP",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    DocumentProgressText = "Documentos: nenhum PDF disponível";
                    return;
                }

                outputPath = result.ConsolidatedPath;
                heading = "PDF consolidado concluído";
                summary =
                    $"{result.Pdfs.Count:N0} PDF(s) reunido(s); {result.DownloadedFiles:N0} " +
                    $"arquivo(s) baixado(s) e {result.ReusedFiles:N0} reutilizado(s) do cache.";
                warnings = result.Warnings;
                DocumentProgressText =
                    $"Documentos: {result.Pdfs.Count:N0} PDF(s), {result.DownloadedFiles:N0} baixado(s), " +
                    $"{result.ReusedFiles:N0} reutilizado(s)";
            }

            DocumentProgress = 100;
            var resultWindow = new Views.DocumentResultWindow(outputPath, heading, summary, warnings)
            {
                Owner = Application.Current.MainWindow
            };
            resultWindow.ShowDialog();
            if (resultWindow.SelectedAction == Views.DocumentResultAction.OpenPdf)
            {
                OpenWithWindowsShell(outputPath);
            }
            else if (resultWindow.SelectedAction == Views.DocumentResultAction.OpenFolder)
            {
                OpenWithWindowsShell(Path.GetDirectoryName(outputPath) ?? downloadsFolder);
            }
        }
        catch (OperationCanceledException)
        {
            DocumentProgressText = "Documentos: operação cancelada";
            StatusText = "Processamento de documentos cancelado; o cache concluído foi preservado.";
        }
        catch (Exception exception)
        {
            DocumentProgressText = "Documentos: falha";
            MessageBox.Show(exception.Message, "Documentos PNCP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _documentCancellation.Dispose();
            _documentCancellation = null;
            IsDocumentBusy = false;
            NotifyCommands();
        }
    }

    private async Task ClearDocumentCacheAsync()
    {
        if (MessageBox.Show(
                "Limpar o cache de documentos, PDFs extraídos e índices de texto?\n\n" +
                "O banco de dados, as planilhas, os relatórios e os PDFs consolidados em Downloads serão preservados.",
                "Limpar cache de documentos",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        IsDocumentBusy = true;
        _documentCancellation = new CancellationTokenSource();
        DocumentProgressText = "Documentos: limpando cache…";
        try
        {
            var removedBytes = await _documentService.ClearCacheAsync(_documentCancellation.Token).ConfigureAwait(true);
            DocumentProgress = 0;
            DocumentProgressText = $"Documentos: cache limpo ({FormatBytes(removedBytes)})";
            StatusText = "Cache de documentos limpo; o cache permanente do banco não foi alterado.";
        }
        catch (OperationCanceledException)
        {
            DocumentProgressText = "Documentos: limpeza cancelada";
        }
        catch (Exception exception)
        {
            DocumentProgressText = "Documentos: falha ao limpar o cache";
            MessageBox.Show(exception.Message, "Limpar cache de documentos", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _documentCancellation.Dispose();
            _documentCancellation = null;
            IsDocumentBusy = false;
            NotifyCommands();
        }
    }

    private IProgress<DocumentProcessingProgress> CreateDocumentProgress() =>
        new Progress<DocumentProcessingProgress>(progress =>
        {
            DocumentProgress = progress.Total <= 0
                ? 0
                : progress.Completed * 100d / progress.Total;
            DocumentProgressText = $"Documentos: {progress.Message}";
        });

    private static string GetUniqueFilePath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem}_{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Não foi possível reservar um nome para o PDF consolidado.");
    }

    private static string GetDownloadsFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            const string shellFolders =
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
            const string downloadsFolderId = "{374DE290-123F-4565-9164-39C4925E467B}";
            if (Registry.GetValue(shellFolders, downloadsFolderId, null) is string configured &&
                !string.IsNullOrWhiteSpace(configured))
            {
                return Environment.ExpandEnvironmentVariables(configured);
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.Combine(userProfile, "Downloads");
    }

    private static void OpenWithWindowsShell(string path) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });

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
        if (value && usesNetwork)
        {
            _backgroundCacheSuppression ??= _requestScheduler.SuppressBackgroundRequests();
        }
        else if (!value)
        {
            _backgroundCacheSuppression?.Dispose();
            _backgroundCacheSuppression = null;
        }

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
                     RestartItemSearchCommand,
                     CalculateExactContractCountCommand, CancelExactContractCountCommand,
                     LoadNextItemPageCommand, FireBatchesCommand, ApplyPriceFilterCommand,
                     StopItemSearchCommand, CalculatePreflightCommand, StartSyncCommand,
                     PauseSyncCommand, CancelIndexCommand, HydrateCommand, RetryPendingCommand,
                     OpenPncpCommand, AccessDocumentsCommand, AccessItemDocumentsCommand,
                     ClearDocumentCacheCommand,
                     CancelDocumentOperationCommand,
                     ExportBackupCommand, ImportBackupCommand, ClearCacheCommand,
                     ToggleDesktopShortcutCommand,
                     ManageSweetCodesCommand, ToggleContractsPanelCommand,
                     CloseContractsPanelCommand, OpenSelectedContractCacheCommand,
                     ToggleMaintenancePanelCommand,
                     UpdateCatalogCommand, PauseCatalogCommand, CancelCatalogCommand,
                     EstimateAndActivatePriceCacheCommand, PausePriceCacheCommand,
                     CancelPriceCacheCommand, DisablePriceCacheCommand,
                     RemovePriceCacheCommand,
                     UseQuotationSampleCommand, UpdateQuotationSampleCommand,
                     AdjustQuotationWeightsCommand,
                     ConfirmQuotationBasketCommand, ExportQuotationCommand,
                     ExportQuotationPackageCommand, ImportQuotationPackageCommand,
                     PreviousQuotationBasketPageCommand, NextQuotationBasketPageCommand,
                     NewQuotationCommand, NewQuotationItemCommand,
                     RenameQuotationCommand, DeleteQuotationCommand,
                     DeleteQuotationLineCommand, ImportQuotationCommand, AiQuotationCommand,
                     RenameQuotationLineCommand,
                     ResumeQuotationAutomationCommand, CancelQuotationAutomationCommand,
                     RefineQuotationPromptsCommand,
                     OpenRestrictiveQuotationSearchCommand,
                     OpenIntermediateQuotationSearchCommand, OpenBroadQuotationSearchCommand,
                     RenameManualBasketCommand, DeleteManualBasketCommand,
                     RemoveManualBasketReferenceCommand, AccessQuotationDocumentsCommand,
                     OpenQuotationItemCommand
                 })
        {
            switch (command)
            {
                case AsyncRelayCommand asyncCommand:
                    asyncCommand.NotifyCanExecuteChanged();
                    break;
                case AsyncRelayCommand<ContractRecord> asyncContractCommand:
                    asyncContractCommand.NotifyCanExecuteChanged();
                    break;
                case AsyncRelayCommand<ItemSearchDisplayRow> asyncItemSearchCommand:
                    asyncItemSearchCommand.NotifyCanExecuteChanged();
                    break;
                case AsyncRelayCommand<QuotationReferenceDisplay> asyncReferenceCommand:
                    asyncReferenceCommand.NotifyCanExecuteChanged();
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
