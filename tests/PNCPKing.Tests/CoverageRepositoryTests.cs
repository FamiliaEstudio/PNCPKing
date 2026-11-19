using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class CoverageRepositoryTests
{
    [Fact]
    public async Task CoverageShowsCompletedModalitiesWhenNewModalityIsPending()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = database.Repository;
        var start = new DateOnly(2026, 11, 16);
        var end = start.AddDays(2);
        var existingModalities = Enumerable.Range(1, 19).Select(id => (long)id).ToArray();
        await repository.EnsureCoverageWindowAsync(start, end, existingModalities);
        foreach (var modality in existingModalities)
            await repository.SetCoverageStatusAsync(start, end, modality, "ALL", CoverageStatus.Complete);
        await repository.EnsureCoverageWindowAsync(start, end, [..existingModalities, 20L]);

        var days = await repository.GetCoverageDaysAsync(start, end);
        var summary = new CoverageSummary(days, days.Count(day => day.IsComplete), days.Count);
        Assert.Equal(95d, summary.Percentage);
        Assert.Equal(0, summary.CompleteDays);
        Assert.All(days, day => Assert.Equal(CoverageStatus.Partial, day.Status));
        Assert.False(await repository.IsCoverageCompleteAsync(start, end));
        var pending = await repository.GetIncompleteCoverageAsync(start, end, 10, true);
        Assert.Equal(days.Count, pending.Count);
        Assert.All(pending, item => Assert.Equal(20L, item.ModalityId));
    }

    [Fact]
    public async Task CoveragePreparationReleasesWriterEverySevenDaysAndResumesCommittedSlices()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var coordinator = new ReleaseCoordinator(cancellation);
        var repository = new SqliteContractRepository(new SqliteConnectionFactory(
            database.Repository.DatabasePath, coordinator));
        var start = new DateOnly(2026, 7, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.EnsureCoverageWindowAsync(start, start.AddDays(20), [1, 2], cancellationToken: cancellation.Token));
        var partial = await database.Repository.GetCoverageDaysAsync(start, start.AddDays(20));
        Assert.Equal(7, partial.Count(day => day.ExpectedModalities > 0));
        Assert.All(partial.Take(7), day => Assert.Equal(2, day.ExpectedModalities));
        Assert.All(partial.Skip(7), day => Assert.Equal(0, day.ExpectedModalities));
        Assert.Equal(1, coordinator.Released);
        await database.Repository.SetCoverageStatusAsync(start, start, 1, "ALL", CoverageStatus.Complete, 4);
        await repository.EnsureCoverageWindowAsync(start, start.AddDays(20), [1, 2]);
        var complete = await database.Repository.GetCoverageDaysAsync(start, start.AddDays(20));
        Assert.Equal(21, complete.Count);
        Assert.Equal(1, complete[0].CompletedModalities);
        Assert.Equal(4, coordinator.Released);
    }

    private sealed class ReleaseCoordinator(CancellationTokenSource cancellation) : ISqliteWorkCoordinator
    {
        private readonly SqliteWorkCoordinator _inner = new();
        public int Released { get; private set; }
        public bool IsIdle => _inner.IsIdle;
        public ValueTask<IAsyncDisposable> EnterReaderAsync(SqliteWorkPriority priority = SqliteWorkPriority.Visible,
            CancellationToken cancellationToken = default) => _inner.EnterReaderAsync(priority, cancellationToken);
        public async ValueTask<IAsyncDisposable> EnterWriterAsync(SqliteWorkPriority priority = SqliteWorkPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SqliteWorkPriority.Background, priority);
            return new Release(await _inner.EnterWriterAsync(priority, cancellationToken), () =>
            { if (++Released == 1) cancellation.Cancel(); });
        }
        private sealed class Release(IAsyncDisposable lease, Action released) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync() { await lease.DisposeAsync(); released(); }
        }
    }

    [Fact]
    public async Task CoverageWindow_ReconcilesInactiveModalitiesAndReplacesRecordCounts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var start = new DateOnly(2026, 7, 19);
        var end = new DateOnly(2026, 7, 20);
        await database.Repository.EnsureCoverageWindowAsync(start, end, [1, 2]);
        await database.Repository.SetCoverageStatusAsync(
            start,
            end,
            1,
            "ALL",
            CoverageStatus.Complete,
            10);
        await database.Repository.SetCoverageStatusAsync(
            start,
            end,
            2,
            "ALL",
            CoverageStatus.AssumedComplete,
            null);
        // Search geography is independent from the national synchronization
        // scope. A historical UF checkpoint must not make the national 365-day
        // bar incomplete or inflate its expected modality count.
        await database.Repository.SetCoverageStatusAsync(
            start,
            end,
            99,
            "SP",
            CoverageStatus.Failed,
            error: "checkpoint estadual antigo");

        Assert.True(await database.Repository.IsCoverageCompleteAsync(start, end));
        var complete = await database.Repository.GetCoverageDaysAsync(start, end);
        Assert.All(complete, day => Assert.Equal(2, day.CompletedModalities));
        Assert.All(complete, day => Assert.Equal(10, day.RecordsCount));

        // Re-running with the current dynamic modality set removes modality 2 from
        // both progress aggregation and incomplete work selection.
        await database.Repository.EnsureCoverageWindowAsync(start, end, [1]);
        await database.Repository.SetCoverageStatusAsync(
            start,
            end,
            1,
            "ALL",
            CoverageStatus.Complete,
            7);
        var reconciled = await database.Repository.GetCoverageDaysAsync(start, end);
        Assert.All(reconciled, day => Assert.Equal(1, day.ExpectedModalities));
        Assert.All(reconciled, day => Assert.Equal(7, day.RecordsCount));
        Assert.Empty(await database.Repository.GetIncompleteCoverageAsync(start, end, 10, true));
    }
}
