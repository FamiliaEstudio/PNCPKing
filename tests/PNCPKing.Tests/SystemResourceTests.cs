using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class SystemResourceTests
{
    [Fact]
    public async Task SliceDeadlineCoversAllPhasesWithoutAChildTimer()
    {
        var coordinator = new AdaptiveMaintenanceCoordinator(new FixedProbe(
            SystemResourceProbe.CreateSnapshot(8 * Gibibyte, 2 * Gibibyte, 75, 4)));
        await using var slice = coordinator.BeginSlice(duration: TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(2), slice.Token));
        Assert.True(slice.Token.IsCancellationRequested);
    }
    private const long Gibibyte = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(ResourceUsageProfile.Restricted, 32, 16, "Restrito", 32, 64, 128, 1)]
    [InlineData(ResourceUsageProfile.Medium, 32, 16, "Médio", 32, 128, 64, 2)]
    [InlineData(ResourceUsageProfile.Broad, 8, 4, "Amplo", 64, 256, 128, 2)]
    public async Task ManualProfileOverridesHardwareDefaultAndConfiguresNewConnections(
        ResourceUsageProfile selected, int memoryGib, int processors, string name,
        int cacheMib, int migrationMib, int mmapMib, int threads)
    {
        await using var database = await TestDatabase.CreateAsync();
        var resources = SystemResourceProbe.CreateSnapshot(memoryGib * Gibibyte, 4 * Gibibyte, 50, processors);
        var factory = new SqliteConnectionFactory(database.Repository.DatabasePath,
            resourceProbe: new FixedProbe(resources), resourceProfile: selected);
        Assert.Equal(selected, factory.SelectedResourceProfile);
        Assert.Equal(name, factory.ProfileName);
        Assert.Equal(migrationMib * 1024, factory.MigrationCacheKib);
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        foreach (var (pragma, expected) in new[]
        {
            ("cache_size", -cacheMib * 1024L), ("mmap_size", mmapMib * 1024L * 1024), ("threads", (long)threads)
        })
        {
            command.CommandText = $"PRAGMA {pragma};";
            Assert.Equal(expected, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        var calibration = factory.ReadOnlyProfile(new(64), new FixedProbe(resources));
        Assert.Equal(selected, calibration.SelectedResourceProfile);
        Assert.Equal(name, calibration.ProfileName);
    }

    [Fact]
    public void ManualBroadProfileKeepsMemoryProtectionAtStartupAndWhenMemoryDrops()
    {
        var probe = new SqliteCalibrationTests.Probe
        {
            Snapshot = SystemResourceProbe.CreateSnapshot(32 * Gibibyte, 16 * Gibibyte, 50, 16)
        };
        var factory = new SqliteConnectionFactory("unused-profile.db", resourceProbe: probe,
            resourceProfile: ResourceUsageProfile.Broad);
        Assert.Equal(64, factory.SearchTuning.CacheMiB);
        probe.Snapshot = SystemResourceProbe.CreateSnapshot(32 * Gibibyte, 400 * 1024 * 1024, 99, 16);
        Assert.Equal(32, factory.SearchTuning.CacheMiB);
        var lowMemoryStartup = new SqliteConnectionFactory("unused-profile.db", resourceProbe: probe,
            resourceProfile: ResourceUsageProfile.Broad);
        Assert.Equal("Restrito", lowMemoryStartup.ProfileName);
        Assert.Equal(64 * 1024, lowMemoryStartup.MigrationCacheKib);
        Assert.Equal(1, lowMemoryStartup.WorkerThreads);
    }

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

        Assert.True(slice.Token.IsCancellationRequested);
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
    [InlineData(8, 2, 4, "Restrito", 64, 128, 1)]
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
        Assert.Equal(profile == "Restrito", factory.SearchTuning.OrderedItemBatches);
        Assert.Equal(profile == "Amplo" ? 64 : 32, factory.SearchTuning.CacheMiB);
        var calibrated = new SqliteConnectionFactory(path, resourceProbe: new FixedProbe(resources), tuning: new(64, true));
        Assert.Equal(profile == "Restrito", calibrated.SearchTuning.OrderedItemBatches);
        Assert.Equal(migrationCacheMibibytes * 1024, factory.MigrationCacheKib);
        Assert.Equal(mmapMibibytes * 1024L * 1024, factory.MmapBytes);
        Assert.Equal(threads, factory.WorkerThreads);
    }

    [Theory]
    [InlineData(8, 2, 4, -32768, 128)]
    [InlineData(12, 4, 8, -32768, 64)]
    [InlineData(16, 4, 8, -65536, 128)]
    public async Task SqliteProfile_AppliesExpectedActiveCacheMmapAndFileTempStore(
        int totalGibibytes,
        int availableGibibytes,
        int processors,
        long expectedCacheKib,
        long expectedMmapMibibytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"active-profile-{Guid.NewGuid():N}.db");
        var resources = SystemResourceProbe.CreateSnapshot(
            totalGibibytes * Gibibyte,
            availableGibibytes * Gibibyte,
            50,
            processors);
        var factory = new SqliteConnectionFactory(path, resourceProbe: new FixedProbe(resources));

        try
        {
            await using var connection = await factory.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cache_size;";
            Assert.Equal(expectedCacheKib, Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.CommandText = "PRAGMA mmap_size;";
            Assert.Equal(
                expectedMmapMibibytes * 1024 * 1024,
                Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.CommandText = "PRAGMA temp_store;";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
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
