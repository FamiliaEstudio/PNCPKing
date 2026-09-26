using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class OfficialUpdateTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static readonly DateOnly Start = Today.AddDays(-9);

    [Fact]
    public async Task V2ExportsFixedWindowAndLateChangesWithoutCatalogAndImportsIntoIndependentDatabase()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await CompleteCoverageAsync(source);

        var current = Contract("current", Today, Today);
        var corrected = Contract("corrected", Today.AddDays(-40), Today);
        var unchangedOld = Contract("unchanged-old", Today.AddDays(-40), Today.AddDays(-40));
        await SaveEmptyListAsync(source, current);
        await SaveEmptyListAsync(source, corrected);
        await SaveEmptyListAsync(source, unchangedOld);
        await SqlAsync(source, "INSERT INTO catalog_entries(catalog_kind,code,description,search_text) VALUES(1,'123','privado','privado')");
        await SqlAsync(destination, "CREATE TABLE user_test(value TEXT); INSERT INTO user_test VALUES('cotação local')");

        var path = Path.Combine(source.Directory, "window.pncpupdate");
        var manifest = await new OfficialUpdateService(source.Repository.DatabasePath).ExportAsync(path);

        Assert.Equal(2, manifest.Format);
        Assert.Equal(29, manifest.Schema);
        Assert.Equal(Start, manifest.StartDate);
        Assert.Equal(Today, manifest.EndDate);
        Assert.Equal(10, manifest.Chunks.Count(chunk => chunk.Kind == "publication-day"));
        Assert.Single(manifest.Chunks, chunk => chunk.Kind == "late-changes");
        Assert.DoesNotContain(manifest.Chunks, chunk => chunk.Key.Contains("catalog", StringComparison.OrdinalIgnoreCase));

        var firstPayload = Path.Combine(source.Directory, "payload.db");
        using (var zip = ZipFile.OpenRead(path))
            zip.GetEntry(manifest.Chunks[0].Entry)!.ExtractToFile(firstPayload);
        await using (var payload = new SqliteConnection($"Data Source={firstPayload};Mode=ReadOnly;Pooling=False"))
        {
            await payload.OpenAsync();
            await using var tables = payload.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%catalog%'";
            Assert.Equal(0L, Convert.ToInt64(await tables.ExecuteScalarAsync()));
        }

        var result = await new OfficialUpdateService(destination.Repository.DatabasePath).ImportAsync(path);
        Assert.True(result.Applied >= 2);
        Assert.NotNull(await destination.Repository.GetContractAsync("current"));
        Assert.NotNull(await destination.Repository.GetContractAsync("corrected"));
        Assert.Null(await destination.Repository.GetContractAsync("unchanged-old"));
        Assert.Equal("cotação local", await ScalarAsync(destination, "SELECT value FROM user_test"));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(destination, "SELECT COUNT(*) FROM catalog_entries")));
    }

    [Fact]
    public async Task MissingAndNewerRecordsApplyWhileEqualNewerOrUnorderedLocalVersionsArePreserved()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await CompleteCoverageAsync(source);
        var incoming = Contract("same", Today, Today) with { Object = "recebido" };
        var missing = Contract("missing", Today, Today) with { Object = "novo" };
        await SaveEmptyListAsync(source, incoming);
        await SaveEmptyListAsync(source, missing);

        await SaveEmptyListAsync(destination, incoming with
        {
            Object = "local mais novo",
            GlobalUpdatedAt = incoming.GlobalUpdatedAt!.Value.AddDays(1)
        });
        var unordered = Contract("unordered", Today, Today) with { GlobalUpdatedAt = null, Object = "origem" };
        await SaveEmptyListAsync(source, unordered);
        await SaveEmptyListAsync(destination, unordered with { Object = "local" });

        var path = Path.Combine(source.Directory, "versions.pncpupdate");
        await new OfficialUpdateService(source.Repository.DatabasePath).ExportAsync(path);
        await new OfficialUpdateService(destination.Repository.DatabasePath).ImportAsync(path);

        Assert.Equal("local mais novo", (await destination.Repository.GetContractAsync("same"))!.Object);
        Assert.Equal("novo", (await destination.Repository.GetContractAsync("missing"))!.Object);
        Assert.Equal("local", (await destination.Repository.GetContractAsync("unordered"))!.Object);
    }

    [Fact]
    public async Task PartialExportTransfersCompletedWorkAndResumablePublicationPage()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await source.Repository.EnsureCoverageWindowAsync(Start, Today, [6]);
        await source.Repository.SetCoverageStatusAsync(Start, Today.AddDays(-1), 6, "ALL", CoverageStatus.Complete);
        await source.Repository.SetCoverageStatusAsync(Today, Today, 6, "ALL", CoverageStatus.Partial);

        var missingList = Contract("missing-list", Today, Today);
        var pendingResult = Contract("pending-result", Today, Today);
        var completeResult = Contract("complete-result", Today, Today);
        await source.Repository.UpsertContractsAsync([missingList, pendingResult, completeResult]);
        await source.Repository.UpsertItemsAsync(pendingResult.PncpId, [PriceCacheTests.Item(pendingResult, 1)], false);
        await source.Repository.UpsertItemsAsync(completeResult.PncpId, [PriceCacheTests.Item(completeResult, 1)], false);
        await source.Repository.ReplaceItemResultsAsync(completeResult.PncpId, 1,
            [PriceCacheTests.Result(completeResult, 1, 1, true)]);
        await new SqliteQuotationRepository(source.Repository.DatabasePath).CreateProjectAsync("Cotação privada");
        var localQuotation = await new SqliteQuotationRepository(destination.Repository.DatabasePath)
            .CreateProjectAsync("Cotação do destinatário");

        var key = $"Publication:{Today:yyyyMMdd}:{Today:yyyyMMdd}:m6:ufALL";
        await source.Repository.SavePartitionCheckpointAsync(new SyncPartitionCheckpoint
        {
            PartitionKey = key,
            Mode = SyncMode.Publication,
            StartDate = Today,
            EndDate = Today,
            ModalityId = 6,
            Uf = "ALL",
            NextPage = 3,
            TotalPages = 5,
            Status = SyncPartitionStatus.Partial
        });

        var path = Path.Combine(source.Directory, "partial.pncpupdate");
        var manifest = await new OfficialUpdateService(source.Repository.DatabasePath).ExportAsync(path);
        var current = manifest.Chunks.Single(chunk => chunk.Date == Today);
        Assert.Equal(3, current.Contracts);
        Assert.Equal(2, current.ItemSnapshots);
        Assert.Equal(2, current.Items);
        Assert.Equal(1, current.ResultSnapshots);
        Assert.Equal(1, current.Results);
        Assert.Equal(0, current.CoverageCells);

        await new OfficialUpdateService(destination.Repository.DatabasePath).ImportAsync(path);
        Assert.NotNull(await destination.Repository.GetContractAsync(missingList.PncpId));
        Assert.Null(await destination.Repository.GetItemSnapshotAsync(missingList.PncpId));
        Assert.NotNull(await destination.Repository.GetItemSnapshotAsync(pendingResult.PncpId));
        Assert.Equal(ItemHydrationStatus.Stale,
            (await destination.Repository.GetItemAsync(pendingResult.PncpId, 1))!.HydrationStatus);
        Assert.Single((await destination.Repository.GetCachedItemResultsAsync(completeResult.PncpId, 1))!.Results);
        Assert.Equal(3, await destination.Repository.GetPartitionNextPageAsync(key));
        Assert.Contains(await destination.Repository.GetIncompleteCoverageAsync(Today, Today, 100, true),
            cell => cell.Date == Today && cell.ModalityId == 6);
        Assert.True(await destination.Repository.IsCoverageCompleteAsync(Start, Today.AddDays(-1)));
        Assert.False(await destination.Repository.IsCoverageCompleteAsync(Today, Today));
        var destinationQuotations = await new SqliteQuotationRepository(destination.Repository.DatabasePath)
            .GetProjectsAsync();
        Assert.Equal(localQuotation.Id, Assert.Single(destinationQuotations).Id);

        await using var completeDestination = await TestDatabase.CreateAsync();
        await completeDestination.Repository.EnsureCoverageWindowAsync(Today, Today, [6]);
        await completeDestination.Repository.SetCoverageStatusAsync(Today, Today, 6, "ALL", CoverageStatus.Complete);
        await new OfficialUpdateService(completeDestination.Repository.DatabasePath).ImportAsync(path);
        Assert.True(await completeDestination.Repository.IsCoverageCompleteAsync(Today, Today));
        Assert.Null(await completeDestination.Repository.GetPartitionNextPageAsync(key));
    }

    [Fact]
    public async Task EmptyListsAndEmptyResultsRemainCompleteAtomicSnapshots()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await CompleteCoverageAsync(source);
        var emptyList = Contract("empty-list", Today, Today);
        await SaveEmptyListAsync(source, emptyList);
        var emptyResults = Contract("empty-results", Today, Today);
        await source.Repository.UpsertContractsAsync([emptyResults]);
        await source.Repository.UpsertItemsAsync(emptyResults.PncpId, [PriceCacheTests.Item(emptyResults, 1)], false);
        await source.Repository.ReplaceItemResultsAsync(emptyResults.PncpId, 1, []);

        var path = Path.Combine(source.Directory, "empty.pncpupdate");
        await new OfficialUpdateService(source.Repository.DatabasePath).ExportAsync(path);
        await new OfficialUpdateService(destination.Repository.DatabasePath).ImportAsync(path);

        Assert.NotNull(await destination.Repository.GetItemSnapshotAsync(emptyList.PncpId));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(destination,
            "SELECT COUNT(*) FROM items WHERE contract_id='empty-list'")));
        var item = await destination.Repository.GetItemAsync(emptyResults.PncpId, 1);
        Assert.Equal(ItemHydrationStatus.Complete, item!.HydrationStatus);
        Assert.Empty((await destination.Repository.GetCachedItemResultsAsync(emptyResults.PncpId, 1))!.Results);
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(destination,
            "SELECT result_count FROM official_result_snapshots WHERE contract_id='empty-results' AND item_number=1")));
    }

    [Fact]
    public async Task ReimportAndOverlappingUnchangedWindowUseReceiptsWithoutExtraction()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await CompleteCoverageAsync(source);
        await SaveEmptyListAsync(source, Contract("one", Today, Today));
        var exporter = new OfficialUpdateService(source.Repository.DatabasePath);
        var importer = new OfficialUpdateService(destination.Repository.DatabasePath);
        var first = Path.Combine(source.Directory, "first.pncpupdate");
        await exporter.ExportAsync(first);
        await importer.ImportAsync(first);

        var messages = new List<string>();
        var repeated = await importer.ImportAsync(first, new InlineProgress(messages.Add));
        Assert.Equal(0, repeated.Applied);
        Assert.DoesNotContain(messages, message => message.StartsWith("Lendo ", StringComparison.Ordinal));

        var second = Path.Combine(source.Directory, "second.pncpupdate");
        await exporter.ExportAsync(second);
        messages.Clear();
        var overlap = await importer.ImportAsync(second, new InlineProgress(messages.Add));
        Assert.Equal(0, overlap.Applied);
        Assert.DoesNotContain(messages, message => message.StartsWith("Lendo ", StringComparison.Ordinal));
        Assert.Equal(10L, Convert.ToInt64(await ScalarAsync(destination,
            "SELECT COUNT(*) FROM official_update_chunks WHERE completed=1")));
    }

    [Fact]
    public async Task CompactBackupClearsReceiptsSoTheSamePackageCanRestoreDiscardedOfficialLists()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await CompleteCoverageAsync(source);
        var contract = Contract("backup-item", Today, Today);
        await source.Repository.UpsertContractsAsync([contract]);
        await source.Repository.UpsertItemsAsync(contract.PncpId, [PriceCacheTests.Item(contract, 1)], false);
        await source.Repository.ReplaceItemResultsAsync(contract.PncpId, 1,
            [PriceCacheTests.Result(contract, 1, 1, true)]);
        var package = Path.Combine(source.Directory, "backup-source.pncpupdate");
        await new OfficialUpdateService(source.Repository.DatabasePath).ExportAsync(package);
        var updates = new OfficialUpdateService(destination.Repository.DatabasePath);
        await updates.ImportAsync(package);

        var backup = Path.Combine(destination.Directory, "compact.pncpking");
        var backups = new BackupService(destination.Repository);
        await backups.ExportAsync(backup, BackupProfile.Compact);
        await backups.ImportAsync(backup);
        Assert.Null(await destination.Repository.GetItemAsync(contract.PncpId, 1));

        var progress = new List<string>();
        await updates.ImportAsync(package, new InlineProgress(progress.Add));
        Assert.Contains(progress, message => message.StartsWith("Lendo ", StringComparison.Ordinal));
        Assert.NotNull(await destination.Repository.GetItemAsync(contract.PncpId, 1));
        Assert.Single((await destination.Repository.GetCachedItemResultsAsync(contract.PncpId, 1))!.Results);
    }

    [Fact]
    public async Task TransportCorruptionFailsBeforeCurrentBlockMutationAndV1IsRejectedClearly()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await CompleteCoverageAsync(source);
        await SaveEmptyListAsync(source, Contract("first-day", Start, Start));
        var service = new OfficialUpdateService(source.Repository.DatabasePath);
        var path = Path.Combine(source.Directory, "corrupt.pncpupdate");
        var manifest = await service.ExportAsync(path);
        var first = manifest.Chunks.Single(chunk => chunk.Date == Start);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry(first.Entry)!;
            var length = checked((int)entry.Length);
            entry.Delete();
            await using var output = zip.CreateEntry(first.Entry, CompressionLevel.NoCompression).Open();
            await output.WriteAsync(Enumerable.Repeat((byte)0x5a, length).ToArray());
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OfficialUpdateService(destination.Repository.DatabasePath).ImportAsync(path));
        Assert.Contains("Checksum", error.Message);
        Assert.Null(await destination.Repository.GetContractAsync("first-day"));

        var legacy = Path.Combine(source.Directory, "legacy.pncpupdate");
        await using (var file = File.Create(legacy))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            await using (var metadata = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
            {
                await metadata.WriteAsync("{\"format\":1}");
                await metadata.FlushAsync();
            }
            await using var payload = zip.CreateEntry("updates.db").Open();
            await payload.WriteAsync(new byte[] { 1 });
        }
        var legacyError = await Assert.ThrowsAsync<InvalidDataException>(() => OfficialUpdateService.ReadManifestAsync(legacy));
        Assert.Contains("v1 incompatível", legacyError.Message);
    }

    private static ContractRecord Contract(string id, DateOnly publication, DateOnly updated) =>
        PriceCacheTests.RecentContract(id, publication, id.Sum(character => character) % 27 + 1) with
        {
            GlobalUpdatedAt = updated.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)
        };

    private static async Task CompleteCoverageAsync(TestDatabase database)
    {
        await database.Repository.EnsureCoverageWindowAsync(Start, Today, [6]);
        await database.Repository.SetCoverageStatusAsync(Start, Today, 6, "ALL", CoverageStatus.Complete, 0);
    }

    private static async Task SaveEmptyListAsync(TestDatabase database, ContractRecord contract)
    {
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [], false);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static async Task SqlAsync(TestDatabase database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(TestDatabase database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
