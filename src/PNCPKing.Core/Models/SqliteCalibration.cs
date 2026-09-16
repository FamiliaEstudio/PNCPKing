namespace PNCPKing.Core.Models;

public sealed record SqliteSearchTuning(int CacheMiB, bool OrderedItemBatches = false)
{
    public bool IsValid => CacheMiB is 32 or 64 or 96;
    public string Name => CacheMiB switch { 32 => "Conservadora", 64 => "Média", _ => "Maior cache" };
    public string Description => $"{Name} — {CacheMiB} MiB por conexão" +
        (OrderedItemBatches ? "; entrega progressiva" : "; seleção da página completa");

    public bool FitsMemory(SystemResourceSnapshot resources) => IsValid &&
        resources.Pressure != SystemResourcePressure.Critical &&
        4L * CacheMiB * 1024 * 1024 <= Math.Min(512L * 1024 * 1024, resources.AvailablePhysicalMemoryBytes / 4);
}

public sealed record SavedSqliteCalibration(
    string DatabasePath,
    DateTime DatabaseCreatedUtc,
    int SchemaVersion,
    long PhysicalMemoryBytes,
    int LogicalProcessors,
    SqliteSearchTuning Tuning,
    int Version = 2)
{
    public const int CurrentVersion = 2;

    public bool AppliesTo(string path, DateTime createdUtc, int schemaVersion, SystemResourceSnapshot resources) =>
        Version == CurrentVersion && Tuning is { IsValid: true } &&
        string.Equals(DatabasePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
        DatabaseCreatedUtc == createdUtc && SchemaVersion == schemaVersion &&
        PhysicalMemoryBytes == resources.TotalPhysicalMemoryBytes && LogicalProcessors == resources.LogicalProcessors;
}
