using Microsoft.Data.Sqlite;
using System.Globalization;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Data;

public sealed partial class SqliteContractRepository
{
    private static async Task ApplySchemaV28Async(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS official_transfer_state(
                id INTEGER PRIMARY KEY CHECK(id=1), base_id TEXT, origin TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0, base_ready INTEGER NOT NULL DEFAULT 0, journal_suspended INTEGER NOT NULL DEFAULT 0);
            INSERT OR IGNORE INTO official_transfer_state(id,origin) VALUES(1,lower(hex(randomblob(16))));
            CREATE TABLE IF NOT EXISTS official_changes(
                kind INTEGER NOT NULL, key1 TEXT NOT NULL, key2 TEXT NOT NULL DEFAULT '',
                revision INTEGER NOT NULL, PRIMARY KEY(kind,key1,key2)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS official_result_snapshots(
                contract_id TEXT NOT NULL, item_number INTEGER NOT NULL,
                parent_version TEXT, item_version TEXT,
                PRIMARY KEY(contract_id,item_number),
                FOREIGN KEY(contract_id,item_number) REFERENCES items(contract_id,item_number) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS official_imports(
                package_id TEXT PRIMARY KEY, checksum TEXT NOT NULL, initial_base INTEGER NOT NULL DEFAULT 0, inventory_required INTEGER NOT NULL DEFAULT 0, last_sequence INTEGER NOT NULL DEFAULT 0,
                applied INTEGER NOT NULL DEFAULT 0, skipped INTEGER NOT NULL DEFAULT 0,
                conflicts INTEGER NOT NULL DEFAULT 0, completed INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS official_conflicts(
                kind INTEGER NOT NULL, key1 TEXT NOT NULL, key2 TEXT NOT NULL DEFAULT '',
                package_id TEXT NOT NULL, message TEXT NOT NULL, PRIMARY KEY(kind,key1,key2)) WITHOUT ROWID;
            CREATE TRIGGER IF NOT EXISTS official_items_invalidate AFTER UPDATE OF source_updated_at ON items
            WHEN new.source_updated_at IS NOT old.source_updated_at BEGIN
                DELETE FROM official_result_snapshots WHERE contract_id=new.contract_id AND item_number=new.item_number;
                UPDATE items SET hydration_status=4 WHERE contract_id=new.contract_id AND item_number=new.item_number AND has_result=1;
            END;
            CREATE TRIGGER IF NOT EXISTS official_contract_invalidate AFTER UPDATE OF global_updated_at ON contracts
            WHEN new.global_updated_at IS NOT old.global_updated_at BEGIN
                DELETE FROM official_result_snapshots WHERE contract_id=new.pncp_id;
            END;
            CREATE TRIGGER IF NOT EXISTS official_contract_delete AFTER DELETE ON contracts BEGIN
                DELETE FROM official_changes WHERE kind IN(1,2,3) AND key1=old.pncp_id;
                DELETE FROM official_conflicts WHERE kind IN(1,2,3) AND key1=old.pncp_id;
            END;
            UPDATE schema_info SET version=28 WHERE id=1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var operation in new[] { "insert", "update" })
        {
            command.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS official_coverage_{operation} AFTER {operation} ON coverage_day_modalities
                WHEN new.status=3 AND (SELECT base_id FROM official_transfer_state WHERE id=1) IS NOT NULL
                    AND (SELECT journal_suspended FROM official_transfer_state WHERE id=1)=0 BEGIN
                    UPDATE official_transfer_state SET revision=revision+1 WHERE id=1;
                    INSERT INTO official_changes SELECT 5,new.coverage_date,CAST(new.modality_id AS TEXT)||'|'||new.uf,revision
                        FROM official_transfer_state WHERE id=1
                        ON CONFLICT(kind,key1,key2) DO UPDATE SET revision=excluded.revision;
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var (table, kind, key1, key2) in new[] {
                     ("contracts", 1, "new.pncp_id", "''"),
                     ("contract_item_snapshots", 2, "new.contract_id", "''"),
                     ("official_result_snapshots", 3, "new.contract_id", "CAST(new.item_number AS TEXT)"),
                     ("catalog_entries", 4, "CAST(new.catalog_kind AS TEXT)", "new.code") })
        {
            foreach (var operation in new[] { "INSERT", "UPDATE" })
            {
                command.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS official_{table}_{operation} AFTER {operation} ON {table}
                    WHEN (SELECT base_id FROM official_transfer_state WHERE id=1) IS NOT NULL AND (SELECT journal_suspended FROM official_transfer_state WHERE id=1)=0 BEGIN
                        UPDATE official_transfer_state SET revision=revision+1 WHERE id=1;
                        INSERT INTO official_changes(kind,key1,key2,revision)
                            SELECT {kind},{key1},{key2},revision FROM official_transfer_state WHERE id=1
                            ON CONFLICT(kind,key1,key2) DO UPDATE SET revision=excluded.revision;
                    END;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<bool> HasOfficialContractConflictsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM official_conflicts WHERE kind=1);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    internal static async Task RecordOfficialResultsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string contractId, long itemNumber, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM official_conflicts WHERE kind=3 AND key1=$id AND key2=CAST($number AS TEXT);
            INSERT INTO official_result_snapshots(contract_id,item_number,parent_version,item_version)
                SELECT i.contract_id,i.item_number,c.global_updated_at,i.source_updated_at
                FROM items i JOIN contracts c ON c.pncp_id=i.contract_id
                WHERE i.contract_id=$id AND i.item_number=$number
            ON CONFLICT(contract_id,item_number) DO UPDATE SET
                parent_version=excluded.parent_version,item_version=excluded.item_version;
            """;
        command.Parameters.AddWithValue("$id", contractId);
        command.Parameters.AddWithValue("$number", itemNumber);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    internal static async Task RefreshOfficialPriceIndexAsync(SqliteConnection connection, SqliteTransaction transaction,
        string contractId, CancellationToken cancellationToken)
    {
        await using (var refreshPriceIndex = connection.CreateCommand())
        {
            refreshPriceIndex.Transaction = (SqliteTransaction)transaction;
            refreshPriceIndex.CommandText = """
                    UPDATE price_cache_contracts
                       SET price_index_eligible_item_count =
                               (SELECT COUNT(*) FROM items
                                 WHERE contract_id = $contractId AND has_result = 1),
                           price_index_completed_item_count =
                               (SELECT COUNT(*) FROM items
                                 WHERE contract_id = $contractId AND has_result = 1
                                   AND hydration_status = $complete),
                           price_index_priced_item_count =
                               (SELECT COUNT(*) FROM items i
                                 WHERE i.contract_id = $contractId AND i.has_result = 1
                                   AND i.hydration_status = $complete
                                   AND EXISTS(
                                       SELECT 1 FROM item_results r
                                        WHERE r.contract_id = i.contract_id
                                          AND r.item_number = i.item_number
                                          AND r.result_status_id = 1
                                          AND r.unit_value_scaled > 0)),
                           price_index_result_count =
                               (SELECT COUNT(*) FROM item_results
                                 WHERE contract_id = $contractId
                                   AND result_status_id = 1 AND unit_value_scaled > 0),
                           price_index_status = CASE
                               WHEN NOT EXISTS(
                                   SELECT 1 FROM items
                                    WHERE contract_id = $contractId AND has_result = 1
                                      AND hydration_status <> $complete)
                               THEN $checkpointComplete
                               WHEN price_index_status = $downloading THEN $downloading
                               ELSE $pending END,
                           price_index_last_error = CASE
                               WHEN NOT EXISTS(
                                   SELECT 1 FROM items
                                    WHERE contract_id = $contractId AND has_result = 1
                                      AND hydration_status <> $complete)
                               THEN '' ELSE price_index_last_error END,
                           price_index_next_retry_at = CASE
                               WHEN NOT EXISTS(
                                   SELECT 1 FROM items
                                    WHERE contract_id = $contractId AND has_result = 1
                                      AND hydration_status <> $complete)
                               THEN NULL ELSE price_index_next_retry_at END,
                           price_index_completed_at = CASE
                               WHEN NOT EXISTS(
                                   SELECT 1 FROM items
                                    WHERE contract_id = $contractId AND has_result = 1
                                      AND hydration_status <> $complete)
                               THEN $updatedAt ELSE price_index_completed_at END,
                           updated_at = $updatedAt
                     WHERE contract_id = $contractId;
                    """;
            refreshPriceIndex.Parameters.AddWithValue("$contractId", contractId);
            refreshPriceIndex.Parameters.AddWithValue("$complete", (int)ItemHydrationStatus.Complete);
            refreshPriceIndex.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
            refreshPriceIndex.Parameters.AddWithValue("$downloading", (int)PriceCacheContractStatus.Downloading);
            refreshPriceIndex.Parameters.AddWithValue("$checkpointComplete", (int)PriceCacheContractStatus.Complete);
            refreshPriceIndex.Parameters.AddWithValue(
                "$updatedAt",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await refreshPriceIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

    }

}
