using System.Text.Json;
using PNCPKing.Core.Models;

namespace PNCPKing.Guard;

internal sealed record GuardSettings
{
    public string? WorkerId { get; init; }
    public string? WorkerName { get; init; }
    public string? PlanPath { get; init; }
    public string? DriveRoot { get; init; }
    public bool ScheduleEnabled { get; init; }
}

internal sealed class GuardSettingsService
{
    private readonly string _applicationFolder;
    private readonly string _path;

    public GuardSettingsService()
    {
        _applicationFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PNCP Guard");
        _path = Path.Combine(_applicationFolder, "settings.json");
    }

    public string ApplicationFolder => _applicationFolder;
    public string DatabasePath => Path.Combine(_applicationFolder, "guard.db");
    public string OutboxFolder => Path.Combine(_applicationFolder, "outbox");
    public string LogPath => Path.Combine(_applicationFolder, "guard.log");

    public async Task<GuardSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new GuardSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<GuardSettings>(
                       stream,
                       GuardFormat.JsonOptions,
                       cancellationToken)
                       .ConfigureAwait(false)
                   ?? new GuardSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new GuardSettings();
        }
    }

    public async Task SaveAsync(GuardSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_applicationFolder);
        await GuardFileCodec.WriteJsonAtomicAsync(_path, settings, overwrite: true, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class GuardLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public GuardLog(string path) => _path = path;

    public void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            lock (_gate)
            {
                File.AppendAllText(
                    _path,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // O log não deve interromper a coleta.
        }
    }
}
