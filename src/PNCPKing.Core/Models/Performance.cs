namespace PNCPKing.Core.Models;

public sealed record PerformanceMeasurement
{
    public required string Operation { get; init; }
    public required string Phase { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required TimeSpan Duration { get; init; }
    public long Rows { get; init; }
    public long Bytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public bool Succeeded { get; init; }
    public string ErrorKind { get; init; } = string.Empty;
}

public sealed record PerformanceOperationSummary
{
    public required string Operation { get; init; }
    public required string Phase { get; init; }
    public required int Samples { get; init; }
    public required double MedianMilliseconds { get; init; }
    public required double P95Milliseconds { get; init; }
    public required double MaximumMilliseconds { get; init; }
    public required long TotalRows { get; init; }
    public required long TotalBytes { get; init; }
    public required long PeakWorkingSetBytes { get; init; }
}

public sealed record PerformanceReport
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string ApplicationVersion { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Framework { get; init; }
    public required int LogicalProcessors { get; init; }
    public required long AvailableMemoryBytes { get; init; }
    public required long DatabaseBytes { get; init; }
    public required long WalBytes { get; init; }
    public required IReadOnlyList<PerformanceMeasurement> Measurements { get; init; }
    public required IReadOnlyList<PerformanceOperationSummary> Summaries { get; init; }
    public string BaselineApplicationVersion { get; init; } = string.Empty;
    public IReadOnlyList<PerformanceComparison> Comparisons { get; init; } = [];
}

public sealed record PerformanceComparison
{
    public required string Operation { get; init; }
    public required string Phase { get; init; }
    public required double BaselineMedianMilliseconds { get; init; }
    public required double CurrentMedianMilliseconds { get; init; }
    public required double BaselineP95Milliseconds { get; init; }
    public required double CurrentP95Milliseconds { get; init; }
    public required long BaselinePeakWorkingSetBytes { get; init; }
    public required long CurrentPeakWorkingSetBytes { get; init; }
    public required double MedianImprovementPercent { get; init; }
    public required double P95ImprovementPercent { get; init; }
    public required double ThroughputImprovementPercent { get; init; }
}
