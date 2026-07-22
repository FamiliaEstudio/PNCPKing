using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Geography;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Data;

public sealed class SqliteContractRepository : IContractRepository, ICoverageRepository
{
    public const int CurrentSchemaVersion = 6;

    private const string GeographicGroupExpression = "CASE WHEN COALESCE(c.geo_layer, 1) = 0 " +
        "THEN COALESCE(c.municipality_distance_rank, 999999) " +
        "ELSE COALESCE(c.state_proximity_rank, 999) END";

    private readonly string _connectionString;

    public SqliteContractRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var bootstrap = connection.CreateCommand())
        {
            bootstrap.CommandText = "CREATE TABLE IF NOT EXISTS schema_info(id INTEGER PRIMARY KEY CHECK(id = 1), version INTEGER NOT NULL);";
            await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int version;
        await using (var readVersion = connection.CreateCommand())
        {
            readVersion.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
            var value = await readVersion.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            version = value is null || value is DBNull
                ? 0
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        if (version > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"O banco usa a versão {version}, mais nova que a versão {CurrentSchemaVersion} suportada pelo aplicativo.");
        }

        if (version < 1)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var migration = connection.CreateCommand();
            migration.Transaction = (SqliteTransaction)transaction;
            migration.CommandText = SchemaSql;
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var updateVersion = connection.CreateCommand();
            updateVersion.Transaction = (SqliteTransaction)transaction;
            updateVersion.CommandText = "INSERT INTO schema_info(id, version) VALUES(1, 1) ON CONFLICT(id) DO UPDATE SET version = 1;";
            await updateVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            version = 1;
        }

        if (version < 2)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var migration = connection.CreateCommand();
            migration.Transaction = (SqliteTransaction)transaction;
            migration.CommandText = SchemaV2Sql;
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await BackfillNearbyMunicipalityCodesAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    cancellationToken)
                .ConfigureAwait(false);

            await using var updateVersion = connection.CreateCommand();
            updateVersion.Transaction = (SqliteTransaction)transaction;
            updateVersion.CommandText = "UPDATE schema_info SET version = 2 WHERE id = 1;";
            await updateVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            version = 2;
        }

        if (version < 3)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await MigrateLegacySyncStateAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    cancellationToken)
                .ConfigureAwait(false);

            await using var updateVersion = connection.CreateCommand();
            updateVersion.Transaction = (SqliteTransaction)transaction;
            updateVersion.CommandText = "UPDATE schema_info SET version = 3 WHERE id = 1;";
            await updateVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            version = 3;
        }

        if (version < 4)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var migration = connection.CreateCommand();
            migration.Transaction = (SqliteTransaction)transaction;
            migration.CommandText = SchemaV4Sql;
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var updateVersion = connection.CreateCommand();
            updateVersion.Transaction = (SqliteTransaction)transaction;
            updateVersion.CommandText = "UPDATE schema_info SET version = 4 WHERE id = 1;";
            await updateVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            version = 4;
        }

        if (version < 5)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var migration = connection.CreateCommand();
            migration.Transaction = (SqliteTransaction)transaction;
            migration.CommandText = SchemaV5Sql;
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var updateVersion = connection.CreateCommand();
            updateVersion.Transaction = (SqliteTransaction)transaction;
            updateVersion.CommandText = "UPDATE schema_info SET version = 5 WHERE id = 1;";
            await updateVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            version = 5;
        }

        if (version < 6)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var migration = connection.CreateCommand();
            migration.Transaction = (SqliteTransaction)transaction;
            migration.CommandText = SchemaV6Sql;
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var updateVersion = connection.CreateCommand();
            updateVersion.Transaction = (SqliteTransaction)transaction;
            updateVersion.CommandText = "UPDATE schema_info SET version = 6 WHERE id = 1;";
            await updateVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpsertContractsAsync(
        IReadOnlyList<ContractRecord> contracts,
        CancellationToken cancellationToken = default)
    {
        if (contracts.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = ContractUpsertSql;
        AddContractParameters(command);

        foreach (var contract in contracts)
        {
            SetContractParameters(command, contract);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var expression = SearchText.Parse(query.Text);
        var match = expression.ContractMatchQuery;
        var joinsFts = match.Length > 0;
        var conditions = new List<string>();
        if (joinsFts)
        {
            conditions.Add("contracts_fts MATCH $match");
        }

        var geoFilter = query.EffectiveGeoFilter;
        switch (geoFilter.Kind)
        {
            case SearchGeoFilterKind.Southeast:
                conditions.Add("c.uf IN ('ES','MG','RJ','SP')");
                break;
            case SearchGeoFilterKind.State:
                conditions.Add("c.uf = $uf");
                break;
            case SearchGeoFilterKind.NearRibeirao:
                conditions.Add("c.geo_layer = 0");
                break;
        }

        if (query.StartDate is not null)
        {
            conditions.Add("date(c.publication_date) >= date($startDate)");
        }

        if (query.EndDate is not null)
        {
            conditions.Add("date(c.publication_date) <= date($endDate)");
        }

        var from = joinsFts
            ? "contracts c JOIN contracts_fts ON contracts_fts.rowid = c.rowid"
            : "contracts c";
        var where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        var order = query.Sort switch
        {
            SearchSort.Nearest =>
                $" ORDER BY COALESCE(c.geo_layer, 1), {GeographicGroupExpression}, " +
                "c.publication_date DESC, c.pncp_id",
            SearchSort.Newest => " ORDER BY c.publication_date DESC, c.pncp_id",
            _ when joinsFts => " ORDER BY bm25(contracts_fts), c.publication_date DESC, c.pncp_id",
            _ => " ORDER BY c.publication_date DESC, c.pncp_id"
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM {from}{where};";
        AddSearchParameters(countCommand, query, match);
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.pncp_id, c.cnpj, c.purchase_year, c.purchase_sequence, c.object,
                   c.additional_information, c.process, c.organization, c.unit, c.municipality,
                   c.municipality_ibge_code, c.uf, c.modality_id, c.modality_name, c.status, c.publication_date,
                   c.global_updated_at, c.total_homologated_scaled, c.distance_from_ribeirao_km
              FROM {from}{where}{order}
             LIMIT $limit OFFSET $offset;
            """;
        AddSearchParameters(command, query, match);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);

        var results = new List<ContractRecord>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadContract(reader));
        }

        return new SearchPage(results, total, page, pageSize);
    }

    public async Task<ItemCandidatePage> SearchItemCandidatesAsync(
        SearchQuery filters,
        SearchExpression expression,
        long randomPivot,
        ItemCandidateCursor? cursor,
        int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.IsEmpty)
        {
            return new ItemCandidatePage([], null, false);
        }

        pageSize = Math.Clamp(pageSize, 1, 500);
        randomPivot = Math.Clamp(randomPivot, 0, long.MaxValue - 1);
        var conditions = new List<string> { "contracts_fts MATCH $candidateMatch" };
        var geoFilter = filters.EffectiveGeoFilter;
        switch (geoFilter.Kind)
        {
            case SearchGeoFilterKind.Southeast:
                conditions.Add("c.uf IN ('ES','MG','RJ','SP')");
                break;
            case SearchGeoFilterKind.State:
                conditions.Add("c.uf = $uf");
                break;
            case SearchGeoFilterKind.NearRibeirao:
                conditions.Add("c.geo_layer = 0");
                break;
        }

        if (filters.StartDate is not null)
        {
            conditions.Add("date(c.publication_date) >= date($startDate)");
        }

        if (filters.EndDate is not null)
        {
            conditions.Add("date(c.publication_date) <= date($endDate)");
        }

        var where = string.Join(" AND ", conditions);
        var cursorWhere = cursor is null
            ? string.Empty
            : """
               WHERE geographic_layer > $cursorLayer
                  OR (geographic_layer = $cursorLayer AND group_rank > $cursorGroup)
                  OR (geographic_layer = $cursorLayer AND group_rank = $cursorGroup AND rotation_band > $cursorBand)
                  OR (geographic_layer = $cursorLayer AND group_rank = $cursorGroup AND rotation_band = $cursorBand
                      AND random_order_key > $cursorRandom)
                  OR (geographic_layer = $cursorLayer AND group_rank = $cursorGroup AND rotation_band = $cursorBand
                      AND random_order_key = $cursorRandom AND pncp_id > $cursorId)
              """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH ranked AS (
                SELECT c.pncp_id, c.cnpj, c.purchase_year, c.purchase_sequence, c.object,
                       c.additional_information, c.process, c.organization, c.unit, c.municipality,
                       c.municipality_ibge_code, c.uf, c.modality_id, c.modality_name, c.status,
                       c.publication_date, c.global_updated_at, c.total_homologated_scaled,
                       c.distance_from_ribeirao_km,
                       COALESCE(c.geo_layer, 1) AS geographic_layer,
                       {GeographicGroupExpression} AS group_rank,
                       CASE WHEN COALESCE(c.random_order_key, 0) >= $randomPivot THEN 0 ELSE 1 END AS rotation_band,
                       COALESCE(c.random_order_key, 0) AS random_order_key
                  FROM contracts c
                  JOIN contracts_fts ON contracts_fts.rowid = c.rowid
                 WHERE {where}
            )
            SELECT * FROM ranked
            {cursorWhere}
             ORDER BY geographic_layer, group_rank, rotation_band, random_order_key, pncp_id
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$candidateMatch", expression.CandidateMatchQuery);
        command.Parameters.AddWithValue("$randomPivot", randomPivot);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        AddFilterParameters(command, filters);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$cursorLayer", cursor.GeographicLayer);
            command.Parameters.AddWithValue("$cursorGroup", cursor.GroupRank);
            command.Parameters.AddWithValue("$cursorBand", cursor.RotationBand);
            command.Parameters.AddWithValue("$cursorRandom", cursor.RandomOrderKey);
            command.Parameters.AddWithValue("$cursorId", cursor.PncpId);
        }

        var candidates = new List<ItemContractCandidate>(pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var candidateCursor = new ItemCandidateCursor(
                reader.GetInt32(19),
                reader.GetInt32(20),
                reader.GetInt32(21),
                reader.GetInt64(22),
                reader.GetString(0));
            candidates.Add(new ItemContractCandidate(ReadContract(reader), candidateCursor));
        }

        var hasMore = candidates.Count > pageSize;
        if (hasMore)
        {
            candidates.RemoveAt(candidates.Count - 1);
        }

        return new ItemCandidatePage(
            candidates,
            candidates.Count == 0 ? cursor : candidates[^1].Cursor,
            hasMore);
    }

    public async Task<ContractRecord?> GetContractAsync(string pncpId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pncp_id, cnpj, purchase_year, purchase_sequence, object, additional_information,
                   process, organization, unit, municipality, municipality_ibge_code, uf, modality_id, modality_name, status,
                   publication_date, global_updated_at, total_homologated_scaled, distance_from_ribeirao_km
              FROM contracts WHERE pncp_id = $id;
            """;
        command.Parameters.AddWithValue("$id", pncpId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadContract(reader) : null;
    }

    public async Task UpsertItemsAsync(
        string contractId,
        IReadOnlyList<ProcurementItem> items,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // GetItemsAsync only returns after every page has arrived. Keeping the incoming
        // keys in a temporary table lets the complete list and its proof-of-completeness
        // become visible in one transaction, including the valid empty-list case.
        await using (var createIncoming = connection.CreateCommand())
        {
            createIncoming.Transaction = (SqliteTransaction)transaction;
            createIncoming.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS incoming_item_numbers(
                    item_number INTEGER PRIMARY KEY
                ) WITHOUT ROWID;
                DELETE FROM incoming_item_numbers;
                """;
            await createIncoming.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var incoming = connection.CreateCommand();
        incoming.Transaction = (SqliteTransaction)transaction;
        incoming.CommandText = "INSERT OR IGNORE INTO incoming_item_numbers(item_number) VALUES($itemNumber);";
        incoming.Parameters.Add("$itemNumber", SqliteType.Integer);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO items(
                contract_id, item_number, description, unit, requested_quantity_scaled,
                additional_information, item_category, ncm_nbs_code, ncm_nbs_description,
                catalog_code, catalog_name, catalog_category, status, has_result,
                source_updated_at, hydration_status, last_error, cache_updated_at, search_text)
            VALUES($contractId, $itemNumber, $description, $unit, $requestedQuantity,
                   $additionalInformation, $itemCategory, $ncmNbsCode, $ncmNbsDescription,
                   $catalogCode, $catalogName, $catalogCategory, $status, $hasResult,
                   $sourceUpdatedAt, $hydrationStatus, NULL, $cacheUpdatedAt, $searchText)
            ON CONFLICT(contract_id, item_number) DO UPDATE SET
                description = excluded.description,
                unit = excluded.unit,
                requested_quantity_scaled = excluded.requested_quantity_scaled,
                additional_information = excluded.additional_information,
                item_category = excluded.item_category,
                ncm_nbs_code = excluded.ncm_nbs_code,
                ncm_nbs_description = excluded.ncm_nbs_description,
                catalog_code = excluded.catalog_code,
                catalog_name = excluded.catalog_name,
                catalog_category = excluded.catalog_category,
                status = excluded.status,
                has_result = excluded.has_result,
                source_updated_at = excluded.source_updated_at,
                search_text = excluded.search_text,
                hydration_status = CASE
                    WHEN $forceRefresh = 1 THEN excluded.hydration_status
                    WHEN items.hydration_status = 2 THEN items.hydration_status
                    ELSE excluded.hydration_status
                END,
                last_error = CASE WHEN $forceRefresh = 1 THEN NULL ELSE items.last_error END,
                cache_updated_at = excluded.cache_updated_at;
            """;
        command.Parameters.Add("$contractId", SqliteType.Text);
        command.Parameters.Add("$itemNumber", SqliteType.Integer);
        command.Parameters.Add("$description", SqliteType.Text);
        command.Parameters.Add("$unit", SqliteType.Text);
        command.Parameters.Add("$requestedQuantity", SqliteType.Integer);
        command.Parameters.Add("$additionalInformation", SqliteType.Text);
        command.Parameters.Add("$itemCategory", SqliteType.Text);
        command.Parameters.Add("$ncmNbsCode", SqliteType.Text);
        command.Parameters.Add("$ncmNbsDescription", SqliteType.Text);
        command.Parameters.Add("$catalogCode", SqliteType.Text);
        command.Parameters.Add("$catalogName", SqliteType.Text);
        command.Parameters.Add("$catalogCategory", SqliteType.Text);
        command.Parameters.Add("$status", SqliteType.Text);
        command.Parameters.Add("$hasResult", SqliteType.Integer);
        command.Parameters.Add("$sourceUpdatedAt", SqliteType.Text);
        command.Parameters.Add("$hydrationStatus", SqliteType.Integer);
        command.Parameters.Add("$cacheUpdatedAt", SqliteType.Text);
        command.Parameters.Add("$searchText", SqliteType.Text);
        command.Parameters.AddWithValue("$forceRefresh", forceRefresh ? 1 : 0);

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var item in items)
        {
            var description = SearchText.Sanitize(item.Description);
            incoming.Parameters["$itemNumber"].Value = item.ItemNumber;
            await incoming.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            command.Parameters["$contractId"].Value = contractId;
            command.Parameters["$itemNumber"].Value = item.ItemNumber;
            command.Parameters["$description"].Value = description;
            command.Parameters["$unit"].Value = SearchText.Sanitize(item.Unit);
            command.Parameters["$requestedQuantity"].Value = DbValue(item.RequestedQuantityScaled);
            command.Parameters["$additionalInformation"].Value = SearchText.Sanitize(item.AdditionalInformation);
            command.Parameters["$itemCategory"].Value = SearchText.Sanitize(item.Category);
            command.Parameters["$ncmNbsCode"].Value = SearchText.Sanitize(item.NcmNbsCode);
            command.Parameters["$ncmNbsDescription"].Value = SearchText.Sanitize(item.NcmNbsDescription);
            command.Parameters["$catalogCode"].Value = SearchText.Sanitize(item.CatalogCode);
            command.Parameters["$catalogName"].Value = SearchText.Sanitize(item.CatalogName);
            command.Parameters["$catalogCategory"].Value = SearchText.Sanitize(item.CatalogCategory);
            command.Parameters["$status"].Value = SearchText.Sanitize(item.Status);
            command.Parameters["$hasResult"].Value = item.HasResult ? 1 : 0;
            command.Parameters["$sourceUpdatedAt"].Value = DbValue(item.UpdatedAt);
            command.Parameters["$hydrationStatus"].Value = (int)item.HydrationStatus;
            command.Parameters["$cacheUpdatedAt"].Value = now;
            command.Parameters["$searchText"].Value = SearchText.Normalize(description);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var reconcile = connection.CreateCommand())
        {
            reconcile.Transaction = (SqliteTransaction)transaction;
            reconcile.CommandText = """
                DELETE FROM items
                 WHERE contract_id = $contractId
                   AND item_number NOT IN (SELECT item_number FROM incoming_item_numbers);
                """;
            reconcile.Parameters.AddWithValue("$contractId", contractId);
            await reconcile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var removeImpossibleResults = connection.CreateCommand())
        {
            removeImpossibleResults.Transaction = (SqliteTransaction)transaction;
            removeImpossibleResults.CommandText = """
                DELETE FROM item_results
                 WHERE contract_id = $contractId
                   AND item_number IN (
                       SELECT item_number FROM items
                        WHERE contract_id = $contractId AND has_result = 0
                   );
                """;
            removeImpossibleResults.Parameters.AddWithValue("$contractId", contractId);
            await removeImpossibleResults.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var snapshot = connection.CreateCommand())
        {
            snapshot.Transaction = (SqliteTransaction)transaction;
            snapshot.CommandText = """
                INSERT INTO contract_item_snapshots(
                    contract_id, fetched_at, item_count, source_global_updated_at)
                SELECT c.pncp_id, $fetchedAt, $itemCount, c.global_updated_at
                  FROM contracts c
                 WHERE c.pncp_id = $contractId
                ON CONFLICT(contract_id) DO UPDATE SET
                    fetched_at = excluded.fetched_at,
                    item_count = excluded.item_count,
                    source_global_updated_at = excluded.source_global_updated_at;
                """;
            snapshot.Parameters.AddWithValue("$fetchedAt", now);
            snapshot.Parameters.AddWithValue("$itemCount", items.Count);
            snapshot.Parameters.AddWithValue("$contractId", contractId);
            await snapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContractItemSnapshot?> GetItemSnapshotAsync(
        string contractId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT contract_id, fetched_at, item_count, source_global_updated_at
              FROM contract_item_snapshots
             WHERE contract_id = $contractId;
            """;
        command.Parameters.AddWithValue("$contractId", contractId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var fetchedAt = ParseDateTime(reader, 1)
            ?? throw new InvalidDataException($"Snapshot de itens inválido para {contractId}.");
        return new ContractItemSnapshot(
            reader.GetString(0),
            fetchedAt,
            reader.GetInt32(2),
            ParseDateTime(reader, 3));
    }

    public async Task<IReadOnlyList<ProcurementItem>> SearchItemsAsync(
        string contractId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var match = SearchText.BuildMatchQuery(text);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = match.Length == 0
            ? """
                SELECT i.contract_id, i.item_number, i.description, i.unit, i.requested_quantity_scaled,
                       i.additional_information, i.item_category, i.ncm_nbs_code, i.ncm_nbs_description,
                       i.catalog_code, i.catalog_name, i.catalog_category, i.status,
                       i.has_result, i.source_updated_at, i.hydration_status, i.last_error
                  FROM items i
                 WHERE i.contract_id = $contractId
                 ORDER BY i.item_number;
                """
            : """
                SELECT i.contract_id, i.item_number, i.description, i.unit, i.requested_quantity_scaled,
                       i.additional_information, i.item_category, i.ncm_nbs_code, i.ncm_nbs_description,
                       i.catalog_code, i.catalog_name, i.catalog_category, i.status,
                       i.has_result, i.source_updated_at, i.hydration_status, i.last_error
                  FROM items i
                  JOIN items_fts ON items_fts.rowid = i.rowid
                 WHERE i.contract_id = $contractId AND items_fts MATCH $match
                 ORDER BY bm25(items_fts), i.item_number;
                """;
        command.Parameters.AddWithValue("$contractId", contractId);
        if (match.Length > 0)
        {
            command.Parameters.AddWithValue("$match", match);
        }

        var items = new List<ProcurementItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task<CachedItemResults?> GetCachedItemResultsAsync(
        string contractId,
        long itemNumber,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ProcurementItem? item;
        await using (var itemCommand = connection.CreateCommand())
        {
            itemCommand.CommandText = """
                SELECT contract_id, item_number, description, unit, requested_quantity_scaled,
                       additional_information, item_category, ncm_nbs_code, ncm_nbs_description,
                       catalog_code, catalog_name, catalog_category, status, has_result,
                       source_updated_at, hydration_status, last_error
                  FROM items
                 WHERE contract_id = $contractId AND item_number = $itemNumber;
                """;
            itemCommand.Parameters.AddWithValue("$contractId", contractId);
            itemCommand.Parameters.AddWithValue("$itemNumber", itemNumber);
            await using var reader = await itemCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            item = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
        }

        if (item is null)
        {
            return null;
        }

        await using var resultCommand = connection.CreateCommand();
        resultCommand.CommandText = """
            SELECT contract_id, item_number, result_sequence, supplier_tax_id, supplier_name,
                   supplier_type, supplier_municipality, supplier_uf,
                   quantity_scaled, unit_value_scaled, total_value_scaled, result_date,
                   result_status_id, result_status_name
              FROM item_results
             WHERE contract_id = $contractId AND item_number = $itemNumber
             ORDER BY result_sequence;
            """;
        resultCommand.Parameters.AddWithValue("$contractId", contractId);
        resultCommand.Parameters.AddWithValue("$itemNumber", itemNumber);
        var results = new List<HomologationResult>();
        await using var resultReader = await resultCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await resultReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadResult(resultReader));
        }

        return new CachedItemResults(item, results);
    }

    public async Task ReplaceItemResultsAsync(
        string contractId,
        long itemNumber,
        IReadOnlyList<HomologationResult> results,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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
                insert.Parameters["$taxId"].Value = SearchText.Sanitize(result.SupplierTaxId);
                insert.Parameters["$supplier"].Value = SearchText.Sanitize(result.SupplierName);
                insert.Parameters["$supplierType"].Value = SearchText.Sanitize(result.SupplierType);
                insert.Parameters["$supplierMunicipality"].Value = SearchText.Sanitize(result.SupplierMunicipality);
                insert.Parameters["$supplierUf"].Value = SearchText.Sanitize(result.SupplierUf);
                insert.Parameters["$quantity"].Value = DbValue(result.HomologatedQuantityScaled);
                insert.Parameters["$unitValue"].Value = DbValue(result.HomologatedUnitValueScaled);
                insert.Parameters["$totalValue"].Value = DbValue(result.HomologatedTotalValueScaled);
                insert.Parameters["$resultDate"].Value = DbValue(
                    result.ResultDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                insert.Parameters["$statusId"].Value = result.ResultStatusId;
                insert.Parameters["$statusName"].Value = SearchText.Sanitize(result.ResultStatusName);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE items SET hydration_status = $status, last_error = NULL, cache_updated_at = $updatedAt
                 WHERE contract_id = $contractId AND item_number = $itemNumber;
                """;
            update.Parameters.AddWithValue("$status", (int)ItemHydrationStatus.Complete);
            update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$contractId", contractId);
            update.Parameters.AddWithValue("$itemNumber", itemNumber);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetItemHydrationStatusAsync(
        string contractId,
        long itemNumber,
        ItemHydrationStatus status,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE items SET hydration_status = $status, last_error = $error, cache_updated_at = $updatedAt
             WHERE contract_id = $contractId AND item_number = $itemNumber;
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$error", DbValue(error));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$contractId", contractId);
        command.Parameters.AddWithValue("$itemNumber", itemNumber);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProcurementItem>> GetPendingItemsAsync(
        string contractId,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT contract_id, item_number, description, unit, requested_quantity_scaled,
                   additional_information, item_category, ncm_nbs_code, ncm_nbs_description,
                   catalog_code, catalog_name, catalog_category, status, has_result,
                   source_updated_at, hydration_status, last_error
              FROM items
             WHERE contract_id = $contractId AND has_result = 1
               AND ($forceRefresh = 1 OR hydration_status <> $complete)
             ORDER BY item_number;
            """;
        command.Parameters.AddWithValue("$contractId", contractId);
        command.Parameters.AddWithValue("$forceRefresh", forceRefresh ? 1 : 0);
        command.Parameters.AddWithValue("$complete", (int)ItemHydrationStatus.Complete);
        var items = new List<ProcurementItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<ItemDisplayRow>> GetItemDisplayRowsAsync(
        string contractId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.item_number, i.description, i.unit, i.has_result, i.hydration_status,
                   i.last_error, r.result_sequence, r.quantity_scaled, r.unit_value_scaled,
                   r.total_value_scaled, r.supplier_name, r.supplier_tax_id, r.result_date,
                   r.result_status_id, r.result_status_name
              FROM items i
              LEFT JOIN item_results r
                ON r.contract_id = i.contract_id AND r.item_number = i.item_number
             WHERE i.contract_id = $contractId
             ORDER BY i.item_number, r.result_sequence;
            """;
        command.Parameters.AddWithValue("$contractId", contractId);
        var rows = new List<ItemDisplayRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var hasResult = reader.GetInt64(3) == 1;
            var hydration = (ItemHydrationStatus)reader.GetInt32(4);
            var hasStoredResult = !reader.IsDBNull(6);
            var statusId = hasStoredResult ? reader.GetInt32(13) : 0;
            var displayStatus = GetDisplayStatus(hasResult, hydration, hasStoredResult, statusId);
            rows.Add(new ItemDisplayRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                displayStatus,
                DecimalScale.FromScaled(ReadNullableLong(reader, 7)),
                DecimalScale.FromScaled(ReadNullableLong(reader, 8)),
                DecimalScale.FromScaled(ReadNullableLong(reader, 9)),
                reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                ParseDate(reader, 12),
                hasStoredResult && statusId != 1));
        }

        return rows;
    }

    public async Task EnsureCoverageWindowAsync(
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<long> activeModalityIds,
        string uf = "ALL",
        CancellationToken cancellationToken = default)
    {
        if (startDate > endDate)
        {
            throw new ArgumentException("A data inicial deve ser anterior ou igual à data final.");
        }

        var normalizedUf = NormalizeCoverageUf(uf);
        var modalities = activeModalityIds.Distinct().Order().ToArray();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var setup = connection.CreateCommand())
        {
            setup.Transaction = (SqliteTransaction)transaction;
            setup.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS active_coverage_modalities(
                    modality_id INTEGER PRIMARY KEY
                ) WITHOUT ROWID;
                DELETE FROM active_coverage_modalities;
                """;
            await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var addModality = connection.CreateCommand())
        {
            addModality.Transaction = (SqliteTransaction)transaction;
            addModality.CommandText = "INSERT INTO active_coverage_modalities(modality_id) VALUES($id);";
            addModality.Parameters.Add("$id", SqliteType.Integer);
            foreach (var modalityId in modalities)
            {
                addModality.Parameters["$id"].Value = modalityId;
                await addModality.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var removeInactive = connection.CreateCommand())
        {
            removeInactive.Transaction = (SqliteTransaction)transaction;
            removeInactive.CommandText = """
                -- A version-one database could prove a complete national
                -- interval but did not persist the then-active modality list.
                -- Migration stores that proof once as modality 0. On the first
                -- reconciliation, expand it to the current active modalities;
                -- deleting the sentinel ensures modalities discovered later
                -- are correctly introduced as Missing.
                INSERT OR IGNORE INTO coverage_day_modalities(
                    coverage_date, modality_id, uf, status, records_count, updated_at, last_error)
                SELECT legacy.coverage_date, active.modality_id, legacy.uf,
                       $assumedComplete, legacy.records_count, legacy.updated_at, NULL
                  FROM coverage_day_modalities legacy
                  CROSS JOIN active_coverage_modalities active
                 WHERE legacy.coverage_date BETWEEN $start AND $end
                   AND legacy.uf = $uf
                   AND legacy.modality_id = 0
                   AND legacy.status = $assumedComplete;

                DELETE FROM coverage_day_modalities
                 WHERE coverage_date BETWEEN $start AND $end
                   AND uf = $uf
                   AND modality_id NOT IN (SELECT modality_id FROM active_coverage_modalities);
                """;
            removeInactive.Parameters.AddWithValue("$start", FormatDate(startDate));
            removeInactive.Parameters.AddWithValue("$end", FormatDate(endDate));
            removeInactive.Parameters.AddWithValue("$uf", normalizedUf);
            removeInactive.Parameters.AddWithValue("$assumedComplete", (int)CoverageStatus.AssumedComplete);
            await removeInactive.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO coverage_day_modalities(
                coverage_date, modality_id, uf, status, records_count, updated_at, last_error)
            VALUES($date, $modalityId, $uf, $status, NULL, $updatedAt, NULL);
            """;
        insert.Parameters.Add("$date", SqliteType.Text);
        insert.Parameters.Add("$modalityId", SqliteType.Integer);
        insert.Parameters.AddWithValue("$uf", normalizedUf);
        insert.Parameters.AddWithValue("$status", (int)CoverageStatus.Missing);
        insert.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            insert.Parameters["$date"].Value = FormatDate(date);
            foreach (var modalityId in modalities)
            {
                insert.Parameters["$modalityId"].Value = modalityId;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCoverageStatusAsync(
        DateOnly startDate,
        DateOnly endDate,
        long modalityId,
        string uf,
        CoverageStatus status,
        long? recordsCount = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        if (startDate > endDate)
        {
            throw new ArgumentException("A data inicial deve ser anterior ou igual à data final.");
        }

        var normalizedUf = NormalizeCoverageUf(uf);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO coverage_day_modalities(
                coverage_date, modality_id, uf, status, records_count, updated_at, last_error)
            VALUES($date, $modalityId, $uf, $status, $records, $updatedAt, $error)
            ON CONFLICT(coverage_date, modality_id, uf) DO UPDATE SET
                status = excluded.status,
                records_count = excluded.records_count,
                updated_at = excluded.updated_at,
                last_error = excluded.last_error;
            """;
        command.Parameters.Add("$date", SqliteType.Text);
        command.Parameters.AddWithValue("$modalityId", modalityId);
        command.Parameters.AddWithValue("$uf", normalizedUf);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$records", DbValue(recordsCount));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$error", DbValue(error));
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            command.Parameters["$date"].Value = FormatDate(date);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CoverageDay>> GetCoverageDaysAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (startDate > endDate)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT coverage_date, status, records_count, updated_at, last_error
              FROM coverage_day_modalities
             WHERE coverage_date BETWEEN $start AND $end
               AND uf = 'ALL'
             ORDER BY coverage_date, modality_id, uf;
            """;
        command.Parameters.AddWithValue("$start", FormatDate(startDate));
        command.Parameters.AddWithValue("$end", FormatDate(endDate));
        var cells = new Dictionary<DateOnly, List<CoverageCell>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var date = ParseDate(reader, 0);
            if (date is null)
            {
                continue;
            }

            if (!cells.TryGetValue(date.Value, out var values))
            {
                values = [];
                cells.Add(date.Value, values);
            }

            values.Add(new CoverageCell(
                (CoverageStatus)reader.GetInt32(1),
                ReadNullableLong(reader, 2),
                ParseDateTime(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        var days = new List<CoverageDay>(endDate.DayNumber - startDate.DayNumber + 1);
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            cells.TryGetValue(date, out var values);
            values ??= [];
            var completed = values.Count(value => IsCompleteCoverage(value.Status));
            days.Add(new CoverageDay
            {
                Date = date,
                Status = AggregateCoverageStatus(values),
                ExpectedModalities = values.Count,
                CompletedModalities = completed,
                RecordsCount = values.Any(value => value.RecordsCount is not null)
                    ? values.Sum(value => value.RecordsCount ?? 0)
                    : null,
                UpdatedAt = values.Select(value => value.UpdatedAt).Max(),
                LastError = values.LastOrDefault(value => !string.IsNullOrWhiteSpace(value.LastError))?.LastError
            });
        }

        return days;
    }

    public async Task<IReadOnlyList<CoverageWorkItem>> GetIncompleteCoverageAsync(
        DateOnly startDate,
        DateOnly endDate,
        int limit,
        bool newestFirst,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 10_000);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT coverage_date, modality_id, uf
              FROM coverage_day_modalities
             WHERE coverage_date BETWEEN $start AND $end
               AND uf = 'ALL'
               AND status NOT IN ($complete, $assumedComplete)
             ORDER BY coverage_date {(newestFirst ? "DESC" : "ASC")}, modality_id, uf
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$start", FormatDate(startDate));
        command.Parameters.AddWithValue("$end", FormatDate(endDate));
        command.Parameters.AddWithValue("$complete", (int)CoverageStatus.Complete);
        command.Parameters.AddWithValue("$assumedComplete", (int)CoverageStatus.AssumedComplete);
        command.Parameters.AddWithValue("$limit", limit);
        var items = new List<CoverageWorkItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var date = ParseDate(reader, 0);
            if (date is not null)
            {
                items.Add(new CoverageWorkItem(date.Value, reader.GetInt64(1), reader.GetString(2)));
            }
        }

        return items;
    }

    public async Task<bool> IsCoverageCompleteAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (startDate > endDate)
        {
            return false;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COUNT(DISTINCT coverage_date),
                   SUM(CASE WHEN status IN ($complete, $assumedComplete) THEN 0 ELSE 1 END)
              FROM coverage_day_modalities
             WHERE coverage_date BETWEEN $start AND $end
               AND uf = 'ALL';
            """;
        command.Parameters.AddWithValue("$start", FormatDate(startDate));
        command.Parameters.AddWithValue("$end", FormatDate(endDate));
        command.Parameters.AddWithValue("$complete", (int)CoverageStatus.Complete);
        command.Parameters.AddWithValue("$assumedComplete", (int)CoverageStatus.AssumedComplete);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var cells = reader.GetInt64(0);
        var dates = reader.GetInt64(1);
        var incomplete = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
        return cells > 0 &&
               dates == endDate.DayNumber - startDate.DayNumber + 1L &&
               incomplete == 0;
    }

    public async Task<DatasetState> GetDatasetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DateOnly? startDate = null;
        DateOnly? endDate = null;
        var scope = GeoScope.All;
        DateTimeOffset? lastSync = null;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT start_date, end_date, scope_kind, scope_uf, last_successful_sync FROM dataset WHERE id = 1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                startDate = ParseDate(reader, 0);
                endDate = ParseDate(reader, 1);
                var kind = (GeoScopeKind)reader.GetInt32(2);
                scope = kind == GeoScopeKind.State
                    ? GeoScope.State(reader.IsDBNull(3) ? "SP" : reader.GetString(3))
                    : kind == GeoScopeKind.Southeast ? GeoScope.Southeast : GeoScope.All;
                lastSync = ParseDateTime(reader, 4);
            }
        }

        var counts = await GetCountsAsync(cancellationToken).ConfigureAwait(false);
        return new DatasetState(startDate, endDate, scope, lastSync, counts.Contracts, counts.Items, counts.Results);
    }

    public async Task<IncompleteSyncState?> GetLatestIncompleteSyncAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mode, start_date, end_date, started_at
              FROM sync_runs
             WHERE status IN ('Running', 'Failed')
             ORDER BY started_at DESC
             LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var startDate = ParseDate(reader, 1);
        var endDate = ParseDate(reader, 2);
        var startedAt = ParseDateTime(reader, 3);
        if (startDate is null || endDate is null || startedAt is null || startDate > endDate)
        {
            return null;
        }

        return new IncompleteSyncState(
            (SyncMode)reader.GetInt32(0),
            startDate.Value,
            endDate.Value,
            startedAt.Value);
    }

    public async Task SetDatasetStateAsync(
        DateOnly startDate,
        DateOnly endDate,
        GeoScope scope,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dataset(id, start_date, end_date, scope_kind, scope_uf, last_successful_sync)
            VALUES(1, $start, $end, $kind, $uf, $sync)
            ON CONFLICT(id) DO UPDATE SET start_date = excluded.start_date, end_date = excluded.end_date,
                scope_kind = excluded.scope_kind, scope_uf = excluded.scope_uf,
                last_successful_sync = excluded.last_successful_sync;
            """;
        command.Parameters.AddWithValue("$start", startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$kind", (int)scope.Kind);
        command.Parameters.AddWithValue("$uf", DbValue(scope.Uf));
        command.Parameters.AddWithValue("$sync", completedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PruneContractsBeforeAsync(DateOnly cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM contracts WHERE date(publication_date) < date($cutoff);";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> GetCacheSizeBytesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(SUM(pgsize), 0) FROM dbstat
                 WHERE name IN ('items', 'item_results', 'sqlite_autoindex_items_1',
                                'sqlite_autoindex_item_results_1', 'idx_items_contract_status',
                                'items_fts', 'items_fts_data', 'items_fts_idx',
                                'items_fts_docsize', 'items_fts_config',
                                'contract_item_snapshots');
                """;
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            var counts = await GetCountsAsync(cancellationToken).ConfigureAwait(false);
            return checked(counts.Items * 900 + counts.Results * 750);
        }
    }

    public async Task ClearItemCacheAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM items; PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int?> GetPartitionNextPageAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN completed = 1 THEN 0 ELSE next_page END FROM sync_partitions WHERE partition_key = $key;";
        command.Parameters.AddWithValue("$key", partitionKey);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null || value is DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task SavePartitionProgressAsync(
        string partitionKey,
        int nextPage,
        bool completed,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_partitions(partition_key, next_page, completed, updated_at)
            VALUES($key, $page, $completed, $updated)
            ON CONFLICT(partition_key) DO UPDATE SET next_page = excluded.next_page,
                completed = excluded.completed, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$key", partitionKey);
        command.Parameters.AddWithValue("$page", nextPage);
        command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncPartitionCheckpoint?> GetPartitionCheckpointAsync(
        string partitionKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mode, start_date, end_date, modality_id, uf, next_page,
                   total_pages, status, last_error, next_retry_at, updated_at,
                   completed
              FROM sync_partitions
             WHERE partition_key = $key;
            """;
        command.Parameters.AddWithValue("$key", partitionKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) ||
            reader.IsDBNull(3) || reader.IsDBNull(4))
        {
            return null;
        }

        var startDate = ParseDate(reader, 1);
        var endDate = ParseDate(reader, 2);
        var updatedAt = ParseDateTime(reader, 10);
        if (startDate is null || endDate is null || updatedAt is null)
        {
            return null;
        }

        var completed = reader.GetInt64(11) == 1;
        var status = reader.IsDBNull(7)
            ? completed ? SyncPartitionStatus.Complete : SyncPartitionStatus.Pending
            : (SyncPartitionStatus)reader.GetInt32(7);
        return new SyncPartitionCheckpoint
        {
            PartitionKey = partitionKey,
            Mode = (SyncMode)reader.GetInt32(0),
            StartDate = startDate.Value,
            EndDate = endDate.Value,
            ModalityId = reader.GetInt64(3),
            Uf = NormalizeCoverageUf(reader.GetString(4)),
            NextPage = completed ? 0 : reader.GetInt32(5),
            TotalPages = ReadNullableLong(reader, 6),
            Status = completed ? SyncPartitionStatus.Complete : status,
            LastError = reader.IsDBNull(8) ? null : reader.GetString(8),
            NextRetryAt = ParseDateTime(reader, 9),
            UpdatedAt = updatedAt.Value
        };
    }

    public async Task SavePartitionCheckpointAsync(
        SyncPartitionCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.PartitionKey);
        if (checkpoint.StartDate > checkpoint.EndDate)
        {
            throw new ArgumentException("A data inicial do checkpoint deve ser anterior ou igual à data final.", nameof(checkpoint));
        }

        if (checkpoint.NextPage < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint), "A próxima página não pode ser negativa.");
        }

        var complete = checkpoint.Status == SyncPartitionStatus.Complete;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_partitions(
                partition_key, next_page, completed, updated_at, mode, start_date,
                end_date, modality_id, uf, total_pages, status, last_error, next_retry_at)
            VALUES($key, $page, $completed, $updated, $mode, $start, $end,
                   $modality, $uf, $totalPages, $status, $error, $nextRetry)
            ON CONFLICT(partition_key) DO UPDATE SET
                next_page = excluded.next_page,
                completed = excluded.completed,
                updated_at = excluded.updated_at,
                mode = excluded.mode,
                start_date = excluded.start_date,
                end_date = excluded.end_date,
                modality_id = excluded.modality_id,
                uf = excluded.uf,
                total_pages = excluded.total_pages,
                status = excluded.status,
                last_error = excluded.last_error,
                next_retry_at = excluded.next_retry_at;
            """;
        command.Parameters.AddWithValue("$key", checkpoint.PartitionKey);
        command.Parameters.AddWithValue("$page", complete ? 0 : Math.Max(1, checkpoint.NextPage));
        command.Parameters.AddWithValue("$completed", complete ? 1 : 0);
        command.Parameters.AddWithValue(
            "$updated",
            (checkpoint.UpdatedAt == default ? DateTimeOffset.UtcNow : checkpoint.UpdatedAt)
                .ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$mode", (int)checkpoint.Mode);
        command.Parameters.AddWithValue("$start", FormatDate(checkpoint.StartDate));
        command.Parameters.AddWithValue("$end", FormatDate(checkpoint.EndDate));
        command.Parameters.AddWithValue("$modality", checkpoint.ModalityId);
        command.Parameters.AddWithValue("$uf", NormalizeCoverageUf(checkpoint.Uf));
        command.Parameters.AddWithValue("$totalPages", DbValue(checkpoint.TotalPages));
        command.Parameters.AddWithValue("$status", (int)checkpoint.Status);
        command.Parameters.AddWithValue("$error", DbValue(checkpoint.LastError));
        command.Parameters.AddWithValue("$nextRetry", DbValue(checkpoint.NextRetryAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StartSyncRunAsync(
        SyncMode mode,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_runs(id, mode, start_date, end_date, started_at, status, contracts_saved)
            VALUES($id, $mode, $start, $end, $started, 'Running', 0);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$mode", (int)mode);
        command.Parameters.AddWithValue("$start", startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task CompleteSyncRunAsync(
        string runId,
        bool succeeded,
        long contractsSaved,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_runs SET finished_at = $finished, status = $status,
                   contracts_saved = $saved, error = $error WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$finished", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", succeeded ? "Complete" : "Failed");
        command.Parameters.AddWithValue("$saved", contractsSaved);
        command.Parameters.AddWithValue("$error", DbValue(error));
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(long Contracts, long Items, long Results)> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM contracts), (SELECT COUNT(*) FROM items), (SELECT COUNT(*) FROM item_results);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public async Task CheckpointWalAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        connection.CreateFunction<string?, string?, string?, double?>(
            "pncp_geo_distance",
            (code, name, uf) => ResolveGeography(code, name, uf).Distance,
            isDeterministic: true);
        connection.CreateFunction<string?, string?, string?, int?>(
            "pncp_geo_distance_rank",
            (code, name, uf) => ResolveGeography(code, name, uf).DistanceRank,
            isDeterministic: true);
        connection.CreateFunction<string?, int?>(
            "pncp_state_rank",
            uf => GeographicValue(BrazilMunicipalityCatalog.GetStateProximityRank(uf)),
            isDeterministic: true);
        connection.CreateFunction<string?, string?, string?, int>(
            "pncp_geo_layer",
            (code, name, uf) => ResolveGeography(code, name, uf).Layer,
            isDeterministic: true);
        connection.CreateFunction<string?, long>(
            "pncp_random_key",
            StableRandomOrderKey,
            isDeterministic: true);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void AddSearchParameters(SqliteCommand command, SearchQuery query, string match)
    {
        if (match.Length > 0)
        {
            command.Parameters.AddWithValue("$match", match);
        }

        AddFilterParameters(command, query);
    }

    private static void AddFilterParameters(SqliteCommand command, SearchQuery query)
    {
        var geoFilter = query.EffectiveGeoFilter;
        if (geoFilter.Kind == SearchGeoFilterKind.State)
        {
            command.Parameters.AddWithValue("$uf", geoFilter.Uf!);
        }

        if (query.StartDate is not null)
        {
            command.Parameters.AddWithValue("$startDate", query.StartDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (query.EndDate is not null)
        {
            command.Parameters.AddWithValue("$endDate", query.EndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    }

    private static void AddContractParameters(SqliteCommand command)
    {
        foreach (var name in new[]
                 {
                     "$pncpId", "$cnpj", "$year", "$sequence", "$object", "$additional", "$process",
                     "$organization", "$unit", "$municipality", "$municipalityIbge", "$uf", "$modalityId", "$modalityName",
                     "$status", "$publication", "$globalUpdated", "$totalHomologated", "$searchText",
                     "$distance", "$distanceRank", "$stateRank", "$geoLayer", "$randomOrderKey"
                 })
        {
            command.Parameters.Add(name, name is "$year" or "$sequence" or "$modalityId" or "$totalHomologated" or
                "$distanceRank" or "$stateRank" or "$geoLayer" or "$randomOrderKey"
                ? SqliteType.Integer
                : name == "$distance" ? SqliteType.Real
                : SqliteType.Text);
        }
    }

    private static void SetContractParameters(SqliteCommand command, ContractRecord contract)
    {
        var contractObject = SearchText.Sanitize(contract.Object);
        command.Parameters["$pncpId"].Value = SearchText.Sanitize(contract.PncpId);
        command.Parameters["$cnpj"].Value = SearchText.Sanitize(contract.Cnpj);
        command.Parameters["$year"].Value = contract.PurchaseYear;
        command.Parameters["$sequence"].Value = contract.PurchaseSequence;
        command.Parameters["$object"].Value = contractObject;
        command.Parameters["$additional"].Value = SearchText.Sanitize(contract.AdditionalInformation);
        command.Parameters["$process"].Value = SearchText.Sanitize(contract.Process);
        command.Parameters["$organization"].Value = SearchText.Sanitize(contract.Organization);
        command.Parameters["$unit"].Value = SearchText.Sanitize(contract.Unit);
        command.Parameters["$municipality"].Value = SearchText.Sanitize(contract.Municipality);
        // Prefer the official code. The national catalog also accepts normalized
        // municipality/UF as a fallback for older PNCP rows that omitted it.
        command.Parameters["$municipalityIbge"].Value = DbValue(contract.MunicipalityIbgeCode is null
            ? null
            : SearchText.Sanitize(contract.MunicipalityIbgeCode));
        command.Parameters["$uf"].Value = SearchText.Sanitize(contract.Uf);
        command.Parameters["$modalityId"].Value = contract.ModalityId;
        command.Parameters["$modalityName"].Value = SearchText.Sanitize(contract.ModalityName);
        command.Parameters["$status"].Value = SearchText.Sanitize(contract.Status);
        command.Parameters["$publication"].Value = DbValue(contract.PublicationDate);
        command.Parameters["$globalUpdated"].Value = DbValue(contract.GlobalUpdatedAt);
        command.Parameters["$totalHomologated"].Value = DbValue(contract.TotalHomologatedScaled);
        command.Parameters["$searchText"].Value = SearchText.Normalize(contractObject);
        var geography = ResolveGeography(contract.MunicipalityIbgeCode, contract.Municipality, contract.Uf);
        command.Parameters["$distance"].Value = DbValue(geography.Distance);
        command.Parameters["$distanceRank"].Value = DbValue(geography.DistanceRank);
        command.Parameters["$stateRank"].Value = DbValue(geography.StateRank);
        command.Parameters["$geoLayer"].Value = geography.Layer;
        command.Parameters["$randomOrderKey"].Value = StableRandomOrderKey(contract.PncpId);
    }

    private static ContractRecord ReadContract(SqliteDataReader reader) => new()
    {
        PncpId = reader.GetString(0),
        Cnpj = reader.GetString(1),
        PurchaseYear = reader.GetInt32(2),
        PurchaseSequence = reader.GetInt32(3),
        Object = reader.GetString(4),
        AdditionalInformation = reader.GetString(5),
        Process = reader.GetString(6),
        Organization = reader.GetString(7),
        Unit = reader.GetString(8),
        Municipality = reader.GetString(9),
        MunicipalityIbgeCode = reader.IsDBNull(10) ? null : reader.GetString(10),
        Uf = reader.GetString(11),
        ModalityId = reader.GetInt64(12),
        ModalityName = reader.GetString(13),
        Status = reader.GetString(14),
        PublicationDate = ParseDateTime(reader, 15),
        GlobalUpdatedAt = ParseDateTime(reader, 16),
        TotalHomologatedScaled = ReadNullableLong(reader, 17),
        DistanceFromRibeiraoKilometers = reader.FieldCount > 18 && !reader.IsDBNull(18)
            ? reader.GetDouble(18)
            : reader.IsDBNull(10) ? null : GetNearbyDistance(reader.GetString(10))
    };

    private static ProcurementItem ReadItem(SqliteDataReader reader) => new()
    {
        ContractId = reader.GetString(0),
        ItemNumber = reader.GetInt64(1),
        Description = reader.GetString(2),
        Unit = reader.GetString(3),
        RequestedQuantityScaled = ReadNullableLong(reader, 4),
        AdditionalInformation = reader.GetString(5),
        Category = reader.GetString(6),
        NcmNbsCode = reader.GetString(7),
        NcmNbsDescription = reader.GetString(8),
        CatalogCode = reader.GetString(9),
        CatalogName = reader.GetString(10),
        CatalogCategory = reader.GetString(11),
        Status = reader.GetString(12),
        HasResult = reader.GetInt64(13) == 1,
        UpdatedAt = ParseDateTime(reader, 14),
        HydrationStatus = (ItemHydrationStatus)reader.GetInt32(15),
        LastError = reader.IsDBNull(16) ? null : reader.GetString(16)
    };

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
        HomologatedQuantityScaled = ReadNullableLong(reader, 8),
        HomologatedUnitValueScaled = ReadNullableLong(reader, 9),
        HomologatedTotalValueScaled = ReadNullableLong(reader, 10),
        ResultDate = ParseDate(reader, 11),
        ResultStatusId = reader.GetInt32(12),
        ResultStatusName = reader.GetString(13)
    };

    private static string GetDisplayStatus(bool hasResult, ItemHydrationStatus hydration, bool hasStoredResult, int resultStatusId)
    {
        if (hydration == ItemHydrationStatus.Stale)
        {
            return "Preço desatualizado — requer conexão";
        }

        if (hydration == ItemHydrationStatus.Failed)
        {
            return hasStoredResult
                ? "Falha ao consultar — exibindo cache anterior"
                : "Falha ao consultar — tentar novamente";
        }

        if (hydration == ItemHydrationStatus.Loading && hasStoredResult)
        {
            return "Consulta em andamento — exibindo cache anterior";
        }

        if (hasStoredResult)
        {
            return resultStatusId == 1 ? "Preço homologado encontrado" : "Resultado cancelado";
        }

        if (!hasResult)
        {
            return "Item sem resultado homologado";
        }

        return hydration switch
        {
            ItemHydrationStatus.Loading => "Consulta em andamento",
            ItemHydrationStatus.Complete => "Item sem resultado homologado",
            _ => "Consulta pendente — requer conexão"
        };
    }

    private static DateTimeOffset? ParseDateTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ||
        !DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? null
            : value;

    private static DateOnly? ParseDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ||
        !DateOnly.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? null
            : value;

    private static long? ReadNullableLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static double? GetNearbyDistance(string ibgeCode) =>
        BrazilMunicipalityCatalog.TryResolve(ibgeCode, null, null, out var municipality)
            ? municipality.DistanceFromRibeiraoKilometers
            : null;

    private static GeographicMetadata ResolveGeography(string? ibgeCode, string? name, string? uf)
    {
        var stateRank = GeographicValue(BrazilMunicipalityCatalog.GetStateProximityRank(uf));
        if (!BrazilMunicipalityCatalog.TryResolve(ibgeCode, name, uf, out var municipality))
        {
            return new GeographicMetadata(null, null, stateRank, 1);
        }

        var distanceRank = BrazilMunicipalityCatalog.GetDistanceRank(municipality.IbgeCode);
        return new GeographicMetadata(
            municipality.DistanceFromRibeiraoKilometers,
            GeographicValue(distanceRank),
            stateRank,
            distanceRank < 50 ? 0 : 1);
    }

    private static int? GeographicValue(int value) => value == int.MaxValue ? null : value;

    private static long StableRandomOrderKey(string? value)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var octet in System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty))
        {
            hash ^= octet;
            hash *= prime;
        }

        return (long)(hash & long.MaxValue);
    }

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string NormalizeCoverageUf(string? uf) =>
        string.IsNullOrWhiteSpace(uf) ? "ALL" : uf.Trim().ToUpperInvariant();

    private static bool IsCompleteCoverage(CoverageStatus status) =>
        status is CoverageStatus.Complete or CoverageStatus.AssumedComplete;

    private static CoverageStatus AggregateCoverageStatus(IReadOnlyList<CoverageCell> cells)
    {
        if (cells.Count == 0 || cells.All(cell => cell.Status == CoverageStatus.Missing))
        {
            return CoverageStatus.Missing;
        }

        if (cells.All(cell => IsCompleteCoverage(cell.Status)))
        {
            return cells.Any(cell => cell.Status == CoverageStatus.Complete)
                ? CoverageStatus.Complete
                : CoverageStatus.AssumedComplete;
        }

        if (cells.Any(cell => cell.Status == CoverageStatus.Failed))
        {
            return CoverageStatus.Failed;
        }

        if (cells.Any(cell => cell.Status == CoverageStatus.Downloading))
        {
            return CoverageStatus.Downloading;
        }

        return CoverageStatus.Partial;
    }

    private static async Task BackfillNearbyMunicipalityCodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var updates = new List<(string Municipality, string Uf, string IbgeCode)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT DISTINCT municipality, uf
                  FROM contracts
                 WHERE municipality_ibge_code IS NULL OR municipality_ibge_code = '';
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (NearbyRibeiraoCatalog.TryGetByNameAndUf(reader.GetString(0), reader.GetString(1), out var municipality))
                {
                    updates.Add((reader.GetString(0), reader.GetString(1), municipality.IbgeCode));
                }
            }
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE contracts
               SET municipality_ibge_code = $code
             WHERE municipality = $municipality AND uf = $uf
               AND (municipality_ibge_code IS NULL OR municipality_ibge_code = '');
            """;
        update.Parameters.Add("$code", SqliteType.Text);
        update.Parameters.Add("$municipality", SqliteType.Text);
        update.Parameters.Add("$uf", SqliteType.Text);
        foreach (var (municipality, uf, ibgeCode) in updates)
        {
            update.Parameters["$code"].Value = ibgeCode;
            update.Parameters["$municipality"].Value = municipality;
            update.Parameters["$uf"].Value = uf;
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task MigrateLegacySyncStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var migratedAt = DateTimeOffset.UtcNow;
        var legacyPartitions = new List<LegacyPartition>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT partition_key, next_page, completed, updated_at
                  FROM sync_partitions;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (TryParseLegacyPartitionKey(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        reader.GetInt64(2) == 1,
                        ParseDateTime(reader, 3) ?? migratedAt,
                        out var partition))
                {
                    legacyPartitions.Add(partition);
                }
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE sync_partitions
                   SET mode = $mode,
                       start_date = $start,
                       end_date = $end,
                       modality_id = $modality,
                       uf = $uf,
                       status = $status,
                       last_error = NULL,
                       next_retry_at = NULL
                 WHERE partition_key = $key;
                """;
            update.Parameters.Add("$mode", SqliteType.Integer);
            update.Parameters.Add("$start", SqliteType.Text);
            update.Parameters.Add("$end", SqliteType.Text);
            update.Parameters.Add("$modality", SqliteType.Integer);
            update.Parameters.Add("$uf", SqliteType.Text);
            update.Parameters.Add("$status", SqliteType.Integer);
            update.Parameters.Add("$key", SqliteType.Text);
            foreach (var partition in legacyPartitions)
            {
                update.Parameters["$mode"].Value = (int)partition.Mode;
                update.Parameters["$start"].Value = FormatDate(partition.StartDate);
                update.Parameters["$end"].Value = FormatDate(partition.EndDate);
                update.Parameters["$modality"].Value = partition.ModalityId;
                update.Parameters["$uf"].Value = partition.Uf;
                update.Parameters["$status"].Value = (int)partition.Status;
                update.Parameters["$key"].Value = partition.PartitionKey;
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Completed publication checkpoints are direct proof that every page of
        // that date/modalidade/UF partition was received. A checkpoint beyond
        // page one proves only partial progress. Unrecognized keys deliberately
        // create no coverage evidence and will be downloaded again safely.
        await using (var seedCheckpointCoverage = connection.CreateCommand())
        {
            seedCheckpointCoverage.Transaction = transaction;
            seedCheckpointCoverage.CommandText = """
                INSERT INTO coverage_day_modalities(
                    coverage_date, modality_id, uf, status, records_count, updated_at, last_error)
                VALUES($date, $modality, $uf, $status, NULL, $updated, NULL)
                ON CONFLICT(coverage_date, modality_id, uf) DO UPDATE SET
                    status = CASE
                        WHEN coverage_day_modalities.status IN ($complete, $assumedComplete)
                            THEN coverage_day_modalities.status
                        ELSE excluded.status
                    END,
                    updated_at = excluded.updated_at;
                """;
            seedCheckpointCoverage.Parameters.Add("$date", SqliteType.Text);
            seedCheckpointCoverage.Parameters.Add("$modality", SqliteType.Integer);
            seedCheckpointCoverage.Parameters.Add("$uf", SqliteType.Text);
            seedCheckpointCoverage.Parameters.Add("$status", SqliteType.Integer);
            seedCheckpointCoverage.Parameters.Add("$updated", SqliteType.Text);
            seedCheckpointCoverage.Parameters.AddWithValue("$complete", (int)CoverageStatus.Complete);
            seedCheckpointCoverage.Parameters.AddWithValue("$assumedComplete", (int)CoverageStatus.AssumedComplete);
            foreach (var partition in legacyPartitions.Where(partition =>
                         partition.Mode == SyncMode.Publication &&
                         partition.Status is SyncPartitionStatus.Complete or SyncPartitionStatus.Partial))
            {
                var coverageStatus = partition.Status == SyncPartitionStatus.Complete
                    ? CoverageStatus.AssumedComplete
                    : CoverageStatus.Partial;
                seedCheckpointCoverage.Parameters["$modality"].Value = partition.ModalityId;
                seedCheckpointCoverage.Parameters["$uf"].Value = partition.Uf;
                seedCheckpointCoverage.Parameters["$status"].Value = (int)coverageStatus;
                seedCheckpointCoverage.Parameters["$updated"].Value = partition.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
                for (var date = partition.StartDate; date <= partition.EndDate; date = date.AddDays(1))
                {
                    seedCheckpointCoverage.Parameters["$date"].Value = FormatDate(date);
                    await seedCheckpointCoverage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (!await TableExistsAsync(connection, transaction, "dataset", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        DateOnly? declaredStart = null;
        DateOnly? declaredEnd = null;
        await using (var readDataset = connection.CreateCommand())
        {
            readDataset.Transaction = transaction;
            readDataset.CommandText = """
                SELECT start_date, end_date
                  FROM dataset
                 WHERE id = 1
                   AND scope_kind = $all
                   AND last_successful_sync IS NOT NULL;
                """;
            readDataset.Parameters.AddWithValue("$all", (int)GeoScopeKind.All);
            await using var reader = await readDataset.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                declaredStart = ParseDate(reader, 0);
                declaredEnd = ParseDate(reader, 1);
            }
        }

        if (declaredStart is null || declaredEnd is null || declaredStart > declaredEnd)
        {
            return;
        }

        // Version one stored no modality manifest. A one-use modality-zero
        // sentinel preserves the declaration without guessing. The next call
        // to EnsureCoverageWindowAsync expands it to exactly the modality list
        // returned by PNCP and removes the sentinel.
        await using var seedDeclaredCoverage = connection.CreateCommand();
        seedDeclaredCoverage.Transaction = transaction;
        seedDeclaredCoverage.CommandText = """
            INSERT OR IGNORE INTO coverage_day_modalities(
                coverage_date, modality_id, uf, status, records_count, updated_at, last_error)
            VALUES($date, 0, 'ALL', $status, NULL, $updated, NULL);
            """;
        seedDeclaredCoverage.Parameters.Add("$date", SqliteType.Text);
        seedDeclaredCoverage.Parameters.AddWithValue("$status", (int)CoverageStatus.AssumedComplete);
        seedDeclaredCoverage.Parameters.AddWithValue("$updated", migratedAt.ToString("O", CultureInfo.InvariantCulture));
        for (var date = declaredStart.Value; date <= declaredEnd.Value; date = date.AddDays(1))
        {
            seedDeclaredCoverage.Parameters["$date"].Value = FormatDate(date);
            await seedDeclaredCoverage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static bool TryParseLegacyPartitionKey(
        string partitionKey,
        int nextPage,
        bool completed,
        DateTimeOffset updatedAt,
        out LegacyPartition partition)
    {
        partition = default!;
        var parts = partitionKey.Split(':', StringSplitOptions.None);
        if (parts.Length != 5 ||
            !Enum.TryParse(parts[0], ignoreCase: true, out SyncMode mode) ||
            !DateOnly.TryParseExact(parts[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate) ||
            !DateOnly.TryParseExact(parts[2], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate) ||
            startDate > endDate ||
            !parts[3].StartsWith('m') ||
            !long.TryParse(parts[3].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var modalityId) ||
            !parts[4].StartsWith("uf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var status = completed
            ? SyncPartitionStatus.Complete
            : nextPage > 1 ? SyncPartitionStatus.Partial : SyncPartitionStatus.Pending;
        partition = new LegacyPartition(
            partitionKey,
            mode,
            startDate,
            endDate,
            modalityId,
            NormalizeCoverageUf(parts[4][2..]),
            Math.Max(completed ? 0 : 1, nextPage),
            status,
            updatedAt);
        return true;
    }

    private static object DbValue(object? value) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        _ => value
    };

    private sealed record CoverageCell(
        CoverageStatus Status,
        long? RecordsCount,
        DateTimeOffset? UpdatedAt,
        string? LastError);

    private sealed record GeographicMetadata(
        double? Distance,
        int? DistanceRank,
        int? StateRank,
        int Layer);

    private sealed record LegacyPartition(
        string PartitionKey,
        SyncMode Mode,
        DateOnly StartDate,
        DateOnly EndDate,
        long ModalityId,
        string Uf,
        int NextPage,
        SyncPartitionStatus Status,
        DateTimeOffset UpdatedAt);

    private const string ContractUpsertSql = """
        INSERT INTO contracts(
            pncp_id, cnpj, purchase_year, purchase_sequence, object, additional_information,
            process, organization, unit, municipality, municipality_ibge_code, uf, modality_id, modality_name, status,
            publication_date, global_updated_at, total_homologated_scaled, search_text,
            distance_from_ribeirao_km, municipality_distance_rank, state_proximity_rank,
            geo_layer, random_order_key)
        VALUES($pncpId, $cnpj, $year, $sequence, $object, $additional, $process, $organization,
               $unit, $municipality, $municipalityIbge, $uf, $modalityId, $modalityName, $status, $publication,
               $globalUpdated, $totalHomologated, $searchText, $distance, $distanceRank,
               $stateRank, $geoLayer, $randomOrderKey)
        ON CONFLICT(pncp_id) DO UPDATE SET
            cnpj = excluded.cnpj,
            purchase_year = excluded.purchase_year,
            purchase_sequence = excluded.purchase_sequence,
            object = excluded.object,
            additional_information = excluded.additional_information,
            process = excluded.process,
            organization = excluded.organization,
            unit = excluded.unit,
            municipality = excluded.municipality,
            municipality_ibge_code = excluded.municipality_ibge_code,
            uf = excluded.uf,
            modality_id = excluded.modality_id,
            modality_name = excluded.modality_name,
            status = excluded.status,
            publication_date = excluded.publication_date,
            global_updated_at = excluded.global_updated_at,
            total_homologated_scaled = excluded.total_homologated_scaled,
            search_text = excluded.search_text,
            distance_from_ribeirao_km = excluded.distance_from_ribeirao_km,
            municipality_distance_rank = excluded.municipality_distance_rank,
            state_proximity_rank = excluded.state_proximity_rank,
            geo_layer = excluded.geo_layer,
            random_order_key = excluded.random_order_key;
        """;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_info(
            id INTEGER PRIMARY KEY CHECK(id = 1),
            version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS dataset(
            id INTEGER PRIMARY KEY CHECK(id = 1),
            start_date TEXT,
            end_date TEXT,
            scope_kind INTEGER NOT NULL DEFAULT 0,
            scope_uf TEXT,
            last_successful_sync TEXT
        );

        CREATE TABLE IF NOT EXISTS contracts(
            pncp_id TEXT PRIMARY KEY,
            cnpj TEXT NOT NULL,
            purchase_year INTEGER NOT NULL,
            purchase_sequence INTEGER NOT NULL,
            object TEXT NOT NULL DEFAULT '',
            additional_information TEXT NOT NULL DEFAULT '',
            process TEXT NOT NULL DEFAULT '',
            organization TEXT NOT NULL DEFAULT '',
            unit TEXT NOT NULL DEFAULT '',
            municipality TEXT NOT NULL DEFAULT '',
            uf TEXT NOT NULL DEFAULT '',
            modality_id INTEGER NOT NULL,
            modality_name TEXT NOT NULL DEFAULT '',
            status TEXT NOT NULL DEFAULT '',
            publication_date TEXT,
            global_updated_at TEXT,
            total_homologated_scaled INTEGER,
            search_text TEXT NOT NULL DEFAULT ''
        );

        CREATE INDEX IF NOT EXISTS idx_contracts_uf_publication ON contracts(uf, publication_date DESC);
        CREATE INDEX IF NOT EXISTS idx_contracts_publication ON contracts(publication_date DESC);
        CREATE INDEX IF NOT EXISTS idx_contracts_cnpj_year_sequence ON contracts(cnpj, purchase_year, purchase_sequence);

        CREATE VIRTUAL TABLE IF NOT EXISTS contracts_fts USING fts5(
            search_text,
            content='contracts',
            content_rowid='rowid',
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER IF NOT EXISTS contracts_fts_insert AFTER INSERT ON contracts BEGIN
            INSERT INTO contracts_fts(rowid, search_text) VALUES(new.rowid, new.search_text);
        END;

        CREATE TRIGGER IF NOT EXISTS contracts_fts_delete AFTER DELETE ON contracts BEGIN
            INSERT INTO contracts_fts(contracts_fts, rowid, search_text)
            VALUES('delete', old.rowid, old.search_text);
        END;

        CREATE TRIGGER IF NOT EXISTS contracts_fts_update AFTER UPDATE OF search_text ON contracts BEGIN
            INSERT INTO contracts_fts(contracts_fts, rowid, search_text)
            VALUES('delete', old.rowid, old.search_text);
            INSERT INTO contracts_fts(rowid, search_text) VALUES(new.rowid, new.search_text);
        END;

        CREATE TABLE IF NOT EXISTS items(
            contract_id TEXT NOT NULL REFERENCES contracts(pncp_id) ON DELETE CASCADE,
            item_number INTEGER NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            unit TEXT NOT NULL DEFAULT '',
            status TEXT NOT NULL DEFAULT '',
            has_result INTEGER NOT NULL DEFAULT 0,
            source_updated_at TEXT,
            hydration_status INTEGER NOT NULL DEFAULT 0,
            last_error TEXT,
            cache_updated_at TEXT,
            PRIMARY KEY(contract_id, item_number)
        );

        CREATE INDEX IF NOT EXISTS idx_items_contract_status ON items(contract_id, hydration_status, has_result);

        CREATE TABLE IF NOT EXISTS item_results(
            contract_id TEXT NOT NULL,
            item_number INTEGER NOT NULL,
            result_sequence INTEGER NOT NULL,
            supplier_tax_id TEXT NOT NULL DEFAULT '',
            supplier_name TEXT NOT NULL DEFAULT '',
            quantity_scaled INTEGER,
            unit_value_scaled INTEGER,
            total_value_scaled INTEGER,
            result_date TEXT,
            result_status_id INTEGER NOT NULL DEFAULT 0,
            result_status_name TEXT NOT NULL DEFAULT '',
            PRIMARY KEY(contract_id, item_number, result_sequence),
            FOREIGN KEY(contract_id, item_number) REFERENCES items(contract_id, item_number) ON DELETE CASCADE
        );

        CREATE TRIGGER IF NOT EXISTS contracts_mark_items_stale
        AFTER UPDATE OF global_updated_at ON contracts
        WHEN COALESCE(old.global_updated_at, '') <> COALESCE(new.global_updated_at, '')
        BEGIN
            UPDATE items SET hydration_status = 4 WHERE contract_id = new.pncp_id AND has_result = 1;
        END;

        CREATE TABLE IF NOT EXISTS sync_runs(
            id TEXT PRIMARY KEY,
            mode INTEGER NOT NULL,
            start_date TEXT NOT NULL,
            end_date TEXT NOT NULL,
            started_at TEXT NOT NULL,
            finished_at TEXT,
            status TEXT NOT NULL,
            contracts_saved INTEGER NOT NULL DEFAULT 0,
            error TEXT
        );

        CREATE TABLE IF NOT EXISTS sync_partitions(
            partition_key TEXT PRIMARY KEY,
            next_page INTEGER NOT NULL DEFAULT 1,
            completed INTEGER NOT NULL DEFAULT 0,
            updated_at TEXT NOT NULL
        );
        """;

    private const string SchemaV2Sql = """
        ALTER TABLE contracts ADD COLUMN municipality_ibge_code TEXT;
        CREATE INDEX idx_contracts_uf_municipality
            ON contracts(uf, municipality);
        CREATE INDEX idx_contracts_municipality_ibge_publication
            ON contracts(municipality_ibge_code, publication_date DESC);

        ALTER TABLE items ADD COLUMN search_text TEXT NOT NULL DEFAULT '';
        UPDATE items SET search_text = description;

        CREATE VIRTUAL TABLE items_fts USING fts5(
            search_text,
            content='items',
            content_rowid='rowid',
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER items_fts_insert AFTER INSERT ON items BEGIN
            INSERT INTO items_fts(rowid, search_text) VALUES(new.rowid, new.search_text);
        END;

        CREATE TRIGGER items_fts_delete AFTER DELETE ON items BEGIN
            INSERT INTO items_fts(items_fts, rowid, search_text)
            VALUES('delete', old.rowid, old.search_text);
        END;

        CREATE TRIGGER items_fts_update AFTER UPDATE OF search_text ON items BEGIN
            INSERT INTO items_fts(items_fts, rowid, search_text)
            VALUES('delete', old.rowid, old.search_text);
            INSERT INTO items_fts(rowid, search_text) VALUES(new.rowid, new.search_text);
        END;

        INSERT INTO items_fts(items_fts) VALUES('rebuild');

        CREATE TABLE contract_item_snapshots(
            contract_id TEXT PRIMARY KEY REFERENCES contracts(pncp_id) ON DELETE CASCADE,
            fetched_at TEXT NOT NULL,
            item_count INTEGER NOT NULL,
            source_global_updated_at TEXT
        );

        DROP TRIGGER contracts_mark_items_stale;
        CREATE TRIGGER contracts_mark_items_stale
        AFTER UPDATE OF global_updated_at ON contracts
        WHEN COALESCE(old.global_updated_at, '') <> COALESCE(new.global_updated_at, '')
        BEGIN
            UPDATE items SET hydration_status = 4 WHERE contract_id = new.pncp_id AND has_result = 1;
            DELETE FROM contract_item_snapshots WHERE contract_id = new.pncp_id;
        END;

        CREATE TABLE coverage_day_modalities(
            coverage_date TEXT NOT NULL,
            modality_id INTEGER NOT NULL,
            uf TEXT NOT NULL DEFAULT 'ALL',
            status INTEGER NOT NULL DEFAULT 0,
            records_count INTEGER,
            updated_at TEXT NOT NULL,
            last_error TEXT,
            PRIMARY KEY(coverage_date, modality_id, uf)
        );

        CREATE INDEX idx_coverage_date_status
            ON coverage_day_modalities(coverage_date, status);

        ALTER TABLE sync_partitions ADD COLUMN mode INTEGER;
        ALTER TABLE sync_partitions ADD COLUMN start_date TEXT;
        ALTER TABLE sync_partitions ADD COLUMN end_date TEXT;
        ALTER TABLE sync_partitions ADD COLUMN modality_id INTEGER;
        ALTER TABLE sync_partitions ADD COLUMN uf TEXT;
        ALTER TABLE sync_partitions ADD COLUMN total_pages INTEGER;
        ALTER TABLE sync_partitions ADD COLUMN status INTEGER;
        ALTER TABLE sync_partitions ADD COLUMN last_error TEXT;
        ALTER TABLE sync_partitions ADD COLUMN next_retry_at TEXT;
        """;

    private const string SchemaV4Sql = """
        CREATE TABLE IF NOT EXISTS item_results(
            contract_id TEXT NOT NULL,
            item_number INTEGER NOT NULL,
            result_sequence INTEGER NOT NULL,
            supplier_tax_id TEXT NOT NULL DEFAULT '',
            supplier_name TEXT NOT NULL DEFAULT '',
            quantity_scaled INTEGER,
            unit_value_scaled INTEGER,
            total_value_scaled INTEGER,
            result_date TEXT,
            result_status_id INTEGER NOT NULL DEFAULT 0,
            result_status_name TEXT NOT NULL DEFAULT '',
            PRIMARY KEY(contract_id, item_number, result_sequence),
            FOREIGN KEY(contract_id, item_number) REFERENCES items(contract_id, item_number) ON DELETE CASCADE
        );

        ALTER TABLE items ADD COLUMN requested_quantity_scaled INTEGER;
        ALTER TABLE items ADD COLUMN additional_information TEXT NOT NULL DEFAULT '';
        ALTER TABLE items ADD COLUMN item_category TEXT NOT NULL DEFAULT '';
        ALTER TABLE items ADD COLUMN ncm_nbs_code TEXT NOT NULL DEFAULT '';
        ALTER TABLE items ADD COLUMN ncm_nbs_description TEXT NOT NULL DEFAULT '';
        ALTER TABLE items ADD COLUMN catalog_code TEXT NOT NULL DEFAULT '';
        ALTER TABLE items ADD COLUMN catalog_name TEXT NOT NULL DEFAULT '';
        ALTER TABLE items ADD COLUMN catalog_category TEXT NOT NULL DEFAULT '';

        ALTER TABLE item_results ADD COLUMN supplier_type TEXT NOT NULL DEFAULT '';
        ALTER TABLE item_results ADD COLUMN supplier_municipality TEXT NOT NULL DEFAULT '';
        ALTER TABLE item_results ADD COLUMN supplier_uf TEXT NOT NULL DEFAULT '';

        CREATE TABLE quotation_projects(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE quotation_lines(
            id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES quotation_projects(id) ON DELETE CASCADE,
            description TEXT NOT NULL,
            requested_quantity_scaled INTEGER NOT NULL,
            requested_unit TEXT NOT NULL,
            minimum_unit_price_scaled INTEGER,
            maximum_unit_price_scaled INTEGER,
            sample_version INTEGER NOT NULL DEFAULT 1,
            sampled_at TEXT NOT NULL,
            selected_basket_key TEXT,
            selection_confirmed INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX idx_quotation_lines_project ON quotation_lines(project_id, sampled_at);

        CREATE TABLE quotation_references(
            id TEXT NOT NULL,
            line_id TEXT NOT NULL REFERENCES quotation_lines(id) ON DELETE CASCADE,
            contract_id TEXT NOT NULL,
            item_number INTEGER NOT NULL,
            result_sequence INTEGER NOT NULL,
            supplier_name TEXT NOT NULL,
            supplier_tax_id TEXT NOT NULL,
            supplier_type TEXT NOT NULL,
            homologated_quantity_scaled INTEGER,
            unit_price_scaled INTEGER NOT NULL,
            result_date TEXT,
            item_description TEXT NOT NULL,
            item_additional_information TEXT NOT NULL,
            item_unit TEXT NOT NULL,
            item_requested_quantity_scaled INTEGER,
            item_category TEXT NOT NULL,
            ncm_nbs_code TEXT NOT NULL,
            ncm_nbs_description TEXT NOT NULL,
            catalog_code TEXT NOT NULL,
            catalog_name TEXT NOT NULL,
            catalog_category TEXT NOT NULL,
            organization TEXT NOT NULL,
            municipality TEXT NOT NULL,
            uf TEXT NOT NULL,
            distance_ribeirao_km REAL,
            publication_date TEXT,
            portal_url TEXT NOT NULL,
            description_score_scaled INTEGER NOT NULL,
            unit_score_scaled INTEGER NOT NULL,
            quantity_score_scaled INTEGER NOT NULL,
            proximity_score_scaled INTEGER NOT NULL,
            recency_score_scaled INTEGER NOT NULL,
            explanation TEXT NOT NULL,
            state INTEGER NOT NULL,
            state_reason TEXT NOT NULL,
            duplicate_of_reference_id TEXT,
            PRIMARY KEY(line_id, id)
        );

        CREATE INDEX idx_quotation_references_line_state
            ON quotation_references(line_id, state, unit_price_scaled);
        """;

    private const string SchemaV5Sql = """
        ALTER TABLE quotation_lines ADD COLUMN description_weight INTEGER NOT NULL DEFAULT 50;
        ALTER TABLE quotation_lines ADD COLUMN unit_weight INTEGER NOT NULL DEFAULT 20;
        ALTER TABLE quotation_lines ADD COLUMN quantity_weight INTEGER NOT NULL DEFAULT 10;
        ALTER TABLE quotation_lines ADD COLUMN proximity_weight INTEGER NOT NULL DEFAULT 15;
        ALTER TABLE quotation_lines ADD COLUMN recency_weight INTEGER NOT NULL DEFAULT 5;
        """;

    private const string SchemaV6Sql = """
        ALTER TABLE contracts ADD COLUMN distance_from_ribeirao_km REAL;
        ALTER TABLE contracts ADD COLUMN municipality_distance_rank INTEGER;
        ALTER TABLE contracts ADD COLUMN state_proximity_rank INTEGER;
        ALTER TABLE contracts ADD COLUMN geo_layer INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE contracts ADD COLUMN random_order_key INTEGER NOT NULL DEFAULT 0;

        UPDATE contracts
           SET distance_from_ribeirao_km = pncp_geo_distance(municipality_ibge_code, municipality, uf),
               municipality_distance_rank = pncp_geo_distance_rank(municipality_ibge_code, municipality, uf),
               state_proximity_rank = pncp_state_rank(uf),
               geo_layer = pncp_geo_layer(municipality_ibge_code, municipality, uf),
               random_order_key = pncp_random_key(pncp_id);

        CREATE INDEX idx_contracts_geographic_sample
            ON contracts(geo_layer, state_proximity_rank, municipality_distance_rank, random_order_key, pncp_id);
        """;
}
