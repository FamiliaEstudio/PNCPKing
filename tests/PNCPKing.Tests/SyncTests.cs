using System.Net;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class SyncTests
{
    [Fact]
    public async Task Synchronization_ResumesAtCheckpointWithoutDuplicates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var client = new CheckpointClient();
        var service = new SyncService(client, database.Repository);
        var start = new DateOnly(2026, 6, 1);
        var end = new DateOnly(2026, 6, 2);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SynchronizeAsync(
            start,
            end,
            GeoScope.All,
            SyncMode.Publication));
        var incomplete = await database.Repository.GetLatestIncompleteSyncAsync();
        Assert.NotNull(incomplete);
        Assert.Equal(start, incomplete.StartDate);
        Assert.Equal(end, incomplete.EndDate);
        await service.SynchronizeAsync(start, end, GeoScope.All, SyncMode.Publication);

        var counts = await database.Repository.GetCountsAsync();
        Assert.Equal(2, counts.Contracts);
        Assert.Equal([1, 2, 2], client.RequestedPages);
    }

    [Fact]
    public async Task Synchronization_HandlesEmptyPartitionAndSkipsItOnResume()
    {
        await using var database = await TestDatabase.CreateAsync();
        var client = new EmptyClient();
        var service = new SyncService(client, database.Repository);
        var date = new DateOnly(2026, 6, 1);

        await service.SynchronizeAsync(date, date, GeoScope.All, SyncMode.Publication);
        await service.SynchronizeAsync(date, date, GeoScope.All, SyncMode.Publication);

        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Synchronization_PrunesOnlyContractsOutsideTheRolling365Days()
    {
        await using var database = await TestDatabase.CreateAsync();
        var end = new DateOnly(2026, 7, 20);
        var cutoff = end.AddDays(-364);
        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("expired", "Antiga", "SP", 1) with
            {
                PublicationDate = cutoff.AddDays(-1).ToDateTime(new TimeOnly(12, 0))
            },
            RepositorySearchTests.Contract("kept", "No limite", "SP", 2) with
            {
                PublicationDate = cutoff.ToDateTime(new TimeOnly(12, 0))
            }
        ]);
        var service = new SyncService(new EmptyClient(), database.Repository);

        await service.SynchronizeAsync(end, end, GeoScope.All, SyncMode.Publication);

        var results = await database.Repository.SearchAsync(new SearchQuery(string.Empty, GeoScope.All));
        Assert.Single(results.Results);
        Assert.Equal("kept", results.Results[0].PncpId);
    }

    [Fact]
    public async Task Synchronization_FallsBackToDailyPartitionsAfterPncpDateRejection()
    {
        await using var database = await TestDatabase.CreateAsync();
        var client = new DateRejectingClient();
        var service = new SyncService(client, database.Repository);
        var start = new DateOnly(2026, 6, 1);
        var end = new DateOnly(2026, 6, 2);

        await service.SynchronizeAsync(start, end, GeoScope.All, SyncMode.Publication);
        await service.SynchronizeAsync(start, end, GeoScope.All, SyncMode.Publication);

        Assert.Equal(3, client.Requests.Count);
        Assert.Equal((start, end), client.Requests[0]);
        Assert.Contains((start, start), client.Requests);
        Assert.Contains((end, end), client.Requests);
        var counts = await database.Repository.GetCountsAsync();
        Assert.Equal(2, counts.Contracts);
    }

    [Fact]
    public async Task Synchronization_SanitizesMalformedUnicodeAndAdvancesCheckpoint()
    {
        await using var database = await TestDatabase.CreateAsync();
        var date = new DateOnly(2026, 7, 21);
        var service = new SyncService(new MalformedUnicodeClient(), database.Repository);

        await service.SynchronizeAsync(date, date, GeoScope.All, SyncMode.Publication);

        var match = await database.Repository.SearchAsync(new SearchQuery("cafe", GeoScope.All));
        var contract = Assert.Single(match.Results);
        Assert.Equal("Aquisição � � de café", contract.Object);
        var checkpoint = await database.Repository.GetPartitionCheckpointAsync(
            $"Publication:{date:yyyyMMdd}:{date:yyyyMMdd}:m6:ufALL");
        Assert.NotNull(checkpoint);
        Assert.Equal(SyncPartitionStatus.Complete, checkpoint.Status);
        Assert.Equal(0, checkpoint.NextPage);
    }

    private sealed class CheckpointClient : IPncpClient
    {
        private bool _failed;
        public List<int> RequestedPages { get; } = [];

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([new Modality(6, "Pregão")]);

        public Task<ContractPage> GetContractsPageAsync(
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
                throw new HttpRequestException("Interrupção simulada");
            }

            var contract = RepositorySearchTests.Contract(
                page == 1 ? "first" : "second",
                $"Objeto da página {page}",
                "SP",
                page);
            return Task.FromResult(new ContractPage([contract], 2, 2, page, 500, TimeSpan.FromMilliseconds(10)));
        }

        public Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(ContractRecord contract, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcurementItem>>([]);

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(ContractRecord contract, long itemNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HomologationResult>>([]);
    }

    private sealed class EmptyClient : IPncpClient
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([new Modality(6, "Pregão")]);

        public Task<ContractPage> GetContractsPageAsync(DateOnly startDate, DateOnly endDate, long modalityId, string? uf, int page, int pageSize, SyncMode mode, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ContractPage([], 0, 0, page, 0, TimeSpan.Zero));
        }

        public Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(ContractRecord contract, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProcurementItem>>([]);
        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(ContractRecord contract, long itemNumber, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HomologationResult>>([]);
    }

    private sealed class DateRejectingClient : IPncpClient
    {
        public List<(DateOnly Start, DateOnly End)> Requests { get; } = [];

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([new Modality(6, "Pregão")]);

        public Task<ContractPage> GetContractsPageAsync(DateOnly startDate, DateOnly endDate, long modalityId, string? uf, int page, int pageSize, SyncMode mode, CancellationToken cancellationToken = default)
        {
            Requests.Add((startDate, endDate));
            if (startDate != endDate)
            {
                throw new HttpRequestException(
                    "Data Inicial deve ser anterior ou igual à Data Final",
                    null,
                    HttpStatusCode.UnprocessableEntity);
            }

            var contract = RepositorySearchTests.Contract(
                $"day-{startDate:yyyyMMdd}",
                $"Objeto de {startDate:dd/MM/yyyy}",
                "SP",
                startDate.Day);
            return Task.FromResult(new ContractPage([contract], 1, 1, page, 500, TimeSpan.FromMilliseconds(10)));
        }

        public Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(ContractRecord contract, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProcurementItem>>([]);
        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(ContractRecord contract, long itemNumber, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HomologationResult>>([]);
    }

    private sealed class MalformedUnicodeClient : IPncpClient
    {
        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([new Modality(6, "Pregão")]);

        public Task<ContractPage> GetContractsPageAsync(DateOnly startDate, DateOnly endDate, long modalityId, string? uf, int page, int pageSize, SyncMode mode, CancellationToken cancellationToken = default)
        {
            var contract = RepositorySearchTests.Contract("unicode", "Aquisição \uD800 \uFFFE de café", "SP", 1);
            return Task.FromResult(new ContractPage([contract], 1, 1, page, 500, TimeSpan.Zero));
        }

        public Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(ContractRecord contract, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProcurementItem>>([]);
        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(ContractRecord contract, long itemNumber, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HomologationResult>>([]);
    }
}
