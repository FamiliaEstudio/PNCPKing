using System.IO.Compression;
using System.Text.Json;
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

        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("later", "Registro posterior", "BA", 2)
        ]);
        var recoveryPath = await service.ImportAsync(backupPath);
        var result = await database.Repository.SearchAsync(new SearchQuery(string.Empty, GeoScope.All));

        Assert.True(File.Exists(recoveryPath));
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
        var service = new BackupService(database.Repository);
        var backupPath = Path.Combine(database.Directory, "progress.pncpking");
        await service.ExportAsync(backupPath, BackupProfile.Compact);
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
        await cache.SetAuthorizationAsync(true, today.AddDays(-89), today);
        await cache.PrepareWindowAsync(today.AddDays(-89), today);
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
            Assert.False(manifest.ContainsPriceCache);
            Assert.Equal(0, manifest.ItemCount);
            Assert.Equal(0, manifest.ResultCount);
        }
        Assert.True(new FileInfo(compact).Length < new FileInfo(full).Length);

        await service.ImportAsync(compact);
        var importedCache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var policy = await importedCache.GetPolicyAsync();
        var counts = await database.Repository.GetCountsAsync();
        Assert.False(policy.Authorized);
        Assert.False(policy.Enabled);
        Assert.Equal(0, counts.Items);
        Assert.Equal(0, counts.Results);
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
}
