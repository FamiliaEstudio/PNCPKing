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
    public async Task FullBackupWithoutQuotationsKeepsHistoryButCannotReplaceLocalQuotations()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = PriceCacheTests.RecentContract("shared-history", today, 1);
        await source.Repository.UpsertContractsAsync([contract]);
        await source.Repository.UpsertItemsAsync(contract.PncpId, [PriceCacheTests.Item(contract, 1)], false);
        await source.Repository.ReplaceItemResultsAsync(contract.PncpId, 1,
            [PriceCacheTests.Result(contract, 1, 1, true)]);
        var sourceQuotations = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var sourceProject = await sourceQuotations.CreateProjectAsync("PrivateQuotationDoNotDistribute");
        await sourceQuotations.CreateLineAsync(sourceProject.Id,
            new QuotationLineInput("PrivateItemDoNotDistribute", 1m, "unidade", null, null));
        var path = Path.Combine(source.Directory, "history-without-quotations.pncpking");

        await new BackupService(source.Repository).ExportAsync(path, BackupProfile.FullWithoutQuotations);

        Assert.Single(await sourceQuotations.GetProjectsAsync());
        using (var archive = ZipFile.OpenRead(path))
        {
            var manifest = await ReadManifestAsync(path);
            Assert.Equal(3, manifest.ArchiveFormatVersion);
            Assert.Equal(BackupProfile.FullWithoutQuotations, manifest.BackupProfile);
            Assert.Empty(manifest.EvidenceAssets);
            await using (var entry = archive.GetEntry("data.db")!.Open())
            {
                using var bytes = new MemoryStream();
                await entry.CopyToAsync(bytes);
                Assert.True(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)
                    .IndexOf("PrivateQuotationDoNotDistribute"u8) < 0);
            }
            var snapshot = Path.Combine(source.Directory, "distributed.db");
            archive.GetEntry("data.db")!.ExtractToFile(snapshot);
            Assert.Empty(await new SqliteQuotationRepository(snapshot).GetProjectsAsync());
            await using (var connection = new SqliteConnection($"Data Source={snapshot};Mode=ReadOnly;Pooling=False"))
            {
                await connection.OpenAsync();
                await using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM quotation_lines;";
                Assert.Equal(0L, await count.ExecuteScalarAsync());
            }
        }

        var destinationQuotations = new SqliteQuotationRepository(destination.Repository.DatabasePath);
        var localProject = await destinationQuotations.CreateProjectAsync("Cotação local preservada");
        var importer = new BackupService(destination.Repository);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(path));
        Assert.Contains("já contém cotações", error.Message);
        Assert.Equal(localProject.Id, Assert.Single(await destinationQuotations.GetProjectsAsync()).Id);

        await destinationQuotations.DeleteProjectAsync(localProject.Id);
        await importer.ImportAsync(path);
        Assert.NotNull(await destination.Repository.GetContractAsync(contract.PncpId));
        Assert.Single((await destination.Repository.GetCachedItemResultsAsync(contract.PncpId, 1))!.Results);
        Assert.Empty(await destinationQuotations.GetProjectsAsync());
    }

    [Theory]
    [InlineData(BackupProfile.Full)]
    [InlineData(BackupProfile.Compact)]
    public async Task Import_PrunesExpiredContractsWithoutCompactingAndPreservesTheOriginalArchive(BackupProfile profile)
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var old = PriceCacheTests.RecentContract("expired-backup", DataWindow.Start(today).AddDays(-1), 1);
        var recent = PriceCacheTests.RecentContract("recent-backup", today, 2);
        await database.Repository.UpsertContractsAsync([old, recent]);
        await DataRetentionTests.DowngradeTo26Async(database.Repository.DatabasePath);
        await database.Repository.InitializeAsync();
        var service = new BackupService(database.Repository);
        var path = Path.Combine(database.Directory, "old-window.pncpking");
        await service.ExportAsync(path, profile);
        var beforeHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var progress = new RecordingProgress<BackupImportProgress>();
        var recovery = await service.ImportAsync(path, progress);
        Assert.True(File.Exists(recovery));
        Assert.Null(await database.Repository.GetContractAsync(old.PncpId));
        Assert.NotNull(await database.Repository.GetContractAsync(recent.PncpId));
        Assert.Equal(beforeHash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.Contains(progress.Values, item => item.Stage == BackupImportStage.Completed && item.Message.Contains("11 meses"));
        Assert.Equal(1, (await database.Repository.GetCountsAsync()).Contracts);

        // A migrated snapshot has compaction pending. Import must prune expired data
        // without VACUUM or the full integrity checks coupled to compaction.
        await using var connection = new SqliteConnection(
            $"Data Source={database.Repository.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT retention_compaction_completed FROM maintenance_state WHERE id = 1;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        Assert.Contains(progress.Values, item =>
            item.Stage == BackupImportStage.ApplyingRetention && item.IsIndeterminate && item.CanCancel);
        Assert.DoesNotContain(progress.Values, item => item.Message.Contains("Compactação concluída"));
        Assert.Equal(progress.Values.Select(item => item.Percentage).Order(),
            progress.Values.Select(item => item.Percentage));
    }

    [Theory]
    [InlineData(BackupImportStage.CheckingEvidence)]
    [InlineData(BackupImportStage.ApplyingRetention)]
    public async Task Import_CancellationBeforeRetentionPreservesCurrentDatabase(BackupImportStage stage)
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new BackupService(database.Repository);
        var path = Path.Combine(database.Directory, "cancel-before-retention.pncpking");
        await service.ExportAsync(path);
        await database.Repository.UpsertContractsAsync([
            PriceCacheTests.RecentContract("preserved", DateOnly.FromDateTime(DateTime.Today), 1)
        ]);
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress<BackupImportProgress>(item =>
        {
            if (item.Stage == stage)
                cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ImportAsync(path, progress, cancellation.Token));

        Assert.NotNull(await database.Repository.GetContractAsync("preserved"));
        Assert.Empty(service.GetRecoveryBackups());
        Assert.Empty(System.IO.Directory.GetDirectories(database.Directory, ".pncpking-import-*"));
    }

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
        Assert.Equal(BackupProfile.Full, manifest.BackupProfile);
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
        var exportProgress = new RecordingProgress<BackupExportProgress>();
        var exportInspection = await service.InspectExportAsync(backupPath, BackupProfile.Compact);
        await service.ExportAsync(backupPath, BackupProfile.Compact, exportProgress);
        Assert.Contains(("backup", "export-integrity"), telemetry.Measurements);
        Assert.True(exportInspection.CanExport);
        Assert.Equal(BackupProfile.Compact, exportInspection.Profile);
        Assert.Contains(exportProgress.Values, item => item.Stage == BackupExportStage.Snapshotting);
        Assert.Contains(exportProgress.Values, item => item.Stage == BackupExportStage.CheckingIntegrity);
        Assert.Contains(exportProgress.Values, item =>
            item.Stage == BackupExportStage.Completed && item.Percentage == 100d);
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
        Assert.Contains(("backup", "import-activation"), telemetry.Measurements);
        Assert.DoesNotContain(("backup", "import-full-integrity"), telemetry.Measurements);
    }

    [Fact]
    public async Task Import_RejectsBackupWithoutOriginIntegrityProof()
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

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new BackupService(database.Repository, telemetry).ImportAsync(backupPath));

        Assert.Contains("origem", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(("backup", "import-full-integrity"), telemetry.Measurements);
        Assert.DoesNotContain(("backup", "import-origin-validation"), telemetry.Measurements);
    }

    [Fact]
    public async Task Import_MigratesOriginValidatedBackupWithoutReceiverIntegrityScan()
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
        Assert.Contains(("backup", "import-migration"), telemetry.Measurements);
        Assert.Contains(("backup", "import-migration-validation"), telemetry.Measurements);
        Assert.DoesNotContain(("backup", "import-full-integrity"), telemetry.Measurements);
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
    public async Task Import_RejectsCorruptedEvidenceBeforeReplacingCurrentDatabase()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("preserved", "Base que deve permanecer", "SP", 1)
        ]);
        var service = new BackupService(database.Repository);
        var backupPath = Path.Combine(database.Directory, "evidence-corrupted.pncpking");
        await service.ExportAsync(backupPath);
        var expectedHash = new string('0', 64);
        ReplaceEntry(backupPath, "manifest.json", bytes =>
        {
            var manifest = JsonSerializer.Deserialize<DatasetManifest>(bytes)! with
            {
                EvidenceAssets =
                [
                    new EvidenceAssetManifest
                    {
                        Sha256 = expectedHash,
                        ArchivePath = $"internet-evidence/{expectedHash}.png",
                        ByteLength = 1
                    }
                ]
            };
            return JsonSerializer.SerializeToUtf8Bytes(manifest);
        });
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update))
        {
            var evidence = archive.CreateEntry($"internet-evidence/{expectedHash}.png");
            await using var output = evidence.Open();
            await output.WriteAsync(new byte[] { 1 });
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(backupPath));

        Assert.NotNull(await database.Repository.GetContractAsync("preserved"));
        Assert.Empty(service.GetRecoveryBackups());
    }

    [Fact]
    public async Task Export_CancellationRemovesPartialArchive()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("cancel", "Cancelar backup", "SP", 1)
        ]);
        var service = new BackupService(database.Repository);
        var backupPath = Path.Combine(database.Directory, "cancelled.pncpking");
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress<BackupExportProgress>(item =>
        {
            if (item.Stage == BackupExportStage.ArchivingDatabase)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExportAsync(backupPath, BackupProfile.Full, progress, cancellation.Token));

        Assert.False(File.Exists(backupPath));
        Assert.False(File.Exists(backupPath + ".partial"));
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
    public async Task FullBackup_RoundTripsItemsResultsSnapshotsAndCheckpoints()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var current = PriceCacheTests.RecentContract("full-current", today, 1);
        var stale = PriceCacheTests.RecentContract("full-stale", today, 2);
        await database.Repository.UpsertContractsAsync([current, stale]);
        var currentItem = PriceCacheTests.Item(current, 1) with
        {
            Description = "Coffee Break completo",
            HydrationStatus = ItemHydrationStatus.Complete
        };
        var staleItem = PriceCacheTests.Item(stale, 1) with
        {
            Description = "Café invalidado",
            HydrationStatus = ItemHydrationStatus.Complete
        };
        await database.Repository.UpsertItemsAsync(current.PncpId, [currentItem], false);
        await database.Repository.UpsertItemsAsync(stale.PncpId, [staleItem], false);
        await database.Repository.ReplaceItemResultsAsync(current.PncpId, 1,
        [
            PriceCacheTests.Result(current, 1, 1, true),
            PriceCacheTests.Result(current, 1, 2, true) with { SupplierName = "Segundo vencedor" }
        ]);
        await database.Repository.ReplaceItemResultsAsync(stale.PncpId, 1,
        [
            PriceCacheTests.Result(stale, 1, 1, true)
        ]);
        var changedStale = stale with { GlobalUpdatedAt = stale.GlobalUpdatedAt?.AddMinutes(1) };
        await database.Repository.UpsertContractsAsync([changedStale]);
        await database.Repository.SavePartitionProgressAsync("backup-checkpoint", 7, false);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, DataWindow.Start(today), today);
        await cache.PrepareWindowAsync(DataWindow.Start(today), today);
        await cache.MarkContractDownloadingAsync(current.PncpId, true);
        await cache.MarkContractCompleteAsync(current.PncpId, current.GlobalUpdatedAt);

        var service = new BackupService(database.Repository);
        var backupPath = Path.Combine(database.Directory, "complete.pncpking");
        await service.ExportAsync(backupPath, BackupProfile.Full);
        var manifest = await ReadManifestAsync(backupPath);
        await database.Repository.ClearItemCacheAsync();

        var recovery = await service.ImportAsync(backupPath);

        var restoredCurrent = await database.Repository.GetCachedItemResultsAsync(current.PncpId, 1);
        var restoredStale = await database.Repository.GetCachedItemResultsAsync(stale.PncpId, 1);
        Assert.NotNull(restoredCurrent);
        Assert.Equal(2, restoredCurrent.Results.Count);
        Assert.Equal("Segundo vencedor", restoredCurrent.Results[1].SupplierName);
        Assert.NotNull(await database.Repository.GetItemSnapshotAsync(current.PncpId));
        Assert.NotNull(restoredStale);
        Assert.Equal(ItemHydrationStatus.Stale, restoredStale.Item.HydrationStatus);
        Assert.Null(await database.Repository.GetItemSnapshotAsync(stale.PncpId));
        Assert.Equal(7, await database.Repository.GetPartitionNextPageAsync("backup-checkpoint"));
        Assert.Single(await database.Repository.SearchItemsAsync(current.PncpId, "Coffee Break"));
        Assert.True((await cache.GetPolicyAsync()).Authorized);
        Assert.Equal(2, manifest.ArchiveFormatVersion);
        Assert.Equal(BackupProfile.Full, manifest.BackupProfile);
        Assert.Equal("PRAGMA integrity_check", manifest.DatabaseIntegrityKind);
        Assert.NotNull(manifest.DatabaseIntegrityValidatedAt);
        Assert.True(manifest.DatabaseBytes > 0);
        Assert.Contains(service.GetRecoveryBackups(), item => item.Path == recovery);
    }

    [Fact]
    public async Task RecoveryCleanup_DeletesOnlyExactCurrentDatabaseRecoveryPattern()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new BackupService(database.Repository);
        var valid = $"{database.Repository.DatabasePath}.before-import-" +
                    $"20260830-120000-000-{Guid.NewGuid():N}.bak";
        var decoy = database.Repository.DatabasePath + ".before-import-manual.bak";
        await File.WriteAllBytesAsync(valid, [1, 2, 3]);
        await File.WriteAllBytesAsync(decoy, [4, 5]);

        var deleted = service.DeleteRecoveryBackups();

        Assert.Equal(1, deleted.Count);
        Assert.Equal(3, deleted.Bytes);
        Assert.False(File.Exists(valid));
        Assert.True(File.Exists(decoy));
    }

    [Fact]
    public async Task CompactBackup_RemovesReconstructibleCacheAndImportsDisabled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = PriceCacheTests.RecentContract("bulk", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, DataWindow.Start(today), today);
        await cache.SetNationalPriceIndexAuthorizationAsync(true, DataWindow.Start(today), today);
        await cache.PrepareWindowAsync(DataWindow.Start(today), today);
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
        await using var connection = new SqliteConnection(
            $"Data Source={database.Repository.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM items_fts),
                   (SELECT COUNT(*) FROM contract_item_snapshots),
                   (SELECT COUNT(*) FROM price_cache_contracts),
                   (SELECT sql FROM sqlite_master
                     WHERE type = 'table' AND name = 'items_fts');
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
        Assert.Contains("prefix='2 3'", reader.GetString(3), StringComparison.Ordinal);
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

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
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
