using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using SkiaSharp;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

public sealed class FullPriceTransferTests(ITestOutputHelper output)
{
    private const string PrivateMarker = "PRIVATE-SOURCE-NEVER-DISTRIBUTE-9b87";
    private static BackupProfile Profile => BackupProfile.PricesPreservingLocalData;

    [Fact]
    public async Task FullHistory100kPreservesLocalRowsEvidenceAndRecoveryExactlyOnReimport()
        => await RoundTripAsync(100_000);

    // Run explicitly with PNCPKING_FULL_TRANSFER_BENCHMARK=1. No production paths are used.
    [Fact]
    [Trait("Category", "ManualBenchmark")]
    public async Task MillionItemManualBenchmark()
    {
        if (Environment.GetEnvironmentVariable("PNCPKING_FULL_TRANSFER_BENCHMARK") != "1")
        {
            output.WriteLine("Manual benchmark not executed. Set PNCPKING_FULL_TRANSFER_BENCHMARK=1 to enable it.");
            return;
        }
        await RoundTripAsync(1_000_000);
    }

    private async Task RoundTripAsync(int itemCount)
    {
        var timer = Stopwatch.StartNew();
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, itemCount);
        await SeedPricesAsync(destination, 7);
        await SeedLocalAsync(source, PrivateMarker);
        var image = await SeedLocalAsync(destination, "LOCAL-DESTINATION");
        var sourceBefore = await LocalRowsAsync(source.Repository.DatabasePath);
        var before = await LocalRowsAsync(destination.Repository.DatabasePath);
        var path = Path.Combine(source.Directory, "prices.pncpking");
        output.WriteLine($"Synthetic setup ({itemCount:N0} items): {timer.Elapsed}");
        var phase = Stopwatch.StartNew();
        await new BackupService(source.Repository).ExportAsync(path, Profile);
        output.WriteLine($"Export: {phase.Elapsed}; database {new FileInfo(source.Repository.DatabasePath).Length:N0} bytes");
        var archiveHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        Assert.Equal(sourceBefore, await LocalRowsAsync(source.Repository.DatabasePath));
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("evidence", StringComparison.OrdinalIgnoreCase));
            using var data = new MemoryStream();
            await zip.GetEntry("data.db")!.Open().CopyToAsync(data);
            Assert.True(data.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(PrivateMarker)) < 0,
                "Source private text leaked into snapshot pages/free pages.");
            var extracted = Path.Combine(source.Directory, "inspection.db");
            await File.WriteAllBytesAsync(extracted, data.ToArray());
            Assert.Equal(0L, await ScalarAsync(extracted, "SELECT COUNT(*) FROM quotation_projects"));
            Assert.Equal(0L, await ScalarAsync(extracted, "SELECT COUNT(*) FROM guard_workers"));
            Assert.Equal((long)itemCount, await ScalarAsync(extracted, "SELECT COUNT(*) FROM items"));
        }
        var service = new BackupService(destination.Repository);
        var inspection = await service.InspectAsync(path);
        Assert.Equal((itemCount + 19L) / 20, inspection.ContractCount);
        Assert.Equal(itemCount, inspection.ItemCount);
        Assert.Equal(itemCount, inspection.ResultCount);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            phase.Restart();
            var recovery = await service.ImportAsync(path);
            output.WriteLine($"Import {iteration + 1}: {phase.Elapsed}");
            Assert.True(File.Exists(recovery));
            Assert.Equal(before, await LocalRowsAsync(recovery));
            Assert.Equal(iteration == 0 ? 7L : itemCount,
                await ScalarAsync(recovery, "SELECT COUNT(*) FROM items"));
            Assert.Equal(before, await LocalRowsAsync(destination.Repository.DatabasePath));
            Assert.Equal((long)itemCount, await ScalarAsync(destination.Repository.DatabasePath, "SELECT COUNT(*) FROM items"));
            Assert.Equal((long)itemCount, await ScalarAsync(destination.Repository.DatabasePath, "SELECT COUNT(*) FROM item_results"));
            Assert.Equal((itemCount + 19L) / 20, await ScalarAsync(destination.Repository.DatabasePath, "SELECT contract_count FROM dataset_statistics"));
            Assert.Equal((long)itemCount, await ScalarAsync(destination.Repository.DatabasePath, "SELECT item_count FROM dataset_statistics"));
            Assert.Equal((long)itemCount, await ScalarAsync(destination.Repository.DatabasePath, "SELECT result_count FROM dataset_statistics"));
            Assert.Equal("2001-02-03", await ScalarAsync(destination.Repository.DatabasePath,
                "SELECT substr(publication_date,1,10) FROM contracts WHERE pncp_id='history'"));
            Assert.True(await new InternetEvidenceStore(destination.Directory).VerifyAsync(image));
            // Integrity checks belong exclusively to the test, never the receiver's application flow.
            Assert.Equal("ok", await ScalarAsync(destination.Repository.DatabasePath, "PRAGMA integrity_check"));
            Assert.Null(await ScalarAsync(destination.Repository.DatabasePath, "PRAGMA foreign_key_check"));
        }
        Assert.Equal(archiveHash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        await SqlAsync(destination.Repository.DatabasePath, "UPDATE sweet_codes SET expression='changed independently';");
        Assert.Equal(sourceBefore, await LocalRowsAsync(source.Repository.DatabasePath));
        output.WriteLine($"{itemCount:N0} items: {timer.Elapsed}, archive {new FileInfo(path).Length:N0} bytes");
    }

    [Theory]
    [InlineData(BackupImportStage.Extracting)]
    [InlineData(BackupImportStage.VerifyingChecksum)]
    [InlineData(BackupImportStage.CheckingEvidence)]
    [InlineData(BackupImportStage.PreservingCurrentDatabase)]
    public async Task CancellationBeforeActivationLeavesDestinationAndEvidenceUnchanged(BackupImportStage stage)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, 10);
        var image = await SeedLocalAsync(destination, "LOCAL-CANCEL");
        var before = await LocalRowsAsync(destination.Repository.DatabasePath);
        var path = Path.Combine(source.Directory, "cancel.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, Profile);
        var service = new BackupService(destination.Repository);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<BackupImportProgress>(value => { if (value.Stage == stage) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(path, progress, cancellation.Token));
        Assert.Equal(before, await LocalRowsAsync(destination.Repository.DatabasePath));
        Assert.True(await new InternetEvidenceStore(destination.Directory).VerifyAsync(image));
        Assert.Empty(service.GetRecoveryBackups());
        Assert.Empty(Directory.GetDirectories(destination.Directory, ".pncpking-import-*"));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("future-format")]
    [InlineData("unvalidated")]
    public async Task InvalidPackagesFailWithoutChangingDestination(string corruption)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, 10);
        await SeedLocalAsync(destination, "LOCAL-INVALID");
        var before = await LocalRowsAsync(destination.Repository.DatabasePath);
        var path = Path.Combine(source.Directory, "invalid.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, Profile);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            if (corruption == "checksum")
            {
                zip.GetEntry("data.db")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("data.db").Open());
                writer.Write("not a database");
            }
            else
            {
                var entry = zip.GetEntry("manifest.json")!;
                JsonObject manifest;
                using (var reader = new StreamReader(entry.Open())) manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
                var target = corruption == "future-format" ? "ArchiveFormatVersion" : "DatabaseIntegrityValidatedAtExport";
                var key = manifest.Select(pair => pair.Key).Single(name => name.Equals(target, StringComparison.OrdinalIgnoreCase));
                manifest[key] = corruption == "future-format" ? JsonValue.Create(999) : JsonValue.Create(false);
                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                writer.Write(manifest.ToJsonString());
            }
        }
        var service = new BackupService(destination.Repository);
        await Assert.ThrowsAnyAsync<Exception>(() => service.ImportAsync(path));
        Assert.Equal(before, await LocalRowsAsync(destination.Repository.DatabasePath));
        Assert.Empty(service.GetRecoveryBackups());
        Assert.Empty(Directory.GetDirectories(destination.Directory, ".pncpking-import-*"));
    }

    [Fact]
    public async Task FailureImmediatelyBeforeSwapLeavesTheOriginalDatabaseUsable()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, 10);
        await SeedLocalAsync(destination, "LOCAL-FAILURE");
        var before = await LocalRowsAsync(destination.Repository.DatabasePath);
        var path = Path.Combine(source.Directory, "failure.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, Profile);
        var service = new BackupService(destination.Repository);
        var progress = new InlineProgress<BackupImportProgress>(value =>
        {
            if (value.Stage == BackupImportStage.PreservingCurrentDatabase)
                throw new IOException("Synthetic failure before activation");
        });
        var failure = await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(path, progress));
        Assert.Contains("Synthetic failure", failure.Message);
        Assert.Equal(before, await LocalRowsAsync(destination.Repository.DatabasePath));
        Assert.Empty(service.GetRecoveryBackups());
        Assert.Empty(Directory.GetDirectories(destination.Directory, ".pncpking-import-*"));
        // A failed activation must release all gates and handles, not merely leave bytes intact.
        await new SqliteQuotationRepository(destination.Repository.DatabasePath).CreateProjectAsync("Works after failure");
    }

    [Fact]
    public async Task ConcurrentImportersPreserveLocalDataAndDistinctRecoveries()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, 1000);
        await SeedLocalAsync(destination, "LOCAL-CONCURRENT");
        var before = await LocalRowsAsync(destination.Repository.DatabasePath);
        var path = Path.Combine(source.Directory, "parallel.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, Profile);
        var first = new BackupService(destination.Repository);
        var second = new BackupService(new SqliteContractRepository(destination.Repository.DatabasePath));
        var recoveries = await Task.WhenAll(first.ImportAsync(path), second.ImportAsync(path))
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(2, recoveries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var recovery in recoveries)
        {
            Assert.True(File.Exists(recovery));
            Assert.Equal(before, await LocalRowsAsync(recovery));
        }
        Assert.Equal(before, await LocalRowsAsync(destination.Repository.DatabasePath));
        Assert.Equal(1000L, await ScalarAsync(destination.Repository.DatabasePath, "SELECT COUNT(*) FROM items"));
        Assert.Equal("ok", await ScalarAsync(destination.Repository.DatabasePath, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task WriterQueuedDuringGraftCommitsOnlyAfterActivationWithoutLosingItsProject()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, 10);
        await SeedLocalAsync(destination, "LOCAL-WRITER");
        var path = Path.Combine(source.Directory, "writer.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, Profile);
        var quotations = new SqliteQuotationRepository(destination.Repository.DatabasePath);
        Task<QuotationProject>? writer = null;
        var progress = new InlineProgress<BackupImportProgress>(value =>
        {
            if (value.Stage != BackupImportStage.PreservingCurrentDatabase || writer is not null) return;
            writer = quotations.CreateProjectAsync("Concurrent project");
            Assert.False(writer.IsCompleted, "Quotation write must wait until the graft and atomic replacement finish.");
        });
        await new BackupService(destination.Repository).ImportAsync(path, progress);
        Assert.NotNull(writer);
        var created = await writer!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(await quotations.GetProjectsAsync(), project => project.Id == created.Id);
        Assert.Equal(2, (await quotations.GetProjectsAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerSourceDoesNotResurrectAResultRemovedFromItsSnapshot(bool completeSnapshot)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, 2);
        await SeedPricesAsync(destination, 2);
        await SqlAsync(source.Repository.DatabasePath,
            "DELETE FROM item_results WHERE item_number=2; UPDATE contracts SET global_updated_at='2026-11-11T12:00:00+00:00';");
        if (completeSnapshot)
            await SqlAsync(source.Repository.DatabasePath, """
                INSERT INTO contract_item_snapshots(contract_id,fetched_at,item_count,source_global_updated_at)
                VALUES('history','2026-11-11T12:05:00Z',2,'2026-11-11T12:00:00+00:00');
                INSERT INTO official_result_snapshots(contract_id,item_number,parent_version,item_version,result_count)
                SELECT 'history',item_number,'2026-11-11T12:00:00+00:00',source_updated_at,
                    (SELECT COUNT(*) FROM item_results r WHERE r.contract_id=items.contract_id AND r.item_number=items.item_number)
                FROM items;
                """);
        var path=Path.Combine(source.Directory,"removed-result.pncpking");
        await new BackupService(source.Repository).ExportAsync(path,Profile);
        if (completeSnapshot)
        {
            await new BackupService(destination.Repository).ImportAsync(path);
            Assert.Equal(1L, await ScalarAsync(destination.Repository.DatabasePath, "SELECT COUNT(*) FROM item_results"));
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() => new BackupService(destination.Repository).ImportAsync(path));
            Assert.Equal(2L, await ScalarAsync(destination.Repository.DatabasePath, "SELECT COUNT(*) FROM item_results"));
        }
    }

    [Fact]
    public async Task OffsetTimestampCannotHideNewerDestinationData()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, 1);
        await SeedPricesAsync(destination, 1);
        await SqlAsync(source.Repository.DatabasePath,"UPDATE contracts SET global_updated_at='2026-11-11T10:00:00+00:00';");
        await SqlAsync(destination.Repository.DatabasePath,"UPDATE contracts SET global_updated_at='2026-11-11T08:30:00-03:00';");
        var path=Path.Combine(source.Directory,"offset.pncpking");
        await new BackupService(source.Repository).ExportAsync(path,Profile);
        await Assert.ThrowsAnyAsync<Exception>(()=>new BackupService(destination.Repository).ImportAsync(path));
        Assert.Equal("2026-11-11T08:30:00-03:00",await ScalarAsync(destination.Repository.DatabasePath,"SELECT global_updated_at FROM contracts"));
    }

    [Fact]
    public async Task EqualTimestampWithDivergentPriceIsRejectedRatherThanChoosingSilently()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, 1);
        await SeedPricesAsync(destination, 1);
        await SqlAsync(source.Repository.DatabasePath, "UPDATE item_results SET unit_value_scaled=999000000;");
        var previous = await ScalarAsync(destination.Repository.DatabasePath, "SELECT unit_value_scaled FROM item_results");
        var path = Path.Combine(source.Directory, "ambiguous-price.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, Profile);
        await Assert.ThrowsAnyAsync<Exception>(() => new BackupService(destination.Repository).ImportAsync(path));
        Assert.Equal(previous, await ScalarAsync(destination.Repository.DatabasePath, "SELECT unit_value_scaled FROM item_results"));
    }

    [Fact]
    public async Task PendingExclusiveGateAllowsAnExistingOperationToOpenItsNestedConnection()
    {
        await using var database = await TestDatabase.CreateAsync();
        var factory = new SqliteConnectionFactory(database.Repository.DatabasePath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var first = await factory.OpenAsync(cancellation.Token);
        var exclusive = SqliteDatabaseTransferGate.EnterExclusiveAsync(database.Repository.DatabasePath, cancellation.Token).AsTask();
        try
        {
            Assert.False(exclusive.IsCompleted);
            // Real repositories sometimes open another connection before disposing their first.
            // A waiting importer must let that operation drain instead of closing admission early.
            await using (var nested = await factory.OpenAsync(cancellation.Token))
            {
                Assert.Equal(System.Data.ConnectionState.Open, nested.State);
                Assert.False(exclusive.IsCompleted);
            }
            await first.DisposeAsync();
            await using var lease = await exclusive.WaitAsync(cancellation.Token);
        }
        finally
        {
            cancellation.Cancel();
            await first.DisposeAsync();
            try { await using var cleanup = await exclusive; }
            catch (OperationCanceledException) { }
        }
        await using var reopened = await factory.OpenAsync();
        Assert.Equal(System.Data.ConnectionState.Open, reopened.State);
    }

    [Fact]
    public async Task LocalOnlyTreeCoverageAndCatalogRemainAvailableWithAccurateCounts()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, 1);
        await SeedPricesAsync(destination, 1);
        var local = PriceCacheTests.RecentContract("local-only", DateOnly.FromDateTime(DateTime.Today), 2);
        await destination.Repository.UpsertContractsAsync([local]);
        await destination.Repository.UpsertItemsAsync(local.PncpId, [PriceCacheTests.Item(local, 1)], false);
        await destination.Repository.ReplaceItemResultsAsync(local.PncpId, 1, [PriceCacheTests.Result(local, 1, 1, true)]);
        await SqlAsync(source.Repository.DatabasePath, """
            INSERT INTO catalog_entries(catalog_kind,code,description,search_text) VALUES(1,'SOURCE','Cafe','CAFE');
            INSERT INTO catalog_entries(catalog_kind,code,description,search_text,active) VALUES(1,'INACTIVE','Inativo','INATIVO',0);
            INSERT INTO coverage_day_modalities(coverage_date,modality_id,uf,status,updated_at) VALUES('2001-01-01',6,'SP',2,'2001-01-02');
            """);
        await SqlAsync(destination.Repository.DatabasePath, """
            INSERT INTO catalog_entries(catalog_kind,code,description,search_text) VALUES(1,'DESTINATION','Papel','PAPEL');
            INSERT INTO coverage_day_modalities(coverage_date,modality_id,uf,status,updated_at) VALUES('2001-01-02',6,'SP',2,'2001-01-03');
            UPDATE catalog_sync_state SET generation='LOCAL-PENDING',next_page=7,staged_records=12 WHERE catalog_kind=1;
            """);
        var path = Path.Combine(source.Directory, "metadata.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, Profile);
        await new BackupService(destination.Repository).ImportAsync(path);
        foreach (var column in new[] { "contract_count", "item_count", "result_count" })
            Assert.Equal(2L, await ScalarAsync(destination.Repository.DatabasePath, $"SELECT {column} FROM dataset_statistics"));
        Assert.Equal(2L, await ScalarAsync(destination.Repository.DatabasePath, "SELECT COUNT(*) FROM coverage_day_modalities"));
        Assert.Equal(2L, await ScalarAsync(destination.Repository.DatabasePath, "SELECT active_records FROM catalog_sync_state WHERE catalog_kind=1"));
        Assert.Equal("LOCAL-PENDING", await ScalarAsync(destination.Repository.DatabasePath, "SELECT generation FROM catalog_sync_state WHERE catalog_kind=1"));
        Assert.Equal(7L, await ScalarAsync(destination.Repository.DatabasePath, "SELECT next_page FROM catalog_sync_state WHERE catalog_kind=1"));
        Assert.Equal(12L, await ScalarAsync(destination.Repository.DatabasePath, "SELECT staged_records FROM catalog_sync_state WHERE catalog_kind=1"));
        Assert.Equal(1L, await ScalarAsync(destination.Repository.DatabasePath, "SELECT COUNT(*) FROM catalog_entries_fts WHERE catalog_entries_fts MATCH 'PAPEL'"));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    public async Task CoverageKeepsRecentLocalCompletionButDoesNotInventCompletionForNewerSource(int sourceDay, int localStatus)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SqlAsync(source.Repository.DatabasePath, $"""
            INSERT INTO coverage_day_modalities(coverage_date,modality_id,uf,status,records_count,updated_at)
            VALUES('2001-01-01',6,'SP',1,5,'2001-02-{sourceDay:D2}');
            """);
        await SqlAsync(destination.Repository.DatabasePath, $"""
            INSERT INTO coverage_day_modalities(coverage_date,modality_id,uf,status,records_count,updated_at)
            VALUES('2001-01-01',6,'SP',{localStatus},20,'2001-02-02');
            """);
        var path = Path.Combine(source.Directory, "coverage.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, Profile);
        await new BackupService(destination.Repository).ImportAsync(path);
        Assert.Equal(sourceDay > 2 ? 1L : localStatus,
            await ScalarAsync(destination.Repository.DatabasePath, "SELECT status FROM coverage_day_modalities"));
        Assert.Equal(sourceDay > 2 ? 5L : 20L,
            await ScalarAsync(destination.Repository.DatabasePath, "SELECT records_count FROM coverage_day_modalities"));
    }

    private static async Task SeedPricesAsync(TestDatabase db, int count)
    {
        // Twenty items per procurement, spread over municipalities/IDs rather than one giant contract.
        var contractCount = (count + 19) / 20;
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var start = 0; start < contractCount; start += 1000)
        {
            var contracts = Enumerable.Range(start, Math.Min(1000, contractCount - start))
                .Select(index => PriceCacheTests.RecentContract(
                    index == 0 ? "history" : $"history-{index:D6}", today, index % 28 + 1) with
                {
                    PurchaseSequence = index + 1,
                    Uf = new[] { "SP", "MG", "PR", "RJ", "BA" }[index % 5],
                    Municipality = $"Municipio {index % 200}",
                    Organization = $"Prefeitura sintetica {index % 200}"
                }).ToArray();
            await db.Repository.UpsertContractsAsync(contracts);
        }
        await SqlAsync(db.Repository.DatabasePath, $"""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<{count})
            INSERT INTO items(contract_id,item_number,description,unit,has_result,hydration_status)
            SELECT CASE WHEN (x-1)/20=0 THEN 'history' ELSE printf('history-%06d',(x-1)/20) END,
                (x-1)%20+1,
                CASE x%4 WHEN 0 THEN 'Cafe torrado pacote ' WHEN 1 THEN 'Papel A4 resma '
                    WHEN 2 THEN 'Caneta esferografica caixa ' ELSE 'Arroz branco pacote ' END || x,
                CASE x%4 WHEN 1 THEN 'RESMA' WHEN 2 THEN 'CAIXA' ELSE 'KG' END,1,2 FROM n;
            INSERT INTO item_results(contract_id,item_number,result_sequence,supplier_name,unit_value_scaled,result_status_id,result_date)
            SELECT contract_id,item_number,1,'Fornecedor '||(rowid%500),25000000+rowid,1,'2001-02-03' FROM items;
            UPDATE dataset_statistics SET contract_count=(SELECT COUNT(*) FROM contracts),
                item_count=(SELECT COUNT(*) FROM items),result_count=(SELECT COUNT(*) FROM item_results);
            """);
        await WithoutTriggersAsync(db.Repository.DatabasePath,
            "UPDATE contracts SET publication_date='2001-02-03T12:00:00Z',global_updated_at='2001-02-04T12:00:00Z';");
        Assert.Equal((long)contractCount, await ScalarAsync(db.Repository.DatabasePath, "SELECT COUNT(*) FROM contracts"));
        Assert.Equal((long)count, await ScalarAsync(db.Repository.DatabasePath, "SELECT item_count FROM dataset_statistics"));
        Assert.Equal((long)count, await ScalarAsync(db.Repository.DatabasePath, "SELECT result_count FROM dataset_statistics"));
    }

    private static async Task<EvidenceImageDescriptor> SeedLocalAsync(TestDatabase db, string marker)
    {
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var project = await repo.CreateProjectAsync(marker);
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, new(marker, 12, "KG", null, null),
            [new QuotationReference { Id="old-reference", LineId=line, ContractId="missing-old-contract", ItemNumber=1,
                ResultSequence=1, UnitPrice=123.456m, State=QuotationReferenceState.Eligible }]);
        var basket = await repo.SaveManualBasketAsync(line, null, marker, ["old-reference"]);
        await repo.SetManualBasketPriceDivisorAsync(basket.Id, "old-reference", 3);
        using var bitmap = new SKBitmap(3, 3);
        bitmap.Erase(SKColors.Blue);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var image = await new InternetEvidenceStore(db.Directory).SavePngAsync(png.ToArray(), 3, 3);
        var now = DateTimeOffset.UtcNow;
        await repo.SaveInternetPriceReferenceAsync(new QuotationReference {
            Id="internet", LineId=line, ContractId="internet", ItemNumber=1, ResultSequence=1,
            UnitPrice=40, State=QuotationReferenceState.Eligible, Source=QuotationReferenceSource.InternetIncisoIII },
            new InternetPriceEvidence { LineId=line, ReferenceId="internet", SourceUrl="https://example.test/price",
                CapturedAt=now, PriceImage=image, TaxIdImage=image }, basket.Id, basket.Name);
        await repo.SaveInternetPriceDraftAsync(new InternetPriceDraft { Id=Guid.NewGuid(), LineId=line, BasketId=basket.Id,
            Description=marker, SourceUrl="https://example.test/draft", CapturedAt=now, CreatedAt=now, UpdatedAt=now,
            PriceImage=image, TaxIdImage=image, UnitPrice=42 });
        await repo.ConfirmBasketAsync(line, basket.Key);
        await SqlAsync(db.Repository.DatabasePath, $"""
            DELETE FROM sweet_codes;
            INSERT INTO sweet_codes(position,expression) VALUES(1,'{marker}');
            UPDATE sweet_code_settings SET enabled=0;
            INSERT INTO catalog_equivalence_rules(id,rule_kind,canonical,alias) VALUES('local-rule',1,'{marker}','{marker}');
            INSERT INTO guard_master VALUES(1,'{marker}','2001-02-03');
            INSERT INTO guard_workers VALUES('worker','{marker}',3,'2001-02-03');
            INSERT INTO guard_campaigns VALUES('campaign','{marker}','2001-02-03','private/path',1);
            INSERT INTO guard_imported_packages VALUES('package','abc','campaign','worker','2001-02-03',12,2);
            INSERT INTO quotation_automation_runs(id,project_id,output_path,geo_filter_kind,start_date,end_date,created_at,updated_at)
            VALUES('automation','{project.Id:N}','private/export.xlsx',0,'2001-01-01','2001-12-31','2001-02-03','2001-02-03');
            UPDATE quotation_lines SET automation_run_id='automation',automation_message='{marker}';
            INSERT INTO quotation_item_search_workspaces(line_id,prompt_slot,search_text,start_date,end_date,updated_at)
            VALUES('{line:N}',1,'{marker}','2001-01-01','2001-12-31','2001-02-03');
            INSERT INTO quotation_item_search_hits(line_id,prompt_slot,contract_id,item_number,discovered_order)
            VALUES('{line:N}',1,'missing-old-contract',1,7);
            """);
        await WithoutTriggersAsync(db.Repository.DatabasePath,
            "UPDATE quotation_references SET publication_date='2001-02-03T12:00:00Z',result_date='2001-02-03';");
        return image;
    }

    // Compare every column, null and row including optional quotation tables, with stable ordering.
    private static async Task<string[]> LocalRowsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND (name GLOB 'quotation_*' OR name GLOB 'sweet_*' OR name GLOB 'guard_*' OR name='catalog_equivalence_rules') ORDER BY name";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        var result = new List<string>();
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\"";
            using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values.Select(value => value is DBNull ? null : value)));
            }
            result.Add(table + ":" + string.Join("\n", rows.Order(StringComparer.Ordinal)));
        }
        return result.ToArray();
    }

    private static async Task WithoutTriggersAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        var triggers = new List<(string Name, string Sql)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText="SELECT name,sql FROM sqlite_master WHERE type='trigger'";
            using var reader=await read.ExecuteReaderAsync();
            while(await reader.ReadAsync()) triggers.Add((reader.GetString(0),reader.GetString(1)));
        }
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();
        command.Transaction=transaction;
        command.CommandText=string.Join(";",triggers.Select(t=>$"DROP TRIGGER \"{t.Name}\""))+";"+sql+string.Join(";",triggers.Select(t=>t.Sql));
        await command.ExecuteNonQueryAsync();
        transaction.Commit();
    }

    private static async Task SqlAsync(string path,string sql)
    {
        await using var connection=new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        using var command=connection.CreateCommand(); command.CommandText=sql; await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> ScalarAsync(string path,string sql)
    {
        await using var connection=new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command=connection.CreateCommand(); command.CommandText=sql; return await command.ExecuteScalarAsync();
    }
    private sealed class InlineProgress<T>(Action<T> callback):IProgress<T> { public void Report(T value)=>callback(value); }
}
