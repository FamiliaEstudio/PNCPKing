namespace PNCPKing.Core.Models;

public enum SystemResourcePressure
{
    Normal = 0,
    Constrained = 1,
    Critical = 2
}

public sealed record SystemResourceSnapshot
{
    public long TotalPhysicalMemoryBytes { get; init; }
    public long AvailablePhysicalMemoryBytes { get; init; }
    public int MemoryLoadPercent { get; init; }
    public int LogicalProcessors { get; init; } = Environment.ProcessorCount;
    public SystemResourcePressure Pressure { get; init; }
}

public sealed record DatabaseInitializationProgress
{
    public int PreviousVersion { get; init; }
    public int TargetVersion { get; init; }
    public int Percentage { get; init; }
    public string Phase { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed record DatabaseInitializationResult
{
    public int PreviousVersion { get; init; }
    public int CurrentVersion { get; init; }
    public IReadOnlyList<int> AppliedMigrations { get; init; } = [];
    public TimeSpan Duration { get; init; }
}
