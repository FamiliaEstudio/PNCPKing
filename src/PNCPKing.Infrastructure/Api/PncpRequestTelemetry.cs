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

public sealed record PncpRequestCategorySnapshot(
    long Calls,
    long Succeeded,
    long Failed,
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

public interface IPncpRequestTelemetry
{
    PncpRequestTelemetrySnapshot GetSnapshot();
}

/// <summary>
/// Lock-free counters for every actual HTTP attempt, including retries.
/// </summary>
public sealed class PncpRequestTelemetry : IPncpRequestTelemetry
{
    private readonly CategoryCounters[] _counters =
        Enumerable.Range(0, 4).Select(_ => new CategoryCounters()).ToArray();

    public PncpRequestTelemetrySnapshot GetSnapshot()
    {
        var values = Enum.GetValues<PncpRequestCategory>()
            .ToDictionary(category => category, category => _counters[(int)category].Snapshot());
        return new PncpRequestTelemetrySnapshot(
            DateTimeOffset.Now,
            new ReadOnlyDictionary<PncpRequestCategory, PncpRequestCategorySnapshot>(values));
    }

    internal Measurement Begin(PncpRequestCategory category)
    {
        if ((int)category is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return new Measurement(_counters[(int)category]);
    }

    internal sealed class Measurement(CategoryCounters counters)
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

        public void Complete(bool succeeded)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0 || Volatile.Read(ref _dispatched) == 0)
            {
                return;
            }

            var startedAt = Volatile.Read(ref _startedAt);
            Interlocked.Add(
                ref counters.DurationTicks,
                Stopwatch.GetElapsedTime(startedAt, Stopwatch.GetTimestamp()).Ticks);
            if (succeeded)
            {
                Interlocked.Increment(ref counters.Succeeded);
            }
            else
            {
                Interlocked.Increment(ref counters.Failed);
            }
        }
    }

    internal sealed class CategoryCounters
    {
        public long Calls;
        public long Succeeded;
        public long Failed;
        public long BytesReceived;
        public long DurationTicks;
        public long QueueDurationTicks;

        public PncpRequestCategorySnapshot Snapshot() => new(
            Interlocked.Read(ref Calls),
            Interlocked.Read(ref Succeeded),
            Interlocked.Read(ref Failed),
            Interlocked.Read(ref BytesReceived),
            TimeSpan.FromTicks(Interlocked.Read(ref DurationTicks)),
            TimeSpan.FromTicks(Interlocked.Read(ref QueueDurationTicks)));
    }
}
