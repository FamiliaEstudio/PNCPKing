using System.Collections.Concurrent;
using System.Net;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class PreflightTests
{
    [Theory]
    [InlineData("new", true)]
    [InlineData("imported", false)]
    [InlineData("interrupted", false)]
    [InlineData("completed-empty", false)]
    public async Task InitialEstimateIsRequiredOnlyForAnUnstartedEmptyDatabase(string state, bool expected)
    {
        await using var database = await TestDatabase.CreateAsync();
        var date = new DateOnly(2026, 9, 15);
        switch (state)
        {
            case "imported":
                await database.Repository.UpsertContractsAsync([RepositorySearchTests.Contract("imported", "Objeto", "SP", 1)]);
                break;
            case "interrupted":
                var run = await database.Repository.StartSyncRunAsync(SyncMode.Publication, date, date);
                await database.Repository.CompleteSyncRunAsync(run, false, 0, "HTTP 500");
                break;
            case "completed-empty":
                await database.Repository.SetDatasetStateAsync(date, date, GeoScope.All, DateTimeOffset.UtcNow);
                break;
        }
        var client = new CountingClient();

        Assert.Equal(expected, await new PreflightService(client).RequiresInitialEstimateAsync(database.Repository));

        Assert.Equal(0, client.ModalityCalls);
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData("2025-10-15", "2026-09-15")]
    [InlineData("2024-02-27", "2024-03-06")]
    [InlineData("2026-09-15", "2026-09-15")]
    public async Task InitialEstimateCountsEveryDayOncePerModalityUsingAtMostOneWeek(string from, string to)
    {
        var start = DateOnly.Parse(from);
        var end = DateOnly.Parse(to);
        var client = new CountingClient();
        var estimate = await new PreflightService(client).CalculateAsync(start, end, GeoScope.State("SP"), Path.GetTempPath());

        Assert.Equal((end.DayNumber - start.DayNumber + 1L) * 14, estimate.ExactContractCount);
        Assert.Equal(estimate.ExactContractCount * 1_000, estimate.EstimatedTransferBytes);
        Assert.All(client.Requests, request =>
        {
            Assert.InRange(request.End.DayNumber - request.Start.DayNumber, 0, 6);
            Assert.Equal(1, request.Page);
            Assert.Equal(10, request.PageSize);
            Assert.Equal("SP", request.Uf);
            Assert.Equal(SyncMode.Publication, request.Mode);
        });
        foreach (var modality in new[] { 6L, 8L })
        {
            var queriedDays = client.Requests.Where(request => request.Modality == modality)
                .SelectMany(request => Enumerable.Range(request.Start.DayNumber, request.End.DayNumber - request.Start.DayNumber + 1));
            Assert.Equal(Enumerable.Range(start.DayNumber, end.DayNumber - start.DayNumber + 1), queriedDays);
        }
    }

    [Fact]
    public async Task ExistingDatabaseReachesCoverageWithoutCallingTheNationalEstimate()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = new DateOnly(2026, 9, 15);
        var start = DataWindow.Start(today);
        await database.Repository.EnsureCoverageWindowAsync(start, today, [6, 8]);
        foreach (var modality in new[] { 6L, 8L })
            await database.Repository.SetCoverageStatusAsync(start, today, modality, "ALL", CoverageStatus.Complete);
        await database.Repository.SetCoverageStatusAsync(today, today, 6, "ALL", CoverageStatus.Missing);
        await database.Repository.SetDatasetStateAsync(start, today, GeoScope.All,
            new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
        var client = new CountingClient(rejectEstimates: true);
        var preflight = new PreflightService(client);

        Assert.False(await preflight.RequiresInitialEstimateAsync(database.Repository));
        var sync = new SyncService(client, database.Repository);
        var coordinator = new AutoSyncCoordinator(client, database.Repository, sync,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero)));
        await coordinator.SynchronizeAsync(maximumConcurrency: 8);

        Assert.NotEmpty(client.Requests);
        Assert.All(client.Requests, request =>
        {
            Assert.Equal(50, request.PageSize);
            Assert.InRange(request.End.DayNumber - request.Start.DayNumber, 0, 6);
        });
        Assert.True(await database.Repository.IsCoverageCompleteAsync(start, today));
    }

    [Fact]
    public async Task FailedEstimateDoesNotProduceACompletedOrPartialEstimate()
    {
        var client = new CountingClient(rejectEstimates: true);
        var date = new DateOnly(2026, 9, 15);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new PreflightService(client)
            .CalculateAsync(date.AddMonths(-11), date, GeoScope.All, Path.GetTempPath()));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.Single(client.Requests);
    }

    private sealed record Request(DateOnly Start, DateOnly End, long Modality, string? Uf, int Page, int PageSize, SyncMode Mode);

    private sealed class CountingClient(bool rejectEstimates = false) : IPncpClient
    {
        public int ModalityCalls { get; private set; }
        public ConcurrentQueue<Request> Requests { get; } = new();

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default)
        {
            ModalityCalls++;
            return Task.FromResult<IReadOnlyList<Modality>>([new(6, "Pregão"), new(8, "Dispensa")]);
        }

        public Task<ContractPage> GetContractsPageAsync(DateOnly startDate, DateOnly endDate, long modalityId,
            string? uf, int page, int pageSize, SyncMode mode, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(new(startDate, endDate, modalityId, uf, page, pageSize, mode));
            if (rejectEstimates && pageSize == 10)
                throw new HttpRequestException("Failed to obtain JDBC Connection", null, HttpStatusCode.InternalServerError);
            var total = (endDate.DayNumber - startDate.DayNumber + 1) * modalityId;
            return Task.FromResult(new ContractPage([
                RepositorySearchTests.Contract($"{modalityId}-{startDate}", "Objeto", "SP", 1)
            ], total, 1, page, 1_000, TimeSpan.FromMilliseconds(10)));
        }

        public Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(ContractRecord contract, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(ContractRecord contract, long itemNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
