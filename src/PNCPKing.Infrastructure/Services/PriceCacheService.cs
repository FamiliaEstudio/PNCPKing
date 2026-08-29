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
    public const int WindowDays = 365;
    public static TimeSpan DefaultRequestTimeout { get; } = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(6);
    private readonly TimeSpan _requestTimeout = ValidateRequestTimeout(requestTimeout);
    private readonly IPerformanceTelemetry _performance = performance ?? NullPerformanceTelemetry.Instance;
    private readonly AsyncPauseGate _visibleActivityPause = new();

    public bool IsPausedForVisibleActivity => _visibleActivityPause.IsPaused;
    public void PauseForVisibleActivity() => _visibleActivityPause.Pause();
    public void ResumeAfterVisibleActivity() => _visibleActivityPause.Resume();

    public Task SynchronizeAsync(
        IProgress<PriceCacheProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        SynchronizeCoreAsync(
            maximumParallelContracts: 1,
            ignoreVisibleActivity: false,
            progress,
            cancellationToken);

    public Task SynchronizeAggressivelyAsync(
        int maximumParallelContracts,
        IProgress<PriceCacheProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumParallelContracts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumParallelContracts),
                "O paralelismo agressivo deve ser pelo menos 1.");
        }

        return SynchronizeCoreAsync(
            maximumParallelContracts,
            ignoreVisibleActivity: true,
            progress,
            cancellationToken);
    }

    private async Task SynchronizeCoreAsync(
        int maximumParallelContracts,
        bool ignoreVisibleActivity,
        IProgress<PriceCacheProgress>? progress,
        CancellationToken cancellationToken)
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
                    "Aguardando a cobertura completa do índice PNCP para os últimos 365 dias.",
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
        long nextSpaceCheckAt = 0;
        var active = new Dictionary<string, Task<bool>>(StringComparer.Ordinal);
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
                if (!ignoreVisibleActivity)
                {
                    await _visibleActivityPause.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                policy = await cache.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
                if (!policy.Authorized || !policy.Enabled || policy.Paused)
                {
                    if (active.Count == 0)
                    {
                        break;
                    }

                    await CompleteOneAsync(active, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (completedThisRun >= nextSpaceCheckAt)
                {
                    var estimate = await cache.EstimateAsync(start, today, cancellationToken).ConfigureAwait(false);
                    if (!estimate.HasEnoughSpace)
                    {
                        var message =
                            $"Espaço insuficiente: preserve {FormatBytes(estimate.SafetyReserveBytes)} de reserva " +
                            $"e libere aproximadamente {FormatBytes(estimate.RequiredFreeBytes - estimate.AvailableFreeBytes)}.";
                        await cache.SetPausedAsync(true, message, cancellationToken).ConfigureAwait(false);
                        await Task.WhenAll(active.Values).ConfigureAwait(false);
                        progress?.Report(await cache.GetProgressAsync(cancellationToken).ConfigureAwait(false));
                        return;
                    }

                    nextSpaceCheckAt = completedThisRun + 25;
                }

                var noAvailableWork = false;
                while (active.Count < maximumParallelContracts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var work = await cache.GetNextWorkAsync(DateTimeOffset.UtcNow, cancellationToken)
                        .ConfigureAwait(false);
                    if (work is null)
                    {
                        noAvailableWork = true;
                        break;
                    }

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
                    active.Add(
                        work.Contract.PncpId,
                        ProcessContractAsync(
                            work,
                            itemSnapshot?.IsCurrentFor(work.Contract) == true,
                            cancellationToken));
                }

                if (active.Count == 0 && noAvailableWork)
                {
                    var snapshot = await cache.GetProgressAsync(cancellationToken).ConfigureAwait(false);
                    if (snapshot.PendingContracts == 0 && snapshot.FailedContracts == 0)
                    {
                        await cache.SetStatusAsync(
                                PriceCacheStatus.Complete,
                                "Índice móvel de itens dos últimos 365 dias completamente armazenado.",
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

                if (active.Count > 0)
                {
                    if (maximumParallelContracts > 1 && ShouldReportProgress())
                    {
                        progress?.Report(activitySnapshot with
                        {
                            Message = $"modo agressivo: {active.Count:N0} contratação(ões) em processamento"
                        });
                    }

                    if (await CompleteOneAsync(active, cancellationToken).ConfigureAwait(false))
                    {
                        completedThisRun++;
                    }
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
            await AwaitInterruptedWorkersAsync(active.Values).ConfigureAwait(false);

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

    private static async Task<bool> CompleteOneAsync(
        Dictionary<string, Task<bool>> active,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completed = await Task.WhenAny(active.Values).ConfigureAwait(false);
        var contractId = active.First(pair => ReferenceEquals(pair.Value, completed)).Key;
        active.Remove(contractId);
        return await completed.ConfigureAwait(false);
    }

    private static async Task AwaitInterruptedWorkersAsync(IEnumerable<Task<bool>> workers)
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Each worker restored its checkpoint before propagating cancellation.
        }
    }

    private async Task<bool> ProcessContractAsync(
        PriceCacheWorkItem work,
        bool hasCurrentSnapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!hasCurrentSnapshot)
            {
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

            await cache.MarkContractCompleteAsync(
                    work.Contract.PncpId,
                    work.Contract.GlobalUpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
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
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            await cache.MarkContractUnavailableAsync(
                    work.Contract.PncpId,
                    work.Contract.GlobalUpdatedAt,
                    "O índice referencia a contratação, mas o endpoint de itens respondeu 404. " +
                    "Ela será reconsiderada somente se o PNCP publicar uma atualização global.",
                    CancellationToken.None)
                .ConfigureAwait(false);
            return true;
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
            return false;
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
