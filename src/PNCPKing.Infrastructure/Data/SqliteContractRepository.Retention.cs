using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Data;

public sealed record DataRetentionResult(
    long RemovedContracts = 0,
    long RemovedReferences = 0,
    long AffectedLines = 0,
    bool Applied = false,
    bool Compacted = false,
    long BytesBefore = 0,
    long BytesAfter = 0,
    string Message = "");

public sealed partial class SqliteContractRepository
{
    public Task<DataRetentionResult> MaintainRetentionAsync(
        DateOnly today,
        bool compact = false,
        bool force = false,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null) =>
        MaintainRetentionCoreAsync(today, compact, force, cancellationToken, progress);

    internal async Task<DataRetentionResult> MaintainRetentionCoreAsync(
        DateOnly today, bool compact, bool force, CancellationToken cancellationToken,
        IProgress<string>? progress = null, long? availableFreeBytes = null)
    {
        using var span = _performance.Begin("maintenance", "retention");
        var result = new DataRetentionResult();
        var cutoff = DataWindow.Start(today);
        bool applied;
        bool compactionPending;
        await using (var writer = await _connections.WorkCoordinator.EnterWriterAsync(
                         SqliteWorkPriority.Visible, cancellationToken).ConfigureAwait(false))
        await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (var state = connection.CreateCommand())
            {
                state.CommandText = "SELECT last_retention_date, retention_compaction_completed FROM maintenance_state WHERE id = 1;";
                await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                applied = !force && reader.GetString(0) == FormatDate(today);
                compactionPending = reader.GetInt32(1) == 0;
            }
            if (compact && compactionPending)
                await ExecuteRetentionSqlAsync(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                progress?.Report("Removendo preços anteriores à janela de 11 meses…");
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                result = await PruneExpiredDataAsync(connection, transaction, cutoff, cancellationToken).ConfigureAwait(false);
                await NormalizeRetentionWindowsAsync(connection, transaction, cutoff, today, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Reuse the index reconciliation, without changing either authorization.
        // The completion date is saved only after both reconciliations succeed.
        if (!applied)
        {
            var cache = new SqlitePriceCacheRepository(_connections, _performance);
            var itemControl = await cache.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (itemControl.Authorized)
                await cache.PrepareWindowAsync(cutoff, today, cancellationToken).ConfigureAwait(false);
            var priceControl = await cache.GetNationalPriceIndexPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (priceControl.Authorized)
                await cache.PrepareNationalPriceIndexAsync(cutoff, today, cancellationToken).ConfigureAwait(false);
            await using var writer = await _connections.WorkCoordinator.EnterWriterAsync(
                SqliteWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var complete = connection.CreateCommand();
            complete.CommandText = "UPDATE maintenance_state SET last_retention_date = $today WHERE id = 1;";
            complete.Parameters.AddWithValue("$today", FormatDate(today));
            await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        result = result with { Message = $"Janela de 11 meses: {result.RemovedContracts:N0} contratações e " +
            $"{result.RemovedReferences:N0} referências removidas; {result.AffectedLines:N0} itens para reconfirmar." };
        if (compact && compactionPending)
        {
            var before = new FileInfo(DatabasePath).Length;
            var available = availableFreeBytes ?? Math.Min(
                new DriveInfo(Path.GetPathRoot(Path.GetFullPath(DatabasePath))!).AvailableFreeSpace,
                new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace);
            if (available < checked(before * 2))
            {
                result = result with { BytesBefore = before, BytesAfter = before,
                    Message = result.Message + " Compactação pendente: espaço livre insuficiente (reserva de duas vezes o banco)." };
            }
            else
            {
                progress?.Report("Compactando o banco e verificando sua integridade…");
                var watch = Stopwatch.StartNew();
                await using var writer = await _connections.WorkCoordinator.EnterWriterAsync(
                    SqliteWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
                await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteRetentionSqlAsync(connection, null, "PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
                await VerifyRetentionIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
                await ExecuteRetentionSqlAsync(connection, null,
                    "UPDATE maintenance_state SET retention_compaction_completed = 1 WHERE id = 1; PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);",
                    cancellationToken).ConfigureAwait(false);
                result = result with { Compacted = true, BytesBefore = before, BytesAfter = new FileInfo(DatabasePath).Length,
                    Message = result.Message + $" Compactação concluída em {watch.Elapsed.TotalSeconds:N1} s; " +
                        $"{before:N0} → {new FileInfo(DatabasePath).Length:N0} bytes." };
            }
        }
        span.Complete(result.RemovedContracts + result.RemovedReferences);
        return result;
    }

    internal static async Task<DataRetentionResult> PruneExpiredDataAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateOnly cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$cutoff", FormatDate(cutoff));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS expired_contracts(id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE IF NOT EXISTS expired_references(line_id TEXT, id TEXT, PRIMARY KEY(line_id, id)) WITHOUT ROWID;
            CREATE TEMP TABLE IF NOT EXISTS retention_affected_lines(id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE IF NOT EXISTS retention_affected_workspaces(
                line_id TEXT, prompt_slot INTEGER, removed_hits INTEGER, removed_prices INTEGER,
                PRIMARY KEY(line_id, prompt_slot)) WITHOUT ROWID;
            DELETE FROM expired_contracts;
            DELETE FROM expired_references;
            DELETE FROM retention_affected_lines;
            DELETE FROM retention_affected_workspaces;
            INSERT INTO expired_contracts SELECT pncp_id FROM contracts WHERE publication_date < $cutoff;
            INSERT INTO expired_references
            SELECT qr.line_id, qr.id FROM quotation_references qr
            LEFT JOIN contracts c ON c.pncp_id = qr.contract_id
            LEFT JOIN quotation_internet_price_evidence e ON e.line_id = qr.line_id AND e.reference_id = qr.id
            WHERE CASE WHEN qr.source_kind = 1
                THEN COALESCE(qr.result_date, substr(e.captured_at, 1, 10), qr.publication_date)
                ELSE COALESCE(c.publication_date, qr.publication_date) END < $cutoff;
            INSERT INTO retention_affected_lines SELECT DISTINCT line_id FROM expired_references;
            INSERT INTO retention_affected_workspaces
            SELECT h.line_id, h.prompt_slot, COUNT(*), SUM((SELECT COUNT(*) FROM item_results r
                WHERE r.contract_id = h.contract_id AND r.item_number = h.item_number
                  AND r.result_status_id = 1 AND r.unit_value_scaled > 0))
            FROM quotation_item_search_hits h WHERE h.contract_id IN (SELECT id FROM expired_contracts)
            GROUP BY h.line_id, h.prompt_slot;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "SELECT (SELECT COUNT(*) FROM expired_contracts), (SELECT COUNT(*) FROM expired_references), (SELECT COUNT(*) FROM retention_affected_lines);";
        DataRetentionResult result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            result = new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), Applied: true);
        }
        command.CommandText = """
            DELETE FROM quotation_references WHERE (line_id, id) IN (SELECT line_id, id FROM expired_references);
            DELETE FROM quotation_manual_baskets
             WHERE line_id IN (SELECT id FROM retention_affected_lines)
               AND NOT EXISTS(SELECT 1 FROM quotation_manual_basket_references m WHERE m.basket_id = quotation_manual_baskets.id);
            UPDATE quotation_manual_baskets SET updated_at = $now WHERE line_id IN (SELECT id FROM retention_affected_lines);
            UPDATE quotation_lines SET selection_confirmed = 0, selected_basket_key = NULL,
                sample_version = sample_version + 1,
                automation_message = 'Preços anteriores à janela de 11 meses removidos; revise e confirme novamente a cesta.'
             WHERE id IN (SELECT id FROM retention_affected_lines);
            UPDATE quotation_projects SET updated_at = $now
             WHERE id IN (SELECT project_id FROM quotation_lines WHERE id IN (SELECT id FROM retention_affected_lines));
            UPDATE quotation_references SET duplicate_of_reference_id = NULL
             WHERE duplicate_of_reference_id IS NOT NULL AND NOT EXISTS(
                SELECT 1 FROM quotation_references other
                 WHERE other.line_id = quotation_references.line_id AND other.id = quotation_references.duplicate_of_reference_id);
            DELETE FROM quotation_item_search_hits WHERE contract_id IN (SELECT id FROM expired_contracts);
            DELETE FROM quotation_item_search_failures WHERE contract_id IN (SELECT id FROM expired_contracts);
            UPDATE quotation_item_search_workspaces SET
                matched_items = MAX(0, matched_items - (SELECT removed_hits FROM retention_affected_workspaces w
                    WHERE w.line_id = quotation_item_search_workspaces.line_id AND w.prompt_slot = quotation_item_search_workspaces.prompt_slot)),
                revealed_prices = MAX(0, revealed_prices - (SELECT removed_prices FROM retention_affected_workspaces w
                    WHERE w.line_id = quotation_item_search_workspaces.line_id AND w.prompt_slot = quotation_item_search_workspaces.prompt_slot))
             WHERE (line_id, prompt_slot) IN (SELECT line_id, prompt_slot FROM retention_affected_workspaces);
            DELETE FROM contracts WHERE pncp_id IN (SELECT id FROM expired_contracts);
            DELETE FROM coverage_day_modalities WHERE coverage_date < $cutoff;
            DELETE FROM sync_partitions WHERE end_date < $cutoff;
            DELETE FROM sync_runs WHERE end_date < $cutoff;
            DELETE FROM quotation_internet_price_drafts WHERE captured_at < $cutoff;
            DELETE FROM quotation_internet_evidence_assets WHERE sha256 NOT IN (
                SELECT price_image_sha256 FROM quotation_internet_price_evidence
                UNION SELECT tax_id_image_sha256 FROM quotation_internet_price_evidence
                UNION SELECT price_image_sha256 FROM quotation_internet_price_drafts WHERE price_image_sha256 IS NOT NULL
                UNION SELECT tax_id_image_sha256 FROM quotation_internet_price_drafts WHERE tax_id_image_sha256 IS NOT NULL);
            UPDATE maintenance_state SET last_optimize_date = '' WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal static async Task NormalizeRetentionWindowsAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateOnly cutoff, DateOnly today,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$cutoff", FormatDate(cutoff));
        command.Parameters.AddWithValue("$today", FormatDate(today));
        command.CommandText = """
            UPDATE maintenance_state SET retention_cutoff = $cutoff WHERE id = 1;
            UPDATE dataset SET start_date = $cutoff, end_date = $today WHERE start_date IS NOT NULL;
            UPDATE price_cache_control SET window_start = $cutoff, window_end = $today;
            UPDATE national_price_index_control SET window_start = $cutoff, window_end = $today;
            UPDATE quotation_item_search_workspaces SET
                start_date = CASE WHEN start_date > $today OR end_date < $cutoff OR start_date > end_date THEN $cutoff ELSE MAX(start_date, $cutoff) END,
                end_date = CASE WHEN start_date > $today OR end_date < $cutoff OR start_date > end_date THEN $today ELSE MIN(end_date, $today) END,
                cursor_geo_layer = NULL, cursor_group_rank = NULL, cursor_rotation_band = NULL,
                cursor_random_key = NULL, cursor_pncp_id = NULL, contracts_examined = 0,
                batches_completed = 0, candidate_set_exhausted = 0,
                status_message = 'Período ajustado à janela de 11 meses; candidatas reiniciadas.'
             WHERE start_date < $cutoff OR end_date > $today OR end_date < $cutoff OR start_date > end_date;
            CREATE TEMP TABLE IF NOT EXISTS retention_changed_runs(id TEXT PRIMARY KEY) WITHOUT ROWID;
            DELETE FROM retention_changed_runs;
            INSERT INTO retention_changed_runs SELECT id FROM quotation_automation_runs
             WHERE start_date < $cutoff OR end_date > $today OR end_date < $cutoff OR start_date > end_date;
            UPDATE quotation_automation_runs SET
                start_date = CASE WHEN start_date > $today OR end_date < $cutoff OR start_date > end_date THEN $cutoff ELSE MAX(start_date, $cutoff) END,
                end_date = CASE WHEN start_date > $today OR end_date < $cutoff OR start_date > end_date THEN $today ELSE MIN(end_date, $today) END
             WHERE id IN (SELECT id FROM retention_changed_runs);
            UPDATE quotation_contract_search_prompts SET cursor_geo_layer = NULL, cursor_group_rank = NULL,
                cursor_rotation_band = NULL, cursor_random_key = NULL, cursor_pncp_id = NULL,
                candidate_exhausted = 0, contracts_examined = 0
             WHERE run_id IN (SELECT id FROM retention_changed_runs);
            UPDATE quotation_lines SET search_cursor_geo_layer = NULL, search_cursor_group_rank = NULL,
                search_cursor_rotation_band = NULL, search_cursor_random_key = NULL, search_cursor_pncp_id = NULL,
                search_candidate_exhausted = 0, search_contracts_examined = 0, search_batches_completed = 0
             WHERE automation_run_id IN (SELECT id FROM retention_changed_runs);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteRetentionSqlAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyRetentionIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string, "ok", StringComparison.Ordinal))
                throw new InvalidDataException("A verificação do banco compactado falhou.");
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Há relacionamentos inconsistentes no banco compactado.");
        }
        foreach (var table in new[] { "contracts_fts", "items_fts", "catalog_entries_fts", "catalog_description_fts" })
        {
            if (table == "catalog_description_fts")
            {
                await using var state = connection.CreateCommand();
                state.CommandText = "SELECT completed FROM catalog_description_index_state WHERE id = 1;";
                if (Convert.ToInt32(await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                    continue; // This optional index can legitimately be only partially built.
            }
            try
            {
                await ExecuteRetentionSqlAsync(connection, null, $"INSERT INTO {table}({table}, rank) VALUES('integrity-check', 1);", cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 11)
            {
                await ExecuteRetentionSqlAsync(connection, null, $"INSERT INTO {table}({table}) VALUES('rebuild');", cancellationToken).ConfigureAwait(false);
                await ExecuteRetentionSqlAsync(connection, null, $"INSERT INTO {table}({table}, rank) VALUES('integrity-check', 1);", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ApplySchemaV27Async(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var (column, definition) in new[]
        {
            ("last_retention_date", "TEXT NOT NULL DEFAULT ''"),
            ("retention_cutoff", "TEXT NOT NULL DEFAULT ''"),
            ("retention_compaction_completed", "INTEGER NOT NULL DEFAULT 0")
        })
        {
            if (!await HasColumnAsync(connection, transaction, "maintenance_state", column, cancellationToken).ConfigureAwait(false))
                await ExecuteRetentionSqlAsync(connection, transaction,
                    $"ALTER TABLE maintenance_state ADD COLUMN {column} {definition};", cancellationToken).ConfigureAwait(false);
        }
        await ExecuteRetentionSqlAsync(connection, transaction, SchemaV27Sql, cancellationToken).ConfigureAwait(false);
    }

    private const string SchemaV27Sql = """
        CREATE INDEX IF NOT EXISTS idx_quotation_references_contract ON quotation_references(contract_id);
        CREATE TRIGGER IF NOT EXISTS contracts_retention_insert BEFORE INSERT ON contracts
        WHEN new.publication_date < (SELECT retention_cutoff FROM maintenance_state WHERE id = 1)
        BEGIN SELECT RAISE(IGNORE); END;
        CREATE TRIGGER IF NOT EXISTS quotation_references_retention_insert BEFORE INSERT ON quotation_references
        WHEN CASE WHEN new.source_kind = 1 THEN COALESCE(new.result_date, new.publication_date)
             ELSE COALESCE((SELECT publication_date FROM contracts WHERE pncp_id = new.contract_id), new.publication_date) END
             < (SELECT retention_cutoff FROM maintenance_state WHERE id = 1)
        BEGIN SELECT RAISE(IGNORE); END;
        CREATE TRIGGER IF NOT EXISTS quotation_references_retention_update BEFORE UPDATE ON quotation_references
        WHEN CASE WHEN new.source_kind = 1 THEN COALESCE(new.result_date, new.publication_date)
             ELSE COALESCE((SELECT publication_date FROM contracts WHERE pncp_id = new.contract_id), new.publication_date) END
             < (SELECT retention_cutoff FROM maintenance_state WHERE id = 1)
        BEGIN SELECT RAISE(IGNORE); END;
        UPDATE schema_info SET version = 27 WHERE id = 1;
        """;
}
