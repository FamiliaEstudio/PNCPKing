using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class SystemResourceTests
{
    private const long Gibibyte = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(16, 0.49, 8, SystemResourcePressure.Critical)]
    [InlineData(16, 1.00, 8, SystemResourcePressure.Constrained)]
    [InlineData(8, 4.00, 8, SystemResourcePressure.Constrained)]
    [InlineData(16, 4.00, 4, SystemResourcePressure.Constrained)]
    [InlineData(16, 4.00, 8, SystemResourcePressure.Normal)]
    public void Snapshot_ClassifiesPhysicalMemoryAndProcessorPressure(
        int totalGibibytes,
        double availableGibibytes,
        int processors,
        SystemResourcePressure expected)
    {
        var snapshot = SystemResourceProbe.CreateSnapshot(
            totalGibibytes * Gibibyte,
            (long)(availableGibibytes * Gibibyte),
            75,
            processors);

        Assert.Equal(expected, snapshot.Pressure);
    }

    [Fact]
    public void MaintenanceDecision_PausesForTwoMinutesUnderCriticalPressure()
    {
        var coordinator = new AdaptiveMaintenanceCoordinator(new FixedProbe(
            SystemResourceProbe.CreateSnapshot(8 * Gibibyte, 400 * 1024L * 1024, 95, 4)));

        var decision = coordinator.GetDecision();

        Assert.False(decision.CanRun);
        Assert.Equal(TimeSpan.Zero, decision.SliceDuration);
        Assert.Equal(TimeSpan.FromMinutes(2), decision.RetryDelay);
    }

    [Fact]
    public void MaintenanceDecision_UsesTenAndSixtySecondSlices()
    {
        var constrained = new AdaptiveMaintenanceCoordinator(new FixedProbe(
            SystemResourceProbe.CreateSnapshot(8 * Gibibyte, 2 * Gibibyte, 75, 4)));
        var normal = new AdaptiveMaintenanceCoordinator(new FixedProbe(
            SystemResourceProbe.CreateSnapshot(16 * Gibibyte, 4 * Gibibyte, 75, 8)));

        var constrainedDecision = constrained.GetDecision();
        Assert.Equal(TimeSpan.FromSeconds(10), constrainedDecision.SliceDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), constrainedDecision.RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), normal.GetDecision().SliceDuration);
    }

    [Fact]
    public async Task MaintenanceCoordinator_AllowsOnlyOneCycleAtATime()
    {
        var coordinator = new AdaptiveMaintenanceCoordinator(new FixedProbe(
            SystemResourceProbe.CreateSnapshot(16 * Gibibyte, 4 * Gibibyte, 75, 8)));

        var first = coordinator.TryEnter();
        Assert.NotNull(first);
        Assert.Null(coordinator.TryEnter());

        await first.DisposeAsync();
        var next = coordinator.TryEnter();
        Assert.NotNull(next);
        await next.DisposeAsync();
    }

    [Fact]
    public async Task VisibleActivity_CancelsCurrentSliceAndRequiresThirtyIdleSeconds()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
        var coordinator = new AdaptiveMaintenanceCoordinator(
            new FixedProbe(SystemResourceProbe.CreateSnapshot(
                16 * Gibibyte,
                4 * Gibibyte,
                50,
                8)),
            time);
        await using var slice = coordinator.BeginSlice();

        Assert.True(coordinator.NotifyVisibleActivity());

        Assert.True(SpinWait.SpinUntil(
            () => slice.Token.IsCancellationRequested,
            TimeSpan.FromSeconds(1)));
        var immediate = coordinator.GetDecision();
        Assert.False(immediate.CanRun);
        Assert.InRange(immediate.RetryDelay, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30));

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(coordinator.GetDecision().CanRun);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(coordinator.GetDecision().CanRun);
    }

    [Fact]
    public void ConstrainedMaintenance_RequiresSixtyIdleSeconds()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-24T12:00:00Z"));
        var coordinator = new AdaptiveMaintenanceCoordinator(
            new FixedProbe(SystemResourceProbe.CreateSnapshot(
                8 * Gibibyte,
                2 * Gibibyte,
                75,
                4)),
            time);

        coordinator.NotifyVisibleActivity();

        var immediate = coordinator.GetDecision();
        Assert.False(immediate.CanRun);
        Assert.Equal(TimeSpan.FromSeconds(60), immediate.RetryDelay);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(coordinator.GetDecision().CanRun);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(coordinator.GetDecision().CanRun);
    }

    [Theory]
    [InlineData(8, 2, 4, "Restrito", 64, 32, 1)]
    [InlineData(12, 4, 8, "Balanceado", 128, 64, 2)]
    [InlineData(16, 4, 8, "Amplo", 256, 128, 2)]
    public void SqliteProfile_UsesExpectedMigrationCacheMmapAndThreads(
        int totalGibibytes,
        int availableGibibytes,
        int processors,
        string profile,
        int migrationCacheMibibytes,
        int mmapMibibytes,
        int threads)
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-{Guid.NewGuid():N}.db");
        var resources = SystemResourceProbe.CreateSnapshot(
            totalGibibytes * Gibibyte,
            availableGibibytes * Gibibyte,
            50,
            processors);
        var factory = new SqliteConnectionFactory(path, resourceProbe: new FixedProbe(resources));

        Assert.Equal(profile, factory.ProfileName);
        Assert.Equal(migrationCacheMibibytes * 1024, factory.MigrationCacheKib);
        Assert.Equal(mmapMibibytes * 1024L * 1024, factory.MmapBytes);
        Assert.Equal(threads, factory.WorkerThreads);
    }

    private sealed class FixedProbe(SystemResourceSnapshot snapshot) : ISystemResourceProbe
    {
        public SystemResourceSnapshot GetSnapshot() => snapshot;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
