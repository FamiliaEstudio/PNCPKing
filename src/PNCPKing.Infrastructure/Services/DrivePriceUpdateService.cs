using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PNCPKing.Infrastructure.Services;

public sealed record DrivePriceFile(string Id, string Name, long Size, long ModifiedTime)
{
    public override string ToString() => $"{Name} ({Size / (1024d * 1024 * 1024):N2} GiB; " +
        $"modificado em {DateTimeOffset.FromUnixTimeMilliseconds(ModifiedTime).LocalDateTime:g})";
}

/// <summary>Anonymous, read-only access to the fixed public price folder. Only downloads archives;
/// BackupService remains responsible for inspection, validation and preserving local data.</summary>
public sealed class DrivePriceUpdateService(HttpClient client)
{
    public const string FolderId = "1g8N6-G7EnO2Ah_cF8-DDCbLpu2EuTe6g";
    public const string FolderUrl = "https://drive.google.com/drive/folders/" + FolderId;
    private const int MetadataLimit = 4 * 1024 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // Redirects are followed explicitly so a login page or an unrelated host is never used.
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(30)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<IReadOnlyList<DrivePriceFile>> ListAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(new Uri(FolderUrl), ct).ConfigureAwait(false);
        CheckResponse(response);
        return ParseListing(await ReadMetadataAsync(response, ct).ConfigureAwait(false));
    }

    public static IReadOnlyList<DrivePriceFile> ParseListing(string html)
    {
        var match = Regex.Match(html, @"window\['_DRIVE_ivd'\]\s*=\s*'((?:\\.|[^'\\])*)'",
            RegexOptions.CultureInvariant, RegexTimeout);
        if (!match.Success)
            throw new InvalidDataException("Não foi possível ler a pasta pública do Google Drive. Confira o acesso pelo link ou importe o arquivo manualmente.");
        // Decode a JavaScript string as data, never execute the page's scripts.
        var literal = Regex.Replace(match.Groups[1].Value, @"\\x([0-9a-fA-F]{2})", "\\u00$1",
            RegexOptions.CultureInvariant, RegexTimeout).Replace("\\'", "'", StringComparison.Ordinal);
        var json = JsonSerializer.Deserialize<string>("\"" + literal + "\"")
            ?? throw new InvalidDataException("Listagem vazia do Google Drive.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1 ||
            root[0].ValueKind != JsonValueKind.Array || root[0].GetArrayLength() >= 50)
            throw new InvalidDataException("A listagem do Google Drive mudou ou está incompleta. Use a importação manual.");
        var files = new List<DrivePriceFile>();
        foreach (var row in root[0].EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 14 || row[2].ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Formato inesperado na listagem do Google Drive.");
            var name = row[2].GetString()!;
            if (!name.EndsWith(".pncpking", StringComparison.OrdinalIgnoreCase)) continue;
            if (row[0].ValueKind != JsonValueKind.String || row[1].ValueKind != JsonValueKind.Array ||
                !row[1].EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == FolderId) ||
                row[3].ValueKind != JsonValueKind.String || row[3].GetString() == "application/vnd.google-apps.folder" ||
                row[13].ValueKind != JsonValueKind.Number || row[10].ValueKind != JsonValueKind.Number ||
                !row[13].TryGetInt64(out var size) || !row[10].TryGetInt64(out var modified))
                throw new InvalidDataException("Metadados incompletos do pacote de preços no Google Drive.");
            var file = new DrivePriceFile(row[0].GetString()!, name, size, modified);
            Validate(file);
            if (files.Any(f => f.Id == file.Id)) throw new InvalidDataException("Arquivo duplicado na listagem do Google Drive.");
            files.Add(file);
        }
        return files.OrderByDescending(f => f.ModifiedTime).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void Validate(DrivePriceFile file)
    {
        if (file is null || !Regex.IsMatch(file.Id ?? "", @"^[A-Za-z0-9_-]{10,200}$", RegexOptions.CultureInvariant, RegexTimeout) ||
            string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 255 ||
            file.Name.Any(char.IsControl) || !file.Name.EndsWith(".pncpking", StringComparison.OrdinalIgnoreCase) ||
            file.Size < 4 || file.ModifiedTime <= 0 || file.ModifiedTime > 253402300799999L)
            throw new InvalidDataException("Descrição inválida do pacote do Google Drive.");
    }

    public async Task<string> DownloadAsync(DrivePriceFile file, string cacheDirectory,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Validate(file);
        // Recheck membership/identity after the preview or a program restart. Do not silently use a newer file.
        if (!(await ListAsync(ct).ConfigureAwait(false)).Contains(file))
            throw new IOException("O pacote mudou ou saiu da pasta do Google Drive. Consulte novamente as atualizações.");
        Directory.CreateDirectory(cacheDirectory);
        // Remote names never become paths; identity includes the size and modified version.
        var complete = Path.Combine(cacheDirectory,
            $"{file.Id}-{file.ModifiedTime.ToString(CultureInfo.InvariantCulture)}-{file.Size.ToString(CultureInfo.InvariantCulture)}.pncpking");
        ct.ThrowIfCancellationRequested();
        if (File.Exists(complete) && new FileInfo(complete).Length == file.Size)
        {
            PruneOldDownloads(cacheDirectory, complete);
            progress?.Report(new("Usando o pacote já baixado; o importador fará as verificações habituais…", file.Size, file.Size));
            return complete;
        }
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cacheDirectory))!);
        if (drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && file.Size > uint.MaxValue)
            throw new IOException("O pacote excede o limite de arquivo do FAT32. Use um disco NTFS ou exFAT para o download.");
        GitHubUpdateService.EnsureSpace(cacheDirectory, file.Size);
        var temporary = complete + ".partial";
        try
        {
            using var response = await OpenDownloadAsync(file, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml" ||
                response.Content.Headers.ContentLength is { } length && length != file.Size)
                throw new InvalidDataException("O Google Drive não retornou o pacote completo esperado. Nenhum banco foi alterado.");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long received = 0;
            var lastReport = Environment.TickCount64;
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Check the signature before accepting a body with an incorrect content type (login/quota pages).
            idleTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            await input.ReadExactlyAsync(buffer.AsMemory(0, 4), idleTimeout.Token).ConfigureAwait(false);
            if (!buffer.AsSpan(0, 4).SequenceEqual("PK\x03\x04"u8))
                throw new InvalidDataException("A resposta do Google Drive não é um arquivo .pncpking. Use a importação manual se o acesso exigir login.");
            await output.WriteAsync(buffer.AsMemory(0, 4), ct).ConfigureAwait(false);
            received = 4;
            while (true)
            {
                idleTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                var read = await input.ReadAsync(buffer, idleTimeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                received = checked(received + read);
                if (received > file.Size) throw new InvalidDataException("O pacote baixado é maior que o anunciado no Google Drive.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (Environment.TickCount64 - lastReport >= 200)
                {
                    progress?.Report(new($"Baixando {file.Name}…", received, file.Size));
                    lastReport = Environment.TickCount64;
                }
            }
            if (received != file.Size) throw new InvalidDataException("Download incompleto. Tente novamente; o banco ativo foi preservado.");
            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Close();
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, complete, overwrite: true);
            PruneOldDownloads(cacheDirectory, complete);
            progress?.Report(new($"Download concluído: {file.Name}.", received, file.Size));
            return complete;
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new IOException("O Google Drive parou de responder. Tente novamente; nenhum banco foi alterado.", e);
        }
        finally { File.Delete(temporary); }
    }

    private static void PruneOldDownloads(string cacheDirectory, string current)
    {
        // Keep one completed retry package, not a new multi-GB archive for every publication.
        // Only our generated names in this private cache are eligible. Never delete user-named backups.
        try
        {
            foreach (var path in Directory.EnumerateFiles(cacheDirectory))
            {
                if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(Path.GetFileName(path), @"^[A-Za-z0-9_-]{10,200}-[0-9]+-[0-9]+\.pncpking(?:\.partial)?$",
                        RegexOptions.CultureInvariant, RegexTimeout)) continue;
                try { File.Delete(path); }
                catch (IOException) { /* An in-use cache must not prevent a completed download from importing. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(DrivePriceFile file, CancellationToken ct)
    {
        var response = await SendAsync(new Uri($"https://drive.google.com/uc?export=download&id={file.Id}"), ct).ConfigureAwait(false);
        try
        {
            CheckResponse(response);
            if (response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml")) return response;
            var html = await ReadMetadataAsync(response, ct).ConfigureAwait(false);
            var uri = ParseConfirmation(html, file.Id);
            response.Dispose();
            response = await SendAsync(uri, ct).ConfigureAwait(false);
            CheckResponse(response);
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    public static Uri ParseConfirmation(string html, string fileId)
    {
        foreach (Match form in Regex.Matches(html, @"<form\b(?<attributes>[^>]*)>(?<body>.*?)</form>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout))
        {
            var attributes = Attributes(form.Groups["attributes"].Value);
            if (attributes.GetValueOrDefault("id") != "download-form") continue;
            if (attributes.GetValueOrDefault("method") is not { } method || !method.Equals("get", StringComparison.OrdinalIgnoreCase) ||
                attributes.GetValueOrDefault("action") != "https://drive.usercontent.google.com/download") break;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match input in Regex.Matches(form.Groups["body"].Value, @"<input\b([^>]*)>", RegexOptions.IgnoreCase, RegexTimeout))
            {
                var values = Attributes(input.Groups[1].Value);
                if (values.GetValueOrDefault("type") != "hidden") continue;
                var name = values.GetValueOrDefault("name");
                if (name is not ("id" or "export" or "confirm" or "uuid")) continue;
                if (!fields.TryAdd(name, values.GetValueOrDefault("value") ?? ""))
                    throw new InvalidDataException("Confirmação de download ambígua.");
            }
            if (fields.GetValueOrDefault("id") != fileId || fields.GetValueOrDefault("export") != "download" ||
                fields.GetValueOrDefault("confirm") != "t" || string.IsNullOrWhiteSpace(fields.GetValueOrDefault("uuid"))) break;
            return new Uri("https://drive.usercontent.google.com/download?" + string.Join("&",
                fields.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value))));
        }
        throw new IOException("O Google Drive não liberou o download automático (permissão, login ou limite de downloads). Tente mais tarde ou baixe pelo navegador e use Importar base completa de preços.");
    }

    private static Dictionary<string, string> Attributes(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "([a-zA-Z][a-zA-Z0-9_-]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')",
                     RegexOptions.CultureInvariant, RegexTimeout))
            if (!values.TryAdd(match.Groups[1].Value, WebUtility.HtmlDecode(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value)))
                throw new InvalidDataException("Atributos ambíguos na resposta do Google Drive.");
        return values;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.Host is not ("drive.google.com" or "drive.usercontent.google.com"))
                throw new IOException("O Google Drive exige login ou redirecionou para um endereço não permitido. Use a importação manual.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("PNCPKing-Updater/1.2");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) break;
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new IOException("Redirecionamentos em excesso no Google Drive. Tente novamente mais tarde.");
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.NotFound)
            throw new IOException("O pacote do Google Drive está indisponível: confira a permissão pública ou tente novamente mais tarde. A importação manual continua disponível.");
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> ReadMetadataAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MetadataLimit) throw new InvalidDataException("A listagem ou resposta do Google Drive excedeu o limite de metadados.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
