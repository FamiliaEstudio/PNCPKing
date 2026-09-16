using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

public sealed class AggressiveCoverageTests(ITestOutputHelper output)
{
    private static readonly DateOnly Today = new(2026, 7, 23);

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task CoverageAndGlobalUpdatesUseTheRequestedConcurrencyAndFinalizeOnlyAfterBoth(int concurrency)
    {
        await using var database = await TestDatabase.CreateAsync();
        var start = DataWindow.Start(Today);
        var previousSync = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        await database.Repository.EnsureCoverageWindowAsync(start, Today, [6, 8]);
        foreach (var modality in new[] { 6L, 8L })
        {
            await database.Repository.SetCoverageStatusAsync(start, Today, modality, "ALL", CoverageStatus.Complete);
            await database.Repository.SetCoverageStatusAsync(Today, Today, modality, "ALL", CoverageStatus.Missing);
        }
        await database.Repository.SetDatasetStateAsync(start, Today, GeoScope.All, previousSync);
        var entered = new[] { Signal(), Signal() };
        var release = new[] { Signal(), Signal() };
        var active = new int[2];
        var peaks = new int[2];
        var client = new PageClient([new(6, "Pregão"), new(8, "Dispensa")], async (request, token) =>
        {
            if (request.Page > 1)
            {
                var mode = (int)request.Mode;
                var count = Interlocked.Increment(ref active[mode]);
                InterlockedMax(ref peaks[mode], count);
                if (count == concurrency) entered[mode].TrySetResult();
                try { await release[mode].Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref active[mode]); }
            }
            return Page(request, 5);
        });
        var sync = new SyncService(client, database.Repository);
        var coordinator = new AutoSyncCoordinator(client, database.Repository, sync,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 15, 0, 0, TimeSpan.Zero)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var progress = new ConcurrentQueue<SyncProgress>();
        var operation = coordinator.SynchronizeAsync(new InlineProgress(progress.Enqueue), cancellation.Token, concurrency);
        try
        {
            await entered[0].Task.WaitAsync(cancellation.Token);
            Assert.Equal(concurrency, peaks[0]);
            Assert.DoesNotContain(client.Requests, r => r.Mode == SyncMode.GlobalUpdate);
            release[0].TrySetResult();
            await entered[1].Task.WaitAsync(cancellation.Token);
            Assert.Equal(concurrency, peaks[1]);
            Assert.Equal(previousSync, (await database.Repository.GetDatasetStateAsync()).LastSuccessfulSync);
            release[1].TrySetResult();
            Assert.True((await operation).GlobalUpdateCompleted);

            Assert.Equal(10, (await database.Repository.GetCountsAsync()).Contracts);
            Assert.True(await database.Repository.IsCoverageCompleteAsync(start, Today));
            Assert.True((await database.Repository.GetDatasetStateAsync()).LastSuccessfulSync > previousSync);
            Assert.Equal(10, progress.Last().ContractsSaved);
            Assert.Equal(2, progress.Last().CompletedPartitions);
            Assert.Equal(20, client.Requests.Count);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await operation; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowPageBoundsReadAheadAndPreservesCheckpointOnFailureOrCancellation(bool cancel)
    {
        await using var database = await TestDatabase.CreateAsync();
        var laterPagesReceived = Signal();
        var fail = Signal();
        var recovering = false;
        var active = 0;
        var received = 0;
        var client = new PageClient([new(6, "Pregão")], async (request, token) =>
        {
            Interlocked.Increment(ref active);
            try
            {
                if (!recovering && request.Page == 2)
                {
                    await fail.Task.WaitAsync(token);
                    throw new HttpRequestException("Falha da página 2");
                }
                if (!recovering && request.Page >= 3 && Interlocked.Increment(ref received) == 3)
                    laterPagesReceived.TrySetResult();
                return Page(request, 10);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var service = new SyncService(client, database.Repository);
        var options = new SyncExecutionOptions { MaximumConcurrency = 4, FinalizeDataset = false };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var operation = service.SynchronizeAsync(Today, Today, GeoScope.All, SyncMode.Publication,
            options, cancellationToken: cancellation.Token);
        try
        {
            await laterPagesReceived.Task.WaitAsync(cancellation.Token);
            Assert.Equal(5, client.Requests.Count);
            var key = "Publication:20260723:20260723:m6:ufALL";
            Assert.Equal(2, (await database.Repository.GetPartitionCheckpointAsync(key))!.NextPage);
            Assert.Equal(1, (await database.Repository.GetCountsAsync()).Contracts);

            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            }
            else
            {
                fail.TrySetResult();
                var error = await Assert.ThrowsAsync<HttpRequestException>(() => operation);
                Assert.Contains("página 2", error.Message);
            }
            Assert.Equal(0, active);
            var partial = await database.Repository.GetPartitionCheckpointAsync(key);
            Assert.Equal(2, partial!.NextPage);
            Assert.Equal(SyncPartitionStatus.Partial, partial.Status);
            Assert.Equal(5, client.Requests.Count);

            recovering = true;
            await service.SynchronizeAsync(Today, Today, GeoScope.All, SyncMode.Publication, options);
            Assert.Equal(10, (await database.Repository.GetCountsAsync()).Contracts);
            Assert.Single(client.Requests, r => r.Page == 1);
            Assert.Equal(0, (await database.Repository.GetPartitionCheckpointAsync(key))!.NextPage);
            var count = client.Requests.Count;
            await service.SynchronizeAsync(Today, Today, GeoScope.All, SyncMode.Publication, options);
            Assert.Equal(count, client.Requests.Count);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await operation; } catch (Exception) { }
        }
    }

    [Fact]
    public async Task AggressiveCoverageCanBePausedBeforeDispatchAndCancelledWhilePaused()
    {
        await using var database = await TestDatabase.CreateAsync();
        var client = new PageClient([new(6, "Pregão"), new(8, "Dispensa")],
            (request, _) => Task.FromResult(Page(request, 5)));
        var service = new SyncService(client, database.Repository);
        service.Pause();
        using var cancellation = new CancellationTokenSource();
        var operation = service.SynchronizeAsync(Today, Today, GeoScope.All, SyncMode.Publication,
            new SyncExecutionOptions { MaximumConcurrency = 8 }, cancellationToken: cancellation.Token);
        await Task.Delay(50);
        Assert.Empty(client.Requests);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        service.Resume();
        await service.SynchronizeAsync(Today, Today, GeoScope.All, SyncMode.Publication,
            new SyncExecutionOptions { MaximumConcurrency = 8 });
        Assert.Equal(10, (await database.Repository.GetCountsAsync()).Contracts);
    }

    [Fact]
    public async Task CoverageLatencySampleRecordsNormalAndAggressiveThroughput()
    {
        var samples = new List<object>();
        foreach (var concurrency in new[] { 2, 8, 2, 8 })
        {
            await using var database = await TestDatabase.CreateAsync();
            var active = 0;
            var peak = 0;
            var client = new PageClient([new(6, "Pregão"), new(8, "Dispensa")], async (request, token) =>
            {
                InterlockedMax(ref peak, Interlocked.Increment(ref active));
                try { await Task.Delay(80, token); return Page(request, 20); }
                finally { Interlocked.Decrement(ref active); }
            });
            var service = new SyncService(client, database.Repository);
            var timer = Stopwatch.StartNew();
            await service.SynchronizeAsync(Today, Today, GeoScope.All, SyncMode.Publication,
                new SyncExecutionOptions { MaximumConcurrency = concurrency, FinalizeDataset = false });
            timer.Stop();
            Assert.Equal(40, (await database.Repository.GetCountsAsync()).Contracts);
            Assert.Equal(40, client.Requests.Count);
            Assert.Equal(concurrency, peak);
            samples.Add(new { concurrency, peak, requests = client.Requests.Count,
                simulatedLatencyMs = 80, elapsedMs = timer.Elapsed.TotalMilliseconds });
        }
        output.WriteLine(JsonSerializer.Serialize(samples));
    }

    private static ContractPage Page(Request request, int pages) => new([
        RepositorySearchTests.Contract($"m{request.Modality}-p{request.Page}", "Objeto", "SP", request.Page) with
        {
            ModalityId = request.Modality,
            PublicationDate = Today.ToDateTime(new TimeOnly(12, 0))
        }], pages, pages, request.Page, 100, TimeSpan.Zero);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void InterlockedMax(ref int target, int value)
    {
        int previous;
        do { previous = Volatile.Read(ref target); if (previous >= value) return; }
        while (Interlocked.CompareExchange(ref target, value, previous) != previous);
    }

    private sealed record Request(SyncMode Mode, long Modality, int Page);

    private sealed class PageClient(IReadOnlyList<Modality> modalities,
        Func<Request, CancellationToken, Task<ContractPage>> load) : IPncpClient
    {
        public ConcurrentQueue<Request> Requests { get; } = new();
        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(modalities);
        public Task<ContractPage> GetContractsPageAsync(DateOnly startDate, DateOnly endDate, long modalityId, string? uf,
            int page, int pageSize, SyncMode mode, CancellationToken cancellationToken = default)
        {
            var request = new Request(mode, modalityId, page);
            Requests.Enqueue(request);
            return load(request, cancellationToken);
        }
        public Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(ContractRecord contract, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(ContractRecord contract, long itemNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class InlineProgress(Action<SyncProgress> report) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => report(value);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
