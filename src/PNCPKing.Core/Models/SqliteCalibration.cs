namespace PNCPKing.Core.Models;

public sealed record SqliteSearchTuning(int CacheMiB, bool OrderedItemBatches = false)
{
    public bool IsValid => CacheMiB is 32 or 64 or 96;
    public string Name => CacheMiB switch { 32 => "Conservadora", 64 => "Média", _ => "Maior cache" };
    public string Description => $"{Name} — {CacheMiB} MiB por conexão";

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
    int Version = 3,
    ResourceUsageProfile ResourceProfile = ResourceUsageProfile.Automatic)
{
    public const int CurrentVersion = 3;

    public bool AppliesTo(string path, DateTime createdUtc, int schemaVersion, SystemResourceSnapshot resources,
        ResourceUsageProfile resourceProfile = ResourceUsageProfile.Automatic) =>
        Version == CurrentVersion && ResourceProfile == resourceProfile && Tuning is { IsValid: true } &&
        string.Equals(DatabasePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
        DatabaseCreatedUtc == createdUtc && SchemaVersion == schemaVersion &&
        PhysicalMemoryBytes == resources.TotalPhysicalMemoryBytes && LogicalProcessors == resources.LogicalProcessors;
}
