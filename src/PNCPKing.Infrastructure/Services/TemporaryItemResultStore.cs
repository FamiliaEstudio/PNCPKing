using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

internal sealed record TemporaryItemResultEntry(
    bool Succeeded,
    string? Error,
    IReadOnlyList<HomologationResult> Results);

internal sealed record StoredItemSearchSession(
    Guid Id,
    string SearchKey,
    DateTimeOffset StartedAt,
    long RandomPivot,
    ItemCandidateCursor? Cursor,
    int ContractsScanned,
    int ExpandedContracts,
    int FullyResolvedContracts,
    int CachedItemLists,
    int ItemListCalls,
    int ItemResultCalls,
    int CompletedResultCalls,
    int FailedCalls,
    bool CandidateSetExhausted,
    IReadOnlyList<ItemSearchHit> Hits);

internal sealed record StoredContractFailure(
    string ContractId,
    int Attempts,
    string Error);

/// <summary>
/// Search-session prices deliberately live outside the user's permanent index.
/// The file has no pooling. Transient stores are deleted on reset/disposal; the
/// general-search store remains available for resumption after application exit.
/// </summary>
internal sealed class TemporaryItemResultStore(
    string databasePath,
    bool persistent = false) : IAsyncDisposable
{
    private const int SchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly bool _persistent = persistent;
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
        ForeignKeys = true
    }.ToString();

    public bool IsPersistent => _persistent;

    public void ClearAbandonedSession()
    {
        if (!_persistent)
        {
            DeleteFiles();
        }
    }

    public Task ResetAsync(Guid sessionId, CancellationToken cancellationToken) =>
        ResetAsync(
            sessionId,
            string.Empty,
            0,
            DateTimeOffset.UtcNow,
            cancellationToken);

    public async Task ResetAsync(
        Guid sessionId,
        string searchKey,
        long randomPivot,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        DeleteFiles();
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE session_info(
                id TEXT PRIMARY KEY,
                search_key TEXT NOT NULL,
                started_at TEXT NOT NULL,
                random_pivot INTEGER NOT NULL,
                cursor_geo_layer INTEGER,
                cursor_group_rank INTEGER,
                cursor_rotation_band INTEGER,
                cursor_random_key INTEGER,
                cursor_pncp_id TEXT,
                contracts_scanned INTEGER NOT NULL DEFAULT 0,
                expanded_contracts INTEGER NOT NULL DEFAULT 0,
                fully_resolved_contracts INTEGER NOT NULL DEFAULT 0,
                cached_item_lists INTEGER NOT NULL DEFAULT 0,
                item_list_calls INTEGER NOT NULL DEFAULT 0,
                item_result_calls INTEGER NOT NULL DEFAULT 0,
                completed_result_calls INTEGER NOT NULL DEFAULT 0,
                failed_calls INTEGER NOT NULL DEFAULT 0,
                candidate_set_exhausted INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE search_hits(
                contract_id TEXT NOT NULL,
                item_number INTEGER NOT NULL,
                discovered_order INTEGER NOT NULL,
                contract_json TEXT NOT NULL,
                item_json TEXT NOT NULL,
                PRIMARY KEY(contract_id, item_number)
            );
            CREATE INDEX idx_search_hits_order ON search_hits(discovered_order);

            CREATE TABLE queried_items(
                contract_id TEXT NOT NULL,
                item_number INTEGER NOT NULL,
                succeeded INTEGER NOT NULL,
                error TEXT,
                queried_at TEXT NOT NULL,
                PRIMARY KEY(contract_id, item_number)
            );

            CREATE TABLE item_results(
                contract_id TEXT NOT NULL,
                item_number INTEGER NOT NULL,
                result_sequence INTEGER NOT NULL,
                supplier_tax_id TEXT NOT NULL DEFAULT '',
                supplier_name TEXT NOT NULL DEFAULT '',
                supplier_type TEXT NOT NULL DEFAULT '',
                supplier_municipality TEXT NOT NULL DEFAULT '',
                supplier_uf TEXT NOT NULL DEFAULT '',
                quantity_scaled INTEGER,
                unit_value_scaled INTEGER,
                total_value_scaled INTEGER,
                result_date TEXT,
                result_status_id INTEGER NOT NULL DEFAULT 0,
                result_status_name TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(contract_id, item_number, result_sequence),
                FOREIGN KEY(contract_id, item_number)
                    REFERENCES queried_items(contract_id, item_number) ON DELETE CASCADE
            );

            CREATE TABLE contract_failures(
                contract_id TEXT PRIMARY KEY,
                attempts INTEGER NOT NULL DEFAULT 1,
                error TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );
            CREATE INDEX idx_contract_failures_updated
                ON contract_failures(updated_at, contract_id);

            PRAGMA user_version=2;

            INSERT INTO session_info(
                id, search_key, started_at, random_pivot, updated_at)
            VALUES($id, $searchKey, $startedAt, $randomPivot, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
        command.Parameters.AddWithValue("$searchKey", searchKey);
        command.Parameters.AddWithValue("$startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$randomPivot", randomPivot);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredItemSearchSession?> TryRestoreAsync(
        string searchKey,
        CancellationToken cancellationToken)
    {
        if (!_persistent || !File.Exists(_databasePath))
        {
            return null;
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version;";
                var value = Convert.ToInt32(
                    await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (value != SchemaVersion)
                {
                    return null;
                }
            }

            Guid id;
            DateTimeOffset startedAt;
            long randomPivot;
            ItemCandidateCursor? cursor;
            int contractsScanned;
            int expandedContracts;
            int fullyResolvedContracts;
            int cachedItemLists;
            int itemListCalls;
            int itemResultCalls;
            int completedResultCalls;
            int failedCalls;
            bool exhausted;
            await using (var session = connection.CreateCommand())
            {
                session.CommandText = """
                    SELECT id, search_key, started_at, random_pivot,
                           cursor_geo_layer, cursor_group_rank, cursor_rotation_band,
                           cursor_random_key, cursor_pncp_id, contracts_scanned,
                           expanded_contracts, fully_resolved_contracts,
                           cached_item_lists, item_list_calls, item_result_calls,
                           completed_result_calls, failed_calls, candidate_set_exhausted
                      FROM session_info
                     LIMIT 1;
                    """;
                await using var reader = await session.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                    !string.Equals(reader.GetString(1), searchKey, StringComparison.Ordinal))
                {
                    return null;
                }

                id = Guid.ParseExact(reader.GetString(0), "N");
                startedAt = DateTimeOffset.Parse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                randomPivot = reader.GetInt64(3);
                cursor = reader.IsDBNull(4)
                    ? null
                    : new ItemCandidateCursor(
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetString(8));
                contractsScanned = reader.GetInt32(9);
                expandedContracts = reader.GetInt32(10);
                fullyResolvedContracts = reader.GetInt32(11);
                cachedItemLists = reader.GetInt32(12);
                itemListCalls = reader.GetInt32(13);
                itemResultCalls = reader.GetInt32(14);
                completedResultCalls = reader.GetInt32(15);
                failedCalls = reader.GetInt32(16);
                exhausted = reader.GetInt64(17) == 1;
            }

            await using (var resultCounters = connection.CreateCommand())
            {
                resultCounters.CommandText = """
                    SELECT COUNT(*),
                           COALESCE(SUM(CASE WHEN succeeded = 0 THEN 1 ELSE 0 END), 0)
                      FROM queried_items;
                    """;
                await using var reader = await resultCounters.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var persistedResults = checked((int)reader.GetInt64(0));
                    var persistedFailures = checked((int)reader.GetInt64(1));
                    itemResultCalls = Math.Max(itemResultCalls, persistedResults);
                    completedResultCalls = Math.Max(completedResultCalls, persistedResults);
                    failedCalls = Math.Max(failedCalls, persistedFailures);
                }
            }

            var hits = new List<ItemSearchHit>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT contract_json, item_json
                      FROM search_hits
                     ORDER BY discovered_order, contract_id, item_number;
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var contract = JsonSerializer.Deserialize<ContractRecord>(reader.GetString(0), JsonOptions)
                        ?? throw new InvalidDataException("Contratação inválida na pesquisa persistida.");
                    var item = JsonSerializer.Deserialize<ProcurementItem>(reader.GetString(1), JsonOptions)
                        ?? throw new InvalidDataException("Item inválido na pesquisa persistida.");
                    hits.Add(new ItemSearchHit(contract, item));
                }
            }

            return new StoredItemSearchSession(
                id,
                searchKey,
                startedAt,
                randomPivot,
                cursor,
                contractsScanned,
                expandedContracts,
                fullyResolvedContracts,
                cachedItemLists,
                itemListCalls,
                itemResultCalls,
                completedResultCalls,
                failedCalls,
                exhausted,
                hits);
        }
        catch (Exception exception) when (
            exception is SqliteException or JsonException or FormatException or InvalidDataException)
        {
            DeleteFiles();
            return null;
        }
    }

    public async Task SaveHitsAsync(
        IReadOnlyList<(ItemSearchHit Hit, long DiscoveredOrder)> hits,
        CancellationToken cancellationToken)
    {
        if (!_persistent || hits.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO search_hits(
                contract_id, item_number, discovered_order, contract_json, item_json)
            VALUES($contractId, $itemNumber, $order, $contract, $item)
            ON CONFLICT(contract_id, item_number) DO UPDATE SET
                discovered_order = MIN(search_hits.discovered_order, excluded.discovered_order),
                contract_json = excluded.contract_json,
                item_json = excluded.item_json;
            """;
        command.Parameters.Add("$contractId", SqliteType.Text);
        command.Parameters.Add("$itemNumber", SqliteType.Integer);
        command.Parameters.Add("$order", SqliteType.Integer);
        command.Parameters.Add("$contract", SqliteType.Text);
        command.Parameters.Add("$item", SqliteType.Text);
        foreach (var value in hits)
        {
            command.Parameters["$contractId"].Value = value.Hit.Contract.PncpId;
            command.Parameters["$itemNumber"].Value = value.Hit.Item.ItemNumber;
            command.Parameters["$order"].Value = value.DiscoveredOrder;
            command.Parameters["$contract"].Value = JsonSerializer.Serialize(value.Hit.Contract, JsonOptions);
            command.Parameters["$item"].Value = JsonSerializer.Serialize(value.Hit.Item, JsonOptions);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveCheckpointAsync(
        ItemCandidateCursor? cursor,
        int contractsScanned,
        int expandedContracts,
        int fullyResolvedContracts,
        int cachedItemLists,
        int itemListCalls,
        int itemResultCalls,
        int completedResultCalls,
        int failedCalls,
        bool candidateSetExhausted,
        CancellationToken cancellationToken)
    {
        if (!_persistent || !File.Exists(_databasePath))
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE session_info
               SET cursor_geo_layer = $layer,
                   cursor_group_rank = $group,
                   cursor_rotation_band = $band,
                   cursor_random_key = $random,
                   cursor_pncp_id = $pncpId,
                   contracts_scanned = $scanned,
                   expanded_contracts = $expanded,
                   fully_resolved_contracts = $resolved,
                   cached_item_lists = $cachedLists,
                   item_list_calls = $listCalls,
                   item_result_calls = $resultCalls,
                   completed_result_calls = $completedCalls,
                   failed_calls = $failedCalls,
                   candidate_set_exhausted = $exhausted,
                   updated_at = $updatedAt;
            """;
        command.Parameters.AddWithValue("$layer", DbValue(cursor?.GeographicLayer));
        command.Parameters.AddWithValue("$group", DbValue(cursor?.GroupRank));
        command.Parameters.AddWithValue("$band", DbValue(cursor?.RotationBand));
        command.Parameters.AddWithValue("$random", DbValue(cursor?.RandomOrderKey));
        command.Parameters.AddWithValue("$pncpId", DbValue(cursor?.PncpId));
        command.Parameters.AddWithValue("$scanned", Math.Max(0, contractsScanned));
        command.Parameters.AddWithValue("$expanded", Math.Max(0, expandedContracts));
        command.Parameters.AddWithValue("$resolved", Math.Max(0, fullyResolvedContracts));
        command.Parameters.AddWithValue("$cachedLists", Math.Max(0, cachedItemLists));
        command.Parameters.AddWithValue("$listCalls", Math.Max(0, itemListCalls));
        command.Parameters.AddWithValue("$resultCalls", Math.Max(0, itemResultCalls));
        command.Parameters.AddWithValue("$completedCalls", Math.Max(0, completedResultCalls));
        command.Parameters.AddWithValue("$failedCalls", Math.Max(0, failedCalls));
        command.Parameters.AddWithValue("$exhausted", candidateSetExhausted ? 1 : 0);
        command.Parameters.AddWithValue(
            "$updatedAt",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredContractFailure>> GetContractFailuresAsync(
        int maximum,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath) || maximum <= 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT contract_id, attempts, error
              FROM contract_failures
             ORDER BY updated_at, contract_id
             LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$maximum", maximum);
        var failures = new List<StoredContractFailure>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            failures.Add(new StoredContractFailure(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2)));
        }

        return failures;
    }

    public async Task SaveContractFailureAsync(
        string contractId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO contract_failures(contract_id, attempts, error, updated_at)
            VALUES($contractId, 1, $error, $updatedAt)
            ON CONFLICT(contract_id) DO UPDATE SET
                attempts = contract_failures.attempts + 1,
                error = excluded.error,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$contractId", contractId);
        command.Parameters.AddWithValue("$error", error ?? string.Empty);
        command.Parameters.AddWithValue(
            "$updatedAt",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveContractFailureAsync(
        string contractId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM contract_failures WHERE contract_id = $contractId;";
        command.Parameters.AddWithValue("$contractId", contractId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Clear() => DeleteFiles();

    public async Task SaveSuccessAsync(
        string contractId,
        long itemNumber,
        IReadOnlyList<HomologationResult> results,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertQueryStateAsync(connection, (SqliteTransaction)transaction, contractId, itemNumber, true, null, cancellationToken)
            .ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM item_results WHERE contract_id = $contractId AND item_number = $itemNumber;";
            delete.Parameters.AddWithValue("$contractId", contractId);
            delete.Parameters.AddWithValue("$itemNumber", itemNumber);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO item_results(
                    contract_id, item_number, result_sequence, supplier_tax_id, supplier_name,
                    supplier_type, supplier_municipality, supplier_uf,
                    quantity_scaled, unit_value_scaled, total_value_scaled, result_date,
                    result_status_id, result_status_name)
                VALUES($contractId, $itemNumber, $sequence, $taxId, $supplier,
                       $supplierType, $supplierMunicipality, $supplierUf, $quantity,
                       $unitValue, $totalValue, $resultDate, $statusId, $statusName);
                """;
            insert.Parameters.Add("$contractId", SqliteType.Text);
            insert.Parameters.Add("$itemNumber", SqliteType.Integer);
            insert.Parameters.Add("$sequence", SqliteType.Integer);
            insert.Parameters.Add("$taxId", SqliteType.Text);
            insert.Parameters.Add("$supplier", SqliteType.Text);
            insert.Parameters.Add("$supplierType", SqliteType.Text);
            insert.Parameters.Add("$supplierMunicipality", SqliteType.Text);
            insert.Parameters.Add("$supplierUf", SqliteType.Text);
            insert.Parameters.Add("$quantity", SqliteType.Integer);
            insert.Parameters.Add("$unitValue", SqliteType.Integer);
            insert.Parameters.Add("$totalValue", SqliteType.Integer);
            insert.Parameters.Add("$resultDate", SqliteType.Text);
            insert.Parameters.Add("$statusId", SqliteType.Integer);
            insert.Parameters.Add("$statusName", SqliteType.Text);

            foreach (var result in results)
            {
                insert.Parameters["$contractId"].Value = contractId;
                insert.Parameters["$itemNumber"].Value = itemNumber;
                insert.Parameters["$sequence"].Value = result.ResultSequence;
                insert.Parameters["$taxId"].Value = result.SupplierTaxId;
                insert.Parameters["$supplier"].Value = result.SupplierName;
                insert.Parameters["$supplierType"].Value = result.SupplierType;
                insert.Parameters["$supplierMunicipality"].Value = result.SupplierMunicipality;
                insert.Parameters["$supplierUf"].Value = result.SupplierUf;
                insert.Parameters["$quantity"].Value = DbValue(result.HomologatedQuantityScaled);
                insert.Parameters["$unitValue"].Value = DbValue(result.HomologatedUnitValueScaled);
                insert.Parameters["$totalValue"].Value = DbValue(result.HomologatedTotalValueScaled);
                insert.Parameters["$resultDate"].Value = DbValue(result.ResultDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                insert.Parameters["$statusId"].Value = result.ResultStatusId;
                insert.Parameters["$statusName"].Value = result.ResultStatusName;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveFailureAsync(
        string contractId,
        long itemNumber,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertQueryStateAsync(connection, (SqliteTransaction)transaction, contractId, itemNumber, false, error, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemporaryItemResultEntry?> GetAsync(
        string contractId,
        long itemNumber,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        bool succeeded;
        string? error;
        await using (var state = connection.CreateCommand())
        {
            state.CommandText = """
                SELECT succeeded, error FROM queried_items
                 WHERE contract_id = $contractId AND item_number = $itemNumber;
                """;
            state.Parameters.AddWithValue("$contractId", contractId);
            state.Parameters.AddWithValue("$itemNumber", itemNumber);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            succeeded = reader.GetInt64(0) == 1;
            error = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var results = new List<HomologationResult>();
        if (succeeded)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT contract_id, item_number, result_sequence, supplier_tax_id, supplier_name,
                       supplier_type, supplier_municipality, supplier_uf,
                       quantity_scaled, unit_value_scaled, total_value_scaled, result_date,
                       result_status_id, result_status_name
                  FROM item_results
                 WHERE contract_id = $contractId AND item_number = $itemNumber
                 ORDER BY result_sequence;
                """;
            command.Parameters.AddWithValue("$contractId", contractId);
            command.Parameters.AddWithValue("$itemNumber", itemNumber);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(ReadResult(reader));
            }
        }

        return new TemporaryItemResultEntry(succeeded, error, results);
    }

    public ValueTask DisposeAsync()
    {
        if (!_persistent)
        {
            DeleteFiles();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task UpsertQueryStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contractId,
        long itemNumber,
        bool succeeded,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO queried_items(contract_id, item_number, succeeded, error, queried_at)
            VALUES($contractId, $itemNumber, $succeeded, $error, $queriedAt)
            ON CONFLICT(contract_id, item_number) DO UPDATE SET
                succeeded = excluded.succeeded,
                error = excluded.error,
                queried_at = excluded.queried_at;
            """;
        command.Parameters.AddWithValue("$contractId", contractId);
        command.Parameters.AddWithValue("$itemNumber", itemNumber);
        command.Parameters.AddWithValue("$succeeded", succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$error", DbValue(error));
        command.Parameters.AddWithValue("$queriedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HomologationResult ReadResult(SqliteDataReader reader) => new()
    {
        ContractId = reader.GetString(0),
        ItemNumber = reader.GetInt64(1),
        ResultSequence = reader.GetInt64(2),
        SupplierTaxId = reader.GetString(3),
        SupplierName = reader.GetString(4),
        SupplierType = reader.GetString(5),
        SupplierMunicipality = reader.GetString(6),
        SupplierUf = reader.GetString(7),
        HomologatedQuantityScaled = reader.IsDBNull(8) ? null : reader.GetInt64(8),
        HomologatedUnitValueScaled = reader.IsDBNull(9) ? null : reader.GetInt64(9),
        HomologatedTotalValueScaled = reader.IsDBNull(10) ? null : reader.GetInt64(10),
        ResultDate = reader.IsDBNull(11) || !DateOnly.TryParse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? null
            : date,
        ResultStatusId = reader.GetInt32(12),
        ResultStatusName = reader.GetString(13)
    };

    private void DeleteFiles()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;
}
