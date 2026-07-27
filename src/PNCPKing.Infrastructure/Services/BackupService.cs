using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Infrastructure.Services;

public sealed class BackupService(IContractRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!destinationPath.EndsWith(".pncpking", StringComparison.OrdinalIgnoreCase))
        {
            destinationPath += ".pncpking";
        }

        await repository.CheckpointWalAsync(cancellationToken).ConfigureAwait(false);
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var snapshotPath = Path.Combine(temporaryDirectory, "data.db");
            await CreateSnapshotAsync(repository.DatabasePath, snapshotPath, cancellationToken).ConfigureAwait(false);
            var hash = await ComputeSha256Async(snapshotPath, cancellationToken).ConfigureAwait(false);
            var evidenceAssets = await ReadReferencedEvidenceAssetsAsync(snapshotPath, cancellationToken)
                .ConfigureAwait(false);
            var dataFolder = Path.GetDirectoryName(repository.DatabasePath)!;
            foreach (var asset in evidenceAssets)
            {
                var source = ResolveEvidencePath(dataFolder, asset.RelativePath);
                await ValidateEvidenceFileAsync(source, asset.Sha256, asset.ByteLength, cancellationToken)
                    .ConfigureAwait(false);
            }

            var state = await repository.GetDatasetStateAsync(cancellationToken).ConfigureAwait(false);
            var manifest = new DatasetManifest
            {
                SchemaVersion = SqliteContractRepository.CurrentSchemaVersion,
                AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                StartDate = state.StartDate,
                EndDate = state.EndDate,
                Scope = state.Scope.ToString(),
                ContractCount = state.ContractCount,
                ItemCount = state.CachedItemCount,
                ResultCount = state.CachedResultCount,
                CreatedAt = DateTimeOffset.UtcNow,
                DatabaseSha256 = hash,
                EvidenceAssets = evidenceAssets
                    .Select(asset => new EvidenceAssetManifest
                    {
                        Sha256 = asset.Sha256,
                        ArchivePath = $"internet-evidence/{asset.Sha256}.png",
                        ByteLength = asset.ByteLength
                    })
                    .ToArray()
            };
            var manifestPath = Path.Combine(temporaryDirectory, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            var temporaryArchive = destinationPath + ".partial";
            if (File.Exists(temporaryArchive))
            {
                File.Delete(temporaryArchive);
            }

            using (var archive = ZipFile.Open(temporaryArchive, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(snapshotPath, "data.db", CompressionLevel.Fastest);
                archive.CreateEntryFromFile(manifestPath, "manifest.json", CompressionLevel.Optimal);
                foreach (var asset in evidenceAssets)
                {
                    archive.CreateEntryFromFile(
                        ResolveEvidencePath(dataFolder, asset.RelativePath),
                        $"internet-evidence/{asset.Sha256}.png",
                        CompressionLevel.Fastest);
                }
            }

            File.Move(temporaryArchive, destinationPath, true);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }

    public async Task<string> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var importedDatabase = Path.Combine(temporaryDirectory, "data.db");
            var stagedEvidenceFolder = Path.Combine(temporaryDirectory, "internet-evidence");
            DatasetManifest manifest;
            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var manifestEntry = archive.GetEntry("manifest.json")
                    ?? throw new InvalidDataException("O backup não contém manifest.json.");
                var databaseEntry = archive.GetEntry("data.db")
                    ?? throw new InvalidDataException("O backup não contém data.db.");
                await using (var manifestStream = manifestEntry.Open())
                {
                    manifest = await JsonSerializer.DeserializeAsync<DatasetManifest>(
                                   manifestStream,
                                   JsonOptions,
                                   cancellationToken).ConfigureAwait(false)
                               ?? throw new InvalidDataException("Manifesto inválido.");
                }

                if (manifest.SchemaVersion is < 1 or > SqliteContractRepository.CurrentSchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Versão de banco incompatível: {manifest.SchemaVersion}. " +
                        $"Versões aceitas: 1 a {SqliteContractRepository.CurrentSchemaVersion}.");
                }

                await using var source = databaseEntry.Open();
                await using var destination = File.Create(importedDatabase);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

                var manifestAssets = manifest.EvidenceAssets ?? [];
                if (manifestAssets
                    .GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() != 1))
                {
                    throw new InvalidDataException("O backup contém evidências duplicadas no manifesto.");
                }

                foreach (var asset in manifestAssets)
                {
                    ValidateEvidenceManifestEntry(asset);
                    var entry = archive.GetEntry(asset.ArchivePath)
                                ?? throw new InvalidDataException(
                                    $"O backup não contém a evidência {asset.Sha256}.");
                    Directory.CreateDirectory(stagedEvidenceFolder);
                    var stagedPath = Path.Combine(stagedEvidenceFolder, $"{asset.Sha256}.png");
                    await using (var entryStream = entry.Open())
                    await using (var staged = File.Create(stagedPath))
                    {
                        await entryStream.CopyToAsync(staged, cancellationToken).ConfigureAwait(false);
                        await staged.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await ValidateEvidenceFileAsync(
                        stagedPath,
                        asset.Sha256,
                        asset.ByteLength,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var actualHash = await ComputeSha256Async(importedDatabase, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(manifest.DatabaseSha256)))
            {
                throw new InvalidDataException("O checksum do banco não corresponde ao manifesto.");
            }

            await ValidateDatabaseAsync(
                importedDatabase,
                manifest.SchemaVersion,
                cancellationToken).ConfigureAwait(false);

            var databaseAssets = await ReadReferencedEvidenceAssetsAsync(
                importedDatabase,
                cancellationToken).ConfigureAwait(false);
            var manifestHashes = (manifest.EvidenceAssets ?? [])
                .Select(asset => asset.Sha256)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!databaseAssets
                .Select(asset => asset.Sha256)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(manifestHashes))
            {
                throw new InvalidDataException(
                    "O manifesto não corresponde às evidências referenciadas pelo banco.");
            }

            // Migrate an older, otherwise valid PNCP King snapshot while it is
            // still isolated. The live database is not touched until every
            // migration and integrity check succeeds.
            if (manifest.SchemaVersion < SqliteContractRepository.CurrentSchemaVersion)
            {
                var importedRepository = new SqliteContractRepository(importedDatabase);
                await importedRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);
                SqliteConnection.ClearAllPools();
                await ValidateDatabaseAsync(
                    importedDatabase,
                    SqliteContractRepository.CurrentSchemaVersion,
                    cancellationToken).ConfigureAwait(false);
            }

            var dataFolder = Path.GetDirectoryName(repository.DatabasePath)!;
            await InstallStagedEvidenceAsync(
                stagedEvidenceFolder,
                dataFolder,
                manifestHashes,
                cancellationToken).ConfigureAwait(false);
            await repository.CheckpointWalAsync(cancellationToken).ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var recoverableBackup = repository.DatabasePath + $".before-import-{timestamp}.bak";
            if (File.Exists(repository.DatabasePath))
            {
                File.Copy(repository.DatabasePath, recoverableBackup, true);
            }

            DeleteSidecar(repository.DatabasePath, "-wal");
            DeleteSidecar(repository.DatabasePath, "-shm");
            var replacementStarted = false;
            try
            {
                File.Move(importedDatabase, repository.DatabasePath, true);
                replacementStarted = true;
                SqliteConnection.ClearAllPools();
                await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
                CleanupOrphanEvidence(dataFolder, manifestHashes);
            }
            catch
            {
                // A failed migration/open must never leave sidecars belonging to
                // the rejected replacement beside the recovered database.
                SqliteConnection.ClearAllPools();
                DeleteSidecar(repository.DatabasePath, "-wal");
                DeleteSidecar(repository.DatabasePath, "-shm");
                if (File.Exists(recoverableBackup))
                {
                    File.Copy(recoverableBackup, repository.DatabasePath, true);
                    SqliteConnection.ClearAllPools();
                    await repository.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else if (replacementStarted && File.Exists(repository.DatabasePath))
                {
                    File.Delete(repository.DatabasePath);
                }

                throw;
            }

            return recoverableBackup;
        }
        finally
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }

    private static async Task CreateSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private static async Task ValidateDatabaseAsync(
        string path,
        int expectedSchemaVersion,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(
            await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Falha de integridade do SQLite: {result}");
        }

        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
        var schemaVersion = Convert.ToInt32(
            await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (schemaVersion != expectedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Versão interna incompatível: {schemaVersion}. Esperada no manifesto: {expectedSchemaVersion}.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task<IReadOnlyList<EvidenceAssetRow>> ReadReferencedEvidenceAssetsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var table = connection.CreateCommand();
        table.CommandText = """
            SELECT COUNT(*)
              FROM sqlite_master
             WHERE type = 'table' AND name = 'quotation_internet_evidence_assets';
            """;
        if (Convert.ToInt32(
                await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.sha256, a.relative_path, a.byte_length
              FROM quotation_internet_evidence_assets a
             WHERE a.sha256 IN (
                    SELECT price_image_sha256 FROM quotation_internet_price_drafts
                     WHERE price_image_sha256 IS NOT NULL
                    UNION
                    SELECT tax_id_image_sha256 FROM quotation_internet_price_drafts
                     WHERE tax_id_image_sha256 IS NOT NULL
                    UNION
                    SELECT price_image_sha256 FROM quotation_internet_price_evidence
                    UNION
                    SELECT tax_id_image_sha256 FROM quotation_internet_price_evidence)
             ORDER BY a.sha256;
            """;
        var result = new List<EvidenceAssetRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new EvidenceAssetRow(
                reader.GetString(0).ToLowerInvariant(),
                reader.GetString(1),
                reader.GetInt64(2)));
        }

        return result;
    }

    private static void ValidateEvidenceManifestEntry(EvidenceAssetManifest asset)
    {
        if (asset.Sha256.Length != 64 ||
            asset.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
            asset.ByteLength <= 0 ||
            !string.Equals(
                asset.ArchivePath,
                $"internet-evidence/{asset.Sha256}.png",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("O manifesto contém uma evidência inválida.");
        }
    }

    private static async Task ValidateEvidenceFileAsync(
        string path,
        string expectedHash,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"A evidência {expectedHash} não foi encontrada.");
        }

        var info = new FileInfo(path);
        if (info.Length != expectedLength)
        {
            throw new InvalidDataException($"O tamanho da evidência {expectedHash} não corresponde.");
        }

        var actualHash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(expectedHash)))
        {
            throw new InvalidDataException($"O hash da evidência {expectedHash} não corresponde.");
        }
    }

    private static string ResolveEvidencePath(string dataFolder, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("O caminho de uma evidência não pode ser absoluto.");
        }

        var evidenceRoot = Path.GetFullPath(Path.Combine(dataFolder, "internet-evidence"));
        var fullPath = Path.GetFullPath(
            Path.Combine(dataFolder, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = evidenceRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("O caminho de uma evidência saiu da pasta permitida.");
        }

        return fullPath;
    }

    private static async Task InstallStagedEvidenceAsync(
        string stagedFolder,
        string dataFolder,
        IReadOnlySet<string> hashes,
        CancellationToken cancellationToken)
    {
        if (hashes.Count == 0)
        {
            return;
        }

        var destinationFolder = Path.Combine(dataFolder, "internet-evidence");
        Directory.CreateDirectory(destinationFolder);
        foreach (var hash in hashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(stagedFolder, $"{hash}.png");
            var destination = Path.Combine(destinationFolder, $"{hash}.png");
            var temporary = destination + ".importing";
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }

        await Task.CompletedTask;
    }

    private static void CleanupOrphanEvidence(
        string dataFolder,
        IReadOnlySet<string> referencedHashes)
    {
        var evidenceFolder = Path.Combine(dataFolder, "internet-evidence");
        if (!Directory.Exists(evidenceFolder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(evidenceFolder, "*.png", SearchOption.TopDirectoryOnly))
        {
            if (!referencedHashes.Contains(Path.GetFileNameWithoutExtension(path)))
            {
                File.Delete(path);
            }
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PNCPKing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record EvidenceAssetRow(
        string Sha256,
        string RelativePath,
        long ByteLength);

    private static void DeleteSidecar(string databasePath, string suffix)
    {
        var path = databasePath + suffix;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
