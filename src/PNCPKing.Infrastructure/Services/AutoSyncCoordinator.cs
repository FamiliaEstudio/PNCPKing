using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

/// <summary>
/// Keeps the authorized national dataset complete for the rolling 365-day
/// window. The coordinator never performs the initial user authorization; it
/// is intended to run only after the application has recorded that decision.
/// </summary>
public sealed class AutoSyncCoordinator
{
    public const int WindowDays = 365;
    public const int WorkItemReadLimit = 512;

    private readonly IPncpClient _client;
    private readonly IContractRepository _repository;
    private readonly ICoverageRepository _coverageRepository;
    private readonly SyncService _syncService;
    private readonly TimeProvider _timeProvider;

    public AutoSyncCoordinator(
        IPncpClient client,
        IContractRepository repository,
        SyncService syncService,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _repository = repository;
        _coverageRepository = repository as ICoverageRepository ??
            throw new ArgumentException(
                "O repositório precisa implementar ICoverageRepository para a manutenção automática.",
                nameof(repository));
        _syncService = syncService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AutoSyncResult> SynchronizeAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var endDate = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        var startDate = endDate.AddDays(-(WindowDays - 1));
        var modalities = (await _client.GetModalitiesAsync(cancellationToken).ConfigureAwait(false))
            .Where(modality => modality.Active)
            .DistinctBy(modality => modality.Id)
            .ToArray();
        if (modalities.Length == 0)
        {
            throw new InvalidOperationException("O PNCP não informou nenhuma modalidade ativa.");
        }

        var activeModalityIds = modalities.Select(modality => modality.Id).ToArray();
        await _coverageRepository.EnsureCoverageWindowAsync(
            startDate,
            endDate,
            activeModalityIds,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var coverageBatches = 0;
        string? previousBatchKey = null;
        while (!await _coverageRepository.IsCoverageCompleteAsync(
                   startDate,
                   endDate,
                   cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workItems = await _coverageRepository.GetIncompleteCoverageAsync(
                startDate,
                endDate,
                WorkItemReadLimit,
                newestFirst: true,
                cancellationToken).ConfigureAwait(false);
            if (workItems.Count == 0)
            {
                throw new InvalidOperationException(
                    "A cobertura está incompleta, mas o banco não informou nenhuma lacuna para retomar.");
            }

            var newestDate = workItems.Max(item => item.Date);
            var group = workItems
                .Where(item => item.Date == newestDate)
                .GroupBy(item => NormalizeUf(item.Uf), StringComparer.Ordinal)
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .First();
            var modalityIds = group
                .Select(item => item.ModalityId)
                .Distinct()
                .Order()
                .ToHashSet();
            var unknownIds = modalityIds.Except(activeModalityIds).ToArray();
            if (unknownIds.Length > 0)
            {
                throw new InvalidOperationException(
                    $"A cobertura contém modalidade(s) que não estão mais ativas: {string.Join(", ", unknownIds)}.");
            }

            var batchKey = $"{newestDate:yyyyMMdd}:{group.Key}:{string.Join(',', modalityIds)}";
            if (string.Equals(batchKey, previousBatchKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A mesma lacuna permaneceu pendente após uma sincronização concluída; a manutenção foi interrompida para evitar repetição infinita.");
            }

            previousBatchKey = batchKey;
            progress?.Report(new SyncProgress(
                0,
                coverageBatches,
                0,
                $"Preenchendo {newestDate:dd/MM/yyyy} — {modalityIds.Count:N0} modalidade(s)"));
            var scope = group.Key == "ALL" ? GeoScope.All : GeoScope.State(group.Key);
            await _syncService.SynchronizeAsync(
                newestDate,
                newestDate,
                scope,
                SyncMode.Publication,
                new SyncExecutionOptions
                {
                    KnownModalities = modalities,
                    ModalityIds = modalityIds,
                    FinalizeDataset = false
                },
                progress,
                cancellationToken).ConfigureAwait(false);
            coverageBatches++;
        }

        progress?.Report(new SyncProgress(
            0,
            coverageBatches,
            coverageBatches,
            "Cobertura dos 365 dias completa; atualizando as últimas 48 horas"));

        // Date-only PNCP endpoints are inclusive. Starting two dates before
        // today conservatively covers every instant in the preceding 48 hours.
        var globalUpdateStart = endDate.AddDays(-2);
        await _syncService.SynchronizeAsync(
            globalUpdateStart,
            endDate,
            GeoScope.All,
            SyncMode.GlobalUpdate,
            new SyncExecutionOptions
            {
                KnownModalities = modalities,
                FinalizeDataset = false
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        // Pruning and the dataset completion marker are deliberately last. A
        // failure in either the gap fill or global overlap preserves old data
        // and leaves the previous successful dataset state untouched.
        await _repository.PruneContractsBeforeAsync(startDate, cancellationToken).ConfigureAwait(false);
        await _repository.SetDatasetStateAsync(
            startDate,
            endDate,
            GeoScope.All,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        return new AutoSyncResult(startDate, endDate, coverageBatches, GlobalUpdateCompleted: true);
    }

    private static string NormalizeUf(string? uf) =>
        string.IsNullOrWhiteSpace(uf) || string.Equals(uf, "ALL", StringComparison.OrdinalIgnoreCase)
            ? "ALL"
            : uf.Trim().ToUpperInvariant();
}
