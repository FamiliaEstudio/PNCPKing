using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class HistoricalTransferSchemaTests
{
    // v1.2.28/e9132cf used version 34 for guarded FTS triggers and chunk progress.
    // The converter only lowered the version to 33; v1.2.45 then removed the public FK.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportAcceptsHistorical34AndConverted33UpgradedTo34(bool converted)
    {
        await using var source = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, "source", 2);
        await SetHistoricalSchemaAsync(source.Repository.DatabasePath);
        if (converted)
        {
            await SqlAsync(source.Repository.DatabasePath, "UPDATE schema_info SET version=33 WHERE id=1;");
            var result = await source.Repository.InitializeAsync();
            Assert.Equal(33, result.PreviousVersion);
            Assert.Equal(34, result.CurrentVersion);
        }
        var before = await SnapshotAsync(source.Repository.DatabasePath);
        var path = Path.Combine(source.Directory, "historical.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, BackupProfile.FullWithoutQuotations);
        Assert.True(File.Exists(path));
        Assert.Equal(before, await SnapshotAsync(source.Repository.DatabasePath));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(0, 4)]
    [InlineData(1, 3)]
    [InlineData(3, 2)]
    public async Task HistoricalSourceAndDestinationReplacePricesButPreserveLocalHistory(int sourceVariant, int destinationVariant)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, "source-only", 2);
        await SeedPricesAsync(destination, "destination-only", 3);
        await SetVariantAsync(source, sourceVariant);
        await SetVariantAsync(destination, destinationVariant);
        await SeedLocalHistoryAsync(source, "SOURCE-PRIVATE", "source-only");
        await SeedLocalHistoryAsync(destination, "DESTINATION-PRIVATE", "destination-only");
        // ANALYZE creates the exact, legitimate sqlite_stat1 seen in the reported database.
        await SqlAsync(source.Repository.DatabasePath, "ANALYZE;");
        await SqlAsync(destination.Repository.DatabasePath, "ANALYZE;");
        Assert.Equal(1L, await ScalarAsync(source.Repository.DatabasePath,
            "SELECT COUNT(*) FROM sqlite_master WHERE name='sqlite_stat1'"));
        var sourceBefore = await SnapshotAsync(source.Repository.DatabasePath);
        var destinationBefore = await SnapshotAsync(destination.Repository.DatabasePath);
        var localBefore = await SnapshotAsync(destination.Repository.DatabasePath, localOnly: true);
        var path = Path.Combine(source.Directory, "history-roundtrip.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, BackupProfile.FullWithoutQuotations);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var exported = Path.Combine(source.Directory, "export-inspection.db");
        using (var archive = ZipFile.OpenRead(path)) archive.GetEntry("data.db")!.ExtractToFile(exported);
        Assert.Equal(0L, await ScalarAsync(exported, "SELECT COUNT(*) FROM quotation_projects"));
        Assert.Equal(0L, await ScalarAsync(exported, "SELECT COUNT(*) FROM quotation_processed_contracts"));
        Assert.Equal(0L, await ScalarAsync(exported, "SELECT COUNT(*) FROM official_update_chunks"));
        Assert.Equal(0L, await ScalarAsync(exported, PublicHistoryForeignKeysSql));
        if (sourceVariant > 0)
            Assert.Equal(2L, await ScalarAsync(exported,
                "SELECT COUNT(*) FROM pragma_table_info('official_update_chunks') WHERE name IN ('last_contract_id','processed_contracts')"));
        var recovery = await new BackupService(destination.Repository).ImportAsync(path);

        Assert.Equal(sourceBefore, await SnapshotAsync(source.Repository.DatabasePath));
        Assert.Equal(destinationBefore, await SnapshotAsync(recovery));
        Assert.Equal(localBefore, await SnapshotAsync(destination.Repository.DatabasePath, localOnly: true));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        await AssertReplacementAndLocalHistoryAsync(destination.Repository.DatabasePath);
        await AssertSearchUpdatesAsync(destination.Repository.DatabasePath, 2);
        // Run full checks only here in the synthetic test, never in the receiving application.
        Assert.Equal("ok", await ScalarAsync(destination.Repository.DatabasePath, "PRAGMA integrity_check"));
        Assert.Null(await ScalarAsync(destination.Repository.DatabasePath, "PRAGMA foreign_key_check"));
    }

    [Theory]
    [InlineData(33, false, BackupProfile.FullWithoutQuotations)]
    [InlineData(34, false, BackupProfile.FullWithoutQuotations)]
    [InlineData(33, true, BackupProfile.PricesPreservingLocalData)]
    [InlineData(34, true, BackupProfile.PricesPreservingLocalData)]
    public async Task IncomingHistoricalPackageNormalizesPublicForeignKeyOnlyInStaging(
        int version, bool mixedGuards, BackupProfile profile)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, "source-only", 2);
        await SeedPricesAsync(destination, "destination-only", 3);
        await SetVariantAsync(destination, 1);
        await SeedLocalHistoryAsync(destination, "DESTINATION-PRIVATE", "destination-only");
        var before = await SnapshotAsync(destination.Repository.DatabasePath, localOnly: true);
        var sourceBefore = await SnapshotAsync(source.Repository.DatabasePath);
        var path = Path.Combine(source.Directory, "incoming-history.pncpking");
        await new BackupService(source.Repository).ExportAsync(path, profile);
        await RewritePackageAsync(path, async db =>
        {
            await SetHistoricalSchemaAsync(db, mixedGuards);
            await SqlAsync(db, $"UPDATE schema_info SET version={version} WHERE id=1; ANALYZE;");
            Assert.Equal(1L, await ScalarAsync(db, PublicHistoryForeignKeysSql));
        });
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var recovery = await new BackupService(destination.Repository).ImportAsync(path);
        Assert.True(File.Exists(recovery));
        Assert.Equal(before, await SnapshotAsync(destination.Repository.DatabasePath, localOnly: true));
        Assert.Equal(sourceBefore, await SnapshotAsync(source.Repository.DatabasePath));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        await AssertReplacementAndLocalHistoryAsync(destination.Repository.DatabasePath);
        await AssertSearchUpdatesAsync(destination.Repository.DatabasePath, 2);
        Assert.Null(await ScalarAsync(destination.Repository.DatabasePath, "PRAGMA foreign_key_check"));
    }

    [Theory]
    [InlineData("export", "known-trigger", "trigger:contracts_fts_update")]
    [InlineData("export", "trigger-literal", "trigger:contracts_fts_update")]
    [InlineData("export", "unknown-trigger", "trigger:unrecognized_history_trigger")]
    [InlineData("export", "unexpected-column", "table:quotation_processed_contracts")]
    [InlineData("export", "unexpected-chunk-column", "table:official_update_chunks")]
    [InlineData("export", "non-ascii-space", "table:official_update_chunks")]
    [InlineData("package", "known-trigger", "trigger:contracts_fts_update")]
    [InlineData("package", "trigger-literal", "trigger:contracts_fts_update")]
    [InlineData("package", "unknown-trigger", "trigger:unrecognized_history_trigger")]
    [InlineData("package", "unexpected-column", "table:quotation_processed_contracts")]
    [InlineData("package", "unexpected-chunk-column", "table:official_update_chunks")]
    [InlineData("package", "non-ascii-space", "table:official_update_chunks")]
    [InlineData("package33", "known-trigger", "trigger:contracts_fts_update")]
    [InlineData("package33", "trigger-literal", "trigger:contracts_fts_update")]
    [InlineData("package33", "unknown-trigger", "trigger:unrecognized_history_trigger")]
    [InlineData("package33", "unexpected-column", "table:quotation_processed_contracts")]
    [InlineData("package33", "unexpected-chunk-column", "table:official_update_chunks")]
    [InlineData("package33", "non-ascii-space", "table:official_update_chunks")]
    [InlineData("destination", "known-trigger", "trigger:contracts_fts_update")]
    [InlineData("destination", "trigger-literal", "trigger:contracts_fts_update")]
    [InlineData("destination", "unknown-trigger", "trigger:unrecognized_history_trigger")]
    [InlineData("destination", "unexpected-column", "table:quotation_processed_contracts")]
    [InlineData("destination", "unexpected-chunk-column", "table:official_update_chunks")]
    [InlineData("destination", "non-ascii-space", "table:official_update_chunks")]
    public async Task HistoricalAllowanceStillRejectsUnknownSchemaWithoutChangingActiveDatabase(
        string location, string mutation, string rejectedObject)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        await SeedPricesAsync(source, "source-only", 2);
        await SeedPricesAsync(destination, "destination-only", 3);
        var path = Path.Combine(source.Directory, "unknown-schema.pncpking");
        if (location == "export")
        {
            await SetVariantAsync(source, 1);
            await MutateSchemaAsync(source.Repository.DatabasePath, mutation);
        }
        else
        {
            await new BackupService(source.Repository).ExportAsync(path, BackupProfile.FullWithoutQuotations);
            if (location is "package" or "package33")
                await RewritePackageAsync(path, async db =>
                {
                    await SetHistoricalSchemaAsync(db);
                    await MutateSchemaAsync(db, mutation);
                    if (location == "package33")
                        await SqlAsync(db, "UPDATE schema_info SET version=33 WHERE id=1;");
                });
            else
            {
                await SetVariantAsync(destination, 1);
                await MutateSchemaAsync(destination.Repository.DatabasePath, mutation);
            }
        }
        await SeedLocalHistoryAsync(destination, "DESTINATION-PRIVATE", "destination-only");
        var sourceBefore = await SnapshotAsync(source.Repository.DatabasePath);
        var destinationBefore = await SnapshotAsync(destination.Repository.DatabasePath);
        var service = new BackupService(destination.Repository);
        var error = location == "export"
            ? await Assert.ThrowsAsync<InvalidDataException>(() => new BackupService(source.Repository)
                .ExportAsync(path, BackupProfile.FullWithoutQuotations))
            : await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(path));
        Assert.Contains(rejectedObject, error.Message);
        Assert.Equal(sourceBefore, await SnapshotAsync(source.Repository.DatabasePath));
        Assert.Equal(destinationBefore, await SnapshotAsync(destination.Repository.DatabasePath));
        Assert.Empty(service.GetRecoveryBackups());
        Assert.Empty(Directory.GetDirectories(destination.Directory, ".pncpking-import-*"));
        if (location == "export")
        {
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".partial"));
        }
        if (location == "destination" && mutation == "trigger-literal")
        {
            // The intentionally invalid trigger must remain untouched after rejection, not be repaired.
            Assert.Equal(1L, await ScalarAsync(destination.Repository.DatabasePath,
                "SELECT COUNT(*) FROM contracts_fts WHERE contracts_fts MATCH 'SOURCECONTRACTTOKEN'"));
            Assert.Equal(3L, await ScalarAsync(destination.Repository.DatabasePath,
                "SELECT COUNT(*) FROM items_fts WHERE items_fts MATCH 'SOURCEITEMTOKEN'"));
        }
        else
            await AssertSearchUpdatesAsync(destination.Repository.DatabasePath, 3);
    }

    private const string PublicHistoryForeignKeysSql =
        "SELECT COUNT(*) FROM pragma_foreign_key_list('quotation_processed_contracts') WHERE \"table\"='contracts'";

    private static async Task SetVariantAsync(TestDatabase db, int variant)
    {
        if (variant == 0) return;
        await SetHistoricalSchemaAsync(db.Repository.DatabasePath, mixedGuards: variant >= 3);
        if (variant is 2 or 4)
        {
            await SqlAsync(db.Repository.DatabasePath, "UPDATE schema_info SET version=33 WHERE id=1;");
            var result = await db.Repository.InitializeAsync();
            Assert.Equal(33, result.PreviousVersion);
            Assert.Equal(34, result.CurrentVersion);
        }
    }

    private static async Task SeedLocalHistoryAsync(TestDatabase db, string marker, string contractId)
    {
        var repository = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var project = await repository.CreateProjectAsync(marker);
        var line = Guid.NewGuid();
        await repository.SaveSampleAsync(project.Id, line, new(marker, 3, "KG", null, null),
            [new QuotationReference { Id = "saved-reference", LineId = line, ContractId = contractId,
                ItemNumber = 1, ResultSequence = 1, UnitPrice = 123.456m, State = QuotationReferenceState.Eligible }]);
        var basket = await repository.SaveManualBasketAsync(line, null, marker, ["saved-reference"]);
        await repository.SetManualBasketPriceDivisorAsync(basket.Id, "saved-reference", 3);
        await repository.ConfirmBasketAsync(line, basket.Key);
        await SqlAsync(db.Repository.DatabasePath, $"""
            INSERT INTO quotation_automation_runs(id,project_id,output_path,geo_filter_kind,start_date,end_date,created_at,updated_at)
            VALUES('history-run','{project.Id:N}','local/export.xlsx',0,'2001-01-01','2001-12-31','2001-02-03','2001-02-03');
            INSERT INTO quotation_processed_contracts(run_id,contract_id,prompt_order,processed_at,matched_items,revealed_prices)
            VALUES('history-run','{contractId}',2,'2001-02-03',7,11);
            INSERT INTO official_update_chunks(chunk_key,content_digest,sha256,package_id,applied,skipped,completed,imported_at)
            VALUES('old-chunk','digest','hash','package',7,3,1,'2001-02-03');
            """);
        if (Convert.ToInt64(await ScalarAsync(db.Repository.DatabasePath,
                "SELECT COUNT(*) FROM pragma_table_info('official_update_chunks') WHERE name='last_contract_id'")) > 0)
            await SqlAsync(db.Repository.DatabasePath,
                $"UPDATE official_update_chunks SET last_contract_id='{contractId}',processed_contracts=17;");
    }

    private static async Task AssertReplacementAndLocalHistoryAsync(string path)
    {
        Assert.Equal(34L, await ScalarAsync(path, "SELECT version FROM schema_info"));
        Assert.Equal(1L, await ScalarAsync(path, "SELECT COUNT(*) FROM contracts"));
        Assert.Equal(0L, await ScalarAsync(path, "SELECT COUNT(*) FROM contracts WHERE pncp_id='destination-only'"));
        Assert.Equal(2L, await ScalarAsync(path, "SELECT COUNT(*) FROM items WHERE contract_id='source-only'"));
        Assert.Equal(2L, await ScalarAsync(path, "SELECT COUNT(*) FROM item_results WHERE contract_id='source-only' AND result_status_id=1"));
        Assert.Equal(1L, await ScalarAsync(path, "SELECT contract_count FROM dataset_statistics"));
        Assert.Equal(2L, await ScalarAsync(path, "SELECT item_count FROM dataset_statistics"));
        Assert.Equal(2L, await ScalarAsync(path, "SELECT result_count FROM dataset_statistics"));
        Assert.Equal(1L, await ScalarAsync(path,
            "SELECT COUNT(*) FROM quotation_references WHERE contract_id='destination-only'"));
        Assert.Equal(1L, await ScalarAsync(path,
            "SELECT COUNT(*) FROM quotation_processed_contracts WHERE contract_id='destination-only' AND matched_items=7 AND revealed_prices=11"));
        Assert.Equal(0L, await ScalarAsync(path, PublicHistoryForeignKeysSql));
        Assert.Equal(1L, await ScalarAsync(path,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('quotation_processed_contracts') WHERE \"table\"='quotation_automation_runs' AND on_delete='CASCADE'"));
        Assert.Equal(0L, await ScalarAsync(path, "SELECT COUNT(*) FROM official_update_chunks"));
        Assert.Equal(0L, await ScalarAsync(path, "SELECT COUNT(*) FROM quotation_projects WHERE name='SOURCE-PRIVATE'"));
    }

    private static async Task AssertSearchUpdatesAsync(string path, int itemCount)
    {
        Assert.Equal(1L, await ScalarAsync(path, "SELECT COUNT(*) FROM contracts_fts WHERE contracts_fts MATCH 'SOURCECONTRACTTOKEN'"));
        Assert.Equal((long)itemCount, await ScalarAsync(path, "SELECT COUNT(*) FROM items_fts WHERE items_fts MATCH 'SOURCEITEMTOKEN'"));
        await SqlAsync(path, """
            UPDATE contracts SET search_text='CHANGEDCONTRACTTOKEN';
            UPDATE items SET search_text='CHANGEDITEMTOKEN';
            UPDATE contracts SET search_text=search_text;
            UPDATE items SET search_text=search_text;
            """);
        Assert.Equal(0L, await ScalarAsync(path, "SELECT COUNT(*) FROM contracts_fts WHERE contracts_fts MATCH 'SOURCECONTRACTTOKEN'"));
        Assert.Equal(0L, await ScalarAsync(path, "SELECT COUNT(*) FROM items_fts WHERE items_fts MATCH 'SOURCEITEMTOKEN'"));
        Assert.Equal(1L, await ScalarAsync(path, "SELECT COUNT(*) FROM contracts_fts WHERE contracts_fts MATCH 'CHANGEDCONTRACTTOKEN'"));
        Assert.Equal((long)itemCount, await ScalarAsync(path, "SELECT COUNT(*) FROM items_fts WHERE items_fts MATCH 'CHANGEDITEMTOKEN'"));
    }

    private static async Task MutateSchemaAsync(string path, string mutation)
    {
        if (mutation == "non-ascii-space")
        {
            var ddl = (string)(await ScalarAsync(path,
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='official_update_chunks'"))!;
            Assert.Contains("processed_contracts INTEGER NOT NULL", ddl);
            // NBSP is part of SQLite's type name, not SQL whitespace: NOT ceases to be a constraint.
            ddl = ddl.Replace("processed_contracts INTEGER NOT NULL",
                "processed_contracts INTEGER\u00a0NOT NULL", StringComparison.Ordinal);
            await SqlAsync(path, "DROP TABLE official_update_chunks;" + ddl);
            Assert.Equal(0L, await ScalarAsync(path,
                "SELECT \"notnull\" FROM pragma_table_info('official_update_chunks') WHERE name='processed_contracts'"));
            return;
        }
        await SqlAsync(path, mutation switch
        {
            "known-trigger" => """
                DROP TRIGGER contracts_fts_update;
                CREATE TRIGGER contracts_fts_update AFTER UPDATE OF search_text ON contracts
                WHEN old.search_text IS NOT new.search_text BEGIN
                    INSERT INTO contracts_fts(contracts_fts,rowid,search_text)
                    VALUES('delete',old.rowid,old.search_text);
                    INSERT INTO contracts_fts(rowid,search_text) VALUES(new.rowid,new.search_text);
                    SELECT 1;
                END;
                """,
            // Removing IF NOT EXISTS globally used to turn this invalid FTS command into the accepted DDL.
            "trigger-literal" => """
                DROP TRIGGER contracts_fts_update;
                CREATE TRIGGER contracts_fts_update AFTER UPDATE OF search_text ON contracts
                WHEN old.search_text IS NOT new.search_text BEGIN
                    INSERT INTO contracts_fts(contracts_fts,rowid,search_text)
                    VALUES('deleteIF NOT EXISTS',old.rowid,old.search_text);
                    INSERT INTO contracts_fts(rowid,search_text) VALUES(new.rowid,new.search_text);
                END;
                """,
            // This trigger would disappear if normalization dropped the historical table before validation.
            "unknown-trigger" => """
                CREATE TRIGGER unrecognized_history_trigger AFTER INSERT ON quotation_processed_contracts
                BEGIN SELECT 1; END;
                """,
            "unexpected-column" => "ALTER TABLE quotation_processed_contracts ADD COLUMN unexpected TEXT;",
            "unexpected-chunk-column" => "ALTER TABLE official_update_chunks ADD COLUMN unexpected TEXT;",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        });
    }

    private static async Task RewritePackageAsync(string path, Func<string, Task> change)
    {
        var databasePath = Path.Combine(Path.GetDirectoryName(path)!, $"rewritten-{Guid.NewGuid():N}.db");
        DatasetManifest manifest;
        using (var archive = ZipFile.OpenRead(path))
        {
            archive.GetEntry("data.db")!.ExtractToFile(databasePath);
            await using var stream = archive.GetEntry("manifest.json")!.Open();
            manifest = (await JsonSerializer.DeserializeAsync<DatasetManifest>(stream))!;
        }
        await change(databasePath);
        await SqlAsync(databasePath, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
        var bytes = await File.ReadAllBytesAsync(databasePath);
        manifest = manifest with
        {
            SchemaVersion = Convert.ToInt32(await ScalarAsync(databasePath, "SELECT version FROM schema_info")),
            DatabaseSha256 = Convert.ToHexString(SHA256.HashData(bytes)), DatabaseBytes = bytes.LongLength
        };
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry("data.db")!.Delete();
            archive.GetEntry("manifest.json")!.Delete();
            await using (var data = archive.CreateEntry("data.db").Open()) await data.WriteAsync(bytes);
            await using var stream = archive.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(stream, manifest);
        }
    }
    private static async Task SetHistoricalSchemaAsync(string path, bool mixedGuards = false)
    {
        await SqlAsync(path, """
            DROP TRIGGER contracts_fts_update;
            CREATE TRIGGER contracts_fts_update AFTER UPDATE OF search_text ON contracts
            WHEN old.search_text IS NOT new.search_text BEGIN
                INSERT INTO contracts_fts(contracts_fts,rowid,search_text)
                VALUES('delete',old.rowid,old.search_text);
                INSERT INTO contracts_fts(rowid,search_text) VALUES(new.rowid,new.search_text);
            END;
            DROP TRIGGER items_fts_update;
            CREATE TRIGGER items_fts_update AFTER UPDATE OF search_text ON items
            WHEN old.search_text IS NOT new.search_text BEGIN
                INSERT INTO items_fts(items_fts,rowid,search_text)
                VALUES('delete',old.rowid,old.search_text);
                INSERT INTO items_fts(rowid,search_text) VALUES(new.rowid,new.search_text);
            END;
            ALTER TABLE official_update_chunks ADD COLUMN last_contract_id TEXT;
            ALTER TABLE official_update_chunks ADD COLUMN processed_contracts INTEGER NOT NULL DEFAULT 0;
            DROP TABLE quotation_processed_contracts;
            CREATE TABLE quotation_processed_contracts(
                run_id TEXT NOT NULL REFERENCES quotation_automation_runs(id) ON DELETE CASCADE,
                contract_id TEXT NOT NULL REFERENCES contracts(pncp_id) ON DELETE CASCADE,
                prompt_order INTEGER NOT NULL,
                processed_at TEXT NOT NULL,
                matched_items INTEGER NOT NULL DEFAULT 0,
                revealed_prices INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(run_id, contract_id)
            );
            CREATE INDEX idx_processed_contracts_run_time
                ON quotation_processed_contracts(run_id, processed_at, contract_id);
            """);
        if (mixedGuards)
            // CompactSnapshotAsync historically recreated only the item trigger without WHEN.
            await SqlAsync(path, """
                DROP TRIGGER items_fts_update;
                CREATE TRIGGER items_fts_update AFTER UPDATE OF search_text ON items BEGIN
                    INSERT INTO items_fts(items_fts, rowid, search_text)
                    VALUES('delete', old.rowid, old.search_text);
                    INSERT INTO items_fts(rowid, search_text) VALUES(new.rowid, new.search_text);
                END;
                """);
    }

    private static async Task SeedPricesAsync(TestDatabase db, string contractId, int count)
    {
        var contract = PriceCacheTests.RecentContract(contractId, DateOnly.FromDateTime(DateTime.Today), 1);
        await db.Repository.UpsertContractsAsync([contract]);
        await db.Repository.UpsertItemsAsync(contractId,
            Enumerable.Range(1, count).Select(n => PriceCacheTests.Item(contract, n)).ToArray(), false);
        for (var n = 1; n <= count; n++)
            await db.Repository.ReplaceItemResultsAsync(contractId, n, [PriceCacheTests.Result(contract, n, 1, true)]);
        await SqlAsync(db.Repository.DatabasePath, $"""
            UPDATE contracts SET search_text='SOURCECONTRACTTOKEN' WHERE pncp_id='{contractId}';
            UPDATE items SET search_text='SOURCEITEMTOKEN' WHERE contract_id='{contractId}';
            """);
    }

    private static async Task<string[]> SnapshotAsync(string path, bool localOnly = false)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        var tables = new List<string>();
        var snapshot = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = localOnly
                ? "SELECT name,sql FROM sqlite_master WHERE type='table' AND name GLOB 'quotation_*' ORDER BY name"
                : "SELECT name,sql FROM sqlite_master WHERE type='table' ORDER BY name";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
                if (!localOnly) snapshot.Add(reader.GetString(0) + ":" + reader.GetString(1));
            }
        }
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
                rows.Add(JsonSerializer.Serialize(values.Select(v => v is DBNull ? null : v)));
            }
            snapshot.Add(table + ":" + string.Join("\n", rows.Order(StringComparer.Ordinal)));
        }
        if (!localOnly)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT type||':'||name||':'||sql FROM sqlite_master WHERE type IN ('trigger','index','view') AND sql IS NOT NULL ORDER BY name";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) snapshot.Add(reader.GetString(0));
        }
        return snapshot.ToArray();
    }

    private static async Task SqlAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Foreign Keys=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}






