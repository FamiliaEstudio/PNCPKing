using System.Collections.Concurrent;
using System.Net;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class NationalPriceIndexTests
{
    [Fact]
    public async Task Synchronization_StoresEveryUsefulWinnerAndSkipsIneligibleOrEmptyItems()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = PriceCacheTests.RecentContract("national-prices", today, 1);
        var items = new[]
        {
            PriceCacheTests.Item(contract, 1),
            PriceCacheTests.Item(contract, 2),
            PriceCacheTests.Item(contract, 3) with { HasResult = false }
        };
        var cache = await PrepareItemAndPriceIndexesAsync(database, [contract], items);
        var client = new ResultClient((_, itemNumber) => itemNumber switch
        {
            1 =>
            [
                PriceCacheTests.Result(contract, 1, 1, true),
                PriceCacheTests.Result(contract, 1, 2, true) with
                {
                    HomologatedUnitValueScaled = DecimalScale.ToScaled(25m)
                },
                PriceCacheTests.Result(contract, 1, 3, true) with
                {
                    HomologatedUnitValueScaled = 0
                },
                PriceCacheTests.Result(contract, 1, 4, false)
            ],
            _ => []
        });
        var service = new NationalPriceIndexService(client, database.Repository, cache);

        await service.SynchronizeAggressivelyAsync(4);
        var firstCalls = client.ResultCalls;
        await service.SynchronizeAggressivelyAsync(4);

        var first = await database.Repository.GetCachedItemResultsAsync(contract.PncpId, 1);
        var second = await database.Repository.GetCachedItemResultsAsync(contract.PncpId, 2);
        var third = await database.Repository.GetItemAsync(contract.PncpId, 3);
        var progress = await cache.GetNationalPriceIndexProgressAsync();
        var local = await cache.SearchLocalAsync(
            new SearchQuery("cafe", SearchGeoFilter.All, today.AddDays(-30), today, SearchSort.Newest),
            SearchText.Parse("cafe"),
            null,
            null,
            1,
            20);

        Assert.NotNull(first);
        Assert.Equal(2, first.Results.Count);
        Assert.All(first.Results, result =>
        {
            Assert.Equal(1, result.ResultStatusId);
            Assert.True(result.HomologatedUnitValueScaled > 0);
        });
        Assert.NotNull(second);
        Assert.Empty(second.Results);
        Assert.Equal(ItemHydrationStatus.NotLoaded, third!.HydrationStatus);
        Assert.Equal(2, firstCalls);
        Assert.Equal(firstCalls, client.ResultCalls);
        Assert.Equal([1L, 2L], client.CallsByItem.Keys.Order().ToArray());
        Assert.Equal(2, progress.EligibleItems);
        Assert.Equal(2, progress.CompletedItems);
        Assert.Equal(1, progress.PricedItems);
        Assert.Equal(2, progress.ResultRows);
        Assert.Equal(1, progress.NoPriceItems);
        Assert.Equal(PriceCacheStatus.Complete, progress.Status);
        Assert.Equal(2, local.Rows!.Count);
    }

    [Fact]
    public async Task Synchronization_TreatsNotFoundAsCompletedAndDoesNotRetry()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = PriceCacheTests.RecentContract("national-404", today, 1);
        var cache = await PrepareItemAndPriceIndexesAsync(
            database,
            [contract],
            [PriceCacheTests.Item(contract, 1)]);
        var client = new ResultClient((_, _) => throw new HttpRequestException(
            "404 simulado",
            null,
            HttpStatusCode.NotFound));
        var service = new NationalPriceIndexService(client, database.Repository, cache);

        await service.SynchronizeAggressivelyAsync(1);
        await service.SynchronizeAggressivelyAsync(1);

        var item = await database.Repository.GetItemAsync(contract.PncpId, 1);
        var progress = await cache.GetNationalPriceIndexProgressAsync();
        Assert.Equal(1, client.ResultCalls);
        Assert.Equal(ItemHydrationStatus.Complete, item!.HydrationStatus);
        Assert.Contains("404", item.LastError, StringComparison.Ordinal);
        Assert.Equal(1, progress.CompletedItems);
        Assert.Equal(1, progress.NoPriceItems);
        Assert.Equal(0, progress.FailedContracts);
    }

    [Fact]
    public async Task AggressiveSynchronization_UsesDistinctContractsInParallelWithoutDuplicateCalls()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contracts = Enumerable.Range(1, 8)
            .Select(sequence => PriceCacheTests.RecentContract($"price-parallel-{sequence}", today, sequence))
            .ToArray();
        var items = contracts.Select(contract => PriceCacheTests.Item(contract, 1)).ToArray();
        var cache = await PrepareItemAndPriceIndexesAsync(database, contracts, items);
        var client = new ResultClient(async (contract, itemNumber, cancellationToken) =>
        {
            await Task.Delay(75, cancellationToken);
            return [PriceCacheTests.Result(contract, itemNumber, 1, true)];
        });
        var service = new NationalPriceIndexService(client, database.Repository, cache);

        await service.SynchronizeAggressivelyAsync(4);

        Assert.Equal(8, client.ResultCalls);
        Assert.True(client.MaximumConcurrentCalls >= 2);
        Assert.All(client.CallsByContract.Values, count => Assert.Equal(1, count));
        var progress = await cache.GetNationalPriceIndexProgressAsync();
        Assert.Equal(8, progress.CompletedItems);
        Assert.Equal(8, progress.PricedItems);
        Assert.Equal(8, progress.ResultRows);
    }

    [Fact]
    public async Task WorkSelection_UsesPriceQueueIndexAndKeepsPendingAheadOfRetry()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var pending = PriceCacheTests.RecentContract("price-pending", today.AddDays(-1), 1);
        var failed = PriceCacheTests.RecentContract("price-failed", today, 2);
        var cache = await PrepareItemAndPriceIndexesAsync(
            database,
            [pending, failed],
            [PriceCacheTests.Item(pending, 1), PriceCacheTests.Item(failed, 1)]);
        await cache.MarkNationalPriceContractFailedAsync(
            failed.PncpId,
            "Falha retomável simulada.",
            DateTimeOffset.UtcNow.AddMinutes(-1));

        var next = await cache.GetNextNationalPriceWorkAsync(DateTimeOffset.UtcNow);
        var plan = await ReadWorkSelectionPlanAsync(database.Repository.DatabasePath);

        Assert.NotNull(next);
        Assert.Equal(pending.PncpId, next.Contract.PncpId);
        Assert.Equal(
            2,
            plan.Count(detail => detail.Contains(
                "idx_price_cache_contracts_price_work",
                StringComparison.Ordinal)));
        Assert.DoesNotContain(
            plan,
            detail => detail.Contains(
                "idx_price_cache_contracts_status_publication",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_ReturnsClaimedContractsAndItemsToPending()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contracts = Enumerable.Range(1, 4)
            .Select(sequence => PriceCacheTests.RecentContract($"price-cancel-{sequence}", today, sequence))
            .ToArray();
        var items = contracts.Select(contract => PriceCacheTests.Item(contract, 1)).ToArray();
        var cache = await PrepareItemAndPriceIndexesAsync(database, contracts, items);
        var client = new BlockingResultClient(expectedConcurrentCalls: 3);
        var service = new NationalPriceIndexService(client, database.Repository, cache);
        using var cancellation = new CancellationTokenSource();
        var run = service.SynchronizeAggressivelyAsync(3, cancellationToken: cancellation.Token);

        await client.WaitUntilConcurrentAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var progress = await cache.GetNationalPriceIndexProgressAsync();
        Assert.Equal(0, progress.CompletedItems);
        Assert.Equal(0, progress.FailedContracts);
        Assert.Equal(4, progress.PendingContracts);
        Assert.All(
            await Task.WhenAll(items.Select(item => database.Repository.GetItemAsync(item.ContractId, item.ItemNumber))),
            item => Assert.Equal(ItemHydrationStatus.NotLoaded, item!.HydrationStatus));
    }

    [Fact]
    public async Task GlobalUpdate_InvalidatesOldPricesUntilTheListAndRelevantResultAreRefreshed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = PriceCacheTests.RecentContract("price-invalidated", today, 1);
        var cache = await PrepareItemAndPriceIndexesAsync(
            database,
            [contract],
            [PriceCacheTests.Item(contract, 1)]);
        var generation = 0;
        var client = new ResultClient((current, itemNumber) =>
        [
            PriceCacheTests.Result(current, itemNumber, 1, true) with
            {
                HomologatedUnitValueScaled = DecimalScale.ToScaled(generation == 0 ? 25m : 30m)
            }
        ]);
        var service = new NationalPriceIndexService(client, database.Repository, cache);
        await service.SynchronizeAggressivelyAsync(1);

        generation = 1;
        var updated = contract with { GlobalUpdatedAt = contract.GlobalUpdatedAt?.AddMinutes(1) };
        await database.Repository.UpsertContractsAsync([updated]);
        Assert.False((await database.Repository.GetCachedItemResultsAsync(contract.PncpId, 1))!.IsCurrent);
        await service.SynchronizeAggressivelyAsync(1);
        Assert.Equal(1, client.ResultCalls);

        await database.Repository.UpsertItemsAsync(
            updated.PncpId,
            [PriceCacheTests.Item(updated, 1)],
            false);
        await cache.MarkContractCompleteAsync(updated.PncpId, updated.GlobalUpdatedAt);
        await service.SynchronizeAggressivelyAsync(1);

        var refreshed = await database.Repository.GetCachedItemResultsAsync(updated.PncpId, 1);
        Assert.Equal(2, client.ResultCalls);
        Assert.True(refreshed!.IsCurrent);
        Assert.Equal(DecimalScale.ToScaled(30m), Assert.Single(refreshed.Results).HomologatedUnitValueScaled);
    }

    [Fact]
    public async Task SelectiveRemoval_DeletesBulkPricesButPreservesPinnedOnDemandResultsAndItemLists()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var removable = PriceCacheTests.RecentContract("price-removable", today, 1);
        var pinned = PriceCacheTests.RecentContract("price-pinned", today, 2);
        var cache = await PrepareItemAndPriceIndexesAsync(
            database,
            [removable, pinned],
            [PriceCacheTests.Item(removable, 1), PriceCacheTests.Item(pinned, 1)]);
        var client = new ResultClient((contract, itemNumber) =>
        [
            PriceCacheTests.Result(contract, itemNumber, 1, true)
        ]);
        var service = new NationalPriceIndexService(client, database.Repository, cache);
        await service.SynchronizeAggressivelyAsync(2);
        await database.Repository.ReplaceItemResultsAsync(
            pinned.PncpId,
            1,
            [PriceCacheTests.Result(pinned, 1, 1, true)]);

        await cache.RemoveBackgroundPricesAsync();

        var removableItem = await database.Repository.GetItemAsync(removable.PncpId, 1);
        var pinnedItem = await database.Repository.GetItemAsync(pinned.PncpId, 1);
        var pinnedResults = await database.Repository.GetCachedItemResultsAsync(pinned.PncpId, 1);
        var policy = await cache.GetNationalPriceIndexPolicyAsync();
        Assert.NotNull(removableItem);
        Assert.Equal(ItemHydrationStatus.NotLoaded, removableItem.HydrationStatus);
        Assert.NotNull(pinnedItem);
        Assert.Equal(ItemHydrationStatus.Complete, pinnedItem.HydrationStatus);
        Assert.Single(pinnedResults!.Results);
        Assert.False(policy.Authorized);
        Assert.False(policy.Enabled);
    }

    private static async Task<SqlitePriceCacheRepository> PrepareItemAndPriceIndexesAsync(
        TestDatabase database,
        IReadOnlyList<ContractRecord> contracts,
        IReadOnlyList<ProcurementItem> items)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-364);
        await database.Repository.UpsertContractsAsync(contracts);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, start, today);
        await cache.PrepareWindowAsync(start, today);
        foreach (var contract in contracts)
        {
            await cache.MarkContractDownloadingAsync(contract.PncpId, true);
            await database.Repository.UpsertItemsAsync(
                contract.PncpId,
                items.Where(item => item.ContractId == contract.PncpId).ToArray(),
                false);
            await cache.MarkContractCompleteAsync(contract.PncpId, contract.GlobalUpdatedAt);
        }

        await cache.SetNationalPriceIndexAuthorizationAsync(true, start, today);
        await cache.PrepareNationalPriceIndexAsync(start, today);
        return cache;
    }

    private static async Task<IReadOnlyList<string>> ReadWorkSelectionPlanAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " +
                              SqlitePriceCacheRepository.NationalPriceWorkSelectionSql;
        command.Parameters.AddWithValue("$listComplete", (int)PriceCacheContractStatus.Complete);
        command.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
        command.Parameters.AddWithValue("$failed", (int)PriceCacheContractStatus.Failed);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            details.Add(reader.GetString(3));
        }

        return details;
    }

    private sealed class ResultClient : IPncpClient
    {
        private readonly Func<ContractRecord, long, CancellationToken, Task<IReadOnlyList<HomologationResult>>> _results;
        private int _activeCalls;
        private int _resultCalls;
        private int _maximumConcurrentCalls;

        public ResultClient(Func<ContractRecord, long, IReadOnlyList<HomologationResult>> results)
            : this((contract, itemNumber, _) => Task.FromResult(results(contract, itemNumber)))
        {
        }

        public ResultClient(
            Func<ContractRecord, long, CancellationToken, Task<IReadOnlyList<HomologationResult>>> results)
        {
            _results = results;
        }

        public int ResultCalls => Volatile.Read(ref _resultCalls);
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximumConcurrentCalls);
        public ConcurrentDictionary<string, int> CallsByContract { get; } = new();
        public ConcurrentDictionary<long, int> CallsByItem { get; } = new();

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([]);

        public Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public async Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resultCalls);
            CallsByContract.AddOrUpdate(contract.PncpId, 1, (_, current) => current + 1);
            CallsByItem.AddOrUpdate(itemNumber, 1, (_, current) => current + 1);
            var active = Interlocked.Increment(ref _activeCalls);
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumConcurrentCalls);
                if (active <= maximum ||
                    Interlocked.CompareExchange(ref _maximumConcurrentCalls, active, maximum) == maximum)
                {
                    break;
                }
            }

            try
            {
                return await _results(contract, itemNumber, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }
    }

    private sealed class BlockingResultClient(int expectedConcurrentCalls) : IPncpClient
    {
        private readonly TaskCompletionSource _concurrent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task WaitUntilConcurrentAsync() => _concurrent.Task;

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([]);

        public Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public async Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) >= expectedConcurrentCalls)
            {
                _concurrent.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }
}
