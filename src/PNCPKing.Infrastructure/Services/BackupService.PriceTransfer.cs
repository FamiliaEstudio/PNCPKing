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
        "guard_master", "guard_workers", "guard_campaigns", "guard_imported_packages",
        "official_update_packages", "official_update_chunks", "sync_runs", "sync_partitions"
    ];
    private static readonly string[] TransferPublicTables =
    [
        "schema_info", "dataset", "contracts", "items", "item_results", "contract_item_snapshots",
        "coverage_day_modalities", "catalog_entries", "price_cache_control", "price_cache_contracts",
        "dataset_statistics", "national_price_index_control", "catalog_description_index_state",
        "maintenance_state", "official_result_snapshots"
    ];
    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static async Task<SqliteConnection> TransferConnectionAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = false }.ToString());
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
    private static async Task ValidateTransferSchemaAsync(SqliteConnection c, string schema, string directory, CancellationToken ct)
    {
        var referencePath = Path.Combine(directory, $"schema-{Guid.NewGuid():N}.db");
        try
        {
            await new SqliteContractRepository(referencePath).InitializeAsync(ct).ConfigureAwait(false);
            await using var reference = await TransferConnectionAsync(referencePath, ct);
            await using (var tx = reference.BeginTransaction())
            {
                await SqliteQuotationRepository.EnsurePriceDivisorsTableAsync(reference, tx, ct);
                await tx.CommitAsync(ct);
            }
            var expected = await TransferSchemaAsync(reference, "main", ct);
            var actual = await TransferSchemaAsync(c, schema, ct);
            // SQLite preserves harmless whitespace and IF NOT EXISTS in sqlite_master.
            static string Normalize(string sql) => System.Text.RegularExpressions.Regex.Replace(sql.Replace("IF NOT EXISTS", "", StringComparison.OrdinalIgnoreCase), @"\s+", " ").Trim().TrimEnd(';');
            foreach (var entry in actual)
                if (!expected.TryGetValue(entry.Key, out var ddl) || Normalize(ddl) != Normalize(entry.Value))
                    throw new InvalidDataException($"Esquema não reconhecido para transferência segura: {entry.Key}.");
            foreach (var entry in expected)
                if (!actual.ContainsKey(entry.Key) && entry.Key != "table:quotation_manual_price_divisors")
                    throw new InvalidDataException($"Estrutura obrigatória ausente: {entry.Key}.");
            foreach (var name in actual.Keys.Where(k => k.StartsWith("table:")).Select(k => k[6..]))
            {
                var fts = new[] { "contracts_fts", "items_fts", "catalog_entries_fts", "catalog_description_fts" };
                if (!TransferLocalTables.Contains(name) && !TransferPublicTables.Contains(name) &&
                    !fts.Any(f => name == f || new[] { "_data", "_idx", "_content", "_docsize", "_config" }.Any(s => name == f + s)))
                    throw new InvalidDataException($"Tabela sem política de transferência: {name}.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(referencePath + suffix)) File.Delete(referencePath + suffix);
        }
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
    private static async Task RemoveTransferLocalDataAsync(string path, CancellationToken ct)
    {
        await using var c = await TransferConnectionAsync(path, ct);
        await ValidateTransferSchemaAsync(c, "main", Path.GetDirectoryName(path)!, ct);
        await TransferSqlAsync(c, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;", ct);
        var tables = await TransferTableNamesAsync(c,"main",ct);
        await using (var tx = c.BeginTransaction())
        {
            var triggers = await SuspendLocalTriggersAsync(c,tx,ct);
            foreach (var table in TransferLocalTables.Where(tables.Contains)) await TransferSqlAsync(c,$"DELETE FROM {Q(table)};",ct,tx);
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
    private async Task<string> InstallPreservingLocalDataAsync(string staged, IProgress<BackupImportProgress>? progress, CancellationToken ct)
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
            await ValidateTransferSchemaAsync(c,"main",Path.GetDirectoryName(staged)!,ct);
            await using (var attach=c.CreateCommand())
            {
                attach.CommandText="ATTACH DATABASE $path AS local_data;";
                attach.Parameters.AddWithValue("$path",new Uri(target).AbsoluteUri+"?mode=ro&immutable=1");
                await attach.ExecuteNonQueryAsync(ct);
            }
            await TransferSqlAsync(c,"PRAGMA local_data.cache_size=-32768;",ct);
            await ValidateTransferSchemaAsync(c,"local_data",Path.GetDirectoryName(staged)!,ct);
            var localTables = await TransferTableNamesAsync(c,"local_data",ct);
            var stagingTables = await TransferTableNamesAsync(c,"main",ct);
            foreach (var table in TransferLocalTables.Where(stagingTables.Contains))
                if (await TransferScalarAsync(c,$"SELECT EXISTS(SELECT 1 FROM main.{Q(table)});",ct)!=0)
                    throw new InvalidDataException($"O pacote de preços contém dados locais: {table}.");
            await using var tx = c.BeginTransaction();
            if (localTables.Contains("quotation_manual_price_divisors") && !stagingTables.Contains("quotation_manual_price_divisors"))
                await SqliteQuotationRepository.EnsurePriceDivisorsTableAsync(c,tx,ct);
            var triggers=await SuspendLocalTriggersAsync(c,tx,ct);
            await PreservePublicRowsAsync(c,tx,ct);
            foreach (var table in TransferLocalTables)
            {
                if (!localTables.Contains(table)) continue;
                await TransferSqlAsync(c,$"DELETE FROM main.{Q(table)}; INSERT INTO main.{Q(table)} SELECT * FROM local_data.{Q(table)};",ct,tx);
                if (await TransferScalarAsync(c,$"SELECT EXISTS(SELECT * FROM main.{Q(table)} EXCEPT SELECT * FROM local_data.{Q(table)}) OR EXISTS(SELECT * FROM local_data.{Q(table)} EXCEPT SELECT * FROM main.{Q(table)});",ct,tx)!=0)
                    throw new InvalidDataException($"Não foi possível preservar integralmente {table}.");
            }
            // Active catalogue rows describe the incoming public dictionary, not the
            // destination's in-progress staging generation. Keep the latter intact.
            await TransferSqlAsync(c,"UPDATE main.catalog_sync_state SET active_records=(SELECT COUNT(*) FROM main.catalog_entries e WHERE e.catalog_kind=catalog_sync_state.catalog_kind AND e.active=1);",ct,tx);
            foreach(var control in new[]{"price_cache_control","national_price_index_control"})
                await TransferSqlAsync(c,$"UPDATE main.{control} SET (authorized,enabled,paused,authorized_at)=(SELECT authorized,enabled,paused,authorized_at FROM local_data.{control} WHERE id=1) WHERE id=1;",ct,tx);
            await TransferSqlAsync(c,"UPDATE main.price_cache_contracts SET (user_pinned,background_owned)=(SELECT user_pinned,background_owned FROM local_data.price_cache_contracts l WHERE l.contract_id=price_cache_contracts.contract_id) WHERE contract_id IN(SELECT contract_id FROM local_data.price_cache_contracts);",ct,tx);
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

    private static async Task<string[]> TransferColumnsAsync(SqliteConnection c, string table, SqliteTransaction tx, CancellationToken ct)
    {
        var names=new List<string>(); await using var cmd=c.CreateCommand(); cmd.Transaction=tx;
        cmd.CommandText=$"PRAGMA main.table_info({Q(table)});";
        await using var r=await cmd.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)) names.Add(r.GetString(1));
        return names.ToArray();
    }
    private static async Task PreservePublicRowsAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        // Copy whole absent contract trees only. Never union children of an existing
        // contract: absence can be an authoritative PNCP deletion/cancellation.
        await TransferSqlAsync(c,"CREATE TEMP TABLE transfer_missing_contracts(id TEXT PRIMARY KEY); INSERT INTO transfer_missing_contracts SELECT l.pncp_id FROM local_data.contracts l WHERE NOT EXISTS(SELECT 1 FROM main.contracts s WHERE s.pncp_id=l.pncp_id);",ct,tx);
        const string newerParent="COALESCE(julianday(sc.global_updated_at)>julianday(lc.global_updated_at),0)";
        const string completeParent="EXISTS(SELECT 1 FROM main.contract_item_snapshots cs WHERE cs.contract_id=sc.pncp_id AND cs.source_global_updated_at IS sc.global_updated_at AND cs.item_count=(SELECT COUNT(*) FROM main.items ci WHERE ci.contract_id=sc.pncp_id))";
        const string newerItem="COALESCE(julianday(si.source_updated_at)>julianday(li.source_updated_at),0)";
        const string completeResults="EXISTS(SELECT 1 FROM main.official_result_snapshots rs WHERE rs.contract_id=si.contract_id AND rs.item_number=si.item_number AND rs.parent_version IS sc.global_updated_at AND rs.item_version IS si.source_updated_at AND rs.result_count=(SELECT COUNT(*) FROM main.item_results rr WHERE rr.contract_id=si.contract_id AND rr.item_number=si.item_number))";
        async Task RejectAsync(string sql,string message)
        { if(await TransferScalarAsync(c,sql,ct,tx)!=0) throw new InvalidDataException(message); }
        await RejectAsync($"SELECT EXISTS(SELECT 1 FROM local_data.contracts lc JOIN main.contracts sc ON sc.pncp_id=lc.pncp_id WHERE lc.global_updated_at IS NOT sc.global_updated_at AND NOT({newerParent} AND {completeParent}));",
            "Há contratos locais com versão mais recente ou divergência sem snapshot completo na origem. Atualize e reexporte a base.");
        await RejectAsync($"SELECT EXISTS(SELECT 1 FROM local_data.items li JOIN local_data.contracts lc ON lc.pncp_id=li.contract_id JOIN main.contracts sc ON sc.pncp_id=li.contract_id LEFT JOIN main.items si ON si.contract_id=li.contract_id AND si.item_number=li.item_number WHERE (si.contract_id IS NULL AND NOT({newerParent} AND {completeParent})) OR (si.contract_id IS NOT NULL AND li.source_updated_at IS NOT si.source_updated_at AND NOT({newerItem} AND {completeResults})));",
            "Há itens locais ausentes ou divergentes sem versão completa mais recente na origem. Atualize e reexporte a base.");
        var contractColumns=await TransferColumnsAsync(c,"contracts",tx,ct);
        var differentContract="EXISTS(SELECT "+string.Join(",",contractColumns.Select(n=>"lc."+Q(n)))+" EXCEPT SELECT "+string.Join(",",contractColumns.Select(n=>"sc."+Q(n)))+")";
        await RejectAsync($"SELECT EXISTS(SELECT 1 FROM local_data.contracts lc JOIN main.contracts sc ON sc.pncp_id=lc.pncp_id WHERE {differentContract} AND NOT({newerParent} AND {completeParent}));",
            "Há conteúdo de contrato divergente sem versão completa mais recente na origem. Nada foi substituído.");
        var itemColumns=(await TransferColumnsAsync(c,"items",tx,ct)).Where(n=>n is not ("hydration_status" or "last_error" or "cache_updated_at")).ToArray();
        var differentItem="EXISTS(SELECT "+string.Join(",",itemColumns.Select(n=>"li."+Q(n)))+" EXCEPT SELECT "+string.Join(",",itemColumns.Select(n=>"si."+Q(n)))+")";
        await RejectAsync($"SELECT EXISTS(SELECT 1 FROM local_data.items li JOIN local_data.contracts lc ON lc.pncp_id=li.contract_id JOIN main.contracts sc ON sc.pncp_id=li.contract_id JOIN main.items si ON si.contract_id=li.contract_id AND si.item_number=li.item_number WHERE {differentItem} AND NOT(({newerParent} AND {completeParent}) OR ({newerItem} AND {completeResults})));",
            "Há conteúdo de item divergente sem versão completa mais recente na origem. Nada foi substituído.");
        var resultColumns=await TransferColumnsAsync(c,"item_results",tx,ct);
        var differentResults="EXISTS(SELECT "+string.Join(",",resultColumns.Select(n=>"lr."+Q(n)))+" EXCEPT SELECT "+string.Join(",",resultColumns.Select(n=>"sr."+Q(n)))+")";
        await RejectAsync($"SELECT EXISTS(SELECT 1 FROM local_data.item_results lr JOIN local_data.items li ON li.contract_id=lr.contract_id AND li.item_number=lr.item_number JOIN local_data.contracts lc ON lc.pncp_id=lr.contract_id JOIN main.contracts sc ON sc.pncp_id=lr.contract_id LEFT JOIN main.items si ON si.contract_id=lr.contract_id AND si.item_number=lr.item_number LEFT JOIN main.item_results sr ON sr.contract_id=lr.contract_id AND sr.item_number=lr.item_number AND sr.result_sequence=lr.result_sequence WHERE (sr.contract_id IS NULL OR {differentResults}) AND NOT ((si.contract_id IS NULL AND {newerParent} AND {completeParent}) OR (si.contract_id IS NOT NULL AND ({newerParent} OR {newerItem}) AND {completeResults})));",
            "Há preços locais diferentes ou ausentes sem comprovação de uma versão completa mais recente na origem. Nada foi substituído.");
        await TransferSqlAsync(c,"INSERT INTO main.contracts SELECT l.* FROM local_data.contracts l JOIN transfer_missing_contracts m ON m.id=l.pncp_id; UPDATE main.dataset_statistics SET contract_count=contract_count+changes() WHERE id=1;",ct,tx);
        foreach(var table in new[]{"items","item_results","contract_item_snapshots","official_result_snapshots"})
            await TransferSqlAsync(c,$"INSERT INTO main.{table} SELECT l.* FROM local_data.{table} l JOIN transfer_missing_contracts m ON m.id=l.contract_id;",ct,tx);
        // Contracts/snapshot triggers create provisional cache rows. Replace only those
        // belonging to trees absent from the incoming snapshot, keeping cache statistics triggers.
        await TransferSqlAsync(c,"DELETE FROM main.price_cache_contracts WHERE contract_id IN(SELECT id FROM transfer_missing_contracts); INSERT INTO main.price_cache_contracts SELECT l.* FROM local_data.price_cache_contracts l JOIN transfer_missing_contracts m ON m.id=l.contract_id;",ct,tx);
        await TransferSqlAsync(c,"INSERT INTO main.coverage_day_modalities SELECT l.* FROM local_data.coverage_day_modalities l WHERE NOT EXISTS(SELECT 1 FROM main.coverage_day_modalities s WHERE s.coverage_date=l.coverage_date AND s.modality_id=l.modality_id AND s.uf=l.uf);",ct,tx);
        // Do not regress a more recent completed local checkpoint to an older partial source checkpoint.
        // A newer partial source checkpoint may represent work still needed, so it stays partial.
        await TransferSqlAsync(c,"UPDATE main.coverage_day_modalities AS s SET (status,records_count,updated_at,last_error)=(SELECT l.status,l.records_count,l.updated_at,l.last_error FROM local_data.coverage_day_modalities l WHERE l.coverage_date=s.coverage_date AND l.modality_id=s.modality_id AND l.uf=s.uf) WHERE s.status NOT IN(3,4) AND EXISTS(SELECT 1 FROM local_data.coverage_day_modalities l WHERE l.coverage_date=s.coverage_date AND l.modality_id=s.modality_id AND l.uf=s.uf AND l.status IN(3,4) AND julianday(l.updated_at)>=julianday(s.updated_at));",ct,tx);
        await TransferSqlAsync(c,"INSERT INTO main.catalog_entries SELECT l.* FROM local_data.catalog_entries l WHERE NOT EXISTS(SELECT 1 FROM main.catalog_entries s WHERE s.catalog_kind=l.catalog_kind AND s.code=l.code);",ct,tx);
    }
}
