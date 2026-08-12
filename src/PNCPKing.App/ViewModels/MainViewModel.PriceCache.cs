using System.Windows;
using System.Windows.Input;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _priceCacheCycleCancellation;
    private Task? _priceCacheCycleTask;
    private bool _isPriceCacheBusy;
    private bool _isPriceCachePaused;
    private PriceCacheStatus _priceCacheStatus = PriceCacheStatus.NotAuthorized;
    private double _priceCacheProgress;
    private string _priceCacheSummary = "Cache nacional ainda não autorizado.";
    private string _priceCacheActivityText = "Cache de 90 dias: inativo";

    public ICommand EstimateAndActivatePriceCacheCommand { get; private set; } = null!;
    public ICommand PausePriceCacheCommand { get; private set; } = null!;
    public ICommand CancelPriceCacheCommand { get; private set; } = null!;
    public ICommand DisablePriceCacheCommand { get; private set; } = null!;
    public ICommand RemovePriceCacheCommand { get; private set; } = null!;

    public bool IsPriceCacheBusy
    {
        get => _isPriceCacheBusy;
        private set
        {
            if (SetProperty(ref _isPriceCacheBusy, value))
            {
                OnPropertyChanged(nameof(PriceCacheActivityText));
                NotifyCommands();
            }
        }
    }

    public bool IsPriceCachePaused
    {
        get => _isPriceCachePaused;
        private set
        {
            if (SetProperty(ref _isPriceCachePaused, value))
            {
                OnPropertyChanged(nameof(PausePriceCacheButtonText));
                OnPropertyChanged(nameof(PriceCacheActivityText));
                NotifyCommands();
            }
        }
    }

    public PriceCacheStatus PriceCacheStatus
    {
        get => _priceCacheStatus;
        private set => SetProperty(ref _priceCacheStatus, value);
    }

    public double PriceCacheProgress
    {
        get => _priceCacheProgress;
        private set => SetProperty(ref _priceCacheProgress, Math.Clamp(value, 0d, 100d));
    }

    public string PriceCacheSummary
    {
        get => _priceCacheSummary;
        private set => SetProperty(ref _priceCacheSummary, value);
    }

    public string PriceCacheActivityText
    {
        get => _priceCacheActivityText;
        private set => SetProperty(ref _priceCacheActivityText, value);
    }

    public string PausePriceCacheButtonText => IsPriceCachePaused ? "Retomar" : "Pausar";

    private void InitializePriceCache()
    {
        EstimateAndActivatePriceCacheCommand = new AsyncRelayCommand(
            EstimateAndActivatePriceCacheAsync,
            () => !IsFileBusy && !IsPriceCacheBusy);
        PausePriceCacheCommand = new AsyncRelayCommand(
            TogglePriceCachePauseAsync,
            () => PriceCacheStatus is not PriceCacheStatus.NotAuthorized and not PriceCacheStatus.Disabled);
        CancelPriceCacheCommand = new RelayCommand(
            () => _priceCacheCycleCancellation?.Cancel(),
            () => IsPriceCacheBusy && _priceCacheCycleCancellation is not null);
        DisablePriceCacheCommand = new AsyncRelayCommand(
            DisablePriceCacheAsync,
            () => PriceCacheStatus is not PriceCacheStatus.NotAuthorized and not PriceCacheStatus.Disabled);
        RemovePriceCacheCommand = new AsyncRelayCommand(
            RemovePriceCacheAsync,
            () => !IsFileBusy && !IsPriceCacheBusy);
    }

    private async Task EstimateAndActivatePriceCacheAsync()
    {
        var end = DateOnly.FromDateTime(DateTime.Today);
        var start = end.AddDays(-(PriceCacheService.WindowDays - 1));
        PriceCacheActivityText = "Calculando contratos, espaço e duração…";
        var estimate = await _priceCacheRepository.EstimateAsync(start, end).ConfigureAwait(true);
        await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
        if (estimate.ContractCount == 0)
        {
            MessageBox.Show(
                "O índice ainda não possui contratações no período. Conclua primeiro a cobertura dos últimos 90 dias.",
                "Cache de itens e preços",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!estimate.HasEnoughSpace)
        {
            MessageBox.Show(
                $"Espaço livre: {FormatBytes(estimate.AvailableFreeBytes)}\n" +
                $"Carga restante estimada: {FormatBytes(estimate.EstimatedMinimumBytes)} a {FormatBytes(estimate.EstimatedMaximumBytes)}\n" +
                $"Reserva obrigatória: {FormatBytes(estimate.SafetyReserveBytes)}\n\n" +
                "Libere espaço antes de autorizar a carga.",
                "Espaço insuficiente",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var answer = MessageBox.Show(
            "ATIVAR CACHE NACIONAL DE ITENS E PREÇOS\n\n" +
            $"Janela: {start:dd/MM/yyyy} a {end:dd/MM/yyyy}\n" +
            $"Contratações no índice: {estimate.ContractCount:N0}\n" +
            $"Já completas: {estimate.AlreadyCompleteContracts:N0}\n" +
            $"Crescimento estimado: {FormatBytes(estimate.EstimatedMinimumBytes)} a {FormatBytes(estimate.EstimatedMaximumBytes)}\n" +
            $"Espaço livre: {FormatBytes(estimate.AvailableFreeBytes)}\n" +
            $"Reserva preservada: {FormatBytes(estimate.SafetyReserveBytes)}\n" +
            $"Duração inicial estimada: {FormatDuration(estimate.EstimatedMinimumDuration)} a {FormatDuration(estimate.EstimatedMaximumDuration)}\n\n" +
            "A carga continuará nas próximas aberturas, sempre após o índice e cedendo a API imediatamente às pesquisas do usuário. Autorizar?",
            "Confirmar cache de 90 dias",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            PriceCacheActivityText = "Cache de 90 dias: autorização cancelada";
            return;
        }

        await _priceCacheRepository.SetAuthorizationAsync(true, start, end).ConfigureAwait(true);
        await _priceCacheRepository.PrepareWindowAsync(start, end).ConfigureAwait(true);
        await StartPriceCacheCycleAsync().ConfigureAwait(true);
    }

    private async Task TogglePriceCachePauseAsync()
    {
        var policy = await _priceCacheRepository.GetPolicyAsync().ConfigureAwait(true);
        if (policy.Paused)
        {
            await _priceCacheRepository.SetPausedAsync(false).ConfigureAwait(true);
            IsPriceCachePaused = false;
            await StartPriceCacheCycleAsync().ConfigureAwait(true);
        }
        else
        {
            await _priceCacheRepository.SetPausedAsync(
                    true,
                    "Pausa manual; a chamada atual terminará antes da parada.")
                .ConfigureAwait(true);
            IsPriceCachePaused = true;
            PriceCacheActivityText = "Pausa solicitada; aguardando a chamada atual terminar";
            await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
        }
    }

    private async Task DisablePriceCacheAsync()
    {
        var end = DateOnly.FromDateTime(DateTime.Today);
        var start = end.AddDays(-(PriceCacheService.WindowDays - 1));
        await _priceCacheRepository.SetAuthorizationAsync(false, start, end).ConfigureAwait(true);
        _priceCacheCycleCancellation?.Cancel();
        await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
        PriceCacheActivityText = "Cache desativado; os dados já baixados foram conservados";
    }

    private async Task RemovePriceCacheAsync()
    {
        var progress = await _priceCacheRepository.GetProgressAsync().ConfigureAwait(true);
        if (MessageBox.Show(
                $"Remover {FormatBytes(progress.OccupiedBytes)} de dados reconstruíveis da carga de 90 dias?\n\n" +
                "Contratações atualizadas manualmente e preços já usados em cotações serão preservados.",
                "Remover cache nacional",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunFileOperationAsync(async cancellationToken =>
        {
            await _priceCacheRepository.RemoveBackgroundCacheAsync(cancellationToken).ConfigureAwait(true);
            await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
            await RefreshDatasetSummaryAsync().ConfigureAwait(true);
            StatusText = "Cache nacional reconstruível removido; dados fixados foram preservados.";
        }).ConfigureAwait(true);
    }

    private Task TryRunPriceCacheMaintenanceAsync()
    {
        if (_disposed || IsFileBusy || IsPriceCacheBusy)
        {
            return Task.CompletedTask;
        }

        return StartPriceCacheCycleAsync();
    }

    private Task StartPriceCacheCycleAsync()
    {
        if (_priceCacheCycleTask is { IsCompleted: false })
        {
            return _priceCacheCycleTask;
        }

        _priceCacheCycleCancellation?.Dispose();
        _priceCacheCycleCancellation = new CancellationTokenSource();
        _priceCacheCycleTask = RunPriceCacheCycleCoreAsync(_priceCacheCycleCancellation.Token);
        return _priceCacheCycleTask;
    }

    private async Task RunPriceCacheCycleCoreAsync(CancellationToken cancellationToken)
    {
        var policy = await _priceCacheRepository.GetPolicyAsync(cancellationToken).ConfigureAwait(true);
        if (!policy.Authorized || !policy.Enabled || policy.Paused)
        {
            await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
            return;
        }

        IsPriceCacheBusy = true;
        IsPriceCachePaused = false;
        PriceCacheActivityText = "Cache de 90 dias: aguardando oportunidade na API";
        var progress = new Progress<PriceCacheProgress>(UpdatePriceCacheProgress);
        try
        {
            await _priceCacheService.SynchronizeAsync(progress, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PriceCacheActivityText = "Ciclo cancelado; checkpoints preservados";
        }
        catch (Exception exception)
        {
            await _priceCacheRepository.SetStatusAsync(
                    PriceCacheStatus.Failed,
                    exception.Message,
                    CancellationToken.None)
                .ConfigureAwait(true);
            PriceCacheActivityText = $"Falha no cache: {exception.Message}";
        }
        finally
        {
            IsPriceCacheBusy = false;
            await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
            _priceCacheCycleCancellation?.Dispose();
            _priceCacheCycleCancellation = null;
            _priceCacheCycleTask = null;
            NotifyCommands();
        }
    }

    private async Task RefreshPriceCacheProgressAsync()
    {
        var policy = await _priceCacheRepository.GetPolicyAsync().ConfigureAwait(true);
        IsPriceCachePaused = policy.Paused;
        var progress = await _priceCacheRepository.GetProgressAsync().ConfigureAwait(true);
        UpdatePriceCacheProgress(progress);
    }

    private void UpdatePriceCacheProgress(PriceCacheProgress progress)
    {
        PriceCacheStatus = progress.Status;
        PriceCacheProgress = progress.Percentage;
        PriceCacheSummary =
            $"{progress.CompletedContracts:N0}/{progress.TotalContracts:N0} contratações · " +
            $"{progress.ItemCount:N0} itens · {progress.ActiveResultCount:N0} preços ativos · " +
            $"{progress.FailedContracts:N0} falhas · {FormatBytes(progress.OccupiedBytes)}" +
            (progress.EstimatedRemaining is { } eta && eta > TimeSpan.Zero
                ? $" · ETA {FormatDuration(eta)}"
                : string.Empty);
        PriceCacheActivityText = progress.Status switch
        {
            PriceCacheStatus.NotAuthorized => "Cache de 90 dias: aguardando autorização",
            PriceCacheStatus.Downloading when !string.IsNullOrWhiteSpace(progress.Message) =>
                $"Cache de 90 dias: {progress.Message}",
            PriceCacheStatus.Downloading => "Cache de 90 dias: baixando em segundo plano",
            PriceCacheStatus.Paused => "Cache de 90 dias: pausado",
            PriceCacheStatus.Complete => "Cache de 90 dias: completo",
            PriceCacheStatus.Failed => "Cache de 90 dias: há falhas aguardando repetição",
            PriceCacheStatus.InsufficientSpace => "Cache de 90 dias: pausado por falta de espaço",
            PriceCacheStatus.Disabled => "Cache de 90 dias: desativado",
            _ => "Cache de 90 dias: aguardando índice ou próxima tentativa"
        };
        NotifyCommands();
    }
}
