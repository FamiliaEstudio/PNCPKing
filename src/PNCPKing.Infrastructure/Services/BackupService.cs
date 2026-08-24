using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Infrastructure.Services;

public sealed class BackupService(
    IContractRepository repository,
    IPerformanceTelemetry? performance = null)
{
    private const long ImportSafetyReserveBytes = 1L * 1024 * 1024 * 1024;
    private const int ProgressBufferBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly IPerformanceTelemetry _performance = performance ?? NullPerformanceTelemetry.Instance;

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        await ExportAsync(destinationPath, BackupProfile.Full, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportAsync(
        string destinationPath,
        BackupProfile profile,
        CancellationToken cancellationToken = default)
    {
        using var span = _performance.Begin("backup", "export");
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
            if (profile == BackupProfile.Compact)
            {
                await CompactSnapshotAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            }

            using (var integritySpan = _performance.Begin("backup", "export-integrity"))
            {
                await ValidateDatabaseAsync(
                    snapshotPath,
                    SqliteContractRepository.CurrentSchemaVersion,
                    checkIntegrity: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                integritySpan.Complete(bytes: new FileInfo(snapshotPath).Length);
            }

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
            var snapshotCounts = await ReadDatabaseCountsAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            var manifest = new DatasetManifest
            {
                SchemaVersion = SqliteContractRepository.CurrentSchemaVersion,
                AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                StartDate = state.StartDate,
                EndDate = state.EndDate,
                Scope = state.Scope.ToString(),
                ContractCount = state.ContractCount,
                ItemCount = snapshotCounts.Items,
                ResultCount = snapshotCounts.Results,
                CreatedAt = DateTimeOffset.UtcNow,
                DatabaseSha256 = hash,
                DatabaseIntegrityValidatedAtExport = true,
                EvidenceAssets = evidenceAssets
                    .Select(asset => new EvidenceAssetManifest
                    {
                        Sha256 = asset.Sha256,
                        ArchivePath = $"internet-evidence/{asset.Sha256}.png",
                        ByteLength = asset.ByteLength
                    })
                    .ToArray(),
                BackupProfile = profile,
                ContainsPriceCache = profile == BackupProfile.Full && snapshotCounts.PriceCacheContracts > 0,
                PriceCacheContractCount = snapshotCounts.PriceCacheContracts,
                PriceCacheItemCount = snapshotCounts.Items,
                PriceCacheResultCount = snapshotCounts.Results
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
            span.Complete(bytes: new FileInfo(destinationPath).Length);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }

    public async Task<string> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        return await ImportAsync(sourcePath, progress: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackupInspection> InspectAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("O backup informado não foi encontrado.", sourcePath);
        }

        DatasetManifest manifest;
        long databaseBytes;
        using (var archive = ZipFile.OpenRead(sourcePath))
        {
            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("O backup não contém manifest.json.");
            var databaseEntry = archive.GetEntry("data.db")
                ?? throw new InvalidDataException("O backup não contém data.db.");
            await using var manifestStream = manifestEntry.Open();
            manifest = await JsonSerializer.DeserializeAsync<DatasetManifest>(
                           manifestStream,
                           JsonOptions,
                           cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidDataException("Manifesto inválido.");
            databaseBytes = databaseEntry.Length;
        }

        if (manifest.SchemaVersion is < 1 or > SqliteContractRepository.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Versão de banco incompatível: {manifest.SchemaVersion}. " +
                $"Versões aceitas: 1 a {SqliteContractRepository.CurrentSchemaVersion}.");
        }

        var temporaryRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())) ?? Path.GetTempPath();
        var dataRoot = Path.GetPathRoot(repository.DatabasePath) ?? Path.GetDirectoryName(repository.DatabasePath)!;
        var sameVolume = string.Equals(
            temporaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            dataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        var existingDatabaseBytes = File.Exists(repository.DatabasePath)
            ? new FileInfo(repository.DatabasePath).Length
            : 0L;
        var migrationAllowance = Math.Max(ImportSafetyReserveBytes, databaseBytes / 2);
        long temporaryRequired;
        long dataRequired;
        if (sameVolume)
        {
            temporaryRequired = AddSaturated(databaseBytes, existingDatabaseBytes, migrationAllowance);
            dataRequired = temporaryRequired;
        }
        else
        {
            temporaryRequired = AddSaturated(databaseBytes, migrationAllowance);
            dataRequired = AddSaturated(databaseBytes, existingDatabaseBytes, ImportSafetyReserveBytes);
        }

        return new BackupInspection
        {
            SourcePath = Path.GetFullPath(sourcePath),
            SchemaVersion = manifest.SchemaVersion,
            Profile = manifest.BackupProfile,
            ArchiveBytes = new FileInfo(sourcePath).Length,
            DatabaseBytes = databaseBytes,
            ExistingDatabaseBytes = existingDatabaseBytes,
            TemporaryRoot = temporaryRoot,
            DataRoot = dataRoot,
            TemporaryAvailableBytes = GetAvailableBytes(temporaryRoot),
            DataAvailableBytes = sameVolume
                ? GetAvailableBytes(temporaryRoot)
                : GetAvailableBytes(dataRoot),
            TemporaryRequiredBytes = temporaryRequired,
            DataRequiredBytes = dataRequired,
            SharesTemporaryAndDataVolume = sameVolume
        };
    }

    public async Task<string> ImportAsync(
        string sourcePath,
        IProgress<BackupImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        using var span = _performance.Begin("backup", "import");
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Report(progress, BackupImportStage.Inspecting, 1, "Inspecionando o arquivo e o espaço disponível…");
        CleanupStaleTemporaryDirectories(TimeSpan.FromMinutes(5));
        var inspection = await InspectAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!inspection.HasEnoughSpace)
        {
            throw new IOException(
                "Espaço insuficiente para importar com segurança. " +
                $"Temporários: {FormatBytes(inspection.TemporaryAvailableBytes)} livres, " +
                $"{FormatBytes(inspection.TemporaryRequiredBytes)} necessários. " +
                $"Dados: {FormatBytes(inspection.DataAvailableBytes)} livres, " +
                $"{FormatBytes(inspection.DataRequiredBytes)} necessários.");
        }

        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var importedDatabase = Path.Combine(temporaryDirectory, "data.db");
            var stagedEvidenceFolder = Path.Combine(temporaryDirectory, "internet-evidence");
            DatasetManifest manifest;
            string actualHash;
            using var extractionSpan = _performance.Begin("backup", "import-extraction");
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
                actualHash = await CopyWithProgressAsync(
                        source,
                        destination,
                        databaseEntry.Length,
                        (processed, total) => Report(
                            progress,
                            BackupImportStage.Extracting,
                            Scale(processed, total, 5, 28),
                            $"Descompactando o banco: {FormatBytes(processed)} de {FormatBytes(total)}…",
                            processed,
                            total),
                        cancellationToken)
                    .ConfigureAwait(false);

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
            extractionSpan.Complete(bytes: inspection.DatabaseBytes);

            Report(progress, BackupImportStage.VerifyingChecksum, 43, "Checksum calculado durante a extração.");
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(manifest.DatabaseSha256)))
            {
                throw new InvalidDataException("O checksum do banco não corresponde ao manifesto.");
            }

            if (manifest.DatabaseIntegrityValidatedAtExport == true)
            {
                Report(
                    progress,
                    BackupImportStage.CheckingIntegrity,
                    45,
                    "Backup validado integralmente na origem; confirmando a versão interna…");
                using var validationSpan = _performance.Begin("backup", "import-origin-validation");
                await ValidateDatabaseAsync(
                    importedDatabase,
                    manifest.SchemaVersion,
                    checkIntegrity: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                validationSpan.Complete(bytes: inspection.DatabaseBytes);
                Report(
                    progress,
                    BackupImportStage.CheckingIntegrity,
                    58,
                    "Integridade confirmada na origem e protegida pelo checksum SHA-256.");
            }
            else
            {
                Report(
                    progress,
                    BackupImportStage.CheckingIntegrity,
                    45,
                    "Backup legado; verificando integralmente o SQLite neste computador…");
                using var integritySpan = _performance.Begin("backup", "import-full-integrity");
                await ValidateDatabaseAsync(
                    importedDatabase,
                    manifest.SchemaVersion,
                    checkIntegrity: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                integritySpan.Complete(bytes: inspection.DatabaseBytes);
                Report(progress, BackupImportStage.CheckingIntegrity, 58, "Integridade local confirmada.");
            }

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
                Report(
                    progress,
                    BackupImportStage.Migrating,
                    60,
                    $"Migrando o banco do esquema {manifest.SchemaVersion} para " +
                    $"{SqliteContractRepository.CurrentSchemaVersion}…");
                var importedRepository = new SqliteContractRepository(importedDatabase);
                await importedRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);
                SqliteConnection.ClearAllPools();
                Report(
                    progress,
                    BackupImportStage.CheckingIntegrity,
                    70,
                    "Migração concluída; verificando novamente a integridade…");
                using var integritySpan = _performance.Begin("backup", "import-full-integrity");
                await ValidateDatabaseAsync(
                    importedDatabase,
                    SqliteContractRepository.CurrentSchemaVersion,
                    checkIntegrity: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                integritySpan.Complete(bytes: inspection.DatabaseBytes);
            }

            if (manifest.BackupProfile == BackupProfile.Compact)
            {
                await DisableImportedCompactCacheAsync(importedDatabase, cancellationToken).ConfigureAwait(false);
            }

            using var installationSpan = _performance.Begin("backup", "import-installation");
            var dataFolder = Path.GetDirectoryName(repository.DatabasePath)!;
            Report(progress, BackupImportStage.InstallingEvidence, 80, "Validando e instalando evidências…");
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
                Report(
                    progress,
                    BackupImportStage.PreservingCurrentDatabase,
                    86,
                    "Preservando uma cópia recuperável do banco atual…");
                File.Copy(repository.DatabasePath, recoverableBackup, true);
            }

            DeleteSidecar(repository.DatabasePath, "-wal");
            DeleteSidecar(repository.DatabasePath, "-shm");
            var replacementStarted = false;
            try
            {
                Report(progress, BackupImportStage.InstallingDatabase, 93, "Instalando o banco validado…");
                File.Move(importedDatabase, repository.DatabasePath, true);
                replacementStarted = true;
                SqliteConnection.ClearAllPools();
                await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
                CleanupOrphanEvidence(dataFolder, manifestHashes);
                Report(progress, BackupImportStage.Completed, 100, "Backup importado e aberto com sucesso.");
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
            installationSpan.Complete(bytes: inspection.DatabaseBytes);

            span.Complete(bytes: inspection.DatabaseBytes);
            return recoverableBackup;
        }
        finally
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }

    public static int CleanupStaleTemporaryDirectories(TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        var root = Path.Combine(Path.GetTempPath(), "PNCPKing");
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var threshold = DateTime.UtcNow - maximumAge;
        var removed = 0;
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(path) >= threshold)
                {
                    continue;
                }

                Directory.Delete(path, recursive: true);
                removed++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Another PNCP King process may still own this staging folder.
            }
        }

        return removed;
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
            Mode = SqliteOpenMode.ReadWriteCreate,
            // This database lives in a short-lived staging directory. Pooling
            // would keep data.db open on Windows after Dispose and make the
            // successful export look like a failure during cleanup.
            Pooling = false
        }.ToString();
        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private static async Task CompactSnapshotAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM items;
                DELETE FROM contract_item_snapshots;
                DELETE FROM price_cache_contracts;
                UPDATE price_cache_control
                   SET authorized = 0, enabled = 0, paused = 0, status = $disabled,
                       last_error = '', authorized_at = NULL, last_started_at = NULL,
                       last_completed_at = NULL,
                       updated_at = $now
                 WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$disabled", (int)PriceCacheStatus.Disabled);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var optimize = connection.CreateCommand())
        {
            optimize.CommandText = "INSERT INTO items_fts(items_fts) VALUES('optimize');";
            await optimize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DisableImportedCompactCacheAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM price_cache_contracts;
            UPDATE price_cache_control
               SET authorized = 0, enabled = 0, paused = 0, status = $disabled,
                   last_error = '', authorized_at = NULL, last_started_at = NULL,
                   last_completed_at = NULL, updated_at = $now
             WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$disabled", (int)PriceCacheStatus.Disabled);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await ValidateDatabaseAsync(
            databasePath,
            SqliteContractRepository.CurrentSchemaVersion,
            checkIntegrity: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(long Items, long Results, long PriceCacheContracts)> ReadDatabaseCountsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM items),
                   (SELECT COUNT(*) FROM item_results),
                   (SELECT COUNT(*) FROM price_cache_contracts);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task ValidateDatabaseAsync(
        string path,
        int expectedSchemaVersion,
        bool checkIntegrity,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (checkIntegrity)
        {
            await using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(
                await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Falha de integridade do SQLite: {result}");
            }
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
        return await ComputeSha256Async(path, progress: null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ProgressBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ProgressBufferBytes];
        long processed = 0;
        var lastReported = 0L;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            processed += read;
            if (progress is not null &&
                (processed - lastReported >= 16L * ProgressBufferBytes || processed == stream.Length))
            {
                progress(processed, stream.Length);
                lastReported = processed;
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<IReadOnlyList<EvidenceAssetRow>> ReadReferencedEvidenceAssetsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
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

    private static async Task<string> CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long totalBytes,
        Action<long, long> progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ProgressBufferBytes];
        long processed = 0;
        var lastReported = 0L;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            processed += read;
            if (processed - lastReported >= 16L * ProgressBufferBytes || processed == totalBytes)
            {
                progress(processed, totalBytes);
                lastReported = processed;
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static double Scale(long value, long total, double minimum, double maximum)
    {
        if (total <= 0)
        {
            return minimum;
        }

        return minimum + Math.Clamp(value / (double)total, 0d, 1d) * (maximum - minimum);
    }

    private static void Report(
        IProgress<BackupImportProgress>? progress,
        BackupImportStage stage,
        double percentage,
        string message,
        long bytesProcessed = 0,
        long totalBytes = 0) =>
        progress?.Report(new BackupImportProgress(
            stage,
            Math.Clamp(percentage, 0d, 100d),
            message,
            bytesProcessed,
            totalBytes));

    private static long AddSaturated(params long[] values)
    {
        long result = 0;
        foreach (var value in values)
        {
            if (value <= 0)
            {
                continue;
            }

            if (result > long.MaxValue - value)
            {
                return long.MaxValue;
            }

            result += value;
        }

        return result;
    }

    private static long GetAvailableBytes(string root)
    {
        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return $"{value:N1} {units[unit]}";
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
