using System.Text.Json;

namespace PNCPKing.App.Services;

public sealed record ColumnLayoutSetting(
    string Key,
    int DisplayIndex,
    bool IsVisible,
    double Width,
    string WidthUnit);

public sealed record AiProviderSetting(
    string Id,
    string DisplayName,
    string Endpoint,
    string Model,
    string Protocol,
    string OutputMode,
    bool IsFree,
    int ContextWindow,
    int MaximumOutputTokens,
    decimal InputCostBrlPerMillion,
    decimal OutputCostBrlPerMillion);

public sealed record AppSettings(
    string DataFolder,
    bool IsConfigured,
    int SettingsVersion = 3,
    Dictionary<string, List<ColumnLayoutSetting>>? ColumnLayouts = null,
    List<AiProviderSetting>? AiProviders = null,
    string? LastAiProviderId = null,
    decimal AiSafetyMarginPercent = 10m);

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public AppSettingsService()
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsPath = Path.Combine(applicationData, "PNCP King", "settings.json");
    }

    public async Task<AppSettings> LoadAsync()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                await using var stream = File.OpenRead(_settingsPath);
                var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream).ConfigureAwait(false);
                if (settings is not null && !string.IsNullOrWhiteSpace(settings.DataFolder))
                {
                    return settings;
                }
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // A tela inicial permitirá escolher novamente a pasta se o arquivo estiver inválido.
            }
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return new AppSettings(Path.Combine(documents, "PNCP King"), false);
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = await LoadAsync().ConfigureAwait(false);
            var updated = update(current);
            await SaveCoreAsync(updated).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task SaveCoreAsync(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = _settingsPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
