using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class BackupTests
{
    [Fact]
    public async Task Backup_RestoresValidatedSnapshotAndPreservesRecoveryCopy()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("original", "Objeto original", "SP", 1)
        ]);
        var service = new BackupService(database.Repository);
        var backupPath = Path.Combine(database.Directory, "backup.pncpking");
        await service.ExportAsync(backupPath);
        var manifest = await ReadManifestAsync(backupPath);

        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("later", "Registro posterior", "BA", 2)
        ]);
        var recoveryPath = await service.ImportAsync(backupPath);
        var result = await database.Repository.SearchAsync(new SearchQuery(string.Empty, GeoScope.All));

        Assert.True(File.Exists(recoveryPath));
        Assert.True(manifest.DatabaseIntegrityValidatedAtExport is true);
        Assert.Single(result.Results);
        Assert.Equal("original", result.Results[0].PncpId);
    }

    [Fact]
    public async Task Import_InspectsSpaceAndReportsObservablePhases()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("progress", "Progresso de importação", "SP", 1)
        ]);
        var telemetry = new RecordingPerformanceTelemetry();
        var service = new BackupService(database.Repository, telemetry);
        var backupPath = Path.Combine(database.Directory, "progress.pncpking");
        await service.ExportAsync(backupPath, BackupProfile.Compact);
        Assert.Contains(("backup", "export-integrity"), telemetry.Measurements);
        telemetry.Clear();
        var inspection = await service.InspectAsync(backupPath);
        var progress = new RecordingProgress<BackupImportProgress>();

        await service.ImportAsync(backupPath, progress);

        Assert.Equal(SqliteContractRepository.CurrentSchemaVersion, inspection.SchemaVersion);
        Assert.Equal(BackupProfile.Compact, inspection.Profile);
        Assert.True(inspection.DatabaseBytes > 0);
        Assert.True(inspection.HasEnoughSpace);
        Assert.Contains(progress.Values, item => item.Stage == BackupImportStage.Extracting);
        Assert.Contains(progress.Values, item => item.Stage == BackupImportStage.VerifyingChecksum);
        Assert.Contains(progress.Values, item => item.Stage == BackupImportStage.CheckingIntegrity);
        Assert.Contains(progress.Values, item => item.Stage == BackupImportStage.Completed && item.Percentage == 100d);
        Assert.Contains(("backup", "import-extraction"), telemetry.Measurements);
        Assert.Contains(("backup", "import-origin-validation"), telemetry.Measurements);
        Assert.Contains(("backup", "import-installation"), telemetry.Measurements);
        Assert.DoesNotContain(("backup", "import-full-integrity"), telemetry.Measurements);
    }

    [Fact]
    public async Task Import_LegacyBackupKeepsFullLocalIntegrityCheck()
    {
        await using var database = await TestDatabase.CreateAsync();
        var backupPath = Path.Combine(database.Directory, "legacy.pncpking");
        await new BackupService(database.Repository).ExportAsync(backupPath);
        ReplaceEntry(backupPath, "manifest.json", bytes =>
        {
            var manifest = JsonSerializer.Deserialize<DatasetManifest>(bytes)! with
            {
                DatabaseIntegrityValidatedAtExport = null
            };
            return JsonSerializer.SerializeToUtf8Bytes(manifest);
        });
        var telemetry = new RecordingPerformanceTelemetry();

        await new BackupService(database.Repository, telemetry).ImportAsync(backupPath);

        Assert.Contains(("backup", "import-full-integrity"), telemetry.Measurements);
        Assert.DoesNotContain(("backup", "import-origin-validation"), telemetry.Measurements);
    }

    [Fact]
    public async Task Import_ValidatesAgainAfterMigratingOriginValidatedBackup()
    {
        await using var database = await TestDatabase.CreateAsync();
        var backupPath = Path.Combine(database.Directory, "schema-20.pncpking");
        await new BackupService(database.Repository).ExportAsync(backupPath);
        await RewriteBackupSchemaVersionAsync(backupPath, 20);
        var telemetry = new RecordingPerformanceTelemetry();

        await new BackupService(database.Repository, telemetry).ImportAsync(backupPath);
        var initialization = await database.Repository.InitializeAsync();

        Assert.Equal(SqliteContractRepository.CurrentSchemaVersion, initialization.CurrentVersion);
        Assert.Contains(("backup", "import-origin-validation"), telemetry.Measurements);
        Assert.Contains(("backup", "import-full-integrity"), telemetry.Measurements);
    }

    [Fact]
    public async Task Import_RejectsCorruptedAndIncompatibleBackups()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new BackupService(database.Repository);
        var corrupted = Path.Combine(database.Directory, "corrupted.pncpking");
        await service.ExportAsync(corrupted);
        ReplaceEntry(corrupted, "data.db", bytes =>
        {
            bytes[^1] ^= 0xFF;
            return bytes;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(corrupted));

        var incompatible = Path.Combine(database.Directory, "incompatible.pncpking");
        await service.ExportAsync(incompatible);
        ReplaceEntry(incompatible, "manifest.json", bytes =>
        {
            var manifest = JsonSerializer.Deserialize<DatasetManifest>(bytes)! with
            {
                SchemaVersion = SqliteContractRepository.CurrentSchemaVersion + 1
            };
            return JsonSerializer.SerializeToUtf8Bytes(manifest);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(incompatible));
    }

    [Fact]
    public async Task Export_CanReplaceBackupWithoutLeakingTemporaryDatabaseHandles()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("repetido", "Exportação repetida", "SP", 1)
        ]);
        var service = new BackupService(database.Repository);
        var backupPath = Path.Combine(database.Directory, "repetido.pncpking");

        await service.ExportAsync(backupPath);
        await service.ExportAsync(backupPath);

        using var archive = ZipFile.OpenRead(backupPath);
        Assert.NotNull(archive.GetEntry("data.db"));
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.False(File.Exists(backupPath + ".partial"));
    }

    [Fact]
    public async Task CompactBackup_RemovesReconstructibleCacheAndImportsDisabled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = PriceCacheTests.RecentContract("bulk", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.SetNationalPriceIndexAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);
        await cache.MarkContractDownloadingAsync(contract.PncpId, true);
        var items = Enumerable.Range(1, 250).Select(number =>
            PriceCacheTests.Item(contract, number) with
            {
                Description = $"Café especial {number} {Guid.NewGuid():N}"
            }).ToArray();
        await database.Repository.UpsertItemsAsync(contract.PncpId, items, false);
        foreach (var item in items)
        {
            await database.Repository.ReplaceItemResultsAsync(
                contract.PncpId,
                item.ItemNumber,
                [PriceCacheTests.Result(contract, item.ItemNumber, 1, true)]);
        }
        await cache.MarkContractCompleteAsync(contract.PncpId, contract.GlobalUpdatedAt);

        var service = new BackupService(database.Repository);
        var full = Path.Combine(database.Directory, "full.pncpking");
        var compact = Path.Combine(database.Directory, "compact.pncpking");
        await service.ExportAsync(full, BackupProfile.Full);
        await service.ExportAsync(compact, BackupProfile.Compact);

        using (var archive = ZipFile.OpenRead(compact))
        {
            await using var stream = archive.GetEntry("manifest.json")!.Open();
            var manifest = await JsonSerializer.DeserializeAsync<DatasetManifest>(stream);
            Assert.Equal(BackupProfile.Compact, manifest!.BackupProfile);
            Assert.True(manifest.DatabaseIntegrityValidatedAtExport is true);
            Assert.False(manifest.ContainsPriceCache);
            Assert.Equal(0, manifest.ItemCount);
            Assert.Equal(0, manifest.ResultCount);
        }
        Assert.True(new FileInfo(compact).Length < new FileInfo(full).Length);

        await service.ImportAsync(compact);
        var importedCache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var policy = await importedCache.GetPolicyAsync();
        var pricePolicy = await importedCache.GetNationalPriceIndexPolicyAsync();
        var counts = await database.Repository.GetCountsAsync();
        Assert.False(policy.Authorized);
        Assert.False(policy.Enabled);
        Assert.False(pricePolicy.Authorized);
        Assert.False(pricePolicy.Enabled);
        Assert.Equal(0, counts.Items);
        Assert.Equal(0, counts.Results);
    }

    private static async Task<DatasetManifest> ReadManifestAsync(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        await using var stream = archive.GetEntry("manifest.json")!.Open();
        return (await JsonSerializer.DeserializeAsync<DatasetManifest>(stream))!;
    }

    private static async Task RewriteBackupSchemaVersionAsync(string archivePath, int schemaVersion)
    {
        byte[] databaseBytes;
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            await using var input = archive.GetEntry("data.db")!.Open();
            using var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            databaseBytes = memory.ToArray();
        }

        var temporaryDatabase = Path.Combine(
            Path.GetDirectoryName(archivePath)!,
            $"rewrite-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(temporaryDatabase, databaseBytes);
            await using (var connection = new SqliteConnection($"Data Source={temporaryDatabase};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE schema_info SET version = $version WHERE id = 1;";
                command.Parameters.AddWithValue("$version", schemaVersion);
                await command.ExecuteNonQueryAsync();
            }

            databaseBytes = await File.ReadAllBytesAsync(temporaryDatabase);
        }
        finally
        {
            File.Delete(temporaryDatabase);
        }

        var hash = Convert.ToHexString(SHA256.HashData(databaseBytes));
        ReplaceEntry(archivePath, "data.db", _ => databaseBytes);
        ReplaceEntry(archivePath, "manifest.json", bytes =>
        {
            var manifest = JsonSerializer.Deserialize<DatasetManifest>(bytes)! with
            {
                SchemaVersion = schemaVersion,
                DatabaseSha256 = hash,
                DatabaseIntegrityValidatedAtExport = true
            };
            return JsonSerializer.SerializeToUtf8Bytes(manifest);
        });
    }

    private static void ReplaceEntry(string archivePath, string entryName, Func<byte[], byte[]> transform)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var oldEntry = archive.GetEntry(entryName) ?? throw new InvalidDataException(entryName);
        byte[] bytes;
        using (var input = oldEntry.Open())
        using (var memory = new MemoryStream())
        {
            input.CopyTo(memory);
            bytes = memory.ToArray();
        }

        oldEntry.Delete();
        var newEntry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var output = newEntry.Open();
        var replacement = transform(bytes);
        output.Write(replacement);
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class RecordingPerformanceTelemetry : IPerformanceTelemetry
    {
        public List<(string Operation, string Phase)> Measurements { get; } = [];

        public PerformanceSpan Begin(string operation, string phase = "total") =>
            new(this, operation, phase);

        public void Record(
            string operation,
            string phase,
            TimeSpan duration,
            long rows = 0,
            long bytes = 0,
            bool succeeded = true,
            string? errorKind = null) =>
            Measurements.Add((operation, phase));

        public PerformanceReport CreateReport() => throw new NotSupportedException();

        public void Clear() => Measurements.Clear();
    }
}
