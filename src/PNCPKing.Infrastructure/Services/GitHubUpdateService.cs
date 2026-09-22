using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNCPKing.Infrastructure.Services;

/// <summary>Only release assets in the public, fixed repository are used. No remote code or download URLs
/// are accepted from manifests. Large payloads are streamed and complete parts can be reused.</summary>
public sealed class GitHubUpdateService(HttpClient client)
{
    public const string Repository = "FamiliaEstudio/PNCPKing";
    private const int MetadataLimit = 1024 * 1024;

    public async Task<GitHubUpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var app = ReadChannelAsync<AppUpdateManifest>("latest", "app-update.json", cancellationToken);
        var prices = ReadChannelAsync<PricesUpdateManifest>("tags/precos", "prices-update.json", cancellationToken);
        await Task.WhenAll(app, prices).ConfigureAwait(false);
        return new(app.Result.Release, prices.Result.Release,
            app.Result.Error ?? "Programa disponível.", prices.Result.Error ?? "Preços disponíveis.");
    }

    private async Task<(GitHubUpdateRelease<T>? Release, string? Error)> ReadChannelAsync<T>(
        string channel, string manifestName, CancellationToken ct)
    {
        var label = typeof(T) == typeof(AppUpdateManifest) ? "Programa" : "Preços";
        try
        {
            using var response = await SendAsync($"https://api.github.com/repos/{Repository}/releases/{channel}", ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return (null, $"{label}: nenhuma publicação disponível.");
            CheckResponse(response);
            var release = await ReadJsonAsync<Release>(response, ct).ConfigureAwait(false);
            if (release.Draft || release.Prerelease || string.IsNullOrEmpty(release.Tag) || release.Assets is null)
                return (null, $"{label}: nenhuma release estável disponível.");
            if (release.Assets.Any(a => a is null)) throw new InvalidDataException("Lista de anexos inválida.");
            if (typeof(T) == typeof(PricesUpdateManifest) && release.Tag != "precos")
                throw new InvalidDataException("Tag dos preços inválida.");
            var metadata = release.Assets.SingleOrDefault(a => a.Name == manifestName)
                ?? throw new InvalidDataException($"Falta o arquivo {manifestName} na release.");
            if (metadata.Size <= 0 || metadata.Size > MetadataLimit) throw new InvalidDataException("Manifesto excede o tamanho permitido.");
            using var manifestResponse = await SendAsync(AssetUrl(release.Tag, manifestName), ct).ConfigureAwait(false);
            CheckResponse(manifestResponse);
            var manifest = await ReadJsonAsync<T>(manifestResponse, ct).ConfigureAwait(false);
            IEnumerable<ReleaseFile> files;
            if (manifest is AppUpdateManifest app)
            {
                GitHubUpdateValidation.Validate(app, release.Tag);
                files = [app.File];
            }
            else if (manifest is PricesUpdateManifest prices)
            {
                GitHubUpdateValidation.Validate(prices);
                files = prices.Update.Download.Parts;
            }
            else throw new InvalidDataException("Tipo de manifesto desconhecido.");
            foreach (var file in files)
            {
                var asset = release.Assets.SingleOrDefault(a => a.Name == file.Name);
                if (asset is null || asset.Size != file.Size || asset.State != "uploaded")
                    throw new InvalidDataException($"Arquivo ausente ou incompleto: {file.Name}.");
                if (asset.Digest is { Length: > 0 } &&
                    !string.Equals(asset.Digest, "sha256:" + file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Hash publicado divergente: {file.Name}.");
            }
            return (new(release.Tag, manifest), null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or
            OverflowException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (null, $"{label} indisponíveis: {e.Message}");
        }
    }

    public async Task<string> DownloadAsync(string tag, UpdatePayload payload, string cacheDirectory,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!GitHubUpdateValidation.IsHash(payload.Sha256) || payload.Parts is not { Count: 1 } ||
            payload.Size <= 0 || payload.Size >= GitHubUpdateValidation.AssetLimit ||
            payload.Parts[0].Size != payload.Size ||
            !string.Equals(payload.Parts[0].Sha256, payload.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Descrição do download inválida.");
        var part = payload.Parts[0];
        GitHubUpdateValidation.ValidateFile(part);
        Directory.CreateDirectory(cacheDirectory);
        var complete = Path.Combine(cacheDirectory, payload.Sha256.ToLowerInvariant() + ".payload");
        var marker = complete + ".verified";
        if (File.Exists(complete) && new FileInfo(complete).Length == payload.Size && File.Exists(marker) &&
            string.Equals((await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false)).Trim(),
                payload.Sha256, StringComparison.OrdinalIgnoreCase))
            return complete;

        EnsureSpace(cacheDirectory, payload.Size);
        var temporary = complete + ".partial";
        try
        {
            using var response = await SendAsync(AssetUrl(tag, part.Name), cancellationToken).ConfigureAwait(false);
            CheckResponse(response);
            if (response.Content.Headers.ContentLength is { } length && length != payload.Size)
                throw new InvalidDataException($"Tamanho divergente: {part.Name}.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long received = 0;
            var lastReport = Environment.TickCount64;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                received += read;
                if (received > payload.Size)
                    throw new InvalidDataException($"Arquivo maior que o anunciado: {part.Name}.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (Environment.TickCount64 - lastReport >= 100)
                {
                    progress?.Report(new($"Baixando {part.Name}…", received, payload.Size));
                    lastReport = Environment.TickCount64;
                }
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (received != payload.Size || !string.Equals(actual, payload.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Arquivo incompleto ou SHA-256 divergente: {part.Name}.");
            output.Close();
            File.Move(temporary, complete, overwrite: true);
            await File.WriteAllTextAsync(marker, payload.Sha256, cancellationToken).ConfigureAwait(false);
            progress?.Report(new($"Validado durante o download: {part.Name}.", received, payload.Size));
            return complete;
        }
        finally { File.Delete(temporary); }
    }

    public static async Task ValidatePackageAsync(string path, PriceUpdatePackage expected, CancellationToken ct = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected.Download.Size)
            throw new InvalidDataException("Arquivo de preços incompleto ou alterado.");
        var actual = await OfficialUpdateService.ReadManifestAsync(path, ct).ConfigureAwait(false);
        var actualJson = JsonSerializer.SerializeToUtf8Bytes(actual.Manifest, GitHubUpdateValidation.Json);
        var expectedJson = JsonSerializer.SerializeToUtf8Bytes(expected.Manifest, GitHubUpdateValidation.Json);
        if (!actualJson.AsSpan().SequenceEqual(expectedJson) || actual.ExpandedSize != expected.ExpandedSize)
            throw new InvalidDataException("O manifesto interno do pacote diverge da publicação de preços.");
    }

    public static async Task<bool> VerifyAsync(string path, long size, string hash, CancellationToken ct = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
        return string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureSpace(string directory, long bytes)
    {
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        if (bytes < 0 || free - GitHubUpdateValidation.DiskReserve < bytes)
            throw new IOException("Espaço insuficiente para baixar, recompor ou validar a atualização.");
    }

    private static string AssetUrl(string tag, string name) =>
        $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PNCPKing-Updater/1.2");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            request.RequestUri!.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("O GitHub limitou as consultas. Tente novamente mais tarde.");
        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MetadataLimit) throw new InvalidDataException("Metadados excedem o tamanho permitido.");
            output.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<T>(output.ToArray(), GitHubUpdateValidation.Json)
            ?? throw new InvalidDataException("Manifesto ausente.");
    }

    private sealed record Release([property: JsonPropertyName("tag_name")] string Tag,
        bool Draft, bool Prerelease, List<Asset> Assets);
    private sealed record Asset(string Name, long Size, string State, string? Digest);
}
