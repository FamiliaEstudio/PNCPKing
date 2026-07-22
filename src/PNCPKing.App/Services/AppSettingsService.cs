using System.Text.Json;

namespace PNCPKing.App.Services;

public sealed record AppSettings(string DataFolder, bool IsConfigured);

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

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
            catch (JsonException)
            {
                // A tela inicial permitirá escolher novamente a pasta se o arquivo estiver inválido.
            }
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return new AppSettings(Path.Combine(documents, "PNCP King"), false);
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions).ConfigureAwait(false);
    }
}
