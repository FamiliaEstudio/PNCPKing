using System.Globalization;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;

namespace PNCPKing.Infrastructure.Services;

public sealed class SyncService(
    IPncpClient client,
    IContractRepository repository,
    IPerformanceTelemetry? performance = null)
{
    public static TimeSpan AutomaticRetryDelay { get; } = TimeSpan.FromMinutes(10);

    private readonly AsyncPauseGate _pauseGate = new();
    private readonly IPerformanceTelemetry _performance = performance ?? NullPerformanceTelemetry.Instance;

    public bool IsPaused => _pauseGate.IsPaused;

    public void Pause() => _pauseGate.Pause();

    public void Resume() => _pauseGate.Resume();

    public Task WaitWhilePausedAsync(CancellationToken cancellationToken) => _pauseGate.WaitAsync(cancellationToken);

    public Task SynchronizeAsync(
        DateOnly queryStartDate,
        DateOnly endDate,
        GeoScope scope,
        SyncMode mode,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        SynchronizeAsync(
            queryStartDate,
            endDate,
            scope,
            mode,
            SyncExecutionOptions.Default,
            progress,
            cancellationToken);

    public async Task SynchronizeAsync(
        DateOnly queryStartDate,
        DateOnly endDate,
        GeoScope scope,
        SyncMode mode,
        SyncExecutionOptions options,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var span = _performance.Begin("sync", "total");
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumConcurrency, 1);
        if (queryStartDate > endDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queryStartDate),
                $"A data inicial {queryStartDate:dd/MM/yyyy} não pode ser posterior à data final {endDate:dd/MM/yyyy}.");
        }
        var minimumStart = DataWindow.Start(endDate);
        if (queryStartDate < minimumStart)
            queryStartDate = minimumStart;

        // Persist the authorization/run before the first network call. If even
        // the modality catalog times out, the 10-minute maintenance tick can
        // recognize and resume this confirmed load during the same opening.
        var runId = await repository.StartSyncRunAsync(mode, queryStartDate, endDate, cancellationToken).ConfigureAwait(false);
        long contractsSaved = 0;
        try
        {
            var knownModalities = (options.KnownModalities ??
                await client.GetModalitiesAsync(cancellationToken).ConfigureAwait(false))
            .Where(modality => modality.Active)
            .DistinctBy(modality => modality.Id)
            .ToArray();
        if (knownModalities.Length == 0)
        {
            throw new InvalidOperationException("O PNCP não informou nenhuma modalidade ativa.");
        }

        var modalities = options.ModalityIds is null
            ? knownModalities
            : knownModalities.Where(modality => options.ModalityIds.Contains(modality.Id)).ToArray();
        if (modalities.Length == 0)
        {
            throw new ArgumentException("Nenhuma modalidade solicitada está ativa.", nameof(options));
        }

        if (options.ModalityIds is not null)
        {
            var unknownIds = options.ModalityIds.Except(knownModalities.Select(modality => modality.Id)).ToArray();
            if (unknownIds.Length > 0)
            {
                throw new ArgumentException(
                    $"Modalidade(s) inativa(s) ou desconhecida(s): {string.Join(", ", unknownIds)}.",
                    nameof(options));
            }
        }

        var coverageRepository = mode == SyncMode.Publication
            ? repository as ICoverageRepository
            : null;
        if (coverageRepository is not null)
        {
            var activeModalityIds = knownModalities.Select(modality => modality.Id).ToArray();
            foreach (var uf in scope.ApiUfFilters)
            {
                await coverageRepository.EnsureCoverageWindowAsync(
                    queryStartDate,
                    endDate,
                    activeModalityIds,
                    uf ?? "ALL",
                    cancellationToken).ConfigureAwait(false);
            }
        }

            var partitions = BuildPartitions(queryStartDate, endDate, scope, modalities, mode);
            if (options.CheckpointScope is { } checkpointScope)
                partitions = partitions.Select(p => p with { Key = p.Key + ":cycle:" + checkpointScope }).ToArray();
            var completedPartitions = 0;
            Exception? deferredFailure = null;
            var progressGate = new object();
            var partitionConcurrency = Math.Min(partitions.Count, Math.Max(1, options.MaximumConcurrency / 2));
            var pageConcurrency = Math.Max(1, options.MaximumConcurrency / Math.Max(1, partitionConcurrency));

            void ReportProgress(long saved, string message, bool complete = false)
            {
                lock (progressGate)
                {
                    contractsSaved += saved;
                    if (complete) completedPartitions++;
                    progress?.Report(new SyncProgress(contractsSaved, completedPartitions, partitions.Count, message));
                }
            }

            await Parallel.ForEachAsync(partitions, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, partitionConcurrency),
                CancellationToken = cancellationToken
            }, async (partition, partitionToken) =>
            {
                // Finish work already in flight, then move on to the available
                // items/prices instead of opening more partitions during an outage.
                if (Volatile.Read(ref deferredFailure) is not null) return;
                await _pauseGate.WaitAsync(partitionToken).ConfigureAwait(false);
                var savedPage = await repository.GetPartitionNextPageAsync(partition.Key, partitionToken).ConfigureAwait(false);
                if (savedPage == 0)
                {
                    await SetCoverageStatusAsync(
                        coverageRepository,
                        partition,
                        CoverageStatus.Complete,
                        cancellationToken: partitionToken).ConfigureAwait(false);
                    ReportProgress(0, $"Partição já concluída: {partition.Description}", complete: true);
                    return;
                }

                try
                {
                    await DownloadPartitionWithCoverageAsync(
                        partition,
                        savedPage ?? 1,
                        mode,
                        coverageRepository,
                        pageConcurrency,
                        (saved, message) => ReportProgress(saved, message),
                        partitionToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (PncpRequestOptions.RetryTransientFailures &&
                                                   PncpClient.CanDeferContractFailure(exception))
                {
                    Interlocked.CompareExchange(ref deferredFailure, exception, null);
                    ReportProgress(0, $"Pendente para retomar: {partition.Description}. {exception.Message}");
                    return;
                }

                ReportProgress(0, $"Concluída: {partition.Description}", complete: true);
            }).ConfigureAwait(false);

            if (deferredFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(deferredFailure).Throw();
            }

            if (options.FinalizeDataset)
            {
                var rollingStart = DataWindow.Start(endDate);
                await repository.PruneContractsBeforeAsync(rollingStart, cancellationToken).ConfigureAwait(false);
                await repository.SetDatasetStateAsync(
                    rollingStart,
                    endDate,
                    scope,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }

            await repository.CompleteSyncRunAsync(runId, true, contractsSaved, null, cancellationToken).ConfigureAwait(false);
            span.Complete(contractsSaved);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await repository.CompleteSyncRunAsync(
                runId,
                false,
                contractsSaved,
                exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await repository.CompleteSyncRunAsync(
                runId,
                false,
                contractsSaved,
                "Operação cancelada pelo usuário.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<long> DownloadPartitionWithCoverageAsync(
        SyncPartition partition,
        int firstPage,
        SyncMode mode,
        ICoverageRepository? coverageRepository,
        int pageConcurrency,
        Action<long, string> reportProgress,
        CancellationToken cancellationToken)
    {
        var existingCheckpoint = await repository.GetPartitionCheckpointAsync(
            partition.Key,
            cancellationToken).ConfigureAwait(false);
        await repository.SavePartitionCheckpointAsync(
            partition.CreateCheckpoint(
                mode,
                firstPage,
                SyncPartitionStatus.Downloading,
                existingCheckpoint?.TotalPages),
            cancellationToken).ConfigureAwait(false);
        await SetCoverageStatusAsync(
            coverageRepository,
            partition,
            CoverageStatus.Downloading,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            var saved = await DownloadPartitionAsync(
                partition,
                firstPage,
                mode,
                pageConcurrency,
                reportProgress,
                cancellationToken).ConfigureAwait(false);
            long? recordsCount = partition.StartDate == partition.EndDate && firstPage == 1
                ? saved
                : null;
            await SetCoverageStatusAsync(
                coverageRepository,
                partition,
                CoverageStatus.Complete,
                recordsCount,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return saved;
        }
        catch (HttpRequestException exception) when (IsPncpDateRangeRejection(exception) &&
                                                       partition.StartDate < partition.EndDate)
        {
            reportProgress(0,
                $"O PNCP rejeitou a semana {partition.StartDate:dd/MM/yyyy}–{partition.EndDate:dd/MM/yyyy}; tentando dia a dia");

            // The weekly request proved nothing about an individual day. Reset
            // the temporary Downloading state before tracking each day.
            await SetCoverageStatusAsync(
                coverageRepository,
                partition,
                CoverageStatus.Missing,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            long saved = 0;
            for (var day = partition.StartDate; day <= partition.EndDate; day = day.AddDays(1))
            {
                var dailyPartition = partition.ForSingleDay(day, mode);
                var dailySavedPage = await repository.GetPartitionNextPageAsync(
                    dailyPartition.Key,
                    cancellationToken).ConfigureAwait(false);
                if (dailySavedPage == 0)
                {
                    await SetCoverageStatusAsync(
                        coverageRepository,
                        dailyPartition,
                        CoverageStatus.Complete,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    continue;
                }

                saved += await DownloadPartitionWithCoverageAsync(
                    dailyPartition,
                    dailySavedPage ?? 1,
                    mode,
                    coverageRepository,
                    pageConcurrency,
                    reportProgress,
                    cancellationToken).ConfigureAwait(false);
            }

            await repository.SavePartitionCheckpointAsync(
                partition.CreateCheckpoint(mode, 0, SyncPartitionStatus.Complete),
                cancellationToken).ConfigureAwait(false);
            return saved;
        }
        catch (Exception exception)
        {
            await MarkInterruptedCoverageAsync(
                coverageRepository,
                partition,
                firstPage,
                mode,
                exception).ConfigureAwait(false);

            throw;
        }
    }

    private async Task MarkInterruptedCoverageAsync(
        ICoverageRepository? coverageRepository,
        SyncPartition partition,
        int firstPage,
        SyncMode mode,
        Exception exception)
    {
        var savedPage = await repository.GetPartitionNextPageAsync(
            partition.Key,
            CancellationToken.None).ConfigureAwait(false);
        var hasPartialData = firstPage > 1 || savedPage is > 1;
        var error = exception is OperationCanceledException
            ? "Operação cancelada pelo usuário."
            : exception.Message;
        await SetCoverageStatusAsync(
            coverageRepository,
            partition,
            hasPartialData ? CoverageStatus.Partial : CoverageStatus.Failed,
            error: error,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        var checkpoint = await repository.GetPartitionCheckpointAsync(
            partition.Key,
            CancellationToken.None).ConfigureAwait(false);
        await repository.SavePartitionCheckpointAsync(
            partition.CreateCheckpoint(
                checkpoint?.Mode ?? mode,
                savedPage is > 0 ? savedPage.Value : Math.Max(1, firstPage),
                hasPartialData ? SyncPartitionStatus.Partial : SyncPartitionStatus.Failed,
                checkpoint?.TotalPages,
                error,
                exception is OperationCanceledException ? null : DateTimeOffset.UtcNow.Add(AutomaticRetryDelay)),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static Task SetCoverageStatusAsync(
        ICoverageRepository? coverageRepository,
        SyncPartition partition,
        CoverageStatus status,
        long? recordsCount = null,
        string? error = null,
        CancellationToken cancellationToken = default) =>
        coverageRepository is null
            ? Task.CompletedTask
            : coverageRepository.SetCoverageStatusAsync(
                partition.StartDate,
                partition.EndDate,
                partition.ModalityId,
                partition.Uf ?? "ALL",
                status,
                recordsCount,
                error,
                cancellationToken);

    private async Task<long> DownloadPartitionAsync(
        SyncPartition partition,
        int firstPage,
        SyncMode mode,
        int pageConcurrency,
        Action<long, string> reportProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _pauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        reportProgress(0, $"{partition.Description} — página {firstPage}");
        var first = await client.GetContractsPageAsync(
            partition.StartDate,
            partition.EndDate,
            partition.ModalityId,
            partition.Uf,
            firstPage,
            50,
            mode,
            cancellationToken).ConfigureAwait(false);
        var firstIsComplete = first.Contracts.Count == 0 ||
                              first.TotalPages == 0 ||
                              firstPage >= first.TotalPages;
        await repository.CommitSyncPageAsync(
            first.Contracts,
            partition.CreateCheckpoint(
                mode,
                firstIsComplete ? 0 : firstPage + 1,
                firstIsComplete ? SyncPartitionStatus.Complete : SyncPartitionStatus.Partial,
                first.TotalPages),
            cancellationToken).ConfigureAwait(false);
        long savedInPartition = first.Contracts.Count;
        reportProgress(first.Contracts.Count, $"{partition.Description} — página {firstPage}");
        if (firstIsComplete)
        {
            return savedInPartition;
        }

        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pendingPages = new Queue<Task<ContractPage>>();
        var nextPage = firstPage + 1;

        async Task<ContractPage> LoadPageAsync(int pageNumber)
        {
            await _pauseGate.WaitAsync(pendingCancellation.Token).ConfigureAwait(false);
            return await client.GetContractsPageAsync(
                partition.StartDate,
                partition.EndDate,
                partition.ModalityId,
                partition.Uf,
                pageNumber,
                50,
                mode,
                pendingCancellation.Token).ConfigureAwait(false);
        }

        var expectedPage = firstPage + 1;
        try
        {
            // Bound both outstanding requests and downloaded pages awaiting a
            // slower predecessor. Checkpoints advance only in request order.
            while (nextPage <= first.TotalPages && pendingPages.Count < pageConcurrency)
                pendingPages.Enqueue(LoadPageAsync(nextPage++));

            while (pendingPages.TryDequeue(out var pendingPage))
            {
                var orderedPage = await pendingPage.ConfigureAwait(false);
                var complete = orderedPage.Contracts.Count == 0 ||
                               expectedPage >= first.TotalPages;
                await repository.CommitSyncPageAsync(
                    orderedPage.Contracts,
                    partition.CreateCheckpoint(
                        mode,
                        complete ? 0 : expectedPage + 1,
                        complete ? SyncPartitionStatus.Complete : SyncPartitionStatus.Partial,
                        first.TotalPages),
                    cancellationToken).ConfigureAwait(false);
                savedInPartition += orderedPage.Contracts.Count;
                reportProgress(orderedPage.Contracts.Count, $"{partition.Description} — página {expectedPage}");
                expectedPage++;
                if (complete) break;
                if (nextPage <= first.TotalPages)
                    pendingPages.Enqueue(LoadPageAsync(nextPage++));
            }
        }
        finally
        {
            await pendingCancellation.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(pendingPages).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the original failure after draining pending calls. */ }
        }
        return savedInPartition;
    }

    private static bool IsPncpDateRangeRejection(HttpRequestException exception) =>
        (int?)exception.StatusCode == 422 &&
        PncpClient.IsDateRangeRejection(exception.Message);

    private static IReadOnlyList<SyncPartition> BuildPartitions(
        DateOnly startDate,
        DateOnly endDate,
        GeoScope scope,
        IReadOnlyList<Modality> modalities,
        SyncMode mode)
    {
        var partitions = new List<SyncPartition>();
        for (var current = startDate; current <= endDate;)
        {
            // Calendar-aligned weeks keep most checkpoint keys stable if a
            // year-long download is resumed on a later day.
            var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)current.DayOfWeek + 7) % 7;
            var partitionEnd = current.AddDays(daysUntilSunday);
            if (partitionEnd > endDate)
            {
                partitionEnd = endDate;
            }

            foreach (var uf in scope.ApiUfFilters)
            {
                foreach (var modality in modalities)
                {
                    var key = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{mode}:{current:yyyyMMdd}:{partitionEnd:yyyyMMdd}:m{modality.Id}:uf{uf ?? "ALL"}");
                    partitions.Add(new SyncPartition(
                        key,
                        current,
                        partitionEnd,
                        modality.Id,
                        modality.Name,
                        uf,
                        $"{modality.Name}, {uf ?? "Brasil"}, {current:dd/MM/yyyy}–{partitionEnd:dd/MM/yyyy}"));
                }
            }

            current = partitionEnd.AddDays(1);
        }

        return partitions;
    }

    private sealed record SyncPartition(
        string Key,
        DateOnly StartDate,
        DateOnly EndDate,
        long ModalityId,
        string ModalityName,
        string? Uf,
        string Description)
    {
        public SyncPartitionCheckpoint CreateCheckpoint(
            SyncMode mode,
            int nextPage,
            SyncPartitionStatus status,
            long? totalPages = null,
            string? lastError = null,
            DateTimeOffset? nextRetryAt = null) => new()
        {
            PartitionKey = Key,
            Mode = mode,
            StartDate = StartDate,
            EndDate = EndDate,
            ModalityId = ModalityId,
            Uf = Uf ?? "ALL",
            NextPage = nextPage,
            TotalPages = totalPages,
            Status = status,
            LastError = lastError,
            NextRetryAt = nextRetryAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        public SyncPartition ForSingleDay(DateOnly day, SyncMode mode)
        {
            var key = string.Create(
                CultureInfo.InvariantCulture,
                $"{mode}:{day:yyyyMMdd}:{day:yyyyMMdd}:m{ModalityId}:uf{Uf ?? "ALL"}");
            return this with
            {
                Key = Key.Contains(":cycle:", StringComparison.Ordinal)
                    ? key + Key[Key.IndexOf(":cycle:", StringComparison.Ordinal)..] : key,
                StartDate = day,
                EndDate = day,
                Description = $"{ModalityName}, {Uf ?? "Brasil"}, {day:dd/MM/yyyy}"
            };
        }
    }
}
