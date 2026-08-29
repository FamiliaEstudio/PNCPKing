using System.Windows;
using System.Windows.Input;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _nationalPriceCycleCancellation;
    private CancellationTokenSource? _aggressiveNationalPriceIterationCancellation;
    private Task? _nationalPriceCycleTask;
    private bool _isNationalPriceIndexBusy;
    private bool _isNationalPriceIndexPaused;
    private bool _isAggressiveNationalPriceMode;
    private PriceCacheStatus _nationalPriceIndexStatus = PriceCacheStatus.NotAuthorized;
    private NationalPriceIndexProgress? _lastNationalPriceIndexProgress;
    private double _nationalPriceIndexProgress;
    private string _nationalPriceIndexSummary = "Índice nacional de preços ainda não autorizado.";
    private string _nationalPriceIndexActivityText = "Índice de preços de 365 dias: inativo";

    public ICommand EstimateAndActivateNationalPriceIndexCommand { get; private set; } = null!;
    public ICommand PauseNationalPriceIndexCommand { get; private set; } = null!;
    public ICommand CancelNationalPriceIndexCommand { get; private set; } = null!;
    public ICommand ToggleAggressiveNationalPriceIndexCommand { get; private set; } = null!;
    public ICommand DisableNationalPriceIndexCommand { get; private set; } = null!;
    public ICommand RemoveNationalPriceIndexCommand { get; private set; } = null!;

    public bool IsAnyAggressivePncpMode =>
        IsAggressivePriceCacheMode || IsAggressiveNationalPriceMode;

    public bool IsNationalPriceIndexBusy
    {
        get => _isNationalPriceIndexBusy;
        private set
        {
            if (SetProperty(ref _isNationalPriceIndexBusy, value))
            {
                NotifyCommands();
            }
        }
    }

    public bool IsNationalPriceIndexPaused
    {
        get => _isNationalPriceIndexPaused;
        private set
        {
            if (SetProperty(ref _isNationalPriceIndexPaused, value))
            {
                OnPropertyChanged(nameof(PauseNationalPriceIndexButtonText));
                NotifyCommands();
            }
        }
    }

    public bool IsAggressiveNationalPriceMode
    {
        get => _isAggressiveNationalPriceMode;
        private set
        {
            if (SetProperty(ref _isAggressiveNationalPriceMode, value))
            {
                OnPropertyChanged(nameof(IsAnyAggressivePncpMode));
                NotifyCommands();
            }
        }
    }

    public PriceCacheStatus NationalPriceIndexStatus
    {
        get => _nationalPriceIndexStatus;
        private set => SetProperty(ref _nationalPriceIndexStatus, value);
    }

    public double NationalPriceIndexProgress
    {
        get => _nationalPriceIndexProgress;
        private set => SetProperty(ref _nationalPriceIndexProgress, Math.Clamp(value, 0d, 100d));
    }

    public string NationalPriceIndexSummary
    {
        get => _nationalPriceIndexSummary;
        private set => SetProperty(ref _nationalPriceIndexSummary, value);
    }

    public string NationalPriceIndexActivityText
    {
        get => _nationalPriceIndexActivityText;
        private set => SetProperty(ref _nationalPriceIndexActivityText, value);
    }

    public string PauseNationalPriceIndexButtonText =>
        IsNationalPriceIndexPaused ? "Retomar" : "Pausar";

    private void InitializeNationalPriceIndex()
    {
        EstimateAndActivateNationalPriceIndexCommand = new AsyncRelayCommand(
            EstimateAndActivateNationalPriceIndexAsync,
            () => !IsFileBusy && !IsNationalPriceIndexBusy && !IsAnyAggressivePncpMode);
        PauseNationalPriceIndexCommand = new AsyncRelayCommand(
            ToggleNationalPriceIndexPauseAsync,
            () => NationalPriceIndexStatus is not PriceCacheStatus.NotAuthorized and not PriceCacheStatus.Disabled);
        CancelNationalPriceIndexCommand = new RelayCommand(
            CancelNationalPriceIndexCycle,
            () => IsNationalPriceIndexBusy && _nationalPriceCycleCancellation is not null);
        ToggleAggressiveNationalPriceIndexCommand = new AsyncRelayCommand(
            ToggleAggressiveNationalPriceIndexAsync,
            () => IsAggressiveNationalPriceMode ||
                  (!IsAggressivePriceCacheMode && !IsFileBusy && !IsIndexBusy && !IsCatalogBusy &&
                   !IsPriceBusy && !IsForegroundBusy && !IsDocumentBusy && !IsNationalPriceIndexPaused &&
                   NationalPriceIndexStatus is not PriceCacheStatus.NotAuthorized and not PriceCacheStatus.Disabled));
        DisableNationalPriceIndexCommand = new AsyncRelayCommand(
            DisableNationalPriceIndexAsync,
            () => NationalPriceIndexStatus is not PriceCacheStatus.NotAuthorized and not PriceCacheStatus.Disabled);
        RemoveNationalPriceIndexCommand = new AsyncRelayCommand(
            RemoveNationalPriceIndexAsync,
            () => !IsFileBusy && !IsNationalPriceIndexBusy);
    }

    private async Task EstimateAndActivateNationalPriceIndexAsync()
    {
        var itemProgress = await _priceCacheRepository.GetProgressAsync().ConfigureAwait(true);
        if (itemProgress.Status != PriceCacheStatus.Complete ||
            itemProgress.CompletedContracts < itemProgress.TotalContracts)
        {
            MessageBox.Show(
                "Conclua primeiro o índice nacional de itens dos últimos 365 dias. " +
                "A relação completa é necessária para calcular e baixar somente os preços elegíveis.",
                "Índice nacional de preços",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            NationalPriceIndexActivityText = "Índice de preços: aguardando a conclusão das listas de itens";
            return;
        }

        var end = DateOnly.FromDateTime(DateTime.Today);
        var start = end.AddDays(-(PriceCacheService.WindowDays - 1));
        NationalPriceIndexActivityText = "Calculando itens elegíveis, espaço e duração…";
        var estimate = await _priceCacheRepository.EstimateNationalPriceIndexAsync(start, end)
            .ConfigureAwait(true);
        if (estimate.EligibleItems == 0)
        {
            MessageBox.Show(
                "Nenhum item com resultado foi encontrado nas listas atuais.",
                "Índice nacional de preços",
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
            "ATIVAR ÍNDICE NACIONAL DE PREÇOS\n\n" +
            $"Janela: {start:dd/MM/yyyy} a {end:dd/MM/yyyy}\n" +
            $"Itens elegíveis (temResultado=true): {estimate.EligibleItems:N0}\n" +
            $"Itens já consultados: {estimate.CompletedItems:N0}\n" +
            $"Chamadas de resultados restantes: {estimate.RemainingItems:N0}\n" +
            $"Transferência estimada: {FormatBytes(estimate.EstimatedNetworkBytes)}\n" +
            $"Crescimento estimado: {FormatBytes(estimate.EstimatedMinimumBytes)} a {FormatBytes(estimate.EstimatedMaximumBytes)}\n" +
            $"Espaço livre: {FormatBytes(estimate.AvailableFreeBytes)}\n" +
            $"Reserva preservada: {FormatBytes(estimate.SafetyReserveBytes)}\n" +
            $"Duração inicial estimada: {FormatDuration(estimate.EstimatedMinimumDuration)} a {FormatDuration(estimate.EstimatedMaximumDuration)}\n\n" +
            "Serão guardados todos os resultados Informado com valor unitário homologado positivo. " +
            "Normalmente há uma vencedora; múltiplas vencedoras oficiais do mesmo item serão preservadas.\n\n" +
            "A autorização não inicia chamadas. O download só começará quando você ativar o botão " +
            "Download agressivo desta barra. Autorizar?",
            "Confirmar índice de preços de 365 dias",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            NationalPriceIndexActivityText = "Índice de preços de 365 dias: autorização cancelada";
            return;
        }

        IsNationalPriceIndexBusy = true;
        try
        {
            await _priceCacheRepository.SetNationalPriceIndexAuthorizationAsync(true, start, end)
                .ConfigureAwait(true);
            NationalPriceIndexActivityText = "Preparando checkpoints do índice de preços…";
            await Task.Run(
                    () => _priceCacheRepository.PrepareNationalPriceIndexAsync(start, end),
                    CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshNationalPriceIndexProgressAsync().ConfigureAwait(true);
            NationalPriceIndexActivityText =
                "Índice autorizado; ative Download agressivo para iniciar as consultas";
        }
        finally
        {
            IsNationalPriceIndexBusy = false;
        }
    }

    private async Task ToggleAggressiveNationalPriceIndexAsync()
    {
        if (IsAggressiveNationalPriceMode)
        {
            await StopAggressiveNationalPriceIndexAsync().ConfigureAwait(true);
            return;
        }

        if (_quotationItemWindow is { IsVisible: true })
        {
            MessageBox.Show(
                "Feche primeiro a janela de pesquisa do item da cotação. Enquanto o modo agressivo estiver " +
                "ativo, novas pesquisas no PNCP ficarão bloqueadas.",
                "Download agressivo de preços",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            OnPropertyChanged(nameof(IsAggressiveNationalPriceMode));
            return;
        }

        var pricePolicy = await _priceCacheRepository.GetNationalPriceIndexPolicyAsync().ConfigureAwait(true);
        var itemPolicy = await _priceCacheRepository.GetPolicyAsync().ConfigureAwait(true);
        if (!pricePolicy.Authorized || !pricePolicy.Enabled || pricePolicy.Paused)
        {
            MessageBox.Show(
                "Autorize e retome primeiro o índice nacional de preços.",
                "Download agressivo de preços",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!itemPolicy.Authorized || !itemPolicy.Enabled || itemPolicy.Paused)
        {
            MessageBox.Show(
                "Ative e retome primeiro o índice nacional de itens. Ele precisa permanecer disponível para " +
                "atualizar listas pendentes antes dos preços.",
                "Download agressivo de preços",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                "ATIVAR DOWNLOAD AGRESSIVO DE PREÇOS\n\n" +
                "O PNCP King concluirá primeiro qualquer cobertura ou lista de itens pendente e depois " +
                "consultará continuamente os resultados homologados elegíveis. Pesquisas, preços de cotações " +
                "e documentos remotos ficarão indisponíveis até você desligar este botão.\n\n" +
                "O modo respeitará 429, Retry-After, falta de espaço e pressão crítica de memória. Continuar?",
                "Download agressivo de preços",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            OnPropertyChanged(nameof(IsAggressiveNationalPriceMode));
            return;
        }

        await CancelAndAwaitPriceCacheCycleAsync().ConfigureAwait(true);
        IsAggressiveNationalPriceMode = true;
        _maintenanceCoordinator.CancelActiveSlice();
        _maintenanceTimer.Stop();
        MaintenanceActivityText = "Manutenção: modo agressivo dedicado ao índice de preços";
        NationalPriceIndexActivityText = "Índice de preços de 365 dias: iniciando modo agressivo";
        _ = StartNationalPriceIndexCycleAsync();
    }

    private async Task StopAggressiveNationalPriceIndexAsync(bool scheduleNormalMaintenance = true)
    {
        if (!IsAggressiveNationalPriceMode)
        {
            return;
        }

        IsAggressiveNationalPriceMode = false;
        _aggressiveNationalPriceIterationCancellation?.Cancel();
        await CancelAndAwaitNationalPriceIndexCycleAsync().ConfigureAwait(true);
        NationalPriceIndexActivityText = "Download agressivo de preços desligado; checkpoints preservados";
        if (scheduleNormalMaintenance && !_disposed)
        {
            ScheduleNextMaintenance(TimeSpan.FromSeconds(1));
        }
    }

    private async Task ToggleNationalPriceIndexPauseAsync()
    {
        if (IsAggressiveNationalPriceMode)
        {
            await StopAggressiveNationalPriceIndexAsync(scheduleNormalMaintenance: false).ConfigureAwait(true);
        }

        var policy = await _priceCacheRepository.GetNationalPriceIndexPolicyAsync().ConfigureAwait(true);
        await _priceCacheRepository.SetNationalPriceIndexPausedAsync(
                !policy.Paused,
                policy.Paused ? null : "Pausa manual; a chamada atual terminará antes da parada.")
            .ConfigureAwait(true);
        await RefreshNationalPriceIndexProgressAsync().ConfigureAwait(true);
        NationalPriceIndexActivityText = policy.Paused
            ? "Índice retomado; ative Download agressivo para continuar"
            : "Índice de preços pausado; checkpoints preservados";
    }

    private async Task DisableNationalPriceIndexAsync()
    {
        if (IsAggressiveNationalPriceMode)
        {
            await StopAggressiveNationalPriceIndexAsync(scheduleNormalMaintenance: false).ConfigureAwait(true);
        }

        var end = DateOnly.FromDateTime(DateTime.Today);
        var start = end.AddDays(-(PriceCacheService.WindowDays - 1));
        await _priceCacheRepository.SetNationalPriceIndexAuthorizationAsync(false, start, end)
            .ConfigureAwait(true);
        _nationalPriceCycleCancellation?.Cancel();
        await RefreshNationalPriceIndexProgressAsync().ConfigureAwait(true);
        NationalPriceIndexActivityText = "Índice de preços desativado; dados baixados conservados";
    }

    private async Task RemoveNationalPriceIndexAsync()
    {
        var progress = await _priceCacheRepository.GetNationalPriceIndexProgressAsync().ConfigureAwait(true);
        if (MessageBox.Show(
                $"Remover {FormatBytes(progress.OccupiedBytes)} de preços nacionais reconstruíveis?\n\n" +
                "Listas de itens, resultados consultados sob demanda e referências usadas em cotações serão preservados.",
                "Remover índice nacional de preços",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunFileOperationAsync(async cancellationToken =>
        {
            await _priceCacheRepository.RemoveBackgroundPricesAsync(cancellationToken).ConfigureAwait(true);
            await RefreshNationalPriceIndexProgressAsync().ConfigureAwait(true);
            await RefreshDatasetSummaryAsync().ConfigureAwait(true);
            StatusText = "Preços nacionais reconstruíveis removidos; resultados permanentes foram preservados.";
        }).ConfigureAwait(true);
    }

    private Task StartNationalPriceIndexCycleAsync()
    {
        if (_nationalPriceCycleTask is { IsCompleted: false })
        {
            return _nationalPriceCycleTask;
        }

        _nationalPriceCycleCancellation?.Dispose();
        _nationalPriceCycleCancellation = new CancellationTokenSource();
        _nationalPriceCycleTask = RunNationalPriceIndexCycleCoreAsync(
            _nationalPriceCycleCancellation.Token);
        return _nationalPriceCycleTask;
    }

    private async Task RunNationalPriceIndexCycleCoreAsync(CancellationToken cancellationToken)
    {
        IsNationalPriceIndexBusy = true;
        var progress = new Progress<NationalPriceIndexProgress>(UpdateNationalPriceIndexProgress);
        try
        {
            await RunAggressiveNationalPriceIndexLoopAsync(progress, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            NationalPriceIndexActivityText = "Download agressivo interrompido; checkpoints preservados";
        }
        catch (Exception exception)
        {
            await _priceCacheRepository.SetNationalPriceIndexStatusAsync(
                    PriceCacheStatus.Failed,
                    exception.Message,
                    CancellationToken.None)
                .ConfigureAwait(true);
            NationalPriceIndexActivityText = $"Falha no índice de preços: {exception.Message}";
        }
        finally
        {
            IsAggressiveNationalPriceMode = false;
            _aggressiveNationalPriceIterationCancellation = null;
            IsNationalPriceIndexBusy = false;
            await RefreshNationalPriceIndexProgressAsync().ConfigureAwait(true);
            _nationalPriceCycleCancellation?.Dispose();
            _nationalPriceCycleCancellation = null;
            _nationalPriceCycleTask = null;
            if (!_disposed)
            {
                ScheduleNextMaintenance(TimeSpan.FromSeconds(1));
            }
            NotifyCommands();
        }
    }

    private async Task RunAggressiveNationalPriceIndexLoopAsync(
        IProgress<NationalPriceIndexProgress> progress,
        CancellationToken cancellationToken)
    {
        using var aggressiveScheduler = _requestScheduler.EnableAggressiveBackgroundRequests();
        var maximumParallelContracts = _requestScheduler.GetSnapshot().MaximumConcurrency;
        while (IsAggressiveNationalPriceMode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var policy = await _priceCacheRepository.GetNationalPriceIndexPolicyAsync(cancellationToken)
                .ConfigureAwait(true);
            if (!policy.Authorized || !policy.Enabled || policy.Paused)
            {
                break;
            }

            if (_aggressivePriceCacheResourcePressure == SystemResourcePressure.Critical)
            {
                NationalPriceIndexActivityText =
                    "Índice de preços: modo agressivo aguardando a RAM sair do nível crítico";
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(true);
                continue;
            }

            using var iterationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _aggressiveNationalPriceIterationCancellation = iterationCancellation;
            try
            {
                var coverageRan = await TryRunAutomaticMaintenanceAsync(
                        sliceDuration: null,
                        iterationCancellation.Token)
                    .ConfigureAwait(true);
                iterationCancellation.Token.ThrowIfCancellationRequested();
                if (coverageRan)
                {
                    NationalPriceIndexActivityText =
                        "Índice de preços: cobertura atualizada; verificando listas de itens";
                }

                var itemProgress = new Progress<PriceCacheProgress>(UpdatePriceCacheProgress);
                await Task.Run(
                        () => _priceCacheService.SynchronizeAggressivelyAsync(
                            maximumParallelContracts,
                            itemProgress,
                            iterationCancellation.Token),
                        iterationCancellation.Token)
                    .ConfigureAwait(true);
                iterationCancellation.Token.ThrowIfCancellationRequested();

                var itemSnapshot = await _priceCacheRepository.GetProgressAsync(iterationCancellation.Token)
                    .ConfigureAwait(true);
                UpdatePriceCacheProgress(itemSnapshot);
                if (itemSnapshot.Status != PriceCacheStatus.Complete)
                {
                    NationalPriceIndexActivityText =
                        "Índice de preços: aguardando listas de itens pendentes antes dos resultados";
                }
                else
                {
                    await Task.Run(
                            () => _nationalPriceIndexService.SynchronizeAggressivelyAsync(
                                maximumParallelContracts,
                                progress,
                                iterationCancellation.Token),
                            iterationCancellation.Token)
                        .ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                _aggressivePriceCacheResourcePressure == SystemResourcePressure.Critical)
            {
                NationalPriceIndexActivityText =
                    "Índice de preços: chamadas interrompidas por pressão crítica de RAM; retomada automática";
            }
            finally
            {
                if (ReferenceEquals(_aggressiveNationalPriceIterationCancellation, iterationCancellation))
                {
                    _aggressiveNationalPriceIterationCancellation = null;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _priceCacheRepository.GetNationalPriceIndexProgressAsync(cancellationToken)
                .ConfigureAwait(true);
            UpdateNationalPriceIndexProgress(snapshot);
            if (snapshot.Status == PriceCacheStatus.Complete ||
                snapshot.Status is PriceCacheStatus.Paused or PriceCacheStatus.InsufficientSpace or
                    PriceCacheStatus.Disabled)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(true);
        }
    }

    private void CancelNationalPriceIndexCycle()
    {
        IsAggressiveNationalPriceMode = false;
        _aggressiveNationalPriceIterationCancellation?.Cancel();
        _nationalPriceCycleCancellation?.Cancel();
    }

    private async Task CancelAndAwaitNationalPriceIndexCycleAsync()
    {
        var task = _nationalPriceCycleTask;
        _nationalPriceCycleCancellation?.Cancel();
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
            // O serviço já restaurou os checkpoints interrompidos.
        }
    }

    private async Task RefreshNationalPriceIndexProgressAsync()
    {
        var snapshot = await Task.Run(async () =>
        {
            var policy = await _priceCacheRepository.GetNationalPriceIndexPolicyAsync().ConfigureAwait(false);
            var progress = await _priceCacheRepository.GetNationalPriceIndexProgressAsync().ConfigureAwait(false);
            return (Policy: policy, Progress: progress);
        }).ConfigureAwait(true);
        IsNationalPriceIndexPaused = snapshot.Policy.Paused;
        UpdateNationalPriceIndexProgress(snapshot.Progress);
    }

    private void UpdateNationalPriceIndexProgress(NationalPriceIndexProgress progress)
    {
        _lastNationalPriceIndexProgress = progress;
        NationalPriceIndexStatus = progress.Status;
        NationalPriceIndexProgress = progress.Percentage;
        NationalPriceIndexSummary =
            $"{progress.CompletedItems:N0}/{progress.EligibleItems:N0} itens consultados · " +
            $"{progress.PricedItems:N0} com preço · {progress.ResultRows:N0} resultado(s) · " +
            $"{progress.NoPriceItems:N0} sem preço útil · {progress.FailedContracts:N0} falhas · " +
            $"{FormatBytes(progress.OccupiedBytes)}" +
            (progress.EstimatedRemaining is { } eta && eta > TimeSpan.Zero
                ? $" · ETA {FormatDuration(eta)}"
                : string.Empty);

        if (IsAggressiveNationalPriceMode)
        {
            UpdateAggressiveNationalPriceActivity();
        }
        else
        {
            NationalPriceIndexActivityText = progress.Status switch
            {
                PriceCacheStatus.NotAuthorized => "Índice de preços de 365 dias: aguardando autorização",
                PriceCacheStatus.Downloading when !string.IsNullOrWhiteSpace(progress.Message) =>
                    $"Índice de preços de 365 dias: {progress.Message}",
                PriceCacheStatus.Downloading => "Índice de preços de 365 dias: consultando resultados",
                PriceCacheStatus.Paused => "Índice de preços de 365 dias: pausado",
                PriceCacheStatus.Complete => "Índice de preços de 365 dias: completo",
                PriceCacheStatus.Failed => "Índice de preços: há falhas aguardando repetição",
                PriceCacheStatus.InsufficientSpace => "Índice de preços: pausado por falta de espaço",
                PriceCacheStatus.Disabled => "Índice de preços de 365 dias: desativado",
                _ => "Índice de preços: autorizado; ative Download agressivo para continuar"
            };
        }

        if (progress.Status is PriceCacheStatus.Failed or PriceCacheStatus.InsufficientSpace)
        {
            OpenMaintenanceForIssue($"national-price-index-{progress.Status}");
        }

        NotifyCommands();
    }

    private void UpdateAggressiveNationalPriceActivity()
    {
        if (!IsAggressiveNationalPriceMode)
        {
            return;
        }

        if (_aggressivePriceCacheResourcePressure == SystemResourcePressure.Critical)
        {
            NationalPriceIndexActivityText =
                "Índice de preços: modo agressivo aguardando a RAM sair do nível crítico";
            return;
        }

        if (IsIndexBusy)
        {
            NationalPriceIndexActivityText =
                "Índice de preços: AGRESSIVO · finalizando a cobertura PNCP antes dos resultados";
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
        var waiting = _lastNationalPriceIndexProgress?.Status == PriceCacheStatus.Failed
            ? " · aguardando checkpoint retomável"
            : string.Empty;
        NationalPriceIndexActivityText =
            $"Índice de preços: AGRESSIVO · {scheduler.ActiveBackgroundPriceCache:N0} ativa(s) · " +
            $"concorrência {scheduler.EffectiveConcurrency:N0}/{scheduler.MaximumConcurrency:N0} · " +
            $"{throughput:N1} chamada(s)/min{recovery}{waiting}";
    }
}
