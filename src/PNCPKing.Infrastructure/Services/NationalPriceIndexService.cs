using System.Diagnostics;
using System.Net;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;

namespace PNCPKing.Infrastructure.Services;

public sealed class NationalPriceIndexService(
    IPncpClient client,
    IContractRepository contracts,
    IPriceCacheRepository cache,
    TimeSpan? requestTimeout = null,
    IPerformanceTelemetry? performance = null)
{
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(6);
    private readonly TimeSpan _requestTimeout = requestTimeout ?? PriceCacheService.DefaultRequestTimeout;
    private readonly IPerformanceTelemetry _performance = performance ?? NullPerformanceTelemetry.Instance;

    public async Task SynchronizeAggressivelyAsync(
        int maximumParallelContracts,
        IProgress<NationalPriceIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumParallelContracts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumParallelContracts),
                "O paralelismo agressivo deve ser pelo menos 1.");
        }

        using var span = _performance.Begin("national-price-index", "synchronize");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-(PriceCacheService.WindowDays - 1));
        var policy = await cache.GetNationalPriceIndexPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (!policy.Authorized || !policy.Enabled || policy.Paused)
        {
            return;
        }

        await cache.PrepareNationalPriceIndexAsync(start, today, cancellationToken).ConfigureAwait(false);
        await cache.SetNationalPriceIndexStatusAsync(
                PriceCacheStatus.Downloading,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var lastProgressReport = Stopwatch.GetTimestamp();
        long completedThisRun = 0;
        long nextSpaceCheckAt = 0;
        var active = new Dictionary<string, Task<ContractResult>>(StringComparer.Ordinal);

        bool ShouldReportProgress()
        {
            var now = Stopwatch.GetTimestamp();
            if (progress is null ||
                Stopwatch.GetElapsedTime(lastProgressReport, now) < TimeSpan.FromSeconds(1))
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
                policy = await cache.GetNationalPriceIndexPolicyAsync(cancellationToken).ConfigureAwait(false);
                if (!policy.Authorized || !policy.Enabled || policy.Paused)
                {
                    if (active.Count == 0)
                    {
                        break;
                    }

                    var stopping = await CompleteOneAsync(active, cancellationToken).ConfigureAwait(false);
                    completedThisRun += stopping.CompletedItems;
                    continue;
                }

                if (completedThisRun >= nextSpaceCheckAt)
                {
                    var estimate = await cache.EstimateNationalPriceIndexAsync(start, today, cancellationToken)
                        .ConfigureAwait(false);
                    if (!estimate.HasEnoughSpace)
                    {
                        var missing = Math.Max(0, estimate.RequiredFreeBytes - estimate.AvailableFreeBytes);
                        var message =
                            $"Espaço insuficiente: preserve {FormatBytes(estimate.SafetyReserveBytes)} de reserva " +
                            $"e libere aproximadamente {FormatBytes(missing)}.";
                        await cache.SetNationalPriceIndexPausedAsync(true, message, cancellationToken)
                            .ConfigureAwait(false);
                        await Task.WhenAll(active.Values).ConfigureAwait(false);
                        progress?.Report(await cache.GetNationalPriceIndexProgressAsync(cancellationToken)
                            .ConfigureAwait(false));
                        return;
                    }

                    nextSpaceCheckAt = completedThisRun + 5_000;
                }

                var noAvailableWork = false;
                while (active.Count < maximumParallelContracts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var work = await cache.GetNextNationalPriceWorkAsync(
                            DateTimeOffset.UtcNow,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (work is null)
                    {
                        noAvailableWork = true;
                        break;
                    }

                    await cache.MarkNationalPriceContractDownloadingAsync(
                            work.Contract.PncpId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    active.Add(
                        work.Contract.PncpId,
                        ProcessContractAsync(work, cancellationToken));
                }

                if (active.Count == 0 && noAvailableWork)
                {
                    var snapshot = await cache.GetNationalPriceIndexProgressAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (snapshot.CompletedItems >= snapshot.EligibleItems && snapshot.FailedContracts == 0)
                    {
                        await cache.SetNationalPriceIndexStatusAsync(
                                PriceCacheStatus.Complete,
                                "Índice móvel de preços dos últimos 365 dias completamente consultado.",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (snapshot.FailedContracts > 0)
                    {
                        await cache.SetNationalPriceIndexStatusAsync(
                                PriceCacheStatus.Failed,
                                "Há itens aguardando a próxima tentativa automática.",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await cache.SetNationalPriceIndexStatusAsync(
                                PriceCacheStatus.Idle,
                                "Aguardando a atualização das listas de itens.",
                                cancellationToken)
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
                    var completed = await CompleteOneAsync(active, cancellationToken).ConfigureAwait(false);
                    completedThisRun += completed.CompletedItems;
                }

                if (ShouldReportProgress())
                {
                    progress?.Report(await BuildProgressAsync(
                            stopwatch,
                            completedThisRun,
                            cancellationToken)
                        .ConfigureAwait(false));
                }
            }
        }
        catch (OperationCanceledException)
        {
            await AwaitInterruptedWorkersAsync(active.Values).ConfigureAwait(false);
            var current = await cache.GetNationalPriceIndexPolicyAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (current.Enabled && !current.Paused)
            {
                await cache.SetNationalPriceIndexStatusAsync(
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

    private async Task<ContractResult> ProcessContractAsync(
        NationalPriceIndexWorkItem work,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        var failed = 0;
        string? lastError = null;
        try
        {
            var pending = await contracts.GetPendingItemsAsync(
                    work.Contract.PncpId,
                    forceRefresh: false,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await contracts.SetItemHydrationStatusAsync(
                            work.Contract.PncpId,
                            item.ItemNumber,
                            ItemHydrationStatus.Loading,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    using var requestScope = PncpRequestOptions.BeginScope(
                        PncpRequestPriority.BackgroundPriceCache,
                        PncpRequestCategory.ItemResults);
                    var results = await ExecuteWithRequestTimeoutAsync(
                            token => client.GetItemResultsAsync(work.Contract, item.ItemNumber, token),
                            cancellationToken)
                        .ConfigureAwait(false);
                    var useful = results
                        .Where(result => result.ResultStatusId == 1 &&
                                         result.HomologatedUnitValueScaled is > 0)
                        .ToArray();
                    await contracts.ReplaceBackgroundItemResultsAsync(
                            work.Contract.PncpId,
                            item.ItemNumber,
                            useful,
                            cancellationToken)
                        .ConfigureAwait(false);
                    completed++;
                }
                catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    const string reason =
                        "O item foi concluído sem preço porque o endpoint de resultados respondeu 404.";
                    await contracts.ReplaceBackgroundItemResultsAsync(
                            work.Contract.PncpId,
                            item.ItemNumber,
                            [],
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await contracts.SetItemHydrationStatusAsync(
                            work.Contract.PncpId,
                            item.ItemNumber,
                            ItemHydrationStatus.Complete,
                            reason,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    await contracts.SetItemHydrationStatusAsync(
                            work.Contract.PncpId,
                            item.ItemNumber,
                            ItemHydrationStatus.NotLoaded,
                            "Consulta interrompida; item pendente para retomada.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    lastError = exception.Message;
                    await contracts.SetItemHydrationStatusAsync(
                            work.Contract.PncpId,
                            item.ItemNumber,
                            ItemHydrationStatus.Failed,
                            exception.Message,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            if (failed == 0)
            {
                await cache.MarkNationalPriceContractCompleteAsync(
                        work.Contract.PncpId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return new ContractResult(completed, true);
            }

            var retry = DateTimeOffset.UtcNow + RetryDelay(work.Checkpoint.Attempts + 1);
            await cache.MarkNationalPriceContractFailedAsync(
                    work.Contract.PncpId,
                    lastError ?? "Falha ao consultar resultados homologados.",
                    retry,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new ContractResult(completed, false);
        }
        catch (OperationCanceledException)
        {
            await cache.MarkNationalPriceContractPendingAsync(
                    work.Contract.PncpId,
                    "Ciclo interrompido; a contratação será retomada.",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task<NationalPriceIndexProgress> BuildProgressAsync(
        Stopwatch stopwatch,
        long completedThisRun,
        CancellationToken cancellationToken)
    {
        var value = await cache.GetNationalPriceIndexProgressAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan? eta = null;
        var remaining = Math.Max(0, value.EligibleItems - value.CompletedItems);
        if (completedThisRun > 0 && remaining > 0)
        {
            var ticksPerItem = stopwatch.Elapsed.Ticks / completedThisRun;
            var projected = Math.Min(TimeSpan.MaxValue.Ticks, (double)ticksPerItem * remaining);
            eta = TimeSpan.FromTicks((long)projected);
        }

        return value with { EstimatedRemaining = eta };
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
                "o item foi adiado para nova tentativa.",
                exception);
        }
    }

    private static async Task<ContractResult> CompleteOneAsync(
        Dictionary<string, Task<ContractResult>> active,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completed = await Task.WhenAny(active.Values).ConfigureAwait(false);
        var contractId = active.First(pair => ReferenceEquals(pair.Value, completed)).Key;
        active.Remove(contractId);
        return await completed.ConfigureAwait(false);
    }

    private static async Task AwaitInterruptedWorkersAsync(IEnumerable<Task<ContractResult>> workers)
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cada trabalhador restaurou o item e o checkpoint antes de propagar o cancelamento.
        }
    }

    private static TimeSpan RetryDelay(int attempts)
    {
        var minutes = Math.Pow(2, Math.Clamp(attempts - 1, 0, 9));
        return TimeSpan.FromMinutes(Math.Min(MaximumRetryDelay.TotalMinutes, minutes));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var display = (double)Math.Max(0, bytes);
        var unit = 0;
        while (display >= 1024d && unit < units.Length - 1)
        {
            display /= 1024d;
            unit++;
        }

        return $"{display:N1} {units[unit]}";
    }

    private sealed record ContractResult(int CompletedItems, bool Succeeded);
}
