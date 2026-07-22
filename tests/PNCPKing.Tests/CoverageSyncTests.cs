using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class CoverageSyncTests
{
    [Fact]
    public async Task PublicationShowsDownloadingAndCompletesCoverageCell()
    {
        await using var database = await TestDatabase.CreateAsync();
        var coverage = (ICoverageRepository)database.Repository;
        var client = new BlockingEmptyClient();
        var service = new SyncService(client, database.Repository);
        var date = new DateOnly(2026, 7, 20);

        var operation = service.SynchronizeAsync(
            date,
            date,
            GeoScope.All,
            SyncMode.Publication,
            new SyncExecutionOptions { FinalizeDataset = false });

        await client.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var downloading = Assert.Single(await coverage.GetCoverageDaysAsync(date, date));
            Assert.Equal(CoverageStatus.Downloading, downloading.Status);
        }
        finally
        {
            client.ReleaseRequest.TrySetResult(true);
        }

        await operation;
        var complete = Assert.Single(await coverage.GetCoverageDaysAsync(date, date));
        Assert.Equal(CoverageStatus.Complete, complete.Status);
        Assert.Equal(0, complete.RecordsCount);

        var dataset = await database.Repository.GetDatasetStateAsync();
        Assert.Null(dataset.LastSuccessfulSync);
    }

    [Fact]
    public async Task InterruptedPublicationIsPartialAndResumesAtItsCheckpoint()
    {
        await using var database = await TestDatabase.CreateAsync();
        var coverage = (ICoverageRepository)database.Repository;
        var client = new FailsOnceOnSecondPageClient();
        var service = new SyncService(client, database.Repository);
        var date = new DateOnly(2026, 7, 19);
        var options = new SyncExecutionOptions { FinalizeDataset = false };

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SynchronizeAsync(
            date,
            date,
            GeoScope.All,
            SyncMode.Publication,
            options));

        var partial = Assert.Single(await coverage.GetCoverageDaysAsync(date, date));
        Assert.Equal(CoverageStatus.Partial, partial.Status);
        Assert.Contains("simulada", partial.LastError, StringComparison.OrdinalIgnoreCase);
        var key = $"Publication:{date:yyyyMMdd}:{date:yyyyMMdd}:m6:ufALL";
        var partialCheckpoint = await database.Repository.GetPartitionCheckpointAsync(key);
        Assert.NotNull(partialCheckpoint);
        Assert.Equal(SyncMode.Publication, partialCheckpoint.Mode);
        Assert.Equal(date, partialCheckpoint.StartDate);
        Assert.Equal(date, partialCheckpoint.EndDate);
        Assert.Equal(6, partialCheckpoint.ModalityId);
        Assert.Equal("ALL", partialCheckpoint.Uf);
        Assert.Equal(2, partialCheckpoint.NextPage);
        Assert.Equal(2, partialCheckpoint.TotalPages);
        Assert.Equal(SyncPartitionStatus.Partial, partialCheckpoint.Status);
        Assert.Contains("simulada", partialCheckpoint.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.True(partialCheckpoint.NextRetryAt > partialCheckpoint.UpdatedAt);

        await service.SynchronizeAsync(date, date, GeoScope.All, SyncMode.Publication, options);

        var complete = Assert.Single(await coverage.GetCoverageDaysAsync(date, date));
        Assert.Equal(CoverageStatus.Complete, complete.Status);
        Assert.Equal([1, 2, 2], client.RequestedPages);
        var completeCheckpoint = await database.Repository.GetPartitionCheckpointAsync(key);
        Assert.NotNull(completeCheckpoint);
        Assert.Equal(SyncPartitionStatus.Complete, completeCheckpoint.Status);
        Assert.Equal(0, completeCheckpoint.NextPage);
        Assert.Equal(2, completeCheckpoint.TotalPages);
        Assert.Null(completeCheckpoint.LastError);
        Assert.Null(completeCheckpoint.NextRetryAt);
    }

    [Fact]
    public async Task FailureBeforeFirstCheckpointMarksCoverageAsFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var coverage = (ICoverageRepository)database.Repository;
        var client = new AlwaysFailingClient();
        var service = new SyncService(client, database.Repository);
        var date = new DateOnly(2026, 7, 18);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SynchronizeAsync(
            date,
            date,
            GeoScope.All,
            SyncMode.Publication,
            new SyncExecutionOptions { FinalizeDataset = false }));

        var failed = Assert.Single(await coverage.GetCoverageDaysAsync(date, date));
        Assert.Equal(CoverageStatus.Failed, failed.Status);
        Assert.Contains("indisponível", failed.LastError, StringComparison.OrdinalIgnoreCase);
        var key = $"Publication:{date:yyyyMMdd}:{date:yyyyMMdd}:m6:ufALL";
        var checkpoint = await database.Repository.GetPartitionCheckpointAsync(key);
        Assert.NotNull(checkpoint);
        Assert.Equal(SyncPartitionStatus.Failed, checkpoint.Status);
        Assert.Equal(1, checkpoint.NextPage);
        Assert.Null(checkpoint.TotalPages);
        Assert.Contains("indisponível", checkpoint.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(checkpoint.NextRetryAt);
    }

    [Fact]
    public async Task GatewayTimeoutWhileLoadingModalitiesStillRecordsAnAutomaticResumeRun()
    {
        await using var database = await TestDatabase.CreateAsync();
        var date = new DateOnly(2026, 7, 20);
        var service = new SyncService(new ModalityGatewayTimeoutClient(), database.Repository);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.SynchronizeAsync(
            date,
            date,
            GeoScope.All,
            SyncMode.Publication,
            new SyncExecutionOptions { FinalizeDataset = false }));

        Assert.Equal(System.Net.HttpStatusCode.GatewayTimeout, exception.StatusCode);
        var incomplete = await database.Repository.GetLatestIncompleteSyncAsync();
        Assert.NotNull(incomplete);
        Assert.Equal(SyncMode.Publication, incomplete.Mode);
        Assert.Equal(date, incomplete.StartDate);
        Assert.Equal(date, incomplete.EndDate);
    }

    [Fact]
    public async Task GlobalUpdateNeverProvesPublicationCoverage()
    {
        await using var database = await TestDatabase.CreateAsync();
        var coverage = (ICoverageRepository)database.Repository;
        var client = new RecordingEmptyClient();
        var service = new SyncService(client, database.Repository);
        var date = new DateOnly(2026, 7, 20);
        await coverage.EnsureCoverageWindowAsync(date, date, [6]);

        await service.SynchronizeAsync(
            date,
            date,
            GeoScope.All,
            SyncMode.GlobalUpdate,
            new SyncExecutionOptions { FinalizeDataset = false });

        var day = Assert.Single(await coverage.GetCoverageDaysAsync(date, date));
        Assert.Equal(CoverageStatus.Missing, day.Status);
    }

    [Fact]
    public async Task AutomaticMaintenanceFillsNewestGapsThenUpdatesAndFinalizesOnce()
    {
        await using var database = await TestDatabase.CreateAsync();
        var coverage = (ICoverageRepository)database.Repository;
        var today = new DateOnly(2026, 7, 20);
        var start = today.AddDays(-364);
        var olderGap = today.AddDays(-20);
        var modalities = new[] { 6L, 8L };
        await SeedCompleteCoverageAsync(coverage, start, today, modalities);
        await coverage.SetCoverageStatusAsync(olderGap, olderGap, 6, "ALL", CoverageStatus.Missing);
        await coverage.SetCoverageStatusAsync(today, today, 8, "ALL", CoverageStatus.Missing);
        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("expired-auto", "Registro antigo", "SP", 1) with
            {
                PublicationDate = start.AddDays(-1).ToDateTime(new TimeOnly(12, 0))
            }
        ]);

        var client = new RecordingEmptyClient(
            new Modality(6, "Pregão"),
            new Modality(8, "Dispensa"));
        var service = new SyncService(client, database.Repository);
        var coordinator = new AutoSyncCoordinator(
            client,
            database.Repository,
            service,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero)));

        var result = await coordinator.SynchronizeAsync();

        Assert.Equal(start, result.StartDate);
        Assert.Equal(today, result.EndDate);
        Assert.Equal(2, result.CoverageBatches);
        Assert.True(result.GlobalUpdateCompleted);
        var publicationDates = client.Requests
            .Where(request => request.Mode == SyncMode.Publication)
            .Select(request => request.StartDate)
            .ToArray();
        Assert.Equal(new[] { today, olderGap }, publicationDates);
        Assert.Contains(client.Requests, request =>
            request.Mode == SyncMode.GlobalUpdate &&
            request.StartDate == today.AddDays(-2) &&
            request.EndDate <= today);

        Assert.True(await coverage.IsCoverageCompleteAsync(start, today));
        var state = await database.Repository.GetDatasetStateAsync();
        Assert.Equal(start, state.StartDate);
        Assert.Equal(today, state.EndDate);
        Assert.NotNull(state.LastSuccessfulSync);
        Assert.Equal(0, (await database.Repository.GetCountsAsync()).Contracts);
    }

    [Fact]
    public async Task FailedGlobalOverlapDoesNotPruneOrFinalizeDataset()
    {
        await using var database = await TestDatabase.CreateAsync();
        var coverage = (ICoverageRepository)database.Repository;
        var today = new DateOnly(2026, 7, 20);
        var start = today.AddDays(-364);
        await SeedCompleteCoverageAsync(coverage, start, today, [6]);
        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("preserved", "Registro preservado", "SP", 1) with
            {
                PublicationDate = start.AddDays(-1).ToDateTime(new TimeOnly(12, 0))
            }
        ]);

        var client = new RecordingEmptyClient(throwOnGlobalUpdate: true, new Modality(6, "Pregão"));
        var service = new SyncService(client, database.Repository);
        var coordinator = new AutoSyncCoordinator(
            client,
            database.Repository,
            service,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero)));

        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.SynchronizeAsync());

        var state = await database.Repository.GetDatasetStateAsync();
        Assert.Null(state.LastSuccessfulSync);
        Assert.Equal(1, (await database.Repository.GetCountsAsync()).Contracts);
        var globalKey = $"GlobalUpdate:{today.AddDays(-2):yyyyMMdd}:{today.AddDays(-1):yyyyMMdd}:m6:ufALL";
        var checkpoint = await database.Repository.GetPartitionCheckpointAsync(globalKey);
        Assert.NotNull(checkpoint);
        Assert.Equal(SyncMode.GlobalUpdate, checkpoint.Mode);
        Assert.Equal(SyncPartitionStatus.Failed, checkpoint.Status);
        Assert.Equal(1, checkpoint.NextPage);
        Assert.NotNull(checkpoint.NextRetryAt);
    }

    [Fact]
    public async Task FailedGapFillDoesNotPruneExpiredEdge()
    {
        await using var database = await TestDatabase.CreateAsync();
        var coverage = (ICoverageRepository)database.Repository;
        var today = new DateOnly(2026, 7, 20);
        var start = today.AddDays(-364);
        await SeedCompleteCoverageAsync(coverage, start, today, [6]);
        await coverage.SetCoverageStatusAsync(today, today, 6, "ALL", CoverageStatus.Missing);
        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("preserved-gap", "Registro preservado", "SP", 1) with
            {
                PublicationDate = start.AddDays(-1).ToDateTime(new TimeOnly(12, 0))
            }
        ]);

        var client = new AlwaysFailingClient();
        var coordinator = new AutoSyncCoordinator(
            client,
            database.Repository,
            new SyncService(client, database.Repository),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero)));

        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.SynchronizeAsync());

        Assert.Equal(1, (await database.Repository.GetCountsAsync()).Contracts);
        Assert.Null((await database.Repository.GetDatasetStateAsync()).LastSuccessfulSync);
        Assert.Equal(CoverageStatus.Failed, Assert.Single(await coverage.GetCoverageDaysAsync(today, today)).Status);
    }

    private static async Task SeedCompleteCoverageAsync(
        ICoverageRepository repository,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<long> modalityIds)
    {
        await repository.EnsureCoverageWindowAsync(startDate, endDate, modalityIds);
        foreach (var modalityId in modalityIds)
        {
            await repository.SetCoverageStatusAsync(
                startDate,
                endDate,
                modalityId,
                "ALL",
                CoverageStatus.Complete);
        }
    }

    private abstract class ClientBase : IPncpClient
    {
        public virtual Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([new Modality(6, "Pregão")]);

        public abstract Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default);

        public Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcurementItem>>([]);

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HomologationResult>>([]);
    }

    private sealed class BlockingEmptyClient : ClientBase
    {
        public TaskCompletionSource<bool> RequestEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default)
        {
            RequestEntered.TrySetResult(true);
            await ReleaseRequest.Task.WaitAsync(cancellationToken);
            return EmptyPage(page);
        }
    }

    private sealed class FailsOnceOnSecondPageClient : ClientBase
    {
        private bool _failed;

        public List<int> RequestedPages { get; } = [];

        public override Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default)
        {
            RequestedPages.Add(page);
            if (page == 2 && !_failed)
            {
                _failed = true;
                throw new HttpRequestException("Interrupção simulada.");
            }

            var contract = RepositorySearchTests.Contract(
                $"coverage-page-{page}",
                $"Página {page}",
                "SP",
                page);
            return Task.FromResult(new ContractPage(
                [contract],
                2,
                2,
                page,
                500,
                TimeSpan.FromMilliseconds(10)));
        }
    }

    private sealed class AlwaysFailingClient : ClientBase
    {
        public override Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("PNCP indisponível.");
    }

    private sealed class ModalityGatewayTimeoutClient : ClientBase
    {
        public override Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            throw new HttpRequestException(
                "PNCP respondeu 504.",
                null,
                System.Net.HttpStatusCode.GatewayTimeout);

        public override Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A lista não deve ser chamada sem modalidades.");
    }

    private sealed class RecordingEmptyClient : ClientBase
    {
        private readonly IReadOnlyList<Modality> _modalities;
        private readonly bool _throwOnGlobalUpdate;

        public RecordingEmptyClient(params Modality[] modalities)
            : this(false, modalities)
        {
        }

        public RecordingEmptyClient(bool throwOnGlobalUpdate, params Modality[] modalities)
        {
            _throwOnGlobalUpdate = throwOnGlobalUpdate;
            _modalities = modalities.Length == 0 ? [new Modality(6, "Pregão")] : modalities;
        }

        public List<ContractRequest> Requests { get; } = [];

        public override Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_modalities);

        public override Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new ContractRequest(startDate, endDate, modalityId, mode));
            if (_throwOnGlobalUpdate && mode == SyncMode.GlobalUpdate)
            {
                throw new HttpRequestException("Falha simulada na atualização global.");
            }

            return Task.FromResult(EmptyPage(page));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record ContractRequest(
        DateOnly StartDate,
        DateOnly EndDate,
        long ModalityId,
        SyncMode Mode);

    private static ContractPage EmptyPage(int page) =>
        new([], 0, 0, page, 0, TimeSpan.Zero);
}
