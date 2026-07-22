using System.Globalization;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class SyncService(IPncpClient client, IContractRepository repository)
{
    public static TimeSpan AutomaticRetryDelay { get; } = TimeSpan.FromMinutes(10);

    private readonly AsyncPauseGate _pauseGate = new();

    public bool IsPaused => _pauseGate.IsPaused;

    public void Pause() => _pauseGate.Pause();

    public void Resume() => _pauseGate.Resume();

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
        ArgumentNullException.ThrowIfNull(options);
        if (queryStartDate > endDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queryStartDate),
                $"A data inicial {queryStartDate:dd/MM/yyyy} não pode ser posterior à data final {endDate:dd/MM/yyyy}.");
        }

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
            var completedPartitions = 0;

            foreach (var partition in partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _pauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);

                var savedPage = await repository.GetPartitionNextPageAsync(partition.Key, cancellationToken).ConfigureAwait(false);
                if (savedPage == 0)
                {
                    await SetCoverageStatusAsync(
                        coverageRepository,
                        partition,
                        CoverageStatus.Complete,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    completedPartitions++;
                    progress?.Report(new SyncProgress(
                        contractsSaved,
                        completedPartitions,
                        partitions.Count,
                        $"Partição já concluída: {partition.Description}"));
                    continue;
                }

                contractsSaved += await DownloadPartitionWithCoverageAsync(
                    partition,
                    savedPage ?? 1,
                    mode,
                    coverageRepository,
                    contractsSaved,
                    completedPartitions,
                    partitions.Count,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                completedPartitions++;
                progress?.Report(new SyncProgress(
                    contractsSaved,
                    completedPartitions,
                    partitions.Count,
                    $"Concluída: {partition.Description}"));
            }

            if (options.FinalizeDataset)
            {
                var rollingStart = endDate.AddDays(-364);
                await repository.PruneContractsBeforeAsync(rollingStart, cancellationToken).ConfigureAwait(false);
                await repository.SetDatasetStateAsync(
                    rollingStart,
                    endDate,
                    scope,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }

            await repository.CompleteSyncRunAsync(runId, true, contractsSaved, null, cancellationToken).ConfigureAwait(false);
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
        long contractsPreviouslySaved,
        int completedPartitions,
        int totalPartitions,
        IProgress<SyncProgress>? progress,
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
                contractsPreviouslySaved,
                completedPartitions,
                totalPartitions,
                progress,
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
            progress?.Report(new SyncProgress(
                contractsPreviouslySaved,
                completedPartitions,
                totalPartitions,
                $"O PNCP rejeitou a semana {partition.StartDate:dd/MM/yyyy}–{partition.EndDate:dd/MM/yyyy}; tentando dia a dia"));

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
                    contractsPreviouslySaved + saved,
                    completedPartitions,
                    totalPartitions,
                    progress,
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
        long contractsPreviouslySaved,
        int completedPartitions,
        int totalPartitions,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        long savedInPartition = 0;
        for (var pageNumber = firstPage; ; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _pauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new SyncProgress(
                contractsPreviouslySaved + savedInPartition,
                completedPartitions,
                totalPartitions,
                $"{partition.Description} — página {pageNumber}"));

            var page = await client.GetContractsPageAsync(
                partition.StartDate,
                partition.EndDate,
                partition.ModalityId,
                partition.Uf,
                pageNumber,
                50,
                mode,
                cancellationToken).ConfigureAwait(false);
            await repository.UpsertContractsAsync(page.Contracts, cancellationToken).ConfigureAwait(false);
            savedInPartition += page.Contracts.Count;

            var complete = page.Contracts.Count == 0 || page.TotalPages == 0 || pageNumber >= page.TotalPages;
            await repository.SavePartitionCheckpointAsync(
                partition.CreateCheckpoint(
                    mode,
                    complete ? 0 : pageNumber + 1,
                    complete ? SyncPartitionStatus.Complete : SyncPartitionStatus.Partial,
                    page.TotalPages),
                cancellationToken).ConfigureAwait(false);
            if (complete)
            {
                return savedInPartition;
            }
        }
    }

    private static bool IsPncpDateRangeRejection(HttpRequestException exception) =>
        (int?)exception.StatusCode == 422 &&
        exception.Message.Contains("Data Inicial", StringComparison.OrdinalIgnoreCase) &&
        exception.Message.Contains("Data Final", StringComparison.OrdinalIgnoreCase);

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
                Key = key,
                StartDate = day,
                EndDate = day,
                Description = $"{ModalityName}, {Uf ?? "Brasil"}, {day:dd/MM/yyyy}"
            };
        }
    }
}
