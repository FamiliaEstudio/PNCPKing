using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace PNCPKing.Infrastructure.Api;

public enum PncpRequestCategory
{
    Contracts,
    ItemLists,
    ItemResults,
    Other
}

public enum PncpRequestOutcome
{
    Succeeded,
    Failed,
    Canceled
}

public sealed record PncpRequestCategorySnapshot(
    long Calls,
    long Succeeded,
    long Failed,
    long Canceled,
    long BytesReceived,
    TimeSpan TotalDuration,
    TimeSpan TotalQueueDuration)
{
    public double AverageBytesPerCall => Calls == 0 ? 0 : (double)BytesReceived / Calls;

    public TimeSpan AverageDuration => Calls == 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(TotalDuration.Ticks / Calls);

    public TimeSpan AverageQueueDuration => Calls == 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(TotalQueueDuration.Ticks / Calls);
}

public sealed record PncpRequestTelemetrySnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<PncpRequestCategory, PncpRequestCategorySnapshot> Categories)
{
    public PncpRequestCategorySnapshot this[PncpRequestCategory category] => Categories[category];

    public long TotalCalls => Categories.Values.Sum(item => item.Calls);
    public long TotalBytesReceived => Categories.Values.Sum(item => item.BytesReceived);
    public TimeSpan TotalDuration => TimeSpan.FromTicks(Categories.Values.Sum(item => item.TotalDuration.Ticks));
}

public sealed record PncpRecentRequestSnapshot(
    DateTimeOffset CapturedAt,
    long Calls,
    long Succeeded,
    long Failed,
    long Canceled,
    TimeSpan? P50,
    TimeSpan? P95,
    TimeSpan? Maximum);

public interface IPncpRequestTelemetry
{
    PncpRequestTelemetrySnapshot GetSnapshot();

    PncpRecentRequestSnapshot GetRecentSnapshot(TimeSpan window);
}

/// <summary>
/// Lock-free counters for every actual HTTP attempt, including retries.
/// </summary>
public sealed class PncpRequestTelemetry : IPncpRequestTelemetry
{
    private const int MaximumRecentSamples = 4_096;
    private static readonly TimeSpan RecentSampleRetention = TimeSpan.FromMinutes(2);
    private readonly CategoryCounters[] _counters =
        Enumerable.Range(0, 4).Select(_ => new CategoryCounters()).ToArray();
    private readonly ConcurrentQueue<RecentRequestSample> _recentSamples = new();
    private readonly TimeProvider _timeProvider;

    public PncpRequestTelemetry(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public PncpRequestTelemetrySnapshot GetSnapshot()
    {
        var values = Enum.GetValues<PncpRequestCategory>()
            .ToDictionary(category => category, category => _counters[(int)category].Snapshot());
        return new PncpRequestTelemetrySnapshot(
            _timeProvider.GetUtcNow(),
            new ReadOnlyDictionary<PncpRequestCategory, PncpRequestCategorySnapshot>(values));
    }

    public PncpRecentRequestSnapshot GetRecentSnapshot(TimeSpan window)
    {
        if (window <= TimeSpan.Zero || window > RecentSampleRetention)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        var now = _timeProvider.GetUtcNow();
        TrimRecentSamples(now);
        var cutoff = now - window;
        var samples = _recentSamples
            .Where(sample => sample.CompletedAt >= cutoff)
            .ToArray();
        var durations = samples
            .Where(sample => sample.Outcome != PncpRequestOutcome.Canceled)
            .Select(sample => sample.Duration)
            .OrderBy(duration => duration)
            .ToArray();
        return new PncpRecentRequestSnapshot(
            now,
            samples.LongLength,
            samples.LongCount(sample => sample.Outcome == PncpRequestOutcome.Succeeded),
            samples.LongCount(sample => sample.Outcome == PncpRequestOutcome.Failed),
            samples.LongCount(sample => sample.Outcome == PncpRequestOutcome.Canceled),
            Percentile(durations, 0.50),
            Percentile(durations, 0.95),
            durations.Length == 0 ? null : durations[^1]);
    }

    internal Measurement Begin(PncpRequestCategory category)
    {
        if ((int)category is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return new Measurement(this, _counters[(int)category]);
    }

    private void RecordRecent(
        TimeSpan duration,
        PncpRequestOutcome outcome)
    {
        var now = _timeProvider.GetUtcNow();
        _recentSamples.Enqueue(new RecentRequestSample(now, duration, outcome));
        TrimRecentSamples(now);
    }

    private void TrimRecentSamples(DateTimeOffset now)
    {
        var cutoff = now - RecentSampleRetention;
        while (_recentSamples.TryPeek(out var sample) &&
               (sample.CompletedAt < cutoff || _recentSamples.Count > MaximumRecentSamples))
        {
            _recentSamples.TryDequeue(out _);
        }
    }

    private static TimeSpan? Percentile(IReadOnlyList<TimeSpan> ordered, double percentile)
    {
        if (ordered.Count == 0)
        {
            return null;
        }

        var index = Math.Clamp((int)Math.Ceiling(ordered.Count * percentile) - 1, 0, ordered.Count - 1);
        return ordered[index];
    }

    internal sealed class Measurement(
        PncpRequestTelemetry owner,
        CategoryCounters counters)
    {
        private readonly long _queuedAt = Stopwatch.GetTimestamp();
        private long _startedAt;
        private int _dispatched;
        private int _completed;

        public void MarkDispatched()
        {
            if (Interlocked.Exchange(ref _dispatched, 1) != 0)
            {
                return;
            }

            var startedAt = Stopwatch.GetTimestamp();
            Volatile.Write(ref _startedAt, startedAt);
            Interlocked.Increment(ref counters.Calls);
            Interlocked.Add(
                ref counters.QueueDurationTicks,
                Stopwatch.GetElapsedTime(_queuedAt, startedAt).Ticks);
        }

        public void AddBytes(long count)
        {
            if (count > 0)
            {
                Interlocked.Add(ref counters.BytesReceived, count);
            }
        }

        public void Complete(PncpRequestOutcome outcome)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0 || Volatile.Read(ref _dispatched) == 0)
            {
                return;
            }

            var startedAt = Volatile.Read(ref _startedAt);
            var duration = Stopwatch.GetElapsedTime(startedAt, Stopwatch.GetTimestamp());
            Interlocked.Add(ref counters.DurationTicks, duration.Ticks);
            switch (outcome)
            {
                case PncpRequestOutcome.Succeeded:
                    Interlocked.Increment(ref counters.Succeeded);
                    break;
                case PncpRequestOutcome.Failed:
                    Interlocked.Increment(ref counters.Failed);
                    break;
                case PncpRequestOutcome.Canceled:
                    Interlocked.Increment(ref counters.Canceled);
                    break;
            }

            owner.RecordRecent(duration, outcome);
        }
    }

    internal sealed class CategoryCounters
    {
        public long Calls;
        public long Succeeded;
        public long Failed;
        public long Canceled;
        public long BytesReceived;
        public long DurationTicks;
        public long QueueDurationTicks;

        public PncpRequestCategorySnapshot Snapshot() => new(
            Interlocked.Read(ref Calls),
            Interlocked.Read(ref Succeeded),
            Interlocked.Read(ref Failed),
            Interlocked.Read(ref Canceled),
            Interlocked.Read(ref BytesReceived),
            TimeSpan.FromTicks(Interlocked.Read(ref DurationTicks)),
            TimeSpan.FromTicks(Interlocked.Read(ref QueueDurationTicks)));
    }

    private sealed record RecentRequestSample(
        DateTimeOffset CompletedAt,
        TimeSpan Duration,
        PncpRequestOutcome Outcome);
}
