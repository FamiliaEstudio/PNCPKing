using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class CatalogSyncService(
    IComprasCatalogClient client,
    ICatalogRepository repository,
    TimeProvider? timeProvider = null)
{
    private static readonly CatalogKind[] AllKinds = [CatalogKind.Catmat, CatalogKind.Catser];
    public static TimeSpan RefreshInterval { get; } = TimeSpan.FromHours(24);
    private readonly AsyncPauseGate _pause = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public bool IsPaused => _pause.IsPaused;
    public void Pause() => _pause.Pause();
    public void Resume() => _pause.Resume();

    public async Task<bool> IsDueAsync(CancellationToken cancellationToken = default)
        => (await GetDueKindsAsync(RefreshInterval, cancellationToken).ConfigureAwait(false)).Count > 0;

    public async Task<IReadOnlyList<CatalogKind>> GetDueKindsAsync(
        TimeSpan? refreshInterval,
        CancellationToken cancellationToken = default)
    {
        if (refreshInterval is null)
        {
            return [];
        }

        if (refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refreshInterval),
                "O intervalo automático deve ser positivo ou nulo para o modo manual.");
        }

        var states = await repository.GetSyncStatesAsync(cancellationToken).ConfigureAwait(false);
        var statesByKind = states.ToDictionary(state => state.Kind);
        var now = _timeProvider.GetUtcNow();
        return AllKinds.Where(kind =>
        {
            if (!statesByKind.TryGetValue(kind, out var state))
            {
                return true;
            }

            return state.Status != CatalogSyncStatus.Complete ||
                   state.CompletedAt is null ||
                   now - state.CompletedAt.Value >= refreshInterval.Value;
        }).ToArray();
    }

    public async Task BuildDescriptionIndexAsync(
        IProgress<CatalogDescriptionIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var state = await repository.GetDescriptionIndexProgressAsync(cancellationToken).ConfigureAwait(false);
        while (!state.Completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _pause.WaitAsync(cancellationToken).ConfigureAwait(false);
            state = await repository.BuildDescriptionIndexBatchAsync(2000, cancellationToken).ConfigureAwait(false);
            progress?.Report(state);
        }
    }

    public Task SynchronizeAsync(
        IProgress<CatalogSyncProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        SynchronizeAsync(AllKinds, progress, cancellationToken);

    public async Task SynchronizeAsync(
        IReadOnlyCollection<CatalogKind> kinds,
        IProgress<CatalogSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        foreach (var kind in AllKinds)
        {
            if (!kinds.Contains(kind))
            {
                continue;
            }

            await SynchronizeKindAsync(kind, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SynchronizeKindAsync(
        CatalogKind kind,
        IProgress<CatalogSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var state = await repository.GetSyncStateAsync(kind, cancellationToken).ConfigureAwait(false);
        var canResume = (state.Status is CatalogSyncStatus.Downloading or CatalogSyncStatus.Failed or CatalogSyncStatus.Paused) &&
                        state.Generation.Length > 0 && state.NextPage > 1;
        var generation = canResume ? state.Generation : Guid.NewGuid().ToString("N");
        if (!canResume)
        {
            await repository.BeginSyncAsync(kind, generation, cancellationToken).ConfigureAwait(false);
            state = await repository.GetSyncStateAsync(kind, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var pageNumber = Math.Max(1, state.NextPage);
            var expectedPages = state.TotalPages;
            var expectedRecords = state.TotalRecords;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _pause.WaitAsync(cancellationToken).ConfigureAwait(false);
                var page = await client.GetPageAsync(kind, pageNumber, 500, cancellationToken).ConfigureAwait(false);
                if (expectedPages > 0 &&
                    (page.TotalPages != expectedPages || page.TotalRecords != expectedRecords))
                {
                    await repository.BeginSyncAsync(
                        kind,
                        Guid.NewGuid().ToString("N"),
                        cancellationToken).ConfigureAwait(false);
                    throw new InvalidDataException(
                        "O total do catálogo mudou durante a retomada. A próxima execução reiniciará um snapshot completo.");
                }

                expectedPages = page.TotalPages;
                expectedRecords = page.TotalRecords;
                await repository.StagePageAsync(page, generation, cancellationToken).ConfigureAwait(false);
                progress?.Report(new CatalogSyncProgress(
                    kind,
                    pageNumber,
                    page.TotalPages,
                    Math.Min(page.TotalRecords, (long)pageNumber * 500),
                    $"{Label(kind)}: página {pageNumber:N0} de {page.TotalPages:N0}"));
                pageNumber++;
            } while (pageNumber <= expectedPages);

            await repository.PublishAsync(kind, generation, cancellationToken).ConfigureAwait(false);
            progress?.Report(new CatalogSyncProgress(
                kind,
                expectedPages,
                expectedPages,
                expectedRecords,
                $"{Label(kind)}: {expectedRecords:N0} código(s) ativo(s) publicados"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await repository.MarkFailedAsync(kind, exception.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string Label(CatalogKind kind) => kind == CatalogKind.Catmat ? "CATMAT" : "CATSER";
}
