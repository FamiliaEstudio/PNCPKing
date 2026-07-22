using PNCPKing.Core.Models;

namespace PNCPKing.Tests;

public sealed class CoverageRepositoryTests
{
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
