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
}
