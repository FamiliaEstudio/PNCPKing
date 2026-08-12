using System.Collections.ObjectModel;
using System.Windows.Input;
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

    public RangeObservableCollection<CatalogSyncDisplay> CatalogCoverage { get; } = [];
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
        ? "Catálogo: pausado após a página atual"
        : IsCatalogBusy
            ? "Catálogo: baixando snapshot oficial"
            : "Catálogo: inativo";

    public string PauseCatalogButtonText => IsCatalogPaused ? "Retomar catálogo" : "Pausar catálogo";

    internal ICatalogRepository CatalogRepository => _catalogRepository;
    internal ICatalogSearchService CatalogSearchService => _catalogSearchService;

    private void InitializeCatalog(
        ICatalogRepository repository,
        CatalogSyncService syncService,
        ICatalogSearchService searchService)
    {
        _catalogRepository = repository;
        _catalogSyncService = syncService;
        _catalogSearchService = searchService;
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

    private async Task RunAutomaticMaintenanceCycleAsync()
    {
        await TryRunAutomaticMaintenanceAsync().ConfigureAwait(true);
        _ = TryRunPriceCacheMaintenanceAsync();
        await TryRunCatalogMaintenanceAsync().ConfigureAwait(true);
        if (!IsIndexBusy && !IsPriceBusy && !IsForegroundBusy && !IsFileBusy && !IsCatalogBusy && !_disposed)
        {
            await Task.Run(() => _repository.OptimizeAsync()).ConfigureAwait(true);
        }
    }

    private async Task TryRunCatalogMaintenanceAsync()
    {
        if (IsCatalogBusy || IsIndexBusy || IsFileBusy || _disposed)
        {
            return;
        }

        if (await _catalogSyncService.IsDueAsync().ConfigureAwait(true))
        {
            await RunCatalogSyncAsync(showErrorDialog: false).ConfigureAwait(true);
            return;
        }

        await RunCatalogDescriptionIndexAsync().ConfigureAwait(true);
    }

    private async Task RunCatalogDescriptionIndexAsync()
    {
        _catalogCancellation = new CancellationTokenSource();
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

    private async Task RunCatalogSyncAsync(bool showErrorDialog)
    {
        if (IsCatalogBusy || IsIndexBusy)
        {
            return;
        }

        _catalogCancellation = new CancellationTokenSource();
        IsCatalogBusy = true;
        IsCatalogPaused = false;
        CatalogProgress = 0;
        var progress = new Progress<CatalogSyncProgress>(value =>
        {
            CatalogProgress = value.Percentage;
            CatalogProgressText = value.Message;
            if (value.CompletedPages == value.TotalPages || value.CompletedPages % 10 == 0)
            {
                _ = RefreshCatalogCoverageAsync();
            }
        });
        try
        {
            await _catalogSyncService.SynchronizeAsync(progress, _catalogCancellation.Token)
                .ConfigureAwait(true);
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

        var completed = states.Where(state => state.Status == CatalogSyncStatus.Complete).ToArray();
        if (!IsCatalogBusy)
        {
            CatalogProgressText = completed.Length == 2
                ? $"{completed.Sum(state => state.ActiveRecords):N0} códigos ativos · " +
                  $"próxima verificação após {completed.Min(state => state.CompletedAt)?.Add(CatalogSyncService.RefreshInterval):dd/MM/yyyy HH:mm}"
                : states.FirstOrDefault(state => state.Status == CatalogSyncStatus.Failed)?.LastError is { Length: > 0 } error
                    ? $"Falha na última carga: {error}"
                    : "A primeira carga completa do CATMAT/CATSER ainda não foi publicada.";
        }
    }

    public void OpenCatalogDictionary() =>
        new CatalogDictionaryWindow(_catalogRepository)
        {
            Owner = System.Windows.Application.Current.MainWindow
        }.ShowDialog();
}
