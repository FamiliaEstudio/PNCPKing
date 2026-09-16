using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class ManualUpdatePauseTests
{
    [Fact]
    public async Task ItemAndPriceStagesPauseCancelResumeAndReuseCompletedEmptyResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today); var start = DataWindow.Start(today);
        var contract = PriceCacheTests.RecentContract("manual-cycle", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.EnsureCoverageWindowAsync(start, today, [6]);
        await database.Repository.SetCoverageStatusAsync(start, today, 6, "ALL", CoverageStatus.Complete);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, start, today);
        var client = new CountingClient();
        var items = new PriceCacheService(client, database.Repository, database.Repository, cache);
        items.Pause();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancel = new CancellationTokenSource())
        {
            var operation = items.SynchronizeAggressivelyAsync(1, new InlineProgress<PriceCacheProgress>(_ => entered.TrySetResult()), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, client.Items);
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        items.Resume(); await items.SynchronizeAggressivelyAsync(1);
        Assert.Equal(1, client.Items);
        await items.SynchronizeAggressivelyAsync(1); Assert.Equal(1, client.Items);
        await cache.SetNationalPriceIndexAuthorizationAsync(true, start, today);
        var prices = new NationalPriceIndexService(client, database.Repository, cache);
        prices.Pause();
        using (var cancel = new CancellationTokenSource())
        {
            var operation = prices.SynchronizeAggressivelyAsync(1, cancellationToken: cancel.Token);
            await Task.Delay(50); Assert.Equal(0, client.Prices);
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        prices.Resume(); await prices.SynchronizeAggressivelyAsync(1); Assert.Equal(1, client.Prices);
        await prices.SynchronizeAggressivelyAsync(1); Assert.Equal(1, client.Prices);
        Assert.Equal(PriceCacheStatus.Complete, (await cache.GetNationalPriceIndexProgressAsync()).Status);
        Assert.Equal(ItemHydrationStatus.Complete, (await database.Repository.GetItemAsync(contract.PncpId, 1))!.HydrationStatus);
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed class CountingClient : IPncpClient
    {
        public int Items, Prices;
        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Modality>>([new(6, "Pregão")]);
        public Task<ContractPage> GetContractsPageAsync(DateOnly startDate, DateOnly endDate, long modalityId, string? uf, int page, int pageSize, SyncMode mode, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Nenhuma consulta de contratações esperada.");
        public Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(ContractRecord contract, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Items); return Task.FromResult<IReadOnlyList<ProcurementItem>>([PriceCacheTests.Item(contract, 1)]); }
        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(ContractRecord contract, long itemNumber, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Prices); return Task.FromResult<IReadOnlyList<HomologationResult>>([]); }
    }
}
