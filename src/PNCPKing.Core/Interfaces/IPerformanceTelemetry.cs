using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IPerformanceTelemetry
{
    PerformanceSpan Begin(string operation, string phase = "total");

    void Record(
        string operation,
        string phase,
        TimeSpan duration,
        long rows = 0,
        long bytes = 0,
        bool succeeded = true,
        string? errorKind = null);

    PerformanceReport CreateReport();
}
public sealed class PerformanceSpan : IDisposable
{
    private readonly IPerformanceTelemetry _owner;
    private readonly string _operation;
    private readonly string _phase;
    private readonly long _startedTimestamp;
    private readonly Action? _onCompleted;
    private int _completed;

    public PerformanceSpan(
        IPerformanceTelemetry owner,
        string operation,
        string phase,
        Action? onCompleted = null)
    {
        _owner = owner;
        _operation = operation;
        _phase = phase;
        _onCompleted = onCompleted;
        _startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public void Complete(long rows = 0, long bytes = 0) =>
        CompleteCore(rows, bytes, succeeded: true, errorKind: null);

    public void Fail(Exception exception, long rows = 0, long bytes = 0)
    {
        ArgumentNullException.ThrowIfNull(exception);
        CompleteCore(rows, bytes, succeeded: false, exception.GetType().Name);
    }

    public void Dispose() => Complete();

    private void CompleteCore(
        long rows,
        long bytes,
        bool succeeded,
        string? errorKind)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        try
        {
            _owner.Record(
                _operation,
                _phase,
                System.Diagnostics.Stopwatch.GetElapsedTime(_startedTimestamp),
                rows,
                bytes,
                succeeded,
                errorKind);
        }
        finally
        {
            _onCompleted?.Invoke();
        }
    }
}

public sealed class NullPerformanceTelemetry : IPerformanceTelemetry
{
    public static NullPerformanceTelemetry Instance { get; } = new();

    private NullPerformanceTelemetry()
    {
    }

    public PerformanceSpan Begin(string operation, string phase = "total") =>
        new(this, operation, phase);

    public void Record(
        string operation,
        string phase,
        TimeSpan duration,
        long rows = 0,
        long bytes = 0,
        bool succeeded = true,
        string? errorKind = null)
    {
    }

    public PerformanceReport CreateReport() => new()
    {
        GeneratedAt = DateTimeOffset.UtcNow,
        ApplicationVersion = string.Empty,
        OperatingSystem = string.Empty,
        Framework = string.Empty,
        LogicalProcessors = 0,
        AvailableMemoryBytes = 0,
        DatabaseBytes = 0,
        WalBytes = 0,
        Measurements = [],
        Summaries = []
    };
}
