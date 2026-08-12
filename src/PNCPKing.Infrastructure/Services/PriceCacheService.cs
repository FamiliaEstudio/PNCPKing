using System.Diagnostics;
using System.Net;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;

namespace PNCPKing.Infrastructure.Services;

public sealed class PriceCacheService(
    IPncpClient client,
    IContractRepository contracts,
    ICoverageRepository coverage,
    IPriceCacheRepository cache,
    TimeSpan? requestTimeout = null,
    IPerformanceTelemetry? performance = null)
{
    public const int WindowDays = 90;
    public static TimeSpan DefaultRequestTimeout { get; } = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(6);
    private readonly TimeSpan _requestTimeout = ValidateRequestTimeout(requestTimeout);
    private readonly IPerformanceTelemetry _performance = performance ?? NullPerformanceTelemetry.Instance;
    private readonly AsyncPauseGate _visibleActivityPause = new();

    public bool IsPausedForVisibleActivity => _visibleActivityPause.IsPaused;
    public void PauseForVisibleActivity() => _visibleActivityPause.Pause();
    public void ResumeAfterVisibleActivity() => _visibleActivityPause.Resume();

    public async Task SynchronizeAsync(
        IProgress<PriceCacheProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var span = _performance.Begin("price-cache", "synchronize");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-(WindowDays - 1));
        var policy = await cache.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (!policy.Authorized || !policy.Enabled)
        {
            return;
        }

        if (!await coverage.IsCoverageCompleteAsync(start, today, cancellationToken).ConfigureAwait(false))
        {
            await cache.SetStatusAsync(
                    PriceCacheStatus.Idle,
                    "Aguardando a cobertura completa do índice PNCP para os últimos 90 dias.",
                    cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(await cache.GetProgressAsync(cancellationToken).ConfigureAwait(false));
            return;
        }

        await cache.PrepareWindowAsync(start, today, cancellationToken).ConfigureAwait(false);
        await cache.SetStatusAsync(PriceCacheStatus.Downloading, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var lastProgressReport = Stopwatch.GetTimestamp();
        long completedThisRun = 0;
        string? currentContractId = null;
        var activitySnapshot = await cache.GetProgressAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(activitySnapshot);

        bool ShouldReportProgress()
        {
            if (progress is null)
            {
                return false;
            }

            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(lastProgressReport, now) < TimeSpan.FromSeconds(1))
            {
                return false;
            }

            lastProgressReport = now;
            return true;
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _visibleActivityPause.WaitAsync(cancellationToken).ConfigureAwait(false);
                policy = await cache.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
                if (!policy.Authorized || !policy.Enabled || policy.Paused)
                {
                    break;
                }

                if (completedThisRun % 25 == 0)
                {
                    var estimate = await cache.EstimateAsync(start, today, cancellationToken).ConfigureAwait(false);
                    if (!estimate.HasEnoughSpace)
                    {
                        var message =
                            $"Espaço insuficiente: preserve {FormatBytes(estimate.SafetyReserveBytes)} de reserva " +
                            $"e libere aproximadamente {FormatBytes(estimate.RequiredFreeBytes - estimate.AvailableFreeBytes)}.";
                        await cache.SetPausedAsync(true, message, cancellationToken).ConfigureAwait(false);
                        progress?.Report(await cache.GetProgressAsync(cancellationToken).ConfigureAwait(false));
                        return;
                    }
                }

                var work = await cache.GetNextWorkAsync(DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
                if (work is null)
                {
                    var snapshot = await cache.GetProgressAsync(cancellationToken).ConfigureAwait(false);
                    if (snapshot.PendingContracts == 0 && snapshot.FailedContracts == 0)
                    {
                        await cache.SetStatusAsync(
                                PriceCacheStatus.Complete,
                                "Janela móvel de 90 dias completamente armazenada.",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (snapshot.FailedContracts > 0)
                    {
                        await cache.SetStatusAsync(
                                PriceCacheStatus.Failed,
                                "Há contratações aguardando a próxima tentativa automática.",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await cache.SetStatusAsync(PriceCacheStatus.Idle, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }

                    progress?.Report(await BuildProgressAsync(
                            stopwatch,
                            completedThisRun,
                            cancellationToken)
                        .ConfigureAwait(false));
                    span.Complete(completedThisRun);
                    return;
                }

                currentContractId = work.Contract.PncpId;
                var itemSnapshot = await contracts.GetItemSnapshotAsync(
                        work.Contract.PncpId,
                        cancellationToken)
                    .ConfigureAwait(false);
                var ownsNewData = work.Checkpoint.BackgroundOwned ||
                                  itemSnapshot is null && !work.Checkpoint.UserPinned;
                await cache.MarkContractDownloadingAsync(
                        work.Contract.PncpId,
                        ownsNewData,
                        cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    if (itemSnapshot?.IsCurrentFor(work.Contract) != true)
                    {
                        if (ShouldReportProgress())
                        {
                            progress?.Report(activitySnapshot with
                            {
                                Message = $"consultando itens da contratação " +
                                          $"(limite de {_requestTimeout.TotalSeconds:N0} s)"
                            });
                        }
                        using var listScope = PncpRequestOptions.BeginScope(
                            PncpRequestPriority.BackgroundPriceCache,
                            PncpRequestCategory.ItemLists);
                        var items = await ExecuteWithRequestTimeoutAsync(
                                token => client.GetItemsAsync(work.Contract, token),
                                cancellationToken)
                            .ConfigureAwait(false);
                        await contracts.UpsertItemsAsync(
                                work.Contract.PncpId,
                                items,
                                forceRefresh: false,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var pendingItems = await contracts.GetPendingItemsAsync(
                            work.Contract.PncpId,
                            forceRefresh: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    for (var pendingIndex = 0; pendingIndex < pendingItems.Count; pendingIndex++)
                    {
                        var item = pendingItems[pendingIndex];
                        cancellationToken.ThrowIfCancellationRequested();
                        await _visibleActivityPause.WaitAsync(cancellationToken).ConfigureAwait(false);
                        if (ShouldReportProgress())
                        {
                            progress?.Report(activitySnapshot with
                            {
                                Message = $"consultando preço {pendingIndex + 1:N0}/{pendingItems.Count:N0}"
                            });
                        }
                        await contracts.SetItemHydrationStatusAsync(
                                work.Contract.PncpId,
                                item.ItemNumber,
                                ItemHydrationStatus.Loading,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        try
                        {
                            using var resultScope = PncpRequestOptions.BeginScope(
                                PncpRequestPriority.BackgroundPriceCache,
                                PncpRequestCategory.ItemResults);
                            var results = await ExecuteWithRequestTimeoutAsync(
                                    token => client.GetItemResultsAsync(
                                        work.Contract,
                                        item.ItemNumber,
                                        token),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            await contracts.ReplaceItemResultsAsync(
                                    work.Contract.PncpId,
                                    item.ItemNumber,
                                    results,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (HttpRequestException exception) when (
                            exception.StatusCode == HttpStatusCode.NotFound)
                        {
                            // O índice pode manter um item cujo endpoint de resultados já foi removido.
                            // Uma resposta vazia fecha o item sem bloquear toda a contratação.
                            await contracts.ReplaceItemResultsAsync(
                                    work.Contract.PncpId,
                                    item.ItemNumber,
                                    [],
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            await contracts.SetItemHydrationStatusAsync(
                                    work.Contract.PncpId,
                                    item.ItemNumber,
                                    ItemHydrationStatus.NotLoaded,
                                    "Carga interrompida; item preservado para retomada.",
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            throw;
                        }
                        catch (Exception exception)
                        {
                            await contracts.SetItemHydrationStatusAsync(
                                    work.Contract.PncpId,
                                    item.ItemNumber,
                                    ItemHydrationStatus.Failed,
                                    exception.Message,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            throw;
                        }
                    }

                    await cache.MarkContractCompleteAsync(
                            work.Contract.PncpId,
                            work.Contract.GlobalUpdatedAt,
                            cancellationToken)
                        .ConfigureAwait(false);
                    completedThisRun++;
                    currentContractId = null;
                }
                catch (OperationCanceledException)
                {
                    await cache.MarkContractPendingAsync(
                            work.Contract.PncpId,
                            "Ciclo interrompido; a contratação será retomada.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }
                catch (HttpRequestException exception) when (
                    exception.StatusCode == HttpStatusCode.NotFound)
                {
                    await cache.MarkContractUnavailableAsync(
                            work.Contract.PncpId,
                            work.Contract.GlobalUpdatedAt,
                            "O índice referencia a contratação, mas o endpoint de itens respondeu 404. " +
                            "Ela será reconsiderada somente se o PNCP publicar uma atualização global.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    completedThisRun++;
                    currentContractId = null;
                }
                catch (Exception exception)
                {
                    var retry = DateTimeOffset.UtcNow + RetryDelay(work.Checkpoint.Attempts + 1);
                    await cache.MarkContractFailedAsync(
                            work.Contract.PncpId,
                            exception.Message,
                            retry,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    currentContractId = null;
                }

                if (ShouldReportProgress())
                {
                    activitySnapshot = await BuildProgressAsync(
                            stopwatch,
                            completedThisRun,
                            cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(activitySnapshot);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (currentContractId is not null)
            {
                await cache.MarkContractPendingAsync(
                        currentContractId,
                        "Ciclo interrompido; a contratação será retomada.",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var currentPolicy = await cache.GetPolicyAsync(CancellationToken.None).ConfigureAwait(false);
            if (currentPolicy.Enabled && !currentPolicy.Paused)
            {
                await cache.SetStatusAsync(
                        PriceCacheStatus.Idle,
                        "Ciclo cancelado; checkpoints preservados.",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    private async Task<PriceCacheProgress> BuildProgressAsync(
        Stopwatch stopwatch,
        long completedThisRun,
        CancellationToken cancellationToken)
    {
        var value = await cache.GetProgressAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan? eta = null;
        if (completedThisRun > 0 && value.PendingContracts > 0)
        {
            var ticksPerContract = stopwatch.Elapsed.Ticks / completedThisRun;
            var projectedTicks = Math.Min(
                TimeSpan.MaxValue.Ticks,
                (double)ticksPerContract * value.PendingContracts);
            eta = TimeSpan.FromTicks((long)projectedTicks);
        }

        return value with { EstimatedRemaining = eta };
    }

    private static TimeSpan RetryDelay(int attempts)
    {
        var minutes = Math.Pow(2, Math.Clamp(attempts - 1, 0, 9));
        return TimeSpan.FromMinutes(Math.Min(MaximumRetryDelay.TotalMinutes, minutes));
    }

    private async Task<T> ExecuteWithRequestTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            return await operation(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"A API do PNCP não respondeu em {_requestTimeout.TotalSeconds:N0} segundos; " +
                "a contratação foi adiada para nova tentativa.",
                exception);
        }
    }

    private static TimeSpan ValidateRequestTimeout(TimeSpan? requestTimeout)
    {
        var value = requestTimeout ?? DefaultRequestTimeout;
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "O limite de espera deve ser maior que zero.");
        }

        return value;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024d && unit < units.Length - 1)
        {
            display /= 1024d;
            unit++;
        }

        return $"{display:N1} {units[unit]}";
    }
}
