using Microsoft.Data.Sqlite;
using System.Net;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class PriceCacheTests
{
    [Fact]
    public async Task Migration14ToCurrent_CreatesControlStatisticsAndRecognizesExistingSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("existing", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [Item(contract, 1)], false);
        await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [Result(contract, 1, 1, true)]);

        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TRIGGER contracts_invalidate_price_cache;
                DROP TABLE price_cache_contracts;
                DROP TABLE price_cache_control;
                UPDATE schema_info SET version = 14 WHERE id = 1;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        await database.Repository.InitializeAsync();
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var policy = await cache.GetPolicyAsync();
        await cache.SetAuthorizationAsync(true, today.AddDays(-89), today);
        await cache.PrepareWindowAsync(today.AddDays(-89), today);
        var progress = await cache.GetProgressAsync();

        Assert.False(policy.Authorized);
        Assert.Equal(1, progress.TotalContracts);
        Assert.Equal(1, progress.CompletedContracts);
        Assert.Equal(16, SqliteContractRepository.CurrentSchemaVersion);
        Assert.Equal((1L, 1L, 1L), await database.Repository.GetCountsAsync());
    }

    [Fact]
    public async Task Window_IsInclusiveNewestFirstAndExcludesTheNinetiethPreviousDay()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        await database.Repository.UpsertContractsAsync([
            RecentContract("newest", today, 1),
            RecentContract("edge", today.AddDays(-89), 2),
            RecentContract("outside", today.AddDays(-90), 3)
        ]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-89), today);
        await cache.PrepareWindowAsync(today.AddDays(-89), today);

        var progress = await cache.GetProgressAsync();
        var next = await cache.GetNextWorkAsync(DateTimeOffset.UtcNow);

        Assert.Equal(2, progress.TotalContracts);
        Assert.NotNull(next);
        Assert.Equal("newest", next.Contract.PncpId);
    }

    [Fact]
    public async Task LocalSearch_ReturnsOnlyCurrentActivePricesAndHonorsFilters()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("coffee", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-89), today);
        await cache.PrepareWindowAsync(today.AddDays(-89), today);
        await cache.MarkContractDownloadingAsync(contract.PncpId, true);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [Item(contract, 1)], false);
        await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [
            Result(contract, 1, 1, true) with { HomologatedUnitValueScaled = DecimalScale.ToScaled(25m) },
            Result(contract, 1, 2, false) with { HomologatedUnitValueScaled = DecimalScale.ToScaled(10m) }
        ]);
        await cache.MarkContractCompleteAsync(contract.PncpId, contract.GlobalUpdatedAt);

        var query = new SearchQuery(
            "cafe",
            SearchGeoFilter.State("SP"),
            today.AddDays(-30),
            today,
            SearchSort.Newest);
        var found = await cache.SearchLocalAsync(
            query,
            SearchText.Parse("cafe"),
            20m,
            30m,
            1,
            50);
        var excluded = await cache.SearchLocalAsync(
            query with { Text = "cafe -torrado" },
            SearchText.Parse("cafe -torrado"),
            null,
            null,
            1,
            50);

        Assert.Single(found.Hits);
        Assert.Equal("coffee", found.Hits[0].Contract.PncpId);
        Assert.Empty(excluded.Hits);
    }

    [Fact]
    public async Task SelectiveRemoval_DeletesBackgroundDataButPreservesPinnedContract()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var removable = RecentContract("removable", today, 1);
        var pinned = RecentContract("pinned", today, 2);
        await database.Repository.UpsertContractsAsync([removable, pinned]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-89), today);
        await cache.PrepareWindowAsync(today.AddDays(-89), today);
        foreach (var contract in new[] { removable, pinned })
        {
            await cache.MarkContractDownloadingAsync(contract.PncpId, true);
            await database.Repository.UpsertItemsAsync(contract.PncpId, [Item(contract, 1)], false);
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [Result(contract, 1, 1, true)]);
            await cache.MarkContractCompleteAsync(contract.PncpId, contract.GlobalUpdatedAt);
        }
        await cache.MarkContractPinnedAsync(pinned.PncpId);

        await cache.RemoveBackgroundCacheAsync();

        Assert.Null(await database.Repository.GetItemAsync(removable.PncpId, 1));
        Assert.NotNull(await database.Repository.GetItemAsync(pinned.PncpId, 1));
        Assert.False((await cache.GetPolicyAsync()).Enabled);
    }

    [Fact]
    public async Task Synchronization_IsolatesFailureAndResumesOnlyThePendingContract()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var failing = RecentContract("failing", today, 1);
        var healthy = RecentContract("healthy", today.AddDays(-1), 2);
        await database.Repository.UpsertContractsAsync([failing, healthy]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-89), today);
        var client = new CacheClient(failing.PncpId);
        var service = new PriceCacheService(
            client,
            database.Repository,
            new CompleteCoverageRepository(),
            cache);

        await service.SynchronizeAsync();
        var afterFailure = await cache.GetProgressAsync();

        Assert.Equal(1, afterFailure.CompletedContracts);
        Assert.Equal(1, afterFailure.FailedContracts);
        Assert.NotNull(await database.Repository.GetItemAsync(healthy.PncpId, 1));
        Assert.Null(await database.Repository.GetItemAsync(failing.PncpId, 1));

        await cache.MarkContractPendingAsync(failing.PncpId);
        await service.SynchronizeAsync();
        var completed = await cache.GetProgressAsync();

        Assert.Equal(2, completed.CompletedContracts);
        Assert.Equal(0, completed.FailedContracts);
        Assert.NotNull(await database.Repository.GetItemAsync(failing.PncpId, 1));
        Assert.Equal(3, client.ItemListCalls);
        Assert.Equal(2, client.ResultCalls);
    }

    [Fact]
    public async Task PrepareWindow_RecoversContractInterruptedWhileDownloading()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("interrupted", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-89), today);
        await cache.PrepareWindowAsync(today.AddDays(-89), today);
        await cache.MarkContractDownloadingAsync(contract.PncpId, true);

        await cache.PrepareWindowAsync(today.AddDays(-89), today);
        var recovered = await cache.GetNextWorkAsync(DateTimeOffset.UtcNow);

        Assert.NotNull(recovered);
        Assert.Equal(contract.PncpId, recovered.Contract.PncpId);
        Assert.Equal(PriceCacheContractStatus.Pending, recovered.Checkpoint.Status);
    }

    [Fact]
    public async Task Synchronization_TimesOutUnresponsiveContractAndContinues()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var hanging = RecentContract("hanging", today, 1);
        var healthy = RecentContract("healthy-after-timeout", today.AddDays(-1), 2);
        await database.Repository.UpsertContractsAsync([hanging, healthy]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-89), today);
        var service = new PriceCacheService(
            new TimeoutCacheClient(hanging.PncpId),
            database.Repository,
            new CompleteCoverageRepository(),
            cache,
            TimeSpan.FromMilliseconds(25));

        await service.SynchronizeAsync();
        var progress = await cache.GetProgressAsync();

        Assert.Equal(1, progress.CompletedContracts);
        Assert.Equal(1, progress.FailedContracts);
        Assert.NotNull(await database.Repository.GetItemAsync(healthy.PncpId, 1));
        Assert.Null(await database.Repository.GetItemAsync(hanging.PncpId, 1));
    }

    [Fact]
    public async Task Synchronization_TreatsMissingItemListAsUnavailableWithoutRetryLoop()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("missing-list", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-89), today);
        var client = new NotFoundCacheClient(failItemList: true);
        var service = new PriceCacheService(
            client,
            database.Repository,
            new CompleteCoverageRepository(),
            cache);

        await service.SynchronizeAsync();
        await service.SynchronizeAsync();
        var progress = await cache.GetProgressAsync();

        Assert.Equal(1, progress.CompletedContracts);
        Assert.Equal(0, progress.FailedContracts);
        Assert.Equal(1, client.ItemListCalls);
    }

    [Fact]
    public async Task Synchronization_TreatsMissingResultEndpointAsEmptyResult()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("missing-results", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-89), today);
        var service = new PriceCacheService(
            new NotFoundCacheClient(failItemList: false),
            database.Repository,
            new CompleteCoverageRepository(),
            cache);

        await service.SynchronizeAsync();
        var progress = await cache.GetProgressAsync();
        var item = await database.Repository.GetItemAsync(contract.PncpId, 1);

        Assert.Equal(1, progress.CompletedContracts);
        Assert.Equal(0, progress.FailedContracts);
        Assert.NotNull(item);
        Assert.Equal(ItemHydrationStatus.Complete, item.HydrationStatus);
    }

    internal static ContractRecord RecentContract(string id, DateOnly date, int sequence) =>
        RepositorySearchTests.Contract(id, $"Compra de café {id}", "SP", sequence) with
        {
            PublicationDate = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            GlobalUpdatedAt = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)
        };

    internal static ProcurementItem Item(ContractRecord contract, long number) => new()
    {
        ContractId = contract.PncpId,
        ItemNumber = number,
        Description = "Café torrado em grãos",
        Unit = "KG",
        HasResult = true,
        HydrationStatus = ItemHydrationStatus.NotLoaded
    };

    internal static HomologationResult Result(
        ContractRecord contract,
        long itemNumber,
        long sequence,
        bool active) => new()
    {
        ContractId = contract.PncpId,
        ItemNumber = itemNumber,
        ResultSequence = sequence,
        SupplierName = $"Fornecedor {sequence}",
        HomologatedUnitValueScaled = DecimalScale.ToScaled(25m),
        ResultStatusId = active ? 1 : 2,
        ResultStatusName = active ? "Ativo" : "Cancelado"
    };

    private sealed class CacheClient(string failOnceContractId) : IPncpClient
    {
        private bool _failed;

        public int ItemListCalls { get; private set; }
        public int ResultCalls { get; private set; }

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
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default)
        {
            ItemListCalls++;
            if (!_failed && contract.PncpId == failOnceContractId)
            {
                _failed = true;
                throw new HttpRequestException("429 simulado");
            }

            return Task.FromResult<IReadOnlyList<ProcurementItem>>([Item(contract, 1)]);
        }

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default)
        {
            ResultCalls++;
            return Task.FromResult<IReadOnlyList<HomologationResult>>([
                Result(contract, itemNumber, 1, true)
            ]);
        }
    }

    private sealed class TimeoutCacheClient(string hangingContractId) : IPncpClient
    {
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
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => Task.FromResult(1);

        public async Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default)
        {
            if (contract.PncpId == hangingContractId)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return [Item(contract, 1)];
        }

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HomologationResult>>([
                Result(contract, itemNumber, 1, true)
            ]);
    }

    private sealed class NotFoundCacheClient(bool failItemList) : IPncpClient
    {
        public int ItemListCalls { get; private set; }

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
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default)
        {
            ItemListCalls++;
            return failItemList
                ? Task.FromException<IReadOnlyList<ProcurementItem>>(
                    new HttpRequestException("404 simulado", null, HttpStatusCode.NotFound))
                : Task.FromResult<IReadOnlyList<ProcurementItem>>([Item(contract, 1)]);
        }

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<HomologationResult>>(
                new HttpRequestException("404 simulado", null, HttpStatusCode.NotFound));
    }

    private sealed class CompleteCoverageRepository : ICoverageRepository
    {
        public Task EnsureCoverageWindowAsync(
            DateOnly startDate,
            DateOnly endDate,
            IReadOnlyList<long> activeModalityIds,
            string uf = "ALL",
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetCoverageStatusAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string uf,
            CoverageStatus status,
            long? recordsCount = null,
            string? error = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CoverageDay>> GetCoverageDaysAsync(
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoverageDay>>([]);

        public Task<IReadOnlyList<CoverageWorkItem>> GetIncompleteCoverageAsync(
            DateOnly startDate,
            DateOnly endDate,
            int limit,
            bool newestFirst,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoverageWorkItem>>([]);

        public Task<bool> IsCoverageCompleteAsync(
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
