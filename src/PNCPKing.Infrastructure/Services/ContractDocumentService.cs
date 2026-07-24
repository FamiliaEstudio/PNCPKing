using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using SharpCompress.Archives;

namespace PNCPKing.Infrastructure.Services;

public sealed class ContractDocumentService : IContractDocumentService
{
    private const int MaximumArchiveEntries = 2_000;
    private const long MaximumArchiveExpandedBytes = 512L * 1024 * 1024;
    private const long MaximumContractExpandedBytes = 1024L * 1024 * 1024;
    private const long MaximumCacheBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumArchiveDepth = 2;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IPncpDocumentClient _client;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public ContractDocumentService(IPncpDocumentClient client, string dataFolder)
    {
        _client = client;
        _cacheRoot = Path.Combine(dataFolder, "document-cache");
    }

    public async Task<DocumentBundleResult> PrepareAsync(
        PncpContractKey contract,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            var contractFolder = GetContractFolder(contract);
            var sourceFolder = Path.Combine(contractFolder, "sources");
            var pdfFolder = Path.Combine(contractFolder, "pdf");
            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(pdfFolder);

            var manifestPath = Path.Combine(contractFolder, "manifest.json");
            var manifest = await LoadManifestAsync(manifestPath, contract, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DocumentProcessingProgress(
                DocumentProcessingStage.Listing,
                0,
                1,
                $"Consultando documentos de {contract.PncpId}…"));
            var descriptors = await _client.ListDocumentsAsync(contract, cancellationToken).ConfigureAwait(false);
            var warnings = new List<string>();
            var pdfs = new Dictionary<string, CachedPdfDocument>(StringComparer.OrdinalIgnoreCase);
            var downloaded = 0;
            var reused = 0;
            long expandedBytes = 0;

            for (var index = 0; index < descriptors.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = descriptors[index];
                progress?.Report(new DocumentProcessingProgress(
                    DocumentProcessingStage.Downloading,
                    index,
                    descriptors.Count,
                    $"Documento {index + 1:N0} de {descriptors.Count:N0}: {descriptor.Title}"));

                var cached = manifest.Documents.FirstOrDefault(item =>
                    item.Sequence == descriptor.Sequence &&
                    string.Equals(item.Title, descriptor.Title, StringComparison.Ordinal) &&
                    string.Equals(item.DownloadUri, descriptor.DownloadUri, StringComparison.Ordinal) &&
                    File.Exists(Path.Combine(contractFolder, item.SourceRelativePath)));
                byte[] sourceBytes;
                string sourceName;
                if (cached is not null)
                {
                    try
                    {
                        sourceBytes = await File.ReadAllBytesAsync(
                            Path.Combine(contractFolder, cached.SourceRelativePath),
                            cancellationToken).ConfigureAwait(false);
                        var cachedHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
                        if (!string.Equals(
                                cachedHash,
                                cached.SourceSha256,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            cached = null;
                        }
                    }
                    catch (IOException)
                    {
                        cached = null;
                        sourceBytes = [];
                    }
                }
                else
                {
                    sourceBytes = [];
                }

                if (cached is not null)
                {
                    sourceName = cached.SourceName;
                    reused++;
                }
                else
                {
                    var content = await _client.DownloadDocumentAsync(
                        contract,
                        descriptor,
                        cancellationToken).ConfigureAwait(false);
                    sourceBytes = content.Bytes;
                    sourceName = SelectSourceName(content.FileName, descriptor);
                    var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
                    var sourceRelativePath = Path.Combine(
                        "sources",
                        $"{descriptor.Sequence:D6}-{sourceHash[..16]}.bin");
                    await WriteAtomicAsync(
                        Path.Combine(contractFolder, sourceRelativePath),
                        sourceBytes,
                        cancellationToken).ConfigureAwait(false);
                    cached = new CachedDocumentEntry
                    {
                        Sequence = descriptor.Sequence,
                        Title = descriptor.Title,
                        DownloadUri = descriptor.DownloadUri,
                        SourceName = sourceName,
                        SourceRelativePath = sourceRelativePath,
                        SourceSha256 = sourceHash
                    };
                    manifest.Documents.RemoveAll(item => item.Sequence == descriptor.Sequence);
                    manifest.Documents.Add(cached);
                    downloaded++;
                }

                progress?.Report(new DocumentProcessingProgress(
                    DocumentProcessingStage.Extracting,
                    index,
                    descriptors.Count,
                    $"Extraindo PDFs de {descriptor.Title}…"));
                var extraction = new ExtractionContext(
                    contractFolder,
                    pdfFolder,
                    descriptor,
                    pdfs,
                    warnings,
                    expandedBytes);
                await ExtractPayloadAsync(
                    sourceBytes,
                    sourceName,
                    archivePath: string.Empty,
                    depth: 0,
                    countPayloadBytes: true,
                    extraction,
                    cancellationToken).ConfigureAwait(false);
                expandedBytes = extraction.ExpandedBytes;
                if (expandedBytes > MaximumContractExpandedBytes)
                {
                    warnings.Add(
                        $"A contratação excedeu o limite total de 1 GiB descompactado; documentos restantes foram ignorados.");
                    break;
                }

                manifest.LastAccessUtc = DateTimeOffset.UtcNow;
                await SaveManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            }

            manifest.LastAccessUtc = DateTimeOffset.UtcNow;
            await SaveManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            Directory.SetLastWriteTimeUtc(contractFolder, DateTime.UtcNow);
            await EnforceCacheLimitAsync(contractFolder, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DocumentProcessingProgress(
                DocumentProcessingStage.Completed,
                descriptors.Count,
                descriptors.Count,
                $"{pdfs.Count:N0} PDF(s) processado(s)."));

            return new DocumentBundleResult
            {
                Contract = contract,
                Pdfs = pdfs.Values
                    .OrderBy(item => item.DocumentSequence)
                    .ThenBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Warnings = warnings,
                DownloadedFiles = downloaded,
                ReusedFiles = reused
            };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<DocumentBundleResult> CreateConsolidatedPdfAsync(
        PncpContractKey contract,
        string destinationPath,
        IProgress<DocumentProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var bundle = await PrepareAsync(contract, progress, cancellationToken).ConfigureAwait(false);
        var warnings = bundle.Warnings.ToList();
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporaryPath = fullDestination + ".partial";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var pageCount = 0;
        try
        {
            using var output = new PdfDocument();
            foreach (var pdf in bundle.Pdfs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var input = PdfReader.Open(pdf.LocalPath, PdfDocumentOpenMode.Import);
                    for (var page = 0; page < input.PageCount; page++)
                    {
                        output.AddPage(input.Pages[page]);
                        pageCount++;
                    }
                }
                catch (Exception exception)
                {
                    warnings.Add(
                        $"{pdf.DocumentTitle}/{pdf.ArchivePath}: PDF ignorado ({exception.Message}).");
                }
            }

            if (pageCount > 0)
            {
                output.Save(temporaryPath);
                File.Move(temporaryPath, fullDestination, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        if (pageCount == 0)
        {
            warnings.Add("Nenhum PDF processável foi encontrado para gerar o arquivo consolidado.");
        }

        return bundle with
        {
            Warnings = warnings,
            ConsolidatedPath = pageCount > 0 ? fullDestination : null
        };
    }

    public async Task<long> ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_cacheRoot))
            {
                return 0;
            }

            var bytes = GetDirectorySize(_cacheRoot);
            Directory.Delete(_cacheRoot, recursive: true);
            return bytes;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ExtractPayloadAsync(
        byte[] bytes,
        string logicalName,
        string archivePath,
        int depth,
        bool countPayloadBytes,
        ExtractionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsPdf(bytes))
        {
            if (countPayloadBytes)
            {
                context.ExpandedBytes += bytes.LongLength;
            }

            if (context.ExpandedBytes > MaximumContractExpandedBytes)
            {
                context.Warnings.Add(
                    $"{logicalName}: limite total de 1 GiB descompactado atingido.");
                return;
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (context.Pdfs.ContainsKey(hash))
            {
                return;
            }

            var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(logicalName));
            var path = Path.Combine(context.PdfFolder, $"{hash[..20]}-{safeName}.pdf");
            if (!File.Exists(path))
            {
                await WriteAtomicAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }

            context.Pdfs.Add(hash, new CachedPdfDocument
            {
                LocalPath = path,
                Sha256 = hash,
                DocumentSequence = context.Descriptor.Sequence,
                DocumentTitle = context.Descriptor.Title,
                ArchivePath = archivePath.Length == 0 ? logicalName : archivePath
            });
            return;
        }

        if (!LooksLikeArchive(bytes, logicalName))
        {
            return;
        }

        if (depth >= MaximumArchiveDepth)
        {
            context.Warnings.Add($"{logicalName}: limite de {MaximumArchiveDepth} níveis compactados atingido.");
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = ArchiveFactory.OpenArchive(stream);
            var entries = archive.Entries.Where(entry => !entry.IsDirectory).ToArray();
            if (entries.Length > MaximumArchiveEntries)
            {
                context.Warnings.Add(
                    $"{logicalName}: arquivo possui {entries.Length:N0} entradas; limite de {MaximumArchiveEntries:N0}.");
                return;
            }

            long archiveExpanded = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsEncrypted)
                {
                    context.Warnings.Add($"{logicalName}/{entry.Key}: entrada criptografada ignorada.");
                    continue;
                }

                var archiveRemaining = MaximumArchiveExpandedBytes - archiveExpanded;
                var contractRemaining = MaximumContractExpandedBytes - context.ExpandedBytes;
                if (entry.Size < 0 ||
                    entry.Size > archiveRemaining ||
                    entry.Size > contractRemaining ||
                    archiveRemaining <= 0 ||
                    contractRemaining <= 0)
                {
                    context.Warnings.Add($"{logicalName}: limite de expansão atingido; entradas restantes ignoradas.");
                    break;
                }

                await using var entryStream = entry.OpenEntryStream();
                using var entryBytes = new MemoryStream();
                await CopyWithLimitAsync(
                    entryStream,
                    entryBytes,
                    Math.Min(archiveRemaining, contractRemaining),
                    cancellationToken).ConfigureAwait(false);
                archiveExpanded += entryBytes.Length;
                context.ExpandedBytes += entryBytes.Length;
                var entryName = NormalizeArchivePath(entry.Key);
                await ExtractPayloadAsync(
                    entryBytes.ToArray(),
                    entryName,
                    archivePath.Length == 0 ? entryName : $"{archivePath}/{entryName}",
                    depth + 1,
                    countPayloadBytes: false,
                    context,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            context.Warnings.Add($"{logicalName}: não foi possível abrir o arquivo compactado ({exception.Message}).");
        }
    }

    private async Task EnforceCacheLimitAsync(string currentContractFolder, CancellationToken cancellationToken)
    {
        var size = GetDirectorySize(_cacheRoot);
        if (size <= MaximumCacheBytes)
        {
            return;
        }

        foreach (var directory in new DirectoryInfo(_cacheRoot)
                     .EnumerateDirectories()
                     .Where(item => !Path.GetFullPath(item.FullName).Equals(
                         Path.GetFullPath(currentContractFolder),
                         StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directorySize = GetDirectorySize(directory.FullName);
            directory.Delete(recursive: true);
            size -= directorySize;
            if (size <= MaximumCacheBytes)
            {
                break;
            }
        }

        if (size > MaximumCacheBytes)
        {
            var sources = Path.Combine(currentContractFolder, "sources");
            if (Directory.Exists(sources))
            {
                foreach (var source in new DirectoryInfo(sources)
                             .EnumerateFiles()
                             .OrderBy(file => file.LastWriteTimeUtc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceLength = source.Length;
                    source.Delete();
                    size -= sourceLength;
                    if (size <= MaximumCacheBytes)
                    {
                        break;
                    }
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private string GetContractFolder(PncpContractKey contract)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract.PncpId)))
            .ToLowerInvariant();
        return Path.Combine(
            _cacheRoot,
            $"{SanitizeFileName(contract.Cnpj)}-{contract.PurchaseYear}-{contract.PurchaseSequence}-{hash[..12]}");
    }

    private static async Task<CacheManifest> LoadManifestAsync(
        string path,
        PncpContractKey contract,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var manifest = await JsonSerializer.DeserializeAsync<CacheManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (manifest is not null && manifest.ContractId == contract.PncpId)
                {
                    return manifest;
                }
            }
            catch (JsonException)
            {
                // Um manifesto danificado é reconstruído sem afetar o banco.
            }
        }

        return new CacheManifest
        {
            ContractId = contract.PncpId,
            LastAccessUtc = DateTimeOffset.UtcNow
        };
    }

    private static Task SaveManifestAsync(
        string path,
        CacheManifest manifest,
        CancellationToken cancellationToken) =>
        WriteJsonAtomicAsync(path, manifest, cancellationToken);

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task CopyWithLimitAsync(
        Stream input,
        Stream output,
        long limit,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            written += read;
            if (written > limit)
            {
                throw new InvalidDataException("A entrada compactada excede o limite de expansão.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsPdf(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 5 &&
        bytes[..Math.Min(bytes.Length, 1024)].IndexOf("%PDF-"u8) >= 0;

    private static bool LooksLikeArchive(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length >= 4 &&
            bytes[0] == (byte)'P' &&
            bytes[1] == (byte)'K' &&
            bytes[2] is 3 or 5 or 7 &&
            bytes[3] is 4 or 6 or 8)
        {
            return true;
        }

        if (bytes.Length >= 6 && bytes[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
        {
            return true;
        }

        if (bytes.Length >= 7 && bytes[..7].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }))
        {
            return true;
        }

        if (bytes.Length >= 8 &&
            bytes[..8].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }))
        {
            return true;
        }

        var extension = Path.GetExtension(name);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".rar", StringComparison.OrdinalIgnoreCase);
    }

    private static string SelectSourceName(string? responseName, PncpDocumentDescriptor descriptor)
    {
        var candidate = string.IsNullOrWhiteSpace(responseName) ? descriptor.Title : responseName;
        if (Path.HasExtension(candidate))
        {
            return Path.GetFileName(candidate);
        }

        if (Uri.TryCreate(descriptor.DownloadUri, UriKind.Absolute, out var uri) &&
            Path.HasExtension(uri.AbsolutePath))
        {
            return Path.GetFileName(uri.AbsolutePath);
        }

        return candidate;
    }

    private static string NormalizeArchivePath(string? value)
    {
        var parts = (value ?? "arquivo")
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part is not "." and not "..")
            .Select(SanitizeFileName);
        var normalized = string.Join("/", parts);
        return normalized.Length == 0 ? "arquivo" : normalized;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character =>
            invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).Trim();
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100];
        }

        return sanitized.Length == 0 ? "documento" : sanitized;
    }

    private static long GetDirectorySize(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
            : 0;

    private sealed class ExtractionContext(
        string contractFolder,
        string pdfFolder,
        PncpDocumentDescriptor descriptor,
        Dictionary<string, CachedPdfDocument> pdfs,
        List<string> warnings,
        long expandedBytes)
    {
        public string ContractFolder { get; } = contractFolder;
        public string PdfFolder { get; } = pdfFolder;
        public PncpDocumentDescriptor Descriptor { get; } = descriptor;
        public Dictionary<string, CachedPdfDocument> Pdfs { get; } = pdfs;
        public List<string> Warnings { get; } = warnings;
        public long ExpandedBytes { get; set; } = expandedBytes;
    }

    private sealed record CacheManifest
    {
        public string ContractId { get; init; } = string.Empty;
        public DateTimeOffset LastAccessUtc { get; set; }
        public List<CachedDocumentEntry> Documents { get; init; } = [];
    }

    private sealed record CachedDocumentEntry
    {
        public long Sequence { get; init; }
        public string Title { get; init; } = string.Empty;
        public string DownloadUri { get; init; } = string.Empty;
        public string SourceName { get; init; } = string.Empty;
        public string SourceRelativePath { get; init; } = string.Empty;
        public string SourceSha256 { get; init; } = string.Empty;
    }
}
