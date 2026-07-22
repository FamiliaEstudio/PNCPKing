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
                DatabaseSha256 = hash
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

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PNCPKing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteSidecar(string databasePath, string suffix)
    {
        var path = databasePath + suffix;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
