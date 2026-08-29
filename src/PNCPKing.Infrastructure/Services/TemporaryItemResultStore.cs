using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

internal sealed record TemporaryItemResultEntry(
    bool Succeeded,
    string? Error,
    IReadOnlyList<HomologationResult> Results);

internal sealed record StoredItemSearchSession(
    Guid Id,
    string AnchorKey,
    string ScopeKey,
    string CriteriaText,
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
    IReadOnlyList<ItemSearchHit> Hits,
    IReadOnlyList<ContractRecord> ProcessedContracts,
    int HitCount,
    int ProcessedContractCount);

internal sealed record StoredContractFailure(
    string ContractId,
    int Attempts,
    string Error);

internal sealed record StoredCandidateCommit(
    ContractRecord Contract,
    long ProcessedOrder,
    IReadOnlyList<(ItemSearchHit Hit, long DiscoveredOrder)> Hits,
    string? Failure);

internal sealed record StoredSessionCheckpoint(
    ItemCandidateCursor? Cursor,
    int ContractsScanned,
    int ExpandedContracts,
    int FullyResolvedContracts,
    int CachedItemLists,
    int ItemListCalls,
    int ItemResultCalls,
    int CompletedResultCalls,
    int FailedCalls,
    bool CandidateSetExhausted);

/// <summary>
/// Search-session prices deliberately live outside the user's permanent index.
/// The file has no pooling. Transient stores are deleted on reset/disposal; the
/// general-search store remains available for resumption after application exit.
/// </summary>
internal sealed class TemporaryItemResultStore(
    string databasePath,
    bool persistent = false) : IAsyncDisposable
{
    private const int SchemaVersion = 3;
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
            string.Empty,
            string.Empty,
            0,
            DateTimeOffset.UtcNow,
            cancellationToken);

    public async Task ResetAsync(
        Guid sessionId,
        string anchorKey,
        string scopeKey,
        string criteriaText,
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
                anchor_key TEXT NOT NULL,
                scope_key TEXT NOT NULL,
                criteria_text TEXT NOT NULL,
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

            CREATE TABLE processed_contracts(
                contract_id TEXT PRIMARY KEY,
                processed_order INTEGER NOT NULL,
                contract_json TEXT NOT NULL
            );
            CREATE INDEX idx_processed_contracts_order
                ON processed_contracts(processed_order, contract_id);

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

            PRAGMA user_version=3;

            INSERT INTO session_info(
                id, search_key, anchor_key, scope_key, criteria_text,
                started_at, random_pivot, updated_at)
            VALUES($id, $criteriaText, $anchorKey, $scopeKey, $criteriaText,
                   $startedAt, $randomPivot, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
        command.Parameters.AddWithValue("$anchorKey", anchorKey);
        command.Parameters.AddWithValue("$scopeKey", scopeKey);
        command.Parameters.AddWithValue("$criteriaText", criteriaText);
        command.Parameters.AddWithValue("$startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$randomPivot", randomPivot);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredItemSearchSession?> TryRestoreAsync(
        string anchorKey,
        CancellationToken cancellationToken,
        bool loadHistory = true)
    {
        if (!_persistent || !File.Exists(_databasePath))
        {
            return null;
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await UpgradeSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
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
            string scopeKey;
            string criteriaText;
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
                    SELECT id, anchor_key, scope_key, criteria_text, started_at, random_pivot,
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
                    !string.Equals(reader.GetString(1), anchorKey, StringComparison.Ordinal))
                {
                    return null;
                }

                id = Guid.ParseExact(reader.GetString(0), "N");
                scopeKey = reader.GetString(2);
                criteriaText = reader.GetString(3);
                startedAt = DateTimeOffset.Parse(
                    reader.GetString(4),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                randomPivot = reader.GetInt64(5);
                cursor = reader.IsDBNull(6)
                    ? null
                    : new ItemCandidateCursor(
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetInt64(9),
                        reader.GetString(10));
                contractsScanned = reader.GetInt32(11);
                expandedContracts = reader.GetInt32(12);
                fullyResolvedContracts = reader.GetInt32(13);
                cachedItemLists = reader.GetInt32(14);
                itemListCalls = reader.GetInt32(15);
                itemResultCalls = reader.GetInt32(16);
                completedResultCalls = reader.GetInt32(17);
                failedCalls = reader.GetInt32(18);
                exhausted = reader.GetInt64(19) == 1;
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

            int hitCount;
            int processedContractCount;
            await using (var counts = connection.CreateCommand())
            {
                counts.CommandText = """
                    SELECT (SELECT COUNT(*) FROM search_hits),
                           (SELECT COUNT(*) FROM processed_contracts);
                    """;
                await using var reader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                hitCount = checked((int)reader.GetInt64(0));
                processedContractCount = checked((int)reader.GetInt64(1));
            }

            var hits = new List<ItemSearchHit>();
            var processedContracts = new List<ContractRecord>();
            if (loadHistory)
            {
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

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = """
                        SELECT contract_json
                          FROM processed_contracts
                         ORDER BY processed_order, contract_id;
                        """;
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        processedContracts.Add(
                            JsonSerializer.Deserialize<ContractRecord>(reader.GetString(0), JsonOptions)
                            ?? throw new InvalidDataException("Contratação processada inválida."));
                    }
                }
            }

            return new StoredItemSearchSession(
                id,
                anchorKey,
                scopeKey,
                criteriaText,
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
                hits,
                processedContracts,
                hitCount,
                processedContractCount);
        }
        catch (Exception exception) when (
            exception is SqliteException or JsonException or FormatException or InvalidDataException)
        {
            DeleteFiles();
            return null;
        }
    }

    public async Task ResetTraversalAsync(
        Guid sessionId,
        string anchorKey,
        string scopeKey,
        string criteriaText,
        long randomPivot,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            await ResetAsync(
                    sessionId,
                    anchorKey,
                    scopeKey,
                    criteriaText,
                    randomPivot,
                    startedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await UpgradeSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            DELETE FROM search_hits;
            DELETE FROM processed_contracts;
            DELETE FROM contract_failures;
            DELETE FROM session_info;
            INSERT INTO session_info(
                id, search_key, anchor_key, scope_key, criteria_text,
                started_at, random_pivot, updated_at)
            VALUES($id, $criteriaText, $anchorKey, $scopeKey, $criteriaText,
                   $startedAt, $randomPivot, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
        command.Parameters.AddWithValue("$anchorKey", anchorKey);
        command.Parameters.AddWithValue("$scopeKey", scopeKey);
        command.Parameters.AddWithValue("$criteriaText", criteriaText);
        command.Parameters.AddWithValue("$startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$randomPivot", randomPivot);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ItemSearchHit>> LoadHitsAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT contract_json, item_json
              FROM search_hits
             ORDER BY discovered_order, contract_id, item_number;
            """;
        var hits = new List<ItemSearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var contract = JsonSerializer.Deserialize<ContractRecord>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidDataException("Contratação inválida na pesquisa persistida.");
            var item = JsonSerializer.Deserialize<ProcurementItem>(reader.GetString(1), JsonOptions)
                ?? throw new InvalidDataException("Item inválido na pesquisa persistida.");
            hits.Add(new ItemSearchHit(contract, item));
        }

        return hits;
    }

    public async Task<IReadOnlyList<(ContractRecord Contract, long ProcessedOrder)>>
        LoadProcessedContractsPageAsync(
            long afterProcessedOrder,
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
            SELECT contract_json, processed_order
              FROM processed_contracts
             WHERE processed_order > $after
             ORDER BY processed_order, contract_id
             LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$after", Math.Max(0, afterProcessedOrder));
        command.Parameters.AddWithValue("$maximum", maximum);
        var contracts = new List<(ContractRecord Contract, long ProcessedOrder)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            contracts.Add((
                JsonSerializer.Deserialize<ContractRecord>(reader.GetString(0), JsonOptions)
                    ?? throw new InvalidDataException("Contratação processada inválida."),
                reader.GetInt64(1)));
        }

        return contracts;
    }

    public async Task<IReadOnlyList<ItemSearchHit>> GetFailedHitsAsync(
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
            SELECT h.contract_json, h.item_json
              FROM queried_items q
              JOIN search_hits h ON h.contract_id = q.contract_id
                                AND h.item_number = q.item_number
             WHERE q.succeeded = 0
             ORDER BY q.queried_at, h.discovered_order, h.contract_id, h.item_number
             LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$maximum", maximum);
        var hits = new List<ItemSearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var contract = JsonSerializer.Deserialize<ContractRecord>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidDataException("Contratação inválida na falha persistida.");
            var item = JsonSerializer.Deserialize<ProcurementItem>(reader.GetString(1), JsonOptions)
                ?? throw new InvalidDataException("Item inválido na falha persistida.");
            hits.Add(new ItemSearchHit(contract, item));
        }

        return hits;
    }

    public async Task UpdateCriteriaAsync(
        string criteriaText,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE session_info
               SET search_key = $criteriaText,
                   criteria_text = $criteriaText,
                   updated_at = $updatedAt;
            """;
        command.Parameters.AddWithValue("$criteriaText", criteriaText);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceHitsAsync(
        IReadOnlyList<ItemSearchHit> hits,
        CancellationToken cancellationToken)
    {
        if (!_persistent || !File.Exists(_databasePath))
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM search_hits;";
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO search_hits(
                contract_id, item_number, discovered_order, contract_json, item_json)
            VALUES($contractId, $itemNumber, $order, $contract, $item);
            """;
        insert.Parameters.Add("$contractId", SqliteType.Text);
        insert.Parameters.Add("$itemNumber", SqliteType.Integer);
        insert.Parameters.Add("$order", SqliteType.Integer);
        insert.Parameters.Add("$contract", SqliteType.Text);
        insert.Parameters.Add("$item", SqliteType.Text);
        for (var index = 0; index < hits.Count; index++)
        {
            var hit = hits[index];
            insert.Parameters["$contractId"].Value = hit.Contract.PncpId;
            insert.Parameters["$itemNumber"].Value = hit.Item.ItemNumber;
            insert.Parameters["$order"].Value = index + 1L;
            insert.Parameters["$contract"].Value = JsonSerializer.Serialize(hit.Contract, JsonOptions);
            insert.Parameters["$item"].Value = JsonSerializer.Serialize(hit.Item, JsonOptions);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearHitsAsync(CancellationToken cancellationToken)
    {
        if (!_persistent || !File.Exists(_databasePath))
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM search_hits;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveProcessedContractAsync(
        ContractRecord contract,
        long processedOrder,
        CancellationToken cancellationToken)
    {
        if (!_persistent || !File.Exists(_databasePath))
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processed_contracts(contract_id, processed_order, contract_json)
            VALUES($contractId, $order, $contract)
            ON CONFLICT(contract_id) DO UPDATE SET
                processed_order = MIN(processed_contracts.processed_order, excluded.processed_order),
                contract_json = excluded.contract_json;
            """;
        command.Parameters.AddWithValue("$contractId", contract.PncpId);
        command.Parameters.AddWithValue("$order", Math.Max(1, processedOrder));
        command.Parameters.AddWithValue("$contract", JsonSerializer.Serialize(contract, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task CommitCandidatesAsync(
        IReadOnlyList<StoredCandidateCommit> candidates,
        StoredSessionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (!_persistent || !File.Exists(_databasePath))
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var processed = connection.CreateCommand();
        processed.Transaction = (SqliteTransaction)transaction;
        processed.CommandText = """
            INSERT INTO processed_contracts(contract_id, processed_order, contract_json)
            VALUES($contractId, $order, $contract)
            ON CONFLICT(contract_id) DO UPDATE SET
                processed_order = MIN(processed_contracts.processed_order, excluded.processed_order),
                contract_json = excluded.contract_json;
            """;
        processed.Parameters.Add("$contractId", SqliteType.Text);
        processed.Parameters.Add("$order", SqliteType.Integer);
        processed.Parameters.Add("$contract", SqliteType.Text);

        await using var hit = connection.CreateCommand();
        hit.Transaction = (SqliteTransaction)transaction;
        hit.CommandText = """
            INSERT INTO search_hits(
                contract_id, item_number, discovered_order, contract_json, item_json)
            VALUES($contractId, $itemNumber, $order, $contract, $item)
            ON CONFLICT(contract_id, item_number) DO UPDATE SET
                discovered_order = MIN(search_hits.discovered_order, excluded.discovered_order),
                contract_json = excluded.contract_json,
                item_json = excluded.item_json;
            """;
        hit.Parameters.Add("$contractId", SqliteType.Text);
        hit.Parameters.Add("$itemNumber", SqliteType.Integer);
        hit.Parameters.Add("$order", SqliteType.Integer);
        hit.Parameters.Add("$contract", SqliteType.Text);
        hit.Parameters.Add("$item", SqliteType.Text);

        await using var saveFailure = connection.CreateCommand();
        saveFailure.Transaction = (SqliteTransaction)transaction;
        saveFailure.CommandText = """
            INSERT INTO contract_failures(contract_id, attempts, error, updated_at)
            VALUES($contractId, 1, $error, $updatedAt)
            ON CONFLICT(contract_id) DO UPDATE SET
                attempts = contract_failures.attempts + 1,
                error = excluded.error,
                updated_at = excluded.updated_at;
            """;
        saveFailure.Parameters.Add("$contractId", SqliteType.Text);
        saveFailure.Parameters.Add("$error", SqliteType.Text);
        saveFailure.Parameters.Add("$updatedAt", SqliteType.Text);

        await using var removeFailure = connection.CreateCommand();
        removeFailure.Transaction = (SqliteTransaction)transaction;
        removeFailure.CommandText =
            "DELETE FROM contract_failures WHERE contract_id = $contractId;";
        removeFailure.Parameters.Add("$contractId", SqliteType.Text);

        foreach (var candidate in candidates)
        {
            processed.Parameters["$contractId"].Value = candidate.Contract.PncpId;
            processed.Parameters["$order"].Value = Math.Max(1, candidate.ProcessedOrder);
            processed.Parameters["$contract"].Value = JsonSerializer.Serialize(candidate.Contract, JsonOptions);
            await processed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (var value in candidate.Hits)
            {
                hit.Parameters["$contractId"].Value = value.Hit.Contract.PncpId;
                hit.Parameters["$itemNumber"].Value = value.Hit.Item.ItemNumber;
                hit.Parameters["$order"].Value = value.DiscoveredOrder;
                hit.Parameters["$contract"].Value = JsonSerializer.Serialize(value.Hit.Contract, JsonOptions);
                hit.Parameters["$item"].Value = JsonSerializer.Serialize(value.Hit.Item, JsonOptions);
                await hit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (candidate.Failure is null)
            {
                removeFailure.Parameters["$contractId"].Value = candidate.Contract.PncpId;
                await removeFailure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                saveFailure.Parameters["$contractId"].Value = candidate.Contract.PncpId;
                saveFailure.Parameters["$error"].Value = candidate.Failure;
                saveFailure.Parameters["$updatedAt"].Value =
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                await saveFailure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using var saveCheckpoint = connection.CreateCommand();
        saveCheckpoint.Transaction = (SqliteTransaction)transaction;
        saveCheckpoint.CommandText = """
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
        saveCheckpoint.Parameters.AddWithValue("$layer", DbValue(checkpoint.Cursor?.GeographicLayer));
        saveCheckpoint.Parameters.AddWithValue("$group", DbValue(checkpoint.Cursor?.GroupRank));
        saveCheckpoint.Parameters.AddWithValue("$band", DbValue(checkpoint.Cursor?.RotationBand));
        saveCheckpoint.Parameters.AddWithValue("$random", DbValue(checkpoint.Cursor?.RandomOrderKey));
        saveCheckpoint.Parameters.AddWithValue("$pncpId", DbValue(checkpoint.Cursor?.PncpId));
        saveCheckpoint.Parameters.AddWithValue("$scanned", Math.Max(0, checkpoint.ContractsScanned));
        saveCheckpoint.Parameters.AddWithValue("$expanded", Math.Max(0, checkpoint.ExpandedContracts));
        saveCheckpoint.Parameters.AddWithValue("$resolved", Math.Max(0, checkpoint.FullyResolvedContracts));
        saveCheckpoint.Parameters.AddWithValue("$cachedLists", Math.Max(0, checkpoint.CachedItemLists));
        saveCheckpoint.Parameters.AddWithValue("$listCalls", Math.Max(0, checkpoint.ItemListCalls));
        saveCheckpoint.Parameters.AddWithValue("$resultCalls", Math.Max(0, checkpoint.ItemResultCalls));
        saveCheckpoint.Parameters.AddWithValue("$completedCalls", Math.Max(0, checkpoint.CompletedResultCalls));
        saveCheckpoint.Parameters.AddWithValue("$failedCalls", Math.Max(0, checkpoint.FailedCalls));
        saveCheckpoint.Parameters.AddWithValue("$exhausted", checkpoint.CandidateSetExhausted ? 1 : 0);
        saveCheckpoint.Parameters.AddWithValue(
            "$updatedAt",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await saveCheckpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CheckpointPassiveAsync(CancellationToken cancellationToken)
    {
        if (!_persistent || !File.Exists(_databasePath))
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
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

    public async Task<IReadOnlyDictionary<(string ContractId, long ItemNumber), TemporaryItemResultEntry?>>
        GetManyAsync(
            IReadOnlyList<ItemSearchHit> hits,
            CancellationToken cancellationToken)
    {
        var keys = hits
            .Select(hit => (ContractId: hit.Contract.PncpId, ItemNumber: hit.Item.ItemNumber))
            .Distinct()
            .ToArray();
        var entries = keys.ToDictionary(
            key => key,
            _ => (TemporaryItemResultEntry?)null);
        if (!File.Exists(_databasePath) || keys.Length == 0)
        {
            return entries;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var selection = connection.CreateCommand())
        {
            selection.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS selected_temporary_items(
                    contract_id TEXT NOT NULL,
                    item_number INTEGER NOT NULL,
                    PRIMARY KEY(contract_id, item_number)
                ) WITHOUT ROWID;
                DELETE FROM selected_temporary_items;
                """;
            await selection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO selected_temporary_items(contract_id, item_number)
                VALUES($contractId, $itemNumber);
                """;
            insert.Parameters.Add("$contractId", SqliteType.Text);
            insert.Parameters.Add("$itemNumber", SqliteType.Integer);
            foreach (var key in keys)
            {
                insert.Parameters["$contractId"].Value = key.ContractId;
                insert.Parameters["$itemNumber"].Value = key.ItemNumber;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var states = new Dictionary<(string ContractId, long ItemNumber), (bool Succeeded, string? Error)>();
        await using (var state = connection.CreateCommand())
        {
            state.CommandText = """
                SELECT q.contract_id, q.item_number, q.succeeded, q.error
                  FROM selected_temporary_items selected
                  JOIN queried_items q ON q.contract_id = selected.contract_id
                                      AND q.item_number = selected.item_number;
                """;
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                states[(reader.GetString(0), reader.GetInt64(1))] = (
                    reader.GetInt64(2) == 1,
                    reader.IsDBNull(3) ? null : reader.GetString(3));
            }
        }

        var results = states.Keys.ToDictionary(
            key => key,
            _ => new List<HomologationResult>());
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.contract_id, r.item_number, r.result_sequence, r.supplier_tax_id,
                       r.supplier_name, r.supplier_type, r.supplier_municipality, r.supplier_uf,
                       r.quantity_scaled, r.unit_value_scaled, r.total_value_scaled, r.result_date,
                       r.result_status_id, r.result_status_name
                  FROM selected_temporary_items selected
                  JOIN item_results r ON r.contract_id = selected.contract_id
                                     AND r.item_number = selected.item_number
                 ORDER BY r.contract_id, r.item_number, r.result_sequence;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var result = ReadResult(reader);
                results[(result.ContractId, result.ItemNumber)].Add(result);
            }
        }

        foreach (var (key, state) in states)
        {
            entries[key] = new TemporaryItemResultEntry(
                state.Succeeded,
                state.Error,
                results[key]);
        }

        return entries;
    }

    public async Task<int> GetAvailableResultRowCountAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                  FROM search_hits h
                  JOIN queried_items q ON q.contract_id = h.contract_id
                                      AND q.item_number = h.item_number
                                      AND q.succeeded = 1
                  JOIN item_results r ON r.contract_id = h.contract_id
                                     AND r.item_number = h.item_number
                 WHERE r.unit_value_scaled IS NOT NULL AND r.unit_value_scaled > 0;
                """;
            return checked(Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture));
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    public async Task<ItemSearchResultPage> LoadResultPageAsync(
        ItemSearchResultCursor? cursor,
        int pageSize,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return new ItemSearchResultPage([], cursor, false);
        }

        pageSize = Math.Clamp(pageSize, 1, ItemSearchDefaults.ContractsPerBatch);
        var hasRange = minimumUnitPrice is not null || maximumUnitPrice is not null;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.contract_json, h.item_json, h.discovered_order,
                   r.contract_id, r.item_number, r.result_sequence, r.supplier_tax_id,
                   r.supplier_name, r.supplier_type, r.supplier_municipality, r.supplier_uf,
                   r.quantity_scaled, r.unit_value_scaled, r.total_value_scaled, r.result_date,
                   r.result_status_id, r.result_status_name
              FROM search_hits h
              JOIN queried_items q ON q.contract_id = h.contract_id
                                  AND q.item_number = h.item_number
                                  AND q.succeeded = 1
              JOIN item_results r ON r.contract_id = h.contract_id
                                 AND r.item_number = h.item_number
             WHERE r.unit_value_scaled IS NOT NULL
               AND r.unit_value_scaled > 0
               AND ($hasRange = 0 OR r.result_status_id = 1)
               AND ($minimum IS NULL OR r.unit_value_scaled >= $minimum)
               AND ($maximum IS NULL OR r.unit_value_scaled <= $maximum)
               AND ($hasCursor = 0
                    OR h.discovered_order > $order
                    OR (h.discovered_order = $order AND h.contract_id > $contractId)
                    OR (h.discovered_order = $order AND h.contract_id = $contractId
                        AND h.item_number > $itemNumber)
                    OR (h.discovered_order = $order AND h.contract_id = $contractId
                        AND h.item_number = $itemNumber AND r.result_sequence > $sequence))
             ORDER BY h.discovered_order, h.contract_id, h.item_number, r.result_sequence
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$hasRange", hasRange ? 1 : 0);
        command.Parameters.AddWithValue(
            "$minimum",
            DbValue(minimumUnitPrice is null ? null : DecimalScale.ToScaled(minimumUnitPrice.Value)));
        command.Parameters.AddWithValue(
            "$maximum",
            DbValue(maximumUnitPrice is null ? null : DecimalScale.ToScaled(maximumUnitPrice.Value)));
        command.Parameters.AddWithValue("$hasCursor", cursor is null ? 0 : 1);
        command.Parameters.AddWithValue("$order", cursor?.DiscoveredOrder ?? 0);
        command.Parameters.AddWithValue("$contractId", cursor?.ContractId ?? string.Empty);
        command.Parameters.AddWithValue("$itemNumber", cursor?.ItemNumber ?? 0);
        command.Parameters.AddWithValue("$sequence", cursor?.ResultSequence ?? 0);
        command.Parameters.AddWithValue("$limit", pageSize + 1);

        var values = new List<(ItemSearchRow Row, ItemSearchResultCursor Cursor)>(pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var contract = JsonSerializer.Deserialize<ContractRecord>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidDataException("Contratação inválida na página de resultados.");
            var item = JsonSerializer.Deserialize<ProcurementItem>(reader.GetString(1), JsonOptions)
                ?? throw new InvalidDataException("Item inválido na página de resultados.");
            var result = ReadResult(reader, 3);
            values.Add((
                new ItemSearchRow(
                    contract,
                    item,
                    result,
                    result.IsActive ? ItemSearchPriceState.Homologated : ItemSearchPriceState.Cancelled,
                    result.IsActive ? "Preço homologado encontrado" : "Resultado cancelado",
                    true),
                new ItemSearchResultCursor(
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5))));
        }

        var hasMore = values.Count > pageSize;
        if (hasMore)
        {
            values.RemoveAt(values.Count - 1);
        }

        return new ItemSearchResultPage(
            values.Select(value => value.Row).ToArray(),
            values.Count == 0 ? cursor : values[^1].Cursor,
            hasMore);
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

    private static async Task UpgradeSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        int version;
        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        if (version != 2)
        {
            return;
        }

        string searchKey;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT search_key FROM session_info LIMIT 1;";
            searchKey = Convert.ToString(
                await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) ?? string.Empty;
        }

        var parts = searchKey.Split('\u001F');
        var criteriaText = parts.Length > 0 ? parts[0] : searchKey;
        var expression = SearchText.Parse(criteriaText);
        var anchorKey = expression.AnchorTerm.Length > 0
            ? expression.AnchorTerm
            : $"exact:{SearchText.Normalize(criteriaText)}";
        var scopeKey = string.Join(
            '\u001F',
            parts.Length > 1 ? parts[1] : string.Empty,
            parts.Length > 2 ? parts[2] : string.Empty,
            parts.Length > 3 ? parts[3] : string.Empty,
            parts.Length > 4 ? parts[4] : string.Empty,
            expression.ExplicitContractMatchQuery);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var migration = connection.CreateCommand();
        migration.Transaction = (SqliteTransaction)transaction;
        migration.CommandText = """
            ALTER TABLE session_info ADD COLUMN anchor_key TEXT NOT NULL DEFAULT '';
            ALTER TABLE session_info ADD COLUMN scope_key TEXT NOT NULL DEFAULT '';
            ALTER TABLE session_info ADD COLUMN criteria_text TEXT NOT NULL DEFAULT '';

            CREATE TABLE processed_contracts(
                contract_id TEXT PRIMARY KEY,
                processed_order INTEGER NOT NULL,
                contract_json TEXT NOT NULL
            );
            CREATE INDEX idx_processed_contracts_order
                ON processed_contracts(processed_order, contract_id);

            UPDATE session_info
               SET anchor_key = $anchorKey,
                   scope_key = $scopeKey,
                   criteria_text = $criteriaText;
            PRAGMA user_version=3;
            """;
        migration.Parameters.AddWithValue("$anchorKey", anchorKey);
        migration.Parameters.AddWithValue("$scopeKey", scopeKey);
        migration.Parameters.AddWithValue("$criteriaText", criteriaText);
        await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    private static HomologationResult ReadResult(SqliteDataReader reader, int offset = 0) => new()
    {
        ContractId = reader.GetString(offset),
        ItemNumber = reader.GetInt64(offset + 1),
        ResultSequence = reader.GetInt64(offset + 2),
        SupplierTaxId = reader.GetString(offset + 3),
        SupplierName = reader.GetString(offset + 4),
        SupplierType = reader.GetString(offset + 5),
        SupplierMunicipality = reader.GetString(offset + 6),
        SupplierUf = reader.GetString(offset + 7),
        HomologatedQuantityScaled = reader.IsDBNull(offset + 8) ? null : reader.GetInt64(offset + 8),
        HomologatedUnitValueScaled = reader.IsDBNull(offset + 9) ? null : reader.GetInt64(offset + 9),
        HomologatedTotalValueScaled = reader.IsDBNull(offset + 10) ? null : reader.GetInt64(offset + 10),
        ResultDate = reader.IsDBNull(offset + 11) || !DateOnly.TryParse(reader.GetString(offset + 11), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? null
            : date,
        ResultStatusId = reader.GetInt32(offset + 12),
        ResultStatusName = reader.GetString(offset + 13)
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
