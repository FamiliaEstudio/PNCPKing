using PNCPKing.App.Services;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class PerformanceHealthTests
{
    [Fact]
    public void Evaluation_AwaitsPncpWhenOnlyCancellationsExist()
    {
        var evaluation = PerformanceHealthEvaluator.Evaluate(
            Snapshot(SystemResourcePressure.Normal),
            Recent(canceled: 2));

        Assert.Equal(PerformanceIndicatorLevel.Good, evaluation.Interface);
        Assert.Equal(PerformanceIndicatorLevel.Measuring, evaluation.Pncp);
        Assert.Equal("Aguardando", evaluation.PncpLabel);
    }

    [Theory]
    [InlineData(SystemResourcePressure.Normal, PerformanceIndicatorLevel.Good, "Responsiva")]
    [InlineData(SystemResourcePressure.Constrained, PerformanceIndicatorLevel.Warning, "Regular")]
    [InlineData(SystemResourcePressure.Critical, PerformanceIndicatorLevel.Critical, "Lenta")]
    public void Interface_ReflectsResourcePressure(
        SystemResourcePressure pressure,
        PerformanceIndicatorLevel expected,
        string label)
    {
        var evaluation = PerformanceHealthEvaluator.Evaluate(
            Snapshot(pressure),
            Recent(succeeded: 1));

        Assert.Equal(expected, evaluation.Interface);
        Assert.Equal(label, evaluation.InterfaceLabel);
    }

    [Fact]
    public void SlowPncp_DoesNotMakeAResponsiveInterfaceLookSlow()
    {
        var evaluation = PerformanceHealthEvaluator.Evaluate(
            Snapshot(SystemResourcePressure.Normal),
            Recent(succeeded: 3, p95: TimeSpan.FromSeconds(11)));

        Assert.Equal(PerformanceIndicatorLevel.Good, evaluation.Interface);
        Assert.Equal("Responsiva", evaluation.InterfaceLabel);
        Assert.Equal(PerformanceIndicatorLevel.Warning, evaluation.Pncp);
        Assert.Equal("Lento", evaluation.PncpLabel);
    }

    [Fact]
    public void Pncp_ReportsRecoveryDuringConcurrencyCooldown()
    {
        var now = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var scheduler = Scheduler(effective: 8, queued: 0, p95: TimeSpan.FromSeconds(2)) with
        {
            GrowthBlockedUntil = now.AddMinutes(1)
        };
        var evaluation = PerformanceHealthEvaluator.Evaluate(
            Snapshot(SystemResourcePressure.Normal, scheduler, now: now),
            Recent(succeeded: 2));

        Assert.Equal(PerformanceIndicatorLevel.Warning, evaluation.Pncp);
        Assert.Equal("Recuperando", evaluation.PncpLabel);
    }

    [Theory]
    [InlineData(99, PerformanceIndicatorLevel.Good)]
    [InlineData(100, PerformanceIndicatorLevel.Warning)]
    [InlineData(500, PerformanceIndicatorLevel.Critical)]
    public void Interface_UsesDispatcherP95Boundaries(
        int milliseconds,
        PerformanceIndicatorLevel expected)
    {
        var evaluation = PerformanceHealthEvaluator.Evaluate(
            Snapshot(SystemResourcePressure.Normal, dispatcherP95: TimeSpan.FromMilliseconds(milliseconds)),
            Recent(succeeded: 1));

        Assert.Equal(expected, evaluation.Interface);
    }

    [Fact]
    public void Pncp_ReportsOscillationForFailuresMixedWithSuccesses()
    {
        var evaluation = PerformanceHealthEvaluator.Evaluate(
            Snapshot(SystemResourcePressure.Normal),
            Recent(succeeded: 2, failed: 1));

        Assert.Equal(PerformanceIndicatorLevel.Warning, evaluation.Pncp);
        Assert.Equal("Oscilando", evaluation.PncpLabel);
    }

    [Theory]
    [InlineData(10, PerformanceIndicatorLevel.Good, "Normal")]
    [InlineData(11, PerformanceIndicatorLevel.Warning, "Lento")]
    [InlineData(31, PerformanceIndicatorLevel.Critical, "Indisponível")]
    public void Pncp_UsesLatencyBoundaries(
        int seconds,
        PerformanceIndicatorLevel expected,
        string label)
    {
        var evaluation = PerformanceHealthEvaluator.Evaluate(
            Snapshot(SystemResourcePressure.Normal),
            Recent(succeeded: 2, p95: TimeSpan.FromSeconds(seconds)));

        Assert.Equal(expected, evaluation.Pncp);
        Assert.Equal(label, evaluation.PncpLabel);
    }

    [Theory]
    [InlineData(8, PerformanceIndicatorLevel.Good)]
    [InlineData(9, PerformanceIndicatorLevel.Warning)]
    [InlineData(17, PerformanceIndicatorLevel.Critical)]
    public void Pncp_UsesQueueCapacityBoundaries(
        int queued,
        PerformanceIndicatorLevel expected)
    {
        var evaluation = PerformanceHealthEvaluator.Evaluate(
            Snapshot(SystemResourcePressure.Normal, Scheduler(8, queued, TimeSpan.FromSeconds(2))),
            Recent(succeeded: 2));

        Assert.Equal(expected, evaluation.Pncp);
    }

    [Fact]
    public void Pncp_ReportsUnavailableAfterThreeFailuresWithoutSuccess()
    {
        var evaluation = PerformanceHealthEvaluator.Evaluate(
            Snapshot(SystemResourcePressure.Normal),
            Recent(failed: 3));

        Assert.Equal(PerformanceIndicatorLevel.Critical, evaluation.Pncp);
        Assert.Equal("Indisponível", evaluation.PncpLabel);
    }

    [Fact]
    public void Telemetry_LiveSnapshotKeepsOnlyDispatcherDelaysInsideWindow()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var telemetry = new AppPerformanceTelemetry(
            new FixedResourceProbe(SystemResourcePressure.Normal),
            clock);
        telemetry.Record("ui", "dispatcher-delay", TimeSpan.FromMilliseconds(40));
        clock.Advance(TimeSpan.FromSeconds(30));
        telemetry.Record("ui", "dispatcher-delay", TimeSpan.FromMilliseconds(200));

        var current = telemetry.GetLiveSnapshot(TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(31));
        var expired = telemetry.GetLiveSnapshot(TimeSpan.FromSeconds(60));

        Assert.Equal(2, current.DispatcherDelaySamples);
        Assert.Equal(TimeSpan.FromMilliseconds(200), current.DispatcherDelayP95);
        Assert.Equal(1, expired.DispatcherDelaySamples);
        Assert.Equal(TimeSpan.FromMilliseconds(200), expired.DispatcherDelayMaximum);
    }

    private static LivePerformanceSnapshot Snapshot(
        SystemResourcePressure pressure,
        PncpSchedulerSnapshot? scheduler = null,
        TimeSpan? dispatcherP95 = null,
        DateTimeOffset? now = null) => new(
        now ?? new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
        SystemResourceProbe.CreateSnapshot(
            16L * 1024 * 1024 * 1024,
            pressure switch
            {
                SystemResourcePressure.Critical => 400L * 1024 * 1024,
                SystemResourcePressure.Constrained => 1024L * 1024 * 1024,
                _ => 4L * 1024 * 1024 * 1024
            },
            pressure == SystemResourcePressure.Critical ? 95 : pressure == SystemResourcePressure.Constrained ? 80 : 50,
            pressure == SystemResourcePressure.Normal ? 8 : 4),
        scheduler ?? Scheduler(8, 0, TimeSpan.FromSeconds(2)),
        dispatcherP95 is null ? 0 : 1,
        dispatcherP95 ?? TimeSpan.Zero,
        dispatcherP95 ?? TimeSpan.Zero);

    private static PncpRecentRequestSnapshot Recent(
        long succeeded = 0,
        long failed = 0,
        long canceled = 0,
        TimeSpan? p95 = null) => new(
        new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
        succeeded + failed + canceled,
        succeeded,
        failed,
        canceled,
        succeeded + failed == 0 ? null : TimeSpan.FromSeconds(1),
        succeeded + failed == 0 ? null : p95 ?? TimeSpan.FromSeconds(2),
        succeeded + failed == 0 ? null : p95 ?? TimeSpan.FromSeconds(2));

    private static PncpSchedulerSnapshot Scheduler(int effective, int queued, TimeSpan p95) => new(
        MaximumConcurrency: 16,
        EffectiveConcurrency: effective,
        ActiveRequests: 0,
        QueuedUserSelectedItems: queued,
        QueuedVisiblePrices: 0,
        QueuedAdditionalBatches: 0,
        QueuedIndexMaintenance: 0,
        QueuedBackgroundPriceCache: 0,
        ActiveBackgroundPriceCache: 0,
        BackgroundSuppressions: 0,
        ConsecutiveSuccesses: 0,
        ConcurrencyReductions: 0,
        GrowthBlockedUntil: null,
        RollingP50: TimeSpan.FromSeconds(1),
        RollingP95: p95,
        RollingThroughput: 2);

    private sealed class FixedResourceProbe(SystemResourcePressure pressure) : ISystemResourceProbe
    {
        public SystemResourceSnapshot GetSnapshot() => Snapshot(pressure).Resources;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
