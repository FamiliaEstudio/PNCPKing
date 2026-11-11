using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Infrastructure.Services;

public sealed partial class BackupService
{
    // Deliberately explicit. A new table requires a reviewed ownership decision.
    private static readonly string[] TransferLocalTables =
    [
        "quotation_projects", "quotation_groups", "quotation_automation_runs", "quotation_lines",
        "quotation_references", "quotation_manual_baskets", "quotation_manual_basket_references",
        "quotation_manual_price_divisors", "quotation_catalog_selections", "quotation_item_search_workspaces",
        "quotation_item_search_hits", "quotation_item_search_failures", "quotation_internet_evidence_assets",
        "quotation_internet_price_drafts", "quotation_internet_price_evidence", "quotation_line_search_prompts",
        "quotation_contract_search_prompts", "quotation_processed_contracts", "quotation_prompt_revalidations",
        "sweet_code_settings", "sweet_codes", "catalog_equivalence_rules", "catalog_entries_stage", "catalog_sync_state",
        "guard_master", "guard_workers", "guard_campaigns", "sync_runs"
    ];
    // Completion receipts describe prices in the old public database, not user work.
    // Keeping them would make later updates skip data removed by total replacement.
    private static readonly string[] TransferResetTables =
    ["official_update_chunks", "official_update_packages", "guard_imported_packages", "sync_partitions"];
    private static readonly string[] TransferPublicTables =
    [
        "schema_info", "dataset", "contracts", "items", "item_results", "contract_item_snapshots",
        "coverage_day_modalities", "catalog_entries", "price_cache_control", "price_cache_contracts",
        "dataset_statistics", "national_price_index_control", "catalog_description_index_state",
        "maintenance_state", "official_result_snapshots"
    ];
    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static async Task<SqliteConnection> TransferConnectionAsync(
        string path, CancellationToken ct, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri, Mode = mode, Pooling = false, ForeignKeys = false }.ToString());
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            // Bounded caches avoid repeated random reads on mechanical disks; temp data can spill to disk.
            await TransferSqlAsync(connection, "PRAGMA cache_size=-32768; PRAGMA temp_store=FILE;", ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static async Task TransferSqlAsync(SqliteConnection c, string sql, CancellationToken ct, SqliteTransaction? tx = null)
    {
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    private static async Task<long> TransferScalarAsync(SqliteConnection c, string sql, CancellationToken ct, SqliteTransaction? tx = null)
    {
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
    private static async Task<Dictionary<string, string>> TransferSchemaAsync(SqliteConnection c, string schema, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT type||':'||name, sql FROM {schema}.sqlite_master WHERE sql IS NOT NULL AND type IN ('table','trigger','view','index') ORDER BY name;";
        var result = new Dictionary<string,string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }
    public const string TransferSchemaDiagnosticKey = "PNCPKing.TransferSchema";

    private sealed record TransferSchemaRules(
        Dictionary<string, string> Current, Dictionary<string, string> Historical);

    private static async Task<TransferSchemaRules> ReadTransferRulesAsync(string directory, CancellationToken ct)
    {
        var referencePath = Path.Combine(directory, $"schema-{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(referencePath);
        try
        {
            await new SqliteContractRepository(factory).InitializeAsync(ct).ConfigureAwait(false);
            await using var reference = await TransferConnectionAsync(referencePath, ct);
            await using (var tx = reference.BeginTransaction())
            {
                await SqliteQuotationRepository.EnsurePriceDivisorsTableAsync(reference, tx, ct);
                await tx.CommitAsync(ct);
            }
            var expected = await TransferSchemaAsync(reference, "main", ct);
            // Released 1.2.28 used schema 34 for these additions. Its later 34-to-33
            // converter intentionally kept them. Build exact trusted alternatives,
            // never execute or accept an arbitrary definition from the incoming DB.
            await TransferSqlAsync(reference, HistoricalTransferSchemaSql, ct);
            var historical = await TransferSchemaAsync(reference, "main", ct);
            return new(expected, historical);
        }
        finally
        {
            factory.ClearPool();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(referencePath + suffix)) File.Delete(referencePath + suffix);
        }
    }

    private static async Task ValidateTransferSchemaAsync(
        SqliteConnection c, string schema, TransferSchemaRules rules, string origin, string path, CancellationToken ct)
    {
        var (expected, historical) = rules;
        InvalidDataException Rejection(string reason, string key, string? sql)
        {
            var error = new InvalidDataException($"{reason}: {key}. Origem: {origin}.");
            error.Data[TransferSchemaDiagnosticKey] =
                $"origem={origin}{Environment.NewLine}arquivo={Path.GetFullPath(path)}{Environment.NewLine}" +
                $"objeto={key}{Environment.NewLine}SQL encontrado:{Environment.NewLine}{sql ?? "(objeto ausente)"}";
            return error;
        }

        Dictionary<string, string> actual;
        try { actual = await TransferSchemaAsync(c, schema, ct); }
        catch (SqliteException error)
        {
            throw new InvalidDataException(
                $"Não foi possível ler o esquema do {origin}. Arquivo: {Path.GetFullPath(path)}. " +
                "O SQLite não devolveu as definições SQL.", error);
        }
        // sqlite_master already removes CREATE ... IF NOT EXISTS. Preserve quoted
        // contents exactly: changing a literal or identifier must never match a
        // trusted definition. Only ASCII SQL whitespace outside quotes is cosmetic.
        static string Normalize(string sql) => System.Text.RegularExpressions.Regex.Replace(
            sql, @"('(?:''|[^'])*'|""(?:""""|[^""])*""|`(?:``|[^`])*`|\[[^\]]*\])|[ \t\r\n\f]+",
            match => match.Groups[1].Success ? match.Value : " ").Trim(' ').TrimEnd(';');
        foreach (var entry in actual)
            if ((!expected.TryGetValue(entry.Key, out var ddl) || Normalize(ddl) != Normalize(entry.Value)) &&
                (!historical.TryGetValue(entry.Key, out var oldDdl) || Normalize(oldDdl) != Normalize(entry.Value)))
                throw Rejection("Esquema não reconhecido para transferência segura", entry.Key, entry.Value);
        foreach (var entry in expected)
            if (!actual.ContainsKey(entry.Key) && entry.Key != "table:quotation_manual_price_divisors")
                throw Rejection("Estrutura obrigatória ausente", entry.Key, null);
        foreach (var name in actual.Keys.Where(k => k.StartsWith("table:")).Select(k => k[6..]))
        {
            var fts = new[] { "contracts_fts", "items_fts", "catalog_entries_fts", "catalog_description_fts" };
            if (name != "sqlite_stat1" && !TransferLocalTables.Contains(name) && !TransferResetTables.Contains(name) && !TransferPublicTables.Contains(name) &&
                !fts.Any(f => name == f || new[] { "_data", "_idx", "_content", "_docsize", "_config" }.Any(s => name == f + s)))
                throw Rejection("Tabela sem política de transferência", "table:" + name, actual["table:" + name]);
        }
    }

    private async Task CheckLocalTransferSchemaAsync(TransferSchemaRules rules, CancellationToken ct)
    {
        var target = Path.GetFullPath(repository.DatabasePath);
        if (!File.Exists(target))
            throw new FileNotFoundException("Inicialize o banco local antes de importar preços.", target);

        // Observe committed WAL changes and release the reader before extracting a large archive.
        using var lease = await SqliteDatabaseTransferGate.EnterConnectionAsync(target, ct);
        await using var local = await TransferConnectionAsync(target, ct, SqliteOpenMode.ReadOnly);
        await ValidateTransferSchemaAsync(local, "main", rules, "banco local antes da descompactação", target, ct);
    }

    // Only the known historical DDL is an alternative; all other objects still
    // require an exact reviewed definition. ANALYZE runs on this tiny reference DB
    // only, so sqlite_stat1 is optional and has the engine's own trusted definition.
    private const string HistoricalTransferSchemaSql = """
        ALTER TABLE official_update_chunks ADD COLUMN last_contract_id TEXT;
        ALTER TABLE official_update_chunks ADD COLUMN processed_contracts INTEGER NOT NULL DEFAULT 0;
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
        ANALYZE;
        """;

    private static async Task NormalizeTransferHistoryAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        // Call only after validating every object. The historical schema 34 had
        // this FK; normalize just the local table in the disposable snapshot/staging.
        if (await TransferScalarAsync(c,"SELECT COUNT(*) FROM pragma_foreign_key_list('quotation_processed_contracts') WHERE \"table\"='contracts';",ct,tx)==0)
            return;
        await TransferSqlAsync(c,"""
            CREATE TABLE quotation_processed_contracts_transfer(
                run_id TEXT NOT NULL REFERENCES quotation_automation_runs(id) ON DELETE CASCADE,
                contract_id TEXT NOT NULL,
                prompt_order INTEGER NOT NULL,
                processed_at TEXT NOT NULL,
                matched_items INTEGER NOT NULL DEFAULT 0,
                revealed_prices INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(run_id, contract_id)
            );
            INSERT INTO quotation_processed_contracts_transfer SELECT * FROM quotation_processed_contracts;
            DROP TABLE quotation_processed_contracts;
            ALTER TABLE quotation_processed_contracts_transfer RENAME TO quotation_processed_contracts;
            CREATE INDEX idx_processed_contracts_run_time
                ON quotation_processed_contracts(run_id, processed_at, contract_id);
            """,ct,tx);
    }

    private static async Task<List<(string Name, string Sql)>> SuspendLocalTriggersAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        var triggers = new List<(string,string)>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "SELECT name,sql,tbl_name FROM sqlite_master WHERE type='trigger';";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (TransferLocalTables.Contains(r.GetString(2)) || r.GetString(0) == "contracts_retention_insert")
                    triggers.Add((r.GetString(0),r.GetString(1)));
        }
        foreach (var t in triggers) await TransferSqlAsync(c, $"DROP TRIGGER {Q(t.Item1)};", ct, tx);
        return triggers;
    }
    private static async Task<HashSet<string>> TransferTableNamesAsync(SqliteConnection c, string schema, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT name FROM {schema}.sqlite_master WHERE type='table';";
        await using var r = await cmd.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) result.Add(r.GetString(0)); return result;
    }
    private async Task RemoveTransferLocalDataAsync(string path, CancellationToken ct)
    {
        await using var c = await TransferConnectionAsync(path, ct);
        var rules = await ReadTransferRulesAsync(Path.GetDirectoryName(path)!, ct);
        await ValidateTransferSchemaAsync(c, "main", rules, "banco local na cópia de exportação", repository.DatabasePath, ct);
        await TransferSqlAsync(c, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;", ct);
        var tables = await TransferTableNamesAsync(c,"main",ct);
        await using (var tx = c.BeginTransaction())
        {
            await NormalizeTransferHistoryAsync(c,tx,ct);
            var triggers = await SuspendLocalTriggersAsync(c,tx,ct);
            // Count the public dictionary on the exporting PC, not on the old receiver.
            await TransferSqlAsync(c,"CREATE TEMP TABLE transfer_catalog_counts AS SELECT catalog_kind,COUNT(*) AS active_records FROM catalog_entries WHERE active=1 GROUP BY catalog_kind;",ct,tx);
            foreach (var table in TransferLocalTables.Concat(TransferResetTables).Where(tables.Contains)) await TransferSqlAsync(c,$"DELETE FROM {Q(table)};",ct,tx);
            await TransferSqlAsync(c,"INSERT INTO catalog_sync_state(catalog_kind,active_records) SELECT catalog_kind,active_records FROM transfer_catalog_counts; INSERT OR IGNORE INTO catalog_sync_state(catalog_kind) VALUES(1),(2);",ct,tx);
            await TransferSqlAsync(c,"UPDATE price_cache_contracts SET user_pinned=0,background_owned=0; UPDATE price_cache_control SET authorized=0,enabled=0,paused=0,authorized_at=NULL; UPDATE national_price_index_control SET authorized=0,enabled=0,paused=0,authorized_at=NULL;",ct,tx);
            foreach (var trigger in triggers) await TransferSqlAsync(c,trigger.Sql,ct,tx);
            await tx.CommitAsync(ct);
        }
        await TransferSqlAsync(c,"VACUUM;",ct);
    }
    private static async Task ConsolidateTransferDatabaseAsync(string path, CancellationToken ct, bool prepareWal = false)
    {
        await using var c = await TransferConnectionAsync(path,ct);
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.GetInt32(0) != 0 || (r.GetInt32(1) > 0 && r.GetInt32(1) != r.GetInt32(2)))
                throw new IOException("Há operações SQLite ativas; feche outras operações e tente novamente.");
        }
        if (prepareWal) await TransferSqlAsync(c,"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;",ct);
    }
    private async Task<string> InstallPreservingLocalDataAsync(
        string staged, string packagePath, TransferSchemaRules rules, IProgress<BackupImportProgress>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A troca protegida exige Windows.");
        var target = Path.GetFullPath(repository.DatabasePath);
        if (!File.Exists(target)) throw new InvalidOperationException("Inicialize o banco de destino antes de importar preços.");
        await ConsolidateTransferDatabaseAsync(staged,ct,prepareWal:true);
        await using var gate = await SqliteDatabaseTransferGate.EnterExclusiveAsync(target,ct);
        SqliteConnection.ClearAllPools();
        await ConsolidateTransferDatabaseAsync(target,ct);
        // Deny all read/write opens, including SQLite writers outside the factory; allow read-only copying and rename.
        using var guard = new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read | FileShare.Delete);
        if (File.Exists(target+"-wal") && new FileInfo(target+"-wal").Length > 0)
            throw new IOException("O banco foi modificado durante a preparação; tente novamente.");
        Report(progress,BackupImportStage.PreservingCurrentDatabase,80,"Preservando cotações e configurações locais.",isIndeterminate:true);
        await using (var c = await TransferConnectionAsync(staged,ct))
        {
            await ValidateTransferSchemaAsync(c, "main", rules, "pacote de preços", packagePath, ct);
            await using (var attach=c.CreateCommand())
            {
                attach.CommandText="ATTACH DATABASE $path AS local_data;";
                attach.Parameters.AddWithValue("$path",new Uri(target).AbsoluteUri+"?mode=ro&immutable=1");
                await attach.ExecuteNonQueryAsync(ct);
            }
            await TransferSqlAsync(c,"PRAGMA local_data.cache_size=-32768;",ct);
            // The active file or its schema may have changed during extraction. Check again under the swap guard.
            await ValidateTransferSchemaAsync(c, "local_data", rules, "banco local antes da instalação", target, ct);
            var localTables = await TransferTableNamesAsync(c,"local_data",ct);
            var stagingTables = await TransferTableNamesAsync(c,"main",ct);
            await using var tx = c.BeginTransaction();
            if (localTables.Contains("quotation_manual_price_divisors") && !stagingTables.Contains("quotation_manual_price_divisors"))
                await SqliteQuotationRepository.EnsurePriceDivisorsTableAsync(c,tx,ct);
            await NormalizeTransferHistoryAsync(c,tx,ct);
            var triggers=await SuspendLocalTriggersAsync(c,tx,ct);
            foreach (var table in TransferResetTables) await TransferSqlAsync(c,$"DELETE FROM main.{Q(table)};",ct,tx);
            // The incoming public database is authoritative. Save only its tiny catalogue
            // counters before replacing the destination's private synchronization state.
            await TransferSqlAsync(c,"CREATE TEMP TABLE transfer_catalog_counts AS SELECT catalog_kind,active_records FROM main.catalog_sync_state;",ct,tx);
            if (await TransferScalarAsync(c,"SELECT COUNT(*) FROM transfer_catalog_counts;",ct,tx)==0)
                // Version 1.2.44 exported no catalogue counters. This compatibility fallback
                // reads only the incoming dictionary, never destination contracts/items/prices.
                await TransferSqlAsync(c,"INSERT INTO transfer_catalog_counts SELECT catalog_kind,COUNT(*) FROM main.catalog_entries WHERE active=1 GROUP BY catalog_kind;",ct,tx);
            foreach (var table in TransferLocalTables)
            {
                if (stagingTables.Contains(table)) await TransferSqlAsync(c,$"DELETE FROM main.{Q(table)};",ct,tx);
                if (!localTables.Contains(table)) continue;
                await TransferSqlAsync(c,$"INSERT INTO main.{Q(table)} SELECT * FROM local_data.{Q(table)};",ct,tx);
                if (await TransferScalarAsync(c,$"SELECT EXISTS(SELECT * FROM main.{Q(table)} EXCEPT SELECT * FROM local_data.{Q(table)}) OR EXISTS(SELECT * FROM local_data.{Q(table)} EXCEPT SELECT * FROM main.{Q(table)});",ct,tx)!=0)
                    throw new InvalidDataException($"Não foi possível preservar integralmente {table}.");
            }
            // Active catalogue rows describe the incoming public dictionary, not the
            // destination's in-progress staging generation. Keep the latter intact.
            await TransferSqlAsync(c,"UPDATE main.catalog_sync_state SET active_records=COALESCE((SELECT active_records FROM transfer_catalog_counts t WHERE t.catalog_kind=catalog_sync_state.catalog_kind),0);",ct,tx);
            foreach(var control in new[]{"price_cache_control","national_price_index_control"})
                await TransferSqlAsync(c,$"UPDATE main.{control} SET (authorized,enabled,paused,authorized_at)=(SELECT authorized,enabled,paused,authorized_at FROM local_data.{control} WHERE id=1) WHERE id=1;",ct,tx);
            // Legacy complete-without-quotations packages retained the sender's pins.
            // Clear only marked rows via the ownership index before restoring local choices.
            await TransferSqlAsync(c,"UPDATE main.price_cache_contracts SET user_pinned=0,background_owned=0 WHERE contract_id IN(SELECT contract_id FROM main.price_cache_contracts WHERE background_owned=1 UNION ALL SELECT contract_id FROM main.price_cache_contracts WHERE background_owned=0 AND user_pinned=1);",ct,tx);
            await TransferSqlAsync(c,"UPDATE main.price_cache_contracts SET (user_pinned,background_owned)=(SELECT user_pinned,background_owned FROM local_data.price_cache_contracts l WHERE l.contract_id=price_cache_contracts.contract_id) WHERE contract_id IN(SELECT contract_id FROM local_data.price_cache_contracts WHERE background_owned=1 UNION ALL SELECT contract_id FROM local_data.price_cache_contracts WHERE background_owned=0 AND user_pinned=1);",ct,tx);
            foreach(var table in TransferLocalTables.Where(localTables.Contains))
                if(await TransferScalarAsync(c,$"SELECT COUNT(*) FROM pragma_foreign_key_check('{table}');",ct,tx)!=0)
                    throw new InvalidDataException($"Referências locais inválidas: {table}.");
            foreach(var trigger in triggers) await TransferSqlAsync(c,trigger.Sql,ct,tx);
            await tx.CommitAsync(ct);
        }
        await ConsolidateTransferDatabaseAsync(staged,ct,prepareWal:true);
        // Flush all new bytes before the one filesystem replacement. The old file remains a recovery backup.
        using(var file=new FileStream(staged,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) file.Flush(true);
        ct.ThrowIfCancellationRequested();
        var recovery=BuildRecoveryPath();
        Report(progress,BackupImportStage.InstallingDatabase,95,"Instalando a base de preços; cotações preservadas.",canCancel:false);
        File.Replace(staged,target,recovery,ignoreMetadataErrors:false);
        SqliteConnection.ClearAllPools();
        // File.Replace is the commit boundary. Observer failures must not report a failed import.
        try { Report(progress,BackupImportStage.Completed,100,"Base completa de preços importada; dados locais preservados.",canCancel:false); }
        catch (Exception) { /* The durable replacement already succeeded. */ }
        return recovery;
    }

}
