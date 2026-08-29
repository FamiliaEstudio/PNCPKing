using System.Windows;
using System.Windows.Input;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _priceCacheCycleCancellation;
    private Task? _priceCacheCycleTask;
    private bool _isPriceCacheBusy;
    private bool _isPriceCachePaused;
    private bool _isAggressivePriceCacheMode;
    private PriceCacheStatus _priceCacheStatus = PriceCacheStatus.NotAuthorized;
    private PriceCacheProgress? _lastPriceCacheProgress;
    private double _priceCacheProgress;
    private string _priceCacheSummary = "Índice nacional de itens ainda não autorizado.";
    private string _priceCacheActivityText = "Índice de itens de 365 dias: inativo";

    public ICommand EstimateAndActivatePriceCacheCommand { get; private set; } = null!;
    public ICommand PausePriceCacheCommand { get; private set; } = null!;
    public ICommand CancelPriceCacheCommand { get; private set; } = null!;
    public ICommand ToggleAggressivePriceCacheCommand { get; private set; } = null!;
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

    public bool IsAggressivePriceCacheMode
    {
        get => _isAggressivePriceCacheMode;
        private set
        {
            if (SetProperty(ref _isAggressivePriceCacheMode, value))
            {
                OnPropertyChanged(nameof(PriceCacheActivityText));
                OnPropertyChanged(nameof(IsAnyAggressivePncpMode));
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
            () => !IsFileBusy && !IsPriceCacheBusy && !IsAggressiveNationalPriceMode);
        PausePriceCacheCommand = new AsyncRelayCommand(
            TogglePriceCachePauseAsync,
            () => PriceCacheStatus is not PriceCacheStatus.NotAuthorized and not PriceCacheStatus.Disabled);
        CancelPriceCacheCommand = new RelayCommand(
            CancelPriceCacheCycle,
            () => IsPriceCacheBusy && _priceCacheCycleCancellation is not null);
        ToggleAggressivePriceCacheCommand = new AsyncRelayCommand(
            ToggleAggressivePriceCacheAsync,
            () => IsAggressivePriceCacheMode ||
                  (!IsAggressiveNationalPriceMode && !IsFileBusy && !IsIndexBusy && !IsCatalogBusy && !IsPriceBusy &&
                   !IsForegroundBusy && !IsDocumentBusy && !IsPriceCachePaused &&
                   PriceCacheStatus is not PriceCacheStatus.NotAuthorized and not PriceCacheStatus.Disabled));
        DisablePriceCacheCommand = new AsyncRelayCommand(
            DisablePriceCacheAsync,
            () => PriceCacheStatus is not PriceCacheStatus.NotAuthorized and not PriceCacheStatus.Disabled);
        RemovePriceCacheCommand = new AsyncRelayCommand(
            RemovePriceCacheAsync,
            () => !IsFileBusy && !IsPriceCacheBusy);
    }

    private async Task ToggleAggressivePriceCacheAsync()
    {
        if (IsAggressivePriceCacheMode)
        {
            await StopAggressivePriceCacheAsync().ConfigureAwait(true);
            return;
        }

        if (_quotationItemWindow is { IsVisible: true })
        {
            MessageBox.Show(
                "Feche primeiro a janela de pesquisa do item da cotação. Enquanto o modo agressivo estiver " +
                "ativo, novas pesquisas no PNCP ficarão bloqueadas.",
                "Download agressivo",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            OnPropertyChanged(nameof(IsAggressivePriceCacheMode));
            return;
        }

        var policy = await _priceCacheRepository.GetPolicyAsync().ConfigureAwait(true);
        if (!policy.Authorized || !policy.Enabled || policy.Paused)
        {
            MessageBox.Show(
                "Ative e retome primeiro o índice nacional de itens.",
                "Download agressivo",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            OnPropertyChanged(nameof(IsAggressivePriceCacheMode));
            return;
        }

        if (MessageBox.Show(
                "ATIVAR DOWNLOAD AGRESSIVO\n\n" +
                "O PNCP King usará continuamente toda a concorrência adaptativa disponível para baixar " +
                "listas de itens. Pesquisas, preços e documentos do PNCP ficarão indisponíveis até você " +
                "desligar este botão.\n\n" +
                "O modo respeitará bloqueios 429, Retry-After, falta de espaço e pressão crítica de memória. " +
                "Nenhum preço homologado será baixado por este modo. Continuar?",
                "Download agressivo",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            OnPropertyChanged(nameof(IsAggressivePriceCacheMode));
            return;
        }

        await CancelAndAwaitPriceCacheCycleAsync().ConfigureAwait(true);
        IsAggressivePriceCacheMode = true;
        _maintenanceCoordinator.CancelActiveSlice();
        _maintenanceTimer.Stop();
        MaintenanceActivityText = "Manutenção: modo agressivo dedicado ao índice de itens";
        PriceCacheActivityText = "Índice de itens de 365 dias: iniciando modo agressivo";
        _ = StartPriceCacheCycleAsync(aggressive: true);
    }

    private async Task StopAggressivePriceCacheAsync(bool scheduleNormalMaintenance = true)
    {
        if (!IsAggressivePriceCacheMode)
        {
            return;
        }

        IsAggressivePriceCacheMode = false;
        _aggressivePriceCacheIterationCancellation?.Cancel();
        await CancelAndAwaitPriceCacheCycleAsync().ConfigureAwait(true);
        PriceCacheActivityText = "Download agressivo desligado; checkpoints preservados";
        if (scheduleNormalMaintenance && !_disposed)
        {
            ScheduleNextMaintenance(TimeSpan.FromSeconds(1));
        }
    }

    private async Task CancelAndAwaitPriceCacheCycleAsync()
    {
        var task = _priceCacheCycleTask;
        _priceCacheCycleCancellation?.Cancel();
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // O núcleo já restaurou os checkpoints antes de propagar o cancelamento.
        }
    }

    private void CancelPriceCacheCycle()
    {
        if (IsAggressivePriceCacheMode)
        {
            IsAggressivePriceCacheMode = false;
            _aggressivePriceCacheIterationCancellation?.Cancel();
            ScheduleNextMaintenance(TimeSpan.FromSeconds(1));
        }

        _priceCacheCycleCancellation?.Cancel();
    }

    private async Task EstimateAndActivatePriceCacheAsync()
    {
        var end = DateOnly.FromDateTime(DateTime.Today);
        var start = end.AddDays(-(PriceCacheService.WindowDays - 1));
        PriceCacheActivityText = "Calculando contratos, espaço e duração…";
        if (_repository is ICoverageRepository coverage &&
            !await coverage.IsCoverageCompleteAsync(start, end).ConfigureAwait(true))
        {
            MessageBox.Show(
                "Conclua primeiro a cobertura do índice PNCP para os últimos 365 dias. " +
                "A autorização depende dessa cobertura para calcular o volume corretamente.",
                "Índice nacional de itens",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            PriceCacheActivityText = "Índice de itens de 365 dias: aguardando cobertura PNCP";
            return;
        }

        var estimate = await _priceCacheRepository.EstimateAsync(start, end).ConfigureAwait(true);
        await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
        if (estimate.ContractCount == 0)
        {
            MessageBox.Show(
                "O índice ainda não possui contratações no período. Conclua primeiro a cobertura dos últimos 365 dias.",
                "Índice nacional de itens",
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
            "ATIVAR ÍNDICE NACIONAL DE ITENS\n\n" +
            $"Janela: {start:dd/MM/yyyy} a {end:dd/MM/yyyy}\n" +
            $"Contratações no índice: {estimate.ContractCount:N0}\n" +
            $"Listas já indexadas: {estimate.AlreadyCompleteContracts:N0}\n" +
            $"Chamadas de listas restantes: {estimate.RemainingContracts:N0}\n" +
            "Chamadas de resultados na carga de fundo: 0\n" +
            $"Crescimento estimado: {FormatBytes(estimate.EstimatedMinimumBytes)} a {FormatBytes(estimate.EstimatedMaximumBytes)}\n" +
            $"Espaço livre: {FormatBytes(estimate.AvailableFreeBytes)}\n" +
            $"Reserva preservada: {FormatBytes(estimate.SafetyReserveBytes)}\n" +
            $"Duração inicial estimada: {FormatDuration(estimate.EstimatedMinimumDuration)} a {FormatDuration(estimate.EstimatedMaximumDuration)}\n\n" +
            "A carga armazenará somente as listas de itens. Preços homologados serão consultados e preservados " +
            "apenas quando um item corresponder a uma pesquisa. Ela continuará nas próximas aberturas, " +
            "sempre cedendo a API às ações do usuário. Autorizar?",
            "Confirmar índice de itens de 365 dias",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            PriceCacheActivityText = "Índice de itens de 365 dias: autorização cancelada";
            return;
        }

        await _priceCacheRepository.SetAuthorizationAsync(true, start, end).ConfigureAwait(true);
        await _priceCacheRepository.PrepareWindowAsync(start, end).ConfigureAwait(true);
        await StartPriceCacheCycleAsync().ConfigureAwait(true);
    }

    private async Task TogglePriceCachePauseAsync()
    {
        if (IsAggressivePriceCacheMode)
        {
            await StopAggressivePriceCacheAsync(scheduleNormalMaintenance: false).ConfigureAwait(true);
        }

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
        if (IsAggressivePriceCacheMode)
        {
            await StopAggressivePriceCacheAsync(scheduleNormalMaintenance: false).ConfigureAwait(true);
        }

        var end = DateOnly.FromDateTime(DateTime.Today);
        var start = end.AddDays(-(PriceCacheService.WindowDays - 1));
        await _priceCacheRepository.SetAuthorizationAsync(false, start, end).ConfigureAwait(true);
        _priceCacheCycleCancellation?.Cancel();
        await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
        PriceCacheActivityText = "Índice desativado; os dados já baixados foram conservados";
    }

    private async Task RemovePriceCacheAsync()
    {
        var progress = await _priceCacheRepository.GetProgressAsync().ConfigureAwait(true);
        if (MessageBox.Show(
                $"Remover {FormatBytes(progress.OccupiedBytes)} de listas reconstruíveis do índice de 365 dias?\n\n" +
                "Preços consultados sob demanda e referências usadas em cotações serão preservados.",
                "Remover índice nacional de itens",
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
            StatusText = "Índice de itens reconstruível removido; preços permanentes foram preservados.";
        }).ConfigureAwait(true);
    }

    private async Task<bool> TryRunPriceCacheMaintenanceAsync(
        TimeSpan sliceDuration,
        CancellationToken cancellationToken)
    {
        if (_disposed || IsFileBusy || IsPriceCacheBusy)
        {
            return false;
        }

        if (IsAnyAggressivePncpMode)
        {
            return false;
        }

        var policy = await Task.Run(
                () => _priceCacheRepository.GetPolicyAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);
        if (!policy.Authorized || !policy.Enabled || policy.Paused)
        {
            return false;
        }

        using var phaseSpan = _performanceTelemetry.Begin("maintenance", "price-cache");
        try
        {
            await StartPriceCacheCycleAsync(sliceDuration, cancellationToken).ConfigureAwait(true);
            phaseSpan.Complete();
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
        return true;
    }

    private Task StartPriceCacheCycleAsync(
        TimeSpan? sliceDuration = null,
        CancellationToken cancellationToken = default,
        bool aggressive = false)
    {
        if (_priceCacheCycleTask is { IsCompleted: false })
        {
            return _priceCacheCycleTask;
        }

        _priceCacheCycleCancellation?.Dispose();
        _priceCacheCycleCancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        if (sliceDuration is { } budget)
        {
            _priceCacheCycleCancellation.CancelAfter(budget);
        }

        _priceCacheCycleTask = RunPriceCacheCycleCoreAsync(
            _priceCacheCycleCancellation.Token,
            aggressive);
        return _priceCacheCycleTask;
    }

    private async Task RunPriceCacheCycleCoreAsync(
        CancellationToken cancellationToken,
        bool aggressive)
    {
        var policy = await Task.Run(
                () => _priceCacheRepository.GetPolicyAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);
        if (!policy.Authorized || !policy.Enabled || policy.Paused)
        {
            await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
            return;
        }

        IsPriceCacheBusy = true;
        IsPriceCachePaused = false;
        PriceCacheActivityText = "Índice de itens de 365 dias: aguardando oportunidade na API";
        var progress = new Progress<PriceCacheProgress>(UpdatePriceCacheProgress);
        try
        {
            if (aggressive)
            {
                await RunAggressivePriceCacheLoopAsync(progress, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                await Task.Run(
                        () => _priceCacheService.SynchronizeAsync(progress, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            PriceCacheActivityText = aggressive
                ? "Download agressivo interrompido; checkpoints preservados"
                : "Ciclo cancelado; checkpoints preservados";
        }
        catch (Exception exception)
        {
            await Task.Run(() => _priceCacheRepository.SetStatusAsync(
                    PriceCacheStatus.Failed,
                    exception.Message,
                    CancellationToken.None))
                .ConfigureAwait(true);
            PriceCacheActivityText = $"Falha no índice de itens: {exception.Message}";
        }
        finally
        {
            if (aggressive)
            {
                IsAggressivePriceCacheMode = false;
                _aggressivePriceCacheIterationCancellation = null;
            }

            IsPriceCacheBusy = false;
            await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
            _priceCacheCycleCancellation?.Dispose();
            _priceCacheCycleCancellation = null;
            _priceCacheCycleTask = null;
            NotifyCommands();
        }
    }

    private async Task RunAggressivePriceCacheLoopAsync(
        IProgress<PriceCacheProgress> progress,
        CancellationToken cancellationToken)
    {
        using var aggressiveScheduler = _requestScheduler.EnableAggressiveBackgroundRequests();
        var maximumParallelContracts = _requestScheduler.GetSnapshot().MaximumConcurrency;
        while (IsAggressivePriceCacheMode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var policy = await _priceCacheRepository.GetPolicyAsync(cancellationToken).ConfigureAwait(true);
            if (!policy.Authorized || !policy.Enabled || policy.Paused)
            {
                break;
            }

            if (_aggressivePriceCacheResourcePressure == SystemResourcePressure.Critical)
            {
                PriceCacheActivityText =
                    "Índice de itens de 365 dias: modo agressivo aguardando a RAM sair do nível crítico";
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(true);
                continue;
            }

            using var iterationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _aggressivePriceCacheIterationCancellation = iterationCancellation;
            try
            {
                var coverageRan = await TryRunAutomaticMaintenanceAsync(
                        sliceDuration: null,
                        iterationCancellation.Token)
                    .ConfigureAwait(true);
                iterationCancellation.Token.ThrowIfCancellationRequested();
                if (coverageRan)
                {
                    PriceCacheActivityText =
                        "Índice de itens de 365 dias: cobertura atualizada; iniciando listas em modo agressivo";
                }

                await Task.Run(
                        () => _priceCacheService.SynchronizeAggressivelyAsync(
                            maximumParallelContracts,
                            progress,
                            iterationCancellation.Token),
                        iterationCancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                _aggressivePriceCacheResourcePressure == SystemResourcePressure.Critical)
            {
                PriceCacheActivityText =
                    "Índice de itens de 365 dias: chamadas interrompidas por pressão crítica de RAM; retomada automática";
            }
            finally
            {
                if (ReferenceEquals(_aggressivePriceCacheIterationCancellation, iterationCancellation))
                {
                    _aggressivePriceCacheIterationCancellation = null;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _priceCacheRepository.GetProgressAsync(cancellationToken).ConfigureAwait(true);
            UpdatePriceCacheProgress(snapshot);
            if (snapshot.Status == PriceCacheStatus.Complete ||
                snapshot.Status is PriceCacheStatus.Paused or PriceCacheStatus.InsufficientSpace or
                    PriceCacheStatus.Disabled)
            {
                break;
            }

            // No delay is inserted while contracts are available: the service only
            // returns here when it completed the window or is waiting for coverage,
            // resource recovery or a checkpoint retry time.
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task RefreshPriceCacheProgressAsync()
    {
        var snapshot = await Task.Run(async () =>
        {
            var policy = await _priceCacheRepository.GetPolicyAsync().ConfigureAwait(false);
            var progress = await _priceCacheRepository.GetProgressAsync().ConfigureAwait(false);
            return (Policy: policy, Progress: progress);
        }).ConfigureAwait(true);
        var policy = snapshot.Policy;
        IsPriceCachePaused = policy.Paused;
        UpdatePriceCacheProgress(snapshot.Progress);
    }

    private void UpdatePriceCacheProgress(PriceCacheProgress progress)
    {
        _lastPriceCacheProgress = progress;
        PriceCacheStatus = progress.Status;
        PriceCacheProgress = progress.Percentage;
        PriceCacheSummary =
            $"{progress.CompletedContracts:N0}/{progress.TotalContracts:N0} contratações · " +
            $"{progress.ItemCount:N0} itens · {progress.ActiveResultCount:N0} preços permanentes ativos · " +
            $"{progress.FailedContracts:N0} falhas · {FormatBytes(progress.OccupiedBytes)}" +
            (progress.EstimatedRemaining is { } eta && eta > TimeSpan.Zero
                ? $" · ETA {FormatDuration(eta)}"
                : string.Empty);
        if (IsAggressivePriceCacheMode)
        {
            UpdateAggressivePriceCacheActivity();
        }
        else
        {
            PriceCacheActivityText = progress.Status switch
            {
                PriceCacheStatus.NotAuthorized => "Índice de itens de 365 dias: aguardando autorização",
                PriceCacheStatus.Downloading when !string.IsNullOrWhiteSpace(progress.Message) =>
                    $"Índice de itens de 365 dias: {progress.Message}",
                PriceCacheStatus.Downloading => "Índice de itens de 365 dias: baixando listas em segundo plano",
                PriceCacheStatus.Paused => "Índice de itens de 365 dias: pausado",
                PriceCacheStatus.Complete => "Índice de itens de 365 dias: completo",
                PriceCacheStatus.Failed => "Índice de itens de 365 dias: há falhas aguardando repetição",
                PriceCacheStatus.InsufficientSpace => "Índice de itens de 365 dias: pausado por falta de espaço",
                PriceCacheStatus.Disabled => "Índice de itens de 365 dias: desativado",
                _ => "Índice de itens de 365 dias: aguardando cobertura ou próxima tentativa"
            };
        }
        if (progress.Status is PriceCacheStatus.Failed or PriceCacheStatus.InsufficientSpace)
        {
            OpenMaintenanceForIssue($"price-cache-{progress.Status}");
        }

        NotifyCommands();
    }

    private void UpdateAggressivePriceCacheActivity()
    {
        if (!IsAggressivePriceCacheMode)
        {
            return;
        }

        if (_aggressivePriceCacheResourcePressure == SystemResourcePressure.Critical)
        {
            PriceCacheActivityText =
                "Índice de itens de 365 dias: modo agressivo aguardando a RAM sair do nível crítico";
            return;
        }

        if (IsIndexBusy)
        {
            PriceCacheActivityText =
                "Índice de itens de 365 dias: AGRESSIVO · finalizando a cobertura PNCP antes das listas";
            return;
        }

        var scheduler = _requestScheduler.GetSnapshot();
        var throughput = scheduler.RollingThroughput * 60d;
        var recovery = scheduler.GrowthBlockedUntil is { } blockedUntil && blockedUntil > DateTimeOffset.UtcNow
            ? $" · recuo até {blockedUntil.ToLocalTime():HH:mm:ss}" +
              (string.IsNullOrWhiteSpace(scheduler.LastReductionReason)
                  ? string.Empty
                  : $" ({scheduler.LastReductionReason})")
            : string.Empty;
        var waiting = _lastPriceCacheProgress?.Status == PriceCacheStatus.Failed
            ? " · aguardando checkpoint retomável"
            : string.Empty;
        PriceCacheActivityText =
            $"Índice de itens de 365 dias: AGRESSIVO · " +
            $"{scheduler.ActiveBackgroundPriceCache:N0} ativa(s) · " +
            $"concorrência {scheduler.EffectiveConcurrency:N0}/{scheduler.MaximumConcurrency:N0} · " +
            $"{throughput:N1} chamada(s)/min{recovery}{waiting}";
    }
}
