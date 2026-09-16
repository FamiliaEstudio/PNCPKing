using System.Text.Json;
using System.Text.RegularExpressions;

namespace PNCPKing.Infrastructure.Services;

public sealed record ReleaseFile(string Name, long Size, string Sha256);
public sealed record UpdatePayload(long Size, string Sha256, IReadOnlyList<ReleaseFile> Parts);
public sealed record AppUpdateManifest(int Format, string Version, string Platform, int Schema, ReleaseFile File);
public sealed record PriceUpdatePackage(OfficialUpdateManifest Manifest, long ExpandedSize, UpdatePayload Download);
public sealed record PricesUpdateManifest(int Format, DateTimeOffset PublishedAt, string MinimumAppVersion,
    int Schema, PriceUpdatePackage Base, PriceUpdatePackage? Cumulative);
public sealed record GitHubUpdateRelease<T>(string Tag, T Manifest);
public sealed record GitHubUpdateCheck(GitHubUpdateRelease<AppUpdateManifest>? App,
    GitHubUpdateRelease<PricesUpdateManifest>? Prices, string AppStatus, string PricesStatus);
public sealed record GitHubUpdatePlan(AppUpdateManifest? App, IReadOnlyList<PriceUpdatePackage> Packages,
    bool ReplaceBase, string AppStatus, string PricesStatus)
{
    public bool HasUpdates => App is not null || Packages.Count > 0;
    public long DownloadSize => checked((App?.File.Size ?? 0) + Packages.Sum(p => p.Download.Size));
}
public sealed record UpdateDownloadProgress(string Message, long Received, long Total);

public static class GitHubUpdateValidation
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const long AssetLimit = 2L * 1024 * 1024 * 1024;
    public const long DiskReserve = 512L * 1024 * 1024;

    public static Version ParseVersion(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$",
                RegexOptions.CultureInvariant) || !Version.TryParse(value, out var version))
            throw new InvalidDataException("Versão inválida; use X.Y.Z, sem pré-release.");
        return version;
    }

    public static void ValidateFile(ReleaseFile file)
    {
        if (file is null || string.IsNullOrEmpty(file.Name) || file.Name.Length > 180 ||
            !Regex.IsMatch(file.Name, @"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant) ||
            file.Size <= 0 || file.Size >= AssetLimit || !IsHash(file.Sha256))
            throw new InvalidDataException("Arquivo de atualização inválido.");
    }

    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static void Validate(AppUpdateManifest manifest, string tag)
    {
        if (manifest is null || manifest.Format != 1 || manifest.Platform != "win-x64" || manifest.Schema < 1 ||
            tag != "v" + manifest.Version || manifest.File?.Name != "PNCPKing.exe")
            throw new InvalidDataException("Manifesto do programa incompatível com a release.");
        ParseVersion(manifest.Version);
        ValidateFile(manifest.File);
    }

    public static void Validate(PricesUpdateManifest manifest)
    {
        if (manifest is null || manifest.Format != 1 || manifest.Schema < 1 || manifest.PublishedAt == default ||
            manifest.Base?.Manifest?.InitialBase != true)
            throw new InvalidDataException("Manifesto dos preços inválido.");
        ParseVersion(manifest.MinimumAppVersion);
        ValidatePackage(manifest.Base, manifest.Schema);
        if (manifest.Cumulative is { } delta)
        {
            ValidatePackage(delta, manifest.Schema);
            if (delta.Manifest.InitialBase || delta.Manifest.BaseId != manifest.Base.Manifest.BaseId ||
                delta.Manifest.Origin != manifest.Base.Manifest.Origin ||
                delta.Manifest.Revision < manifest.Base.Manifest.Revision ||
                delta.Manifest.PackageId == manifest.Base.Manifest.PackageId)
                throw new InvalidDataException("Base e cumulativo dos preços são incompatíveis.");
        }
        var files = new[] { manifest.Base, manifest.Cumulative }.OfType<PriceUpdatePackage>()
            .SelectMany(p => p.Download.Parts).ToArray();
        if (files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new InvalidDataException("Nomes de partes repetidos.");
    }

    private static void ValidatePackage(PriceUpdatePackage package, int schema)
    {
        var m = package.Manifest;
        var download = package.Download;
        if (m is null || m.Format != 1 || m.Schema != schema ||
            !Guid.TryParseExact(m.BaseId, "N", out _) || !Guid.TryParseExact(m.Origin, "N", out _) ||
            !Guid.TryParseExact(m.PackageId, "N", out _) || m.Revision < 0 || m.Units < 0 ||
            !IsHash(m.Sha256) || package.ExpandedSize <= 0 || download is null || download.Size <= 0 ||
            !IsHash(download.Sha256) || download.Parts is not { Count: > 0 and <= 1000 })
            throw new InvalidDataException("Pacote de preços inválido.");
        foreach (var part in download.Parts) ValidateFile(part);
        if (download.Parts.Sum(p => p.Size) != download.Size)
            throw new InvalidDataException("Tamanho das partes divergente.");
    }

    public static GitHubUpdatePlan Plan(GitHubUpdateCheck check, Version currentVersion, int currentSchema,
        OfficialTransferStatus state)
    {
        var app = check.App?.Manifest;
        if (app is not null && (ParseVersion(app.Version) <= currentVersion || app.Schema < currentSchema)) app = null;
        var appStatus = $"Programa instalado: {currentVersion.ToString(3)}. " + (app is null
            ? (check.App is null ? check.AppStatus : $"GitHub: {check.App.Manifest.Version}. Nenhuma versão compatível mais recente.")
            : $"Disponível: {app.Version}. Reinício necessário.");
        var packages = new List<PriceUpdatePackage>();
        var replaceBase = false;
        var pricesStatus = check.PricesStatus;
        if (check.Prices?.Manifest is { } prices)
        {
            if (prices.Schema != (app?.Schema ?? currentSchema) ||
                ParseVersion(prices.MinimumAppVersion) > (app is null ? currentVersion : ParseVersion(app.Version)))
                pricesStatus = "Preços indisponíveis: exigem uma versão compatível do programa.";
            else
            {
                replaceBase = state.BaseId is not null && state.BaseId != prices.Base.Manifest.BaseId;
                var needsBase = state.BaseId != prices.Base.Manifest.BaseId || !state.BaseReady;
                if (needsBase) packages.Add(prices.Base);
                if (prices.Cumulative is { } delta && (needsBase || !Completed(delta))) packages.Add(delta);
                pricesStatus = packages.Count == 0 ? "Preços já atualizados neste banco." :
                    $"Preços publicados em {prices.PublishedAt.LocalDateTime:g}. " +
                    (needsBase ? "Inclui a base inicial necessária." : "Pacote cumulativo disponível.");
            }
        }
        return new(app, packages, replaceBase, appStatus, pricesStatus);

        bool Completed(PriceUpdatePackage package)
        {
            if (!state.Imports.TryGetValue(package.Manifest.PackageId, out var receipt)) return false;
            if (!string.Equals(receipt.Checksum, package.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Identidade do pacote reutilizada com conteúdo diferente.");
            return receipt.Completed;
        }
    }
}
