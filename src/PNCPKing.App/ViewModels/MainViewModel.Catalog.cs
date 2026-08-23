using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PNCPKing.App.Services;
using PNCPKing.App.Views;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    private ICatalogRepository _catalogRepository = null!;
    private CatalogSyncService _catalogSyncService = null!;
    private ICatalogSearchService _catalogSearchService = null!;
    private CancellationTokenSource? _catalogCancellation;
    private bool _isCatalogBusy;
    private bool _isCatalogPaused;
    private double _catalogProgress;
    private string _catalogProgressText = "CATMAT/CATSER: catálogo local ainda não verificado.";
    private long _publishedCatalogRecords;
    private CatalogRefreshOption _selectedCatalogRefreshOption = null!;
    private CatalogDictionaryWindow? _catalogDictionaryWindow;

    public RangeObservableCollection<CatalogSyncDisplay> CatalogCoverage { get; } = [];
    public IReadOnlyList<CatalogRefreshOption> CatalogRefreshOptions { get; private set; } = [];
    public ICommand UpdateCatalogCommand { get; private set; } = null!;
    public ICommand PauseCatalogCommand { get; private set; } = null!;
    public ICommand CancelCatalogCommand { get; private set; } = null!;

    public bool IsCatalogBusy
    {
        get => _isCatalogBusy;
        private set
        {
            if (SetProperty(ref _isCatalogBusy, value))
            {
                OnPropertyChanged(nameof(CatalogActivityText));
                NotifyCommands();
            }
        }
    }

    public bool IsCatalogPaused
    {
        get => _isCatalogPaused;
        private set
        {
            if (SetProperty(ref _isCatalogPaused, value))
            {
                OnPropertyChanged(nameof(CatalogActivityText));
                OnPropertyChanged(nameof(PauseCatalogButtonText));
            }
        }
    }

    public double CatalogProgress
    {
        get => _catalogProgress;
        private set => SetProperty(ref _catalogProgress, Math.Clamp(value, 0d, 100d));
    }

    public string CatalogProgressText
    {
        get => _catalogProgressText;
        private set => SetProperty(ref _catalogProgressText, value);
    }

    public string CatalogActivityText => IsCatalogPaused
        ? _publishedCatalogRecords > 0
            ? "Catálogo: pausado; versão publicada disponível"
            : "Catálogo: pausado após a página atual"
        : IsCatalogBusy
            ? _publishedCatalogRecords > 0
                ? "Catálogo: atualizando; versão publicada disponível"
                : "Catálogo: baixando primeiro snapshot oficial"
            : "Catálogo: inativo";

    public string PauseCatalogButtonText => IsCatalogPaused ? "Retomar catálogo" : "Pausar catálogo";

    public CatalogRefreshOption SelectedCatalogRefreshOption
    {
        get => _selectedCatalogRefreshOption;
        set
        {
            if (value is null || !SetProperty(ref _selectedCatalogRefreshOption, value))
            {
                return;
            }

            _ = PersistCatalogRefreshIntervalAsync(value.IntervalDays);
        }
    }

    internal ICatalogRepository CatalogRepository => _catalogRepository;
    internal ICatalogSearchService CatalogSearchService => _catalogSearchService;

    private void InitializeCatalog(
        ICatalogRepository repository,
        CatalogSyncService syncService,
        ICatalogSearchService searchService,
        int refreshIntervalDays)
    {
        _catalogRepository = repository;
        _catalogSyncService = syncService;
        _catalogSearchService = searchService;
        CatalogRefreshOptions =
        [
            new CatalogRefreshOption("A cada 2 dias", 2),
            new CatalogRefreshOption("Uma semana", 7),
            new CatalogRefreshOption("A cada 15 dias", 15),
            new CatalogRefreshOption("Manualmente", 0)
        ];
        var normalizedInterval = AppSettings.NormalizeCatalogRefreshIntervalDays(refreshIntervalDays);
        _selectedCatalogRefreshOption = CatalogRefreshOptions.Single(option =>
            option.IntervalDays == normalizedInterval);
        UpdateCatalogCommand = new AsyncRelayCommand(
            () => RunCatalogSyncAsync(showErrorDialog: true),
            () => !IsCatalogBusy && !IsIndexBusy && !IsFileBusy);
        PauseCatalogCommand = new RelayCommand(
            ToggleCatalogPause,
            () => IsCatalogBusy);
        CancelCatalogCommand = new RelayCommand(
            () => _catalogCancellation?.Cancel(),
            () => IsCatalogBusy);
    }

    private async Task PersistCatalogRefreshIntervalAsync(int intervalDays)
    {
        try
        {
            await _settingsService.UpdateAsync(settings => settings with
            {
                SettingsVersion = Math.Max(AppSettings.CurrentVersion, settings.SettingsVersion),
                CatalogRefreshIntervalDays =
                    AppSettings.NormalizeCatalogRefreshIntervalDays(intervalDays)
            }).ConfigureAwait(true);
            await RefreshCatalogCoverageAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
        {
            _diagnosticLog.Warning(
                "catalog",
                $"Não foi possível salvar a frequência automática: {exception.Message}");
        }
    }

    private async Task RunAutomaticMaintenanceCycleAsync()
    {
        if (_disposed || DateTimeOffset.UtcNow < _nextMaintenanceAllowedAt)
        {
            return;
        }

        await using var lease = _maintenanceCoordinator.TryEnter();
        if (lease is null)
        {
            MaintenanceActivityText = "Manutenção: outro ciclo ainda está ativo";
            return;
        }

        var decision = _maintenanceCoordinator.GetDecision();
        ResourceStatusText =
            $"RAM livre: {FormatBytes(decision.Resources.AvailablePhysicalMemoryBytes)} de " +
            $"{FormatBytes(decision.Resources.TotalPhysicalMemoryBytes)} " +
            $"({decision.Resources.MemoryLoadPercent}% em uso)";
        if (!decision.CanRun)
        {
            MaintenanceActivityText = $"Manutenção: {decision.Description}";
            ScheduleNextMaintenance(decision.RetryDelay);
            return;
        }

        if (IsPriceBusy || IsForegroundBusy || IsFileBusy || IsDocumentBusy || _disposed)
        {
            MaintenanceActivityText = "Manutenção: aguardando atividade visível terminar";
            ScheduleNextMaintenance(decision.RetryDelay);
            return;
        }

        using var span = _performanceTelemetry.Begin("maintenance", "adaptive-slice");
        await using var slice = _maintenanceCoordinator.BeginSlice(_startupCancellation.Token);
        try
        {
            if (await TryRunAutomaticMaintenanceAsync(decision.SliceDuration, slice.Token).ConfigureAwait(true))
            {
                slice.Token.ThrowIfCancellationRequested();
                MaintenanceActivityText = "Manutenção: fatia da cobertura PNCP concluída";
            }
            else if (_preferPriceCacheMaintenance &&
                     await TryRunPriceCacheMaintenanceAsync(decision.SliceDuration, slice.Token).ConfigureAwait(true))
            {
                slice.Token.ThrowIfCancellationRequested();
                _preferPriceCacheMaintenance = false;
                MaintenanceActivityText = "Manutenção: fatia do cache de preços concluída";
            }
            else if (await TryRunCatalogMaintenanceAsync(decision.SliceDuration, slice.Token).ConfigureAwait(true))
            {
                slice.Token.ThrowIfCancellationRequested();
                _preferPriceCacheMaintenance = true;
                MaintenanceActivityText = "Manutenção: fatia do catálogo concluída";
            }
            else if (await TryRunPriceCacheMaintenanceAsync(decision.SliceDuration, slice.Token).ConfigureAwait(true))
            {
                slice.Token.ThrowIfCancellationRequested();
                _preferPriceCacheMaintenance = false;
                MaintenanceActivityText = "Manutenção: fatia do cache de preços concluída";
            }
            else if (_lastOptimizeDate != DateOnly.FromDateTime(DateTime.Today))
            {
                MaintenanceActivityText = "Manutenção: otimizando estatísticas SQLite";
                await Task.Run(() => _repository.OptimizeAsync(slice.Token), slice.Token).ConfigureAwait(true);
                _lastOptimizeDate = DateOnly.FromDateTime(DateTime.Today);
            }
            else
            {
                MaintenanceActivityText = "Manutenção: banco atualizado; aguardando próximo ciclo";
            }

            await _repository.MaintainWalAsync(slice.Token).ConfigureAwait(true);
            span.Complete();
        }
        catch (OperationCanceledException) when (_disposed || slice.Token.IsCancellationRequested)
        {
            MaintenanceActivityText = _disposed
                ? "Manutenção: encerrada com o aplicativo"
                : "Manutenção: cedendo à interação do usuário; checkpoints preservados";
            _performanceTelemetry.Record("maintenance", "user-yield", TimeSpan.Zero);
        }
        catch (Exception exception)
        {
            span.Fail(exception);
            MaintenanceActivityText = "Manutenção: adiada após uma falha recuperável";
            _diagnosticLog.Warning("maintenance", $"Fatia adaptativa adiada: {exception.Message}");
        }
        finally
        {
            ScheduleNextMaintenance(decision.RetryDelay);
        }
    }

    private void ScheduleNextMaintenance(TimeSpan delay)
    {
        _nextMaintenanceAllowedAt = DateTimeOffset.UtcNow + delay;
        _maintenanceTimer.Stop();
        _maintenanceTimer.Interval = delay;
        if (!_disposed)
        {
            _maintenanceTimer.Start();
        }
    }

    private async Task<bool> TryRunCatalogMaintenanceAsync(
        TimeSpan sliceDuration,
        CancellationToken cancellationToken)
    {
        if (IsCatalogBusy || IsIndexBusy || IsFileBusy || _disposed)
        {
            return false;
        }

        var intervalDays = SelectedCatalogRefreshOption.IntervalDays;
        IReadOnlyList<CatalogKind> dueKinds = intervalDays == 0
            ? []
            : await _catalogSyncService.GetDueKindsAsync(
                    TimeSpan.FromDays(intervalDays),
                    cancellationToken)
                .ConfigureAwait(true);
        if (dueKinds.Count > 0)
        {
            await RunCatalogSyncAsync(
                    showErrorDialog: false,
                    sliceDuration,
                    cancellationToken,
                    dueKinds)
                .ConfigureAwait(true);
            return true;
        }

        var index = await _catalogRepository.GetDescriptionIndexProgressAsync().ConfigureAwait(true);
        if (index.Completed)
        {
            return false;
        }

        await RunCatalogDescriptionIndexAsync(sliceDuration, cancellationToken).ConfigureAwait(true);
        return true;
    }

    private async Task RunCatalogDescriptionIndexAsync(
        TimeSpan? sliceDuration = null,
        CancellationToken cancellationToken = default)
    {
        _catalogCancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        if (sliceDuration is { } budget)
        {
            _catalogCancellation.CancelAfter(budget);
        }
        IsCatalogBusy = true;
        IsCatalogPaused = false;
        var progress = new Progress<CatalogDescriptionIndexProgress>(value =>
        {
            CatalogProgress = value.Percentage;
            CatalogProgressText = value.Completed
                ? "Índice de descrições oficiais pronto."
                : $"Indexando descrições oficiais: {value.Percentage:N1}%";
        });
        try
        {
            await _catalogSyncService.BuildDescriptionIndexAsync(progress, _catalogCancellation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            CatalogProgressText = "Indexação de descrições pausada; o checkpoint foi preservado.";
        }
        finally
        {
            _catalogCancellation.Dispose();
            _catalogCancellation = null;
            IsCatalogBusy = false;
            IsCatalogPaused = false;
        }
    }

    private async Task RunCatalogSyncAsync(
        bool showErrorDialog,
        TimeSpan? sliceDuration = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<CatalogKind>? kinds = null)
    {
        if (IsCatalogBusy || IsIndexBusy)
        {
            return;
        }

        var states = await _catalogRepository.GetSyncStatesAsync(cancellationToken).ConfigureAwait(true);
        UpdatePublishedCatalogRecords(states.Sum(state => state.ActiveRecords));

        _catalogCancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        if (sliceDuration is { } budget)
        {
            _catalogCancellation.CancelAfter(budget);
        }
        IsCatalogBusy = true;
        IsCatalogPaused = false;
        CatalogProgress = 0;
        var progress = new Progress<CatalogSyncProgress>(value =>
        {
            CatalogProgress = value.Percentage;
            CatalogProgressText = _publishedCatalogRecords > 0
                ? $"{value.Message} · catálogo publicado continua disponível"
                : value.Message;
            if (value.CompletedPages == value.TotalPages || value.CompletedPages % 10 == 0)
            {
                _ = RefreshCatalogCoverageAsync();
            }
        });
        try
        {
            if (kinds is null)
            {
                await _catalogSyncService.SynchronizeAsync(progress, _catalogCancellation.Token)
                    .ConfigureAwait(true);
            }
            else
            {
                await _catalogSyncService.SynchronizeAsync(kinds, progress, _catalogCancellation.Token)
                    .ConfigureAwait(true);
            }
            await _catalogSyncService.BuildDescriptionIndexAsync(
                    new Progress<CatalogDescriptionIndexProgress>(value =>
                    {
                        CatalogProgress = value.Percentage;
                        CatalogProgressText = value.Completed
                            ? "Índice de descrições oficiais pronto."
                            : $"Indexando descrições oficiais: {value.Percentage:N1}%";
                    }),
                    _catalogCancellation.Token)
                .ConfigureAwait(true);
            CatalogProgress = 100;
            CatalogProgressText = "CATMAT e CATSER ativos foram publicados no catálogo local.";
        }
        catch (OperationCanceledException)
        {
            CatalogProgressText = "Atualização do catálogo cancelada; o checkpoint foi preservado.";
        }
        catch (Exception exception)
        {
            CatalogProgressText = $"Falha no catálogo: {exception.Message}";
            if (showErrorDialog)
            {
                System.Windows.MessageBox.Show(
                    exception.Message,
                    "CATMAT/CATSER",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        finally
        {
            _catalogCancellation.Dispose();
            _catalogCancellation = null;
            IsCatalogBusy = false;
            IsCatalogPaused = false;
            await RefreshCatalogCoverageAsync().ConfigureAwait(true);
        }
    }

    private void ToggleCatalogPause()
    {
        if (!IsCatalogBusy)
        {
            return;
        }

        if (IsCatalogPaused)
        {
            _catalogSyncService.Resume();
            IsCatalogPaused = false;
        }
        else
        {
            _catalogSyncService.Pause();
            IsCatalogPaused = true;
        }
    }

    private async Task RefreshCatalogCoverageAsync()
    {
        var states = await _catalogRepository.GetSyncStatesAsync().ConfigureAwait(true);
        CatalogCoverage.ReplaceAll(states.Select(state => new CatalogSyncDisplay(state)));
        var publishedRecords = states.Sum(state => state.ActiveRecords);
        UpdatePublishedCatalogRecords(publishedRecords);

        if (!IsCatalogBusy)
        {
            var hasPublishedCatalog = states.Count == 2 && states.All(state => state.ActiveRecords > 0);
            var failedState = states.FirstOrDefault(state => state.Status == CatalogSyncStatus.Failed);
            if (hasPublishedCatalog && failedState?.LastError is { Length: > 0 } error)
            {
                CatalogProgressText =
                    $"{publishedRecords:N0} códigos publicados continuam disponíveis · " +
                    $"falha na atualização: {error}";
            }
            else if (hasPublishedCatalog && states.Any(state => state.Status != CatalogSyncStatus.Complete))
            {
                CatalogProgressText =
                    $"{publishedRecords:N0} códigos publicados continuam disponíveis · " +
                    (SelectedCatalogRefreshOption.IntervalDays == 0
                        ? "use Atualizar agora para retomar a carga incompleta"
                        : "a atualização incompleta será retomada automaticamente");
            }
            else if (hasPublishedCatalog && SelectedCatalogRefreshOption.IntervalDays == 0)
            {
                CatalogProgressText =
                    $"{publishedRecords:N0} códigos ativos · atualização automática desativada";
            }
            else if (hasPublishedCatalog && states.All(state => state.CompletedAt is not null))
            {
                var interval = TimeSpan.FromDays(SelectedCatalogRefreshOption.IntervalDays);
                var nextRefresh = states.Min(state => state.CompletedAt!.Value.Add(interval));
                CatalogProgressText =
                    $"{publishedRecords:N0} códigos ativos · " +
                    $"próxima verificação após {nextRefresh:dd/MM/yyyy HH:mm}";
            }
            else if (hasPublishedCatalog)
            {
                CatalogProgressText = $"{publishedRecords:N0} códigos publicados disponíveis";
            }
            else if (failedState?.LastError is { Length: > 0 } firstLoadError)
            {
                CatalogProgressText = $"Falha na última carga: {firstLoadError}";
            }
            else if (SelectedCatalogRefreshOption.IntervalDays == 0)
            {
                CatalogProgressText =
                    "Primeira carga não publicada · use Atualizar agora para baixar o catálogo";
            }
            else
            {
                CatalogProgressText =
                    "A primeira carga completa do CATMAT/CATSER ainda não foi publicada.";
            }
        }

        if (states.Any(state => state.Status == CatalogSyncStatus.Failed))
        {
            OpenMaintenanceForIssue("catalog-failed");
        }
    }

    private void UpdatePublishedCatalogRecords(long value)
    {
        if (_publishedCatalogRecords == value)
        {
            return;
        }

        _publishedCatalogRecords = value;
        OnPropertyChanged(nameof(CatalogActivityText));
    }

    public void OpenCatalogDictionary(Window owner)
    {
        if (_catalogDictionaryWindow is { IsVisible: true } existing)
        {
            _performanceTelemetry.Record("ui", "interaction-suppressed", TimeSpan.Zero);
            existing.Activate();
            return;
        }

        var window = new CatalogDictionaryWindow(
            _catalogRepository,
            _diagnosticLog,
            _performanceTelemetry)
        {
            Owner = owner
        };
        _catalogDictionaryWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_catalogDictionaryWindow, window))
            {
                _catalogDictionaryWindow = null;
            }
        };
        window.Show();
    }
}
