using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PNCPKing.Core.Models;

public static class GuardFormat
{
    public const string PlanKind = "PNCPKing.GuardPlan";
    public const string ControlKind = "PNCPKing.GuardControl";
    public const string PackageKind = "PNCPKing.GuardPackage";
    public const int Version = 1;
    public const int PartitionCount = 4096;
    public const string PlanExtension = ".pncpguardplan";
    public const string PackageExtension = ".pncpguard";
    public const long MaximumManifestBytes = 2 * 1024 * 1024;
    public const long MaximumPayloadBytes = 512 * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

public sealed record GuardWorkerDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int Weight { get; init; } = 1;
}

public sealed record GuardPlanContract
{
    public required string PncpId { get; init; }
    public required string Cnpj { get; init; }
    public required int PurchaseYear { get; init; }
    public required int PurchaseSequence { get; init; }
    public DateTimeOffset? PublicationDate { get; init; }
    public DateTimeOffset? GlobalUpdatedAt { get; init; }
}

public sealed record GuardWorkerPlan
{
    public string Kind { get; init; } = GuardFormat.PlanKind;
    public int Version { get; init; } = GuardFormat.Version;
    public required string CampaignId { get; init; }
    public required string MasterId { get; init; }
    public required GuardWorkerDefinition Worker { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required IReadOnlyList<GuardPlanContract> Contracts { get; init; }
}

public sealed record GuardControlWorker
{
    public required string WorkerId { get; init; }
    public required string PlanRelativePath { get; init; }
}

public sealed record GuardControl
{
    public string Kind { get; init; } = GuardFormat.ControlKind;
    public int Version { get; init; } = GuardFormat.Version;
    public required string CampaignId { get; init; }
    public required string MasterId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required IReadOnlyList<GuardControlWorker> Workers { get; init; }
}

public sealed record GuardItem
{
    public required long ItemNumber { get; init; }
    public string Description { get; init; } = string.Empty;
    public string AdditionalInformation { get; init; } = string.Empty;
    public long? RequestedQuantityScaled { get; init; }
    public string Unit { get; init; } = string.Empty;
    public bool HasResult { get; init; }
}

public sealed record GuardResult
{
    public required long ItemNumber { get; init; }
    public required long ResultSequence { get; init; }
    public string SupplierTaxId { get; init; } = string.Empty;
    public string SupplierName { get; init; } = string.Empty;
    public string SupplierType { get; init; } = string.Empty;
    public string SupplierMunicipality { get; init; } = string.Empty;
    public string SupplierUf { get; init; } = string.Empty;
    public long? HomologatedQuantityScaled { get; init; }
    public long? HomologatedUnitValueScaled { get; init; }
    public long? HomologatedTotalValueScaled { get; init; }
    public DateOnly? ResultDate { get; init; }
    public int ResultStatusId { get; init; }
    public string ResultStatusName { get; init; } = string.Empty;
}

public sealed record GuardContractSnapshot
{
    public required GuardPlanContract Contract { get; init; }
    public required DateTimeOffset CollectedAt { get; init; }
    public required IReadOnlyList<GuardItem> Items { get; init; }
    public required IReadOnlyList<GuardResult> Results { get; init; }
}

public sealed record GuardPackagePayload
{
    public required IReadOnlyList<GuardContractSnapshot> Contracts { get; init; }
}

public sealed record GuardPackageManifest
{
    public string Kind { get; init; } = GuardFormat.PackageKind;
    public int Version { get; init; } = GuardFormat.Version;
    public required string PackageId { get; init; }
    public required string CampaignId { get; init; }
    public required string WorkerId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required int ContractCount { get; init; }
    public required int ItemCount { get; init; }
    public required int ResultCount { get; init; }
    public required long UncompressedBytes { get; init; }
    public required string PayloadSha256 { get; init; }
}

public sealed record GuardPackage(
    GuardPackageManifest Manifest,
    GuardPackagePayload Payload,
    string FileSha256);

public sealed record GuardAck
{
    public required string PackageId { get; init; }
    public required string PackageSha256 { get; init; }
    public required string CampaignId { get; init; }
    public required DateTimeOffset ImportedAt { get; init; }
}

public static class GuardPartitioner
{
    public static int GetPartition(string pncpId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pncpId);
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pncpId.Trim()));
        return ((hash[0] << 8) | hash[1]) % GuardFormat.PartitionCount;
    }

    public static IReadOnlyList<string> AssignPartitions(IReadOnlyList<GuardWorkerDefinition> workers)
    {
        ArgumentNullException.ThrowIfNull(workers);
        if (workers.Count == 0 || workers.Any(worker => worker.Weight <= 0))
        {
            throw new ArgumentException("Informe ao menos um trabalhador, todos com peso positivo.", nameof(workers));
        }

        if (workers.Select(worker => worker.Id).Distinct(StringComparer.Ordinal).Count() != workers.Count)
        {
            throw new ArgumentException("Os identificadores dos trabalhadores devem ser únicos.", nameof(workers));
        }

        var totalWeight = workers.Sum(worker => (long)worker.Weight);
        var exact = workers.Select(worker => GuardFormat.PartitionCount * (double)worker.Weight / totalWeight).ToArray();
        var quotas = exact.Select(Math.Floor).Select(value => (int)value).ToArray();
        var remaining = GuardFormat.PartitionCount - quotas.Sum();
        foreach (var index in Enumerable.Range(0, workers.Count)
                     .OrderByDescending(index => exact[index] - quotas[index])
                     .ThenBy(index => workers[index].Id, StringComparer.Ordinal)
                     .Take(remaining))
        {
            quotas[index]++;
        }

        var current = new long[workers.Count];
        var assignments = new string[GuardFormat.PartitionCount];
        for (var partition = 0; partition < assignments.Length; partition++)
        {
            var chosen = -1;
            for (var index = 0; index < workers.Count; index++)
            {
                current[index] += quotas[index];
                if (quotas[index] > 0 && (chosen < 0 || current[index] > current[chosen] ||
                    current[index] == current[chosen] && string.CompareOrdinal(workers[index].Id, workers[chosen].Id) < 0))
                {
                    chosen = index;
                }
            }

            assignments[partition] = workers[chosen].Id;
            current[chosen] -= GuardFormat.PartitionCount;
        }

        return assignments;
    }
}

public static class GuardFileCodec
{
    private const string ManifestEntry = "manifest.json";
    private const string PayloadEntry = "snapshots.json";

    public static async Task WriteJsonAtomicAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".partial";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, GuardFormat.JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T>(stream, GuardFormat.JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException($"O arquivo {Path.GetFileName(path)} está vazio ou inválido.");
    }

    public static async Task<GuardPackageManifest> WritePackageAsync(
        string destinationPath,
        string campaignId,
        string workerId,
        IReadOnlyList<GuardContractSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        var payload = new GuardPackagePayload { Contracts = snapshots };
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, GuardFormat.JsonOptions);
        if (payloadBytes.LongLength > GuardFormat.MaximumPayloadBytes)
        {
            throw new InvalidDataException("O conteúdo do pacote excede 512 MiB.");
        }

        var manifest = new GuardPackageManifest
        {
            PackageId = Guid.NewGuid().ToString("D"),
            CampaignId = campaignId,
            WorkerId = workerId,
            CreatedAt = DateTimeOffset.UtcNow,
            ContractCount = snapshots.Count,
            ItemCount = snapshots.Sum(snapshot => snapshot.Items.Count),
            ResultCount = snapshots.Sum(snapshot => snapshot.Results.Count),
            UncompressedBytes = payloadBytes.LongLength,
            PayloadSha256 = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant()
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, GuardFormat.JsonOptions);
        var normalized = Path.ChangeExtension(destinationPath, GuardFormat.PackageExtension);
        var partial = normalized + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(normalized))!);
        try
        {
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                await WriteEntryAsync(archive, ManifestEntry, manifestBytes, cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(archive, PayloadEntry, payloadBytes, cancellationToken).ConfigureAwait(false);
            }

            _ = await ReadPackageAsync(partial, cancellationToken).ConfigureAwait(false);
            File.Move(partial, normalized, overwrite: false);
            return manifest;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    public static async Task<GuardPackage> ReadPackageAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileHash = await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var manifestBytes = await ReadEntryAsync(archive, ManifestEntry, GuardFormat.MaximumManifestBytes, cancellationToken)
            .ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<GuardPackageManifest>(manifestBytes, GuardFormat.JsonOptions)
                       ?? throw new InvalidDataException("Manifesto ausente ou inválido.");
        ValidateManifest(manifest);
        var payloadBytes = await ReadEntryAsync(archive, PayloadEntry, GuardFormat.MaximumPayloadBytes, cancellationToken)
            .ConfigureAwait(false);
        var payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        if (!string.Equals(payloadHash, manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            payloadBytes.LongLength != manifest.UncompressedBytes)
        {
            throw new InvalidDataException("O checksum ou o tamanho do conteúdo do pacote não confere.");
        }

        var payload = JsonSerializer.Deserialize<GuardPackagePayload>(payloadBytes, GuardFormat.JsonOptions)
                      ?? throw new InvalidDataException("Conteúdo do pacote ausente ou inválido.");
        if (payload.Contracts.Count != manifest.ContractCount ||
            payload.Contracts.Sum(snapshot => snapshot.Items.Count) != manifest.ItemCount ||
            payload.Contracts.Sum(snapshot => snapshot.Results.Count) != manifest.ResultCount)
        {
            throw new InvalidDataException("As contagens do manifesto não correspondem ao conteúdo.");
        }

        if (payload.Contracts.Select(snapshot => snapshot.Contract.PncpId)
                .Distinct(StringComparer.Ordinal).Count() != payload.Contracts.Count)
        {
            throw new InvalidDataException("O pacote contém snapshots duplicados da mesma contratação.");
        }

        foreach (var snapshot in payload.Contracts)
        {
            if (snapshot.Items.Select(item => item.ItemNumber).Distinct().Count() != snapshot.Items.Count ||
                snapshot.Results.Any(result => snapshot.Items.All(item => item.ItemNumber != result.ItemNumber)) ||
                snapshot.Results.Select(result => (result.ItemNumber, result.ResultSequence)).Distinct().Count() !=
                snapshot.Results.Count)
            {
                throw new InvalidDataException(
                    "O pacote contém itens/resultados duplicados ou resultado sem item correspondente.");
            }
        }

        return new GuardPackage(manifest, payload, fileHash);
    }

    public static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ValidateManifest(GuardPackageManifest manifest)
    {
        if (manifest.Kind != GuardFormat.PackageKind || manifest.Version != GuardFormat.Version ||
            !Guid.TryParse(manifest.PackageId, out _) || string.IsNullOrWhiteSpace(manifest.CampaignId) ||
            string.IsNullOrWhiteSpace(manifest.WorkerId) || manifest.ContractCount < 0 ||
            manifest.ItemCount < 0 || manifest.ResultCount < 0 || manifest.UncompressedBytes < 0 ||
            manifest.PayloadSha256.Length != 64)
        {
            throw new InvalidDataException("Manifesto do pacote incompatível ou inválido.");
        }
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchive archive,
        string name,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Entrada {name} ausente.");
        if (entry.Length > maximumBytes)
        {
            throw new InvalidDataException($"Entrada {name} excede o limite permitido.");
        }

        await using var input = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        if (output.Length > maximumBytes)
        {
            throw new InvalidDataException($"Entrada {name} excede o limite permitido.");
        }

        return output.ToArray();
    }
}
