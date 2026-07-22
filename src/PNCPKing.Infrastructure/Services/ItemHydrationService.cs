using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class ItemHydrationService(IPncpClient client, IContractRepository repository)
{
    public Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default) =>
        client.GetItemCountAsync(contract, cancellationToken);

    public async Task<HydrationPreparation> PrepareAsync(
        ContractRecord contract,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        var items = await client.GetItemsAsync(contract, cancellationToken).ConfigureAwait(false);
        await repository.UpsertItemsAsync(contract.PncpId, items, forceRefresh, cancellationToken).ConfigureAwait(false);
        var pending = await repository.GetPendingItemsAsync(contract.PncpId, forceRefresh, cancellationToken).ConfigureAwait(false);
        var requestBatches = (int)Math.Ceiling(pending.Count / 2d);
        return new HydrationPreparation(
            items.Count,
            items.Count(item => item.HasResult),
            pending.Count,
            TimeSpan.FromSeconds(requestBatches * 1.5),
            TimeSpan.FromSeconds(requestBatches * 6));
    }

    public async Task HydrateAsync(
        ContractRecord contract,
        bool forceRefresh,
        IProgress<HydrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new HydrationProgress(0, 0, 0, "Carregando a lista de itens…"));
        await PrepareAsync(contract, forceRefresh, cancellationToken).ConfigureAwait(false);
        await HydratePreparedAsync(contract, forceRefresh, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task HydratePreparedAsync(
        ContractRecord contract,
        bool forceRefresh,
        IProgress<HydrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pending = await repository.GetPendingItemsAsync(contract.PncpId, forceRefresh, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            progress?.Report(new HydrationProgress(0, 0, 0, "Todos os preços disponíveis já estão no cache."));
            return;
        }

        var completed = 0;
        var failed = 0;
        using var semaphore = new SemaphoreSlim(2, 2);
        var tasks = pending.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await repository.SetItemHydrationStatusAsync(
                    contract.PncpId,
                    item.ItemNumber,
                    ItemHydrationStatus.Loading,
                    null,
                    cancellationToken).ConfigureAwait(false);
                var results = await client.GetItemResultsAsync(contract, item.ItemNumber, cancellationToken).ConfigureAwait(false);
                await repository.ReplaceItemResultsAsync(
                    contract.PncpId,
                    item.ItemNumber,
                    results,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await repository.SetItemHydrationStatusAsync(
                    contract.PncpId,
                    item.ItemNumber,
                    ItemHydrationStatus.NotLoaded,
                    "Consulta interrompida; item pendente para retomada.",
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failed);
                await repository.SetItemHydrationStatusAsync(
                    contract.PncpId,
                    item.ItemNumber,
                    ItemHydrationStatus.Failed,
                    exception.Message,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new HydrationProgress(
                    current,
                    pending.Count,
                    Volatile.Read(ref failed),
                    $"Preços consultados: {current} de {pending.Count} itens"));
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
