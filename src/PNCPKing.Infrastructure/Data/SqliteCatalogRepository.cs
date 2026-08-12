using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Data;

public sealed class SqliteCatalogRepository : ICatalogRepository
{
    private readonly ISqliteConnectionFactory _connections;
    private readonly IPerformanceTelemetry _performance;

    public SqliteCatalogRepository(
        string databasePath,
        IPerformanceTelemetry? performance = null)
        : this(new SqliteConnectionFactory(databasePath), performance)
    {
    }

    public SqliteCatalogRepository(
        ISqliteConnectionFactory connections,
        IPerformanceTelemetry? performance = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _performance = performance ?? NullPerformanceTelemetry.Instance;
    }

    public async Task<CatalogSyncState> GetSyncStateAsync(
        CatalogKind kind,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SyncStateSelect + " WHERE catalog_kind = $kind;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSyncState(reader)
            : new CatalogSyncState { Kind = kind, Status = CatalogSyncStatus.Missing };
    }

    public async Task<IReadOnlyList<CatalogSyncState>> GetSyncStatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SyncStateSelect + " ORDER BY catalog_kind;";
        var result = new List<CatalogSyncState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadSyncState(reader));
        }

        return result;
    }

    public async Task BeginSyncAsync(
        CatalogKind kind,
        string generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM catalog_entries_stage WHERE catalog_kind = $kind;";
            clear.Parameters.AddWithValue("$kind", (int)kind);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = (SqliteTransaction)transaction;
            state.CommandText = """
                UPDATE catalog_sync_state
                   SET status = $status, generation = $generation, next_page = 1,
                       total_pages = 0, total_records = 0, staged_records = 0,
                       started_at = $started, last_error = ''
                 WHERE catalog_kind = $kind;
                """;
            state.Parameters.AddWithValue("$status", (int)CatalogSyncStatus.Downloading);
            state.Parameters.AddWithValue("$generation", generation);
            state.Parameters.AddWithValue("$started", FormatDateTime(DateTimeOffset.UtcNow));
            state.Parameters.AddWithValue("$kind", (int)kind);
            await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StagePageAsync(
        CatalogPage page,
        string generation,
        CancellationToken cancellationToken = default)
    {
        using var span = _performance.Begin("catalog", "stage-page");
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO catalog_entries_stage(
                generation, catalog_kind, code, description,
                level1_code, level1_name, level2_code, level2_name,
                level3_code, level3_name, level4_code, level4_name,
                level5_code, level5_name, ncm_code, sustainable,
                exclusive_central, remote_updated_at, search_text)
            VALUES($generation, $kind, $code, $description,
                   $level1Code, $level1Name, $level2Code, $level2Name,
                   $level3Code, $level3Name, $level4Code, $level4Name,
                   $level5Code, $level5Name, $ncm, $sustainable,
                   $exclusive, $updated, $searchText)
            ON CONFLICT(generation, catalog_kind, code) DO UPDATE SET
                description = excluded.description,
                level1_code = excluded.level1_code, level1_name = excluded.level1_name,
                level2_code = excluded.level2_code, level2_name = excluded.level2_name,
                level3_code = excluded.level3_code, level3_name = excluded.level3_name,
                level4_code = excluded.level4_code, level4_name = excluded.level4_name,
                level5_code = excluded.level5_code, level5_name = excluded.level5_name,
                ncm_code = excluded.ncm_code, sustainable = excluded.sustainable,
                exclusive_central = excluded.exclusive_central,
                remote_updated_at = excluded.remote_updated_at,
                search_text = excluded.search_text;
            """;
        AddEntryParameters(insert);
        foreach (var entry in page.Entries)
        {
            insert.Parameters["$generation"].Value = generation;
            BindEntry(insert, entry);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var state = connection.CreateCommand();
        state.Transaction = (SqliteTransaction)transaction;
        state.CommandText = """
            UPDATE catalog_sync_state
               SET status = $status, next_page = $nextPage,
                   total_pages = $totalPages, total_records = $totalRecords,
                   staged_records = MIN($totalRecords, staged_records + $pageRecords),
                   last_error = ''
             WHERE catalog_kind = $kind AND generation = $generation;
            """;
        state.Parameters.AddWithValue("$status", (int)CatalogSyncStatus.Downloading);
        state.Parameters.AddWithValue("$nextPage", page.Page + 1);
        state.Parameters.AddWithValue("$totalPages", page.TotalPages);
        state.Parameters.AddWithValue("$totalRecords", page.TotalRecords);
        state.Parameters.AddWithValue("$pageRecords", page.Entries.Count);
        state.Parameters.AddWithValue("$generation", generation);
        state.Parameters.AddWithValue("$kind", (int)page.Kind);
        if (await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("O checkpoint do catálogo pertence a outra geração.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        span.Complete(page.Entries.Count);
    }

    public async Task PublishAsync(
        CatalogKind kind,
        string generation,
        CancellationToken cancellationToken = default)
    {
        using var span = _performance.Begin("catalog", "publish");
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long staged;
        long expected;
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = (SqliteTransaction)transaction;
            count.CommandText = """
                SELECT (SELECT COUNT(*) FROM catalog_entries_stage
                         WHERE generation = $generation AND catalog_kind = $kind),
                       total_records
                  FROM catalog_sync_state
                 WHERE catalog_kind = $kind AND generation = $generation;
                """;
            count.Parameters.AddWithValue("$generation", generation);
            count.Parameters.AddWithValue("$kind", (int)kind);
            await using var reader = await count.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("A geração do catálogo não está disponível para publicação.");
            }

            staged = reader.GetInt64(0);
            expected = reader.GetInt64(1);
        }

        if (expected > 0 && staged != expected)
        {
            throw new InvalidDataException(
                $"A API informou {expected:N0} registro(s), mas a carga contém {staged:N0} código(s) distintos.");
        }

        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = (SqliteTransaction)transaction;
            deactivate.CommandText = "UPDATE catalog_entries SET active = 0 WHERE catalog_kind = $kind AND active = 1;";
            deactivate.Parameters.AddWithValue("$kind", (int)kind);
            await deactivate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var publish = connection.CreateCommand())
        {
            publish.Transaction = (SqliteTransaction)transaction;
            publish.CommandText = """
                INSERT INTO catalog_entries(
                    catalog_kind, code, description, active,
                    level1_code, level1_name, level2_code, level2_name,
                    level3_code, level3_name, level4_code, level4_name,
                    level5_code, level5_name, ncm_code, sustainable,
                    exclusive_central, remote_updated_at, search_text)
                SELECT catalog_kind, code, description, 1,
                       level1_code, level1_name, level2_code, level2_name,
                       level3_code, level3_name, level4_code, level4_name,
                       level5_code, level5_name, ncm_code, sustainable,
                       exclusive_central, remote_updated_at, search_text
                  FROM catalog_entries_stage
                 WHERE generation = $generation AND catalog_kind = $kind
                ON CONFLICT(catalog_kind, code) DO UPDATE SET
                    description = excluded.description, active = 1,
                    level1_code = excluded.level1_code, level1_name = excluded.level1_name,
                    level2_code = excluded.level2_code, level2_name = excluded.level2_name,
                    level3_code = excluded.level3_code, level3_name = excluded.level3_name,
                    level4_code = excluded.level4_code, level4_name = excluded.level4_name,
                    level5_code = excluded.level5_code, level5_name = excluded.level5_name,
                    ncm_code = excluded.ncm_code, sustainable = excluded.sustainable,
                    exclusive_central = excluded.exclusive_central,
                    remote_updated_at = excluded.remote_updated_at,
                    search_text = excluded.search_text
                WHERE catalog_entries.description <> excluded.description
                   OR catalog_entries.active <> 1
                   OR catalog_entries.level1_code <> excluded.level1_code
                   OR catalog_entries.level1_name <> excluded.level1_name
                   OR catalog_entries.level2_code <> excluded.level2_code
                   OR catalog_entries.level2_name <> excluded.level2_name
                   OR catalog_entries.level3_code <> excluded.level3_code
                   OR catalog_entries.level3_name <> excluded.level3_name
                   OR catalog_entries.level4_code <> excluded.level4_code
                   OR catalog_entries.level4_name <> excluded.level4_name
                   OR catalog_entries.level5_code <> excluded.level5_code
                   OR catalog_entries.level5_name <> excluded.level5_name
                   OR catalog_entries.ncm_code <> excluded.ncm_code
                   OR catalog_entries.sustainable <> excluded.sustainable
                   OR catalog_entries.exclusive_central <> excluded.exclusive_central
                   OR COALESCE(catalog_entries.remote_updated_at, '') <>
                      COALESCE(excluded.remote_updated_at, '')
                   OR catalog_entries.search_text <> excluded.search_text;
                """;
            publish.Parameters.AddWithValue("$generation", generation);
            publish.Parameters.AddWithValue("$kind", (int)kind);
            await publish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = (SqliteTransaction)transaction;
            state.CommandText = """
                UPDATE catalog_sync_state
                   SET status = $status, next_page = total_pages + 1,
                       staged_records = $records, active_records = $records,
                       completed_at = $completed, last_error = ''
                 WHERE catalog_kind = $kind AND generation = $generation;
                DELETE FROM catalog_entries_stage
                 WHERE generation = $generation AND catalog_kind = $kind;
                """;
            state.Parameters.AddWithValue("$status", (int)CatalogSyncStatus.Complete);
            state.Parameters.AddWithValue("$records", staged);
            state.Parameters.AddWithValue("$completed", FormatDateTime(DateTimeOffset.UtcNow));
            state.Parameters.AddWithValue("$kind", (int)kind);
            state.Parameters.AddWithValue("$generation", generation);
            await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        span.Complete(staged);
    }

    public async Task MarkFailedAsync(
        CatalogKind kind,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE catalog_sync_state
               SET status = $status, last_error = $error
             WHERE catalog_kind = $kind;
            """;
        command.Parameters.AddWithValue("$status", (int)CatalogSyncStatus.Failed);
        command.Parameters.AddWithValue("$error", error ?? string.Empty);
        command.Parameters.AddWithValue("$kind", (int)kind);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CatalogEntry>> FindCandidatesAsync(
        CatalogSearchQuery query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        limit = Math.Clamp(limit, 1, 5000);
        var tokens = SearchText.Normalize(query.Text)
            .Split(new[] { ' ', ',', '.', ';', ':', '/', '\\', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length > 1 || token.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var descriptionIndex = await GetDescriptionIndexProgressAsync(cancellationToken).ConfigureAwait(false);
        var useDescriptionIndex = tokens.Length > 0 && descriptionIndex.Completed;
        var filters = BuildEntryFilters(command, query, tokens.Length > 0);
        if (tokens.Length == 0)
        {
            command.CommandText = EntrySelect + $" WHERE {filters} ORDER BY remote_updated_at DESC, code LIMIT $limit;";
        }
        else
        {
            command.CommandText = (useDescriptionIndex ? EntrySelectWithDescriptionFts : EntrySelectWithFts) +
                $" WHERE {(useDescriptionIndex ? "catalog_description_fts" : "catalog_entries_fts")} MATCH $match AND {filters} " +
                $"ORDER BY bm25({(useDescriptionIndex ? "catalog_description_fts" : "catalog_entries_fts")}), e.remote_updated_at DESC, e.code LIMIT $limit;";
            command.Parameters.AddWithValue(
                "$match",
                string.Join(" OR ", tokens.Select(token => $"\"{token.Replace("\"", "\"\"")}\"*")));
        }

        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<CatalogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadEntry(reader));
        }

        return result;
    }

    public async Task<CatalogDescriptionIndexProgress> GetDescriptionIndexProgressAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT indexed_rowid, target_rowid, completed
              FROM catalog_description_index_state
             WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CatalogDescriptionIndexProgress(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) != 0)
            : new CatalogDescriptionIndexProgress(0, 0, true);
    }

    public async Task<CatalogDescriptionIndexProgress> BuildDescriptionIndexBatchAsync(
        int batchSize = 2000,
        CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 10_000);
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long indexed;
        long target;
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = (SqliteTransaction)transaction;
            state.CommandText = "SELECT indexed_rowid, target_rowid FROM catalog_description_index_state WHERE id = 1;";
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new CatalogDescriptionIndexProgress(0, 0, true);
            }

            indexed = reader.GetInt64(0);
            target = reader.GetInt64(1);
        }

        long batchEnd;
        await using (var findEnd = connection.CreateCommand())
        {
            findEnd.Transaction = (SqliteTransaction)transaction;
            findEnd.CommandText = """
                SELECT COALESCE(MAX(rowid), $indexed)
                  FROM (SELECT rowid FROM catalog_entries
                         WHERE rowid > $indexed AND rowid <= $target
                         ORDER BY rowid LIMIT $limit);
                """;
            findEnd.Parameters.AddWithValue("$indexed", indexed);
            findEnd.Parameters.AddWithValue("$target", target);
            findEnd.Parameters.AddWithValue("$limit", batchSize);
            batchEnd = Convert.ToInt64(
                await findEnd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        if (batchEnd == indexed && indexed < target)
        {
            batchEnd = target;
        }

        if (batchEnd > indexed)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO catalog_description_fts(rowid, description)
                SELECT rowid, description FROM catalog_entries
                 WHERE rowid > $indexed AND rowid <= $batchEnd
                 ORDER BY rowid;
                """;
            insert.Parameters.AddWithValue("$indexed", indexed);
            insert.Parameters.AddWithValue("$batchEnd", batchEnd);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var completed = batchEnd >= target;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE catalog_description_index_state
                   SET indexed_rowid = $indexed, completed = $completed,
                       updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                 WHERE id = 1;
                """;
            update.Parameters.AddWithValue("$indexed", batchEnd);
            update.Parameters.AddWithValue("$completed", completed ? 1 : 0);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CatalogDescriptionIndexProgress(batchEnd, target, completed);
    }

    public async Task<CatalogEntry?> GetEntryAsync(
        CatalogKind kind,
        string code,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = EntrySelect + " WHERE catalog_kind = $kind AND code = $code LIMIT 1;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$code", code.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    public async Task<IReadOnlyList<CatalogHierarchyPath>> GetHierarchyAsync(
        CatalogKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT catalog_kind,
                   level1_code, level1_name, level2_code, level2_name,
                   level3_code, level3_name, level4_code, level4_name,
                   level5_code, level5_name
              FROM catalog_entries
             WHERE active = 1 AND ($kind IS NULL OR catalog_kind = $kind)
             ORDER BY catalog_kind, level1_name, level2_name, level3_name, level4_name, level5_name;
            """;
        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (int)kind.Value);
        var result = new List<CatalogHierarchyPath>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CatalogHierarchyPath(
                (CatalogKind)reader.GetInt32(0),
                reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8),
                reader.GetString(9), reader.GetString(10)));
        }

        return result;
    }

    public async Task<IReadOnlyList<CatalogHierarchyChild>> GetHierarchyChildrenAsync(
        CatalogKind kind,
        CatalogHierarchyFilter? parent = null,
        CancellationToken cancellationToken = default)
    {
        parent ??= new CatalogHierarchyFilter();
        var parentCodes = new[]
        {
            parent.Level1Code,
            parent.Level2Code,
            parent.Level3Code,
            parent.Level4Code,
            parent.Level5Code
        };
        var parentLevel = Array.FindLastIndex(parentCodes, code => !string.IsNullOrWhiteSpace(code)) + 1;
        var childLevel = parentLevel + 1;
        if (childLevel > 5)
        {
            return [];
        }

        var filters = new List<string> { "active = 1", "catalog_kind = $kind" };
        for (var index = 0; index < parentLevel; index++)
        {
            filters.Add($"level{index + 1}_code = $level{index + 1}");
        }

        var nextLevel = childLevel < 5 ? childLevel + 1 : 0;
        var hasChildrenSql = nextLevel == 0
            ? "0"
            : $"MAX(CASE WHEN COALESCE(level{nextLevel}_code, '') <> '' OR COALESCE(level{nextLevel}_name, '') <> '' THEN 1 ELSE 0 END)";
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT level{childLevel}_code, level{childLevel}_name, {hasChildrenSql}
              FROM catalog_entries
             WHERE {string.Join(" AND ", filters)}
               AND (COALESCE(level{childLevel}_code, '') <> '' OR COALESCE(level{childLevel}_name, '') <> '')
             GROUP BY level{childLevel}_code, level{childLevel}_name
             ORDER BY level{childLevel}_name, level{childLevel}_code;
            """;
        command.Parameters.AddWithValue("$kind", (int)kind);
        for (var index = 0; index < parentLevel; index++)
        {
            command.Parameters.AddWithValue($"$level{index + 1}", parentCodes[index]);
        }

        var result = new List<CatalogHierarchyChild>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var code = reader.GetString(0);
            var codes = (string[])parentCodes.Clone();
            codes[childLevel - 1] = code;
            result.Add(new CatalogHierarchyChild(
                kind,
                childLevel,
                code,
                reader.GetString(1),
                new CatalogHierarchyFilter(codes[0], codes[1], codes[2], codes[3], codes[4]),
                reader.GetInt32(2) != 0));
        }

        return result;
    }

    public async Task<IReadOnlyList<CatalogEquivalenceRule>> GetEquivalenceRulesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, rule_kind, canonical, alias, dimension, factor, is_default
              FROM catalog_equivalence_rules
             ORDER BY rule_kind, dimension, canonical, alias;
            """;
        var result = new List<CatalogEquivalenceRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadRule(reader));
        }

        return result;
    }

    public async Task SaveEquivalenceRuleAsync(
        CatalogEquivalenceRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Canonical) || string.IsNullOrWhiteSpace(rule.Alias) || rule.Factor <= 0)
        {
            throw new ArgumentException("Informe regra, termo canônico, alias e fator positivo.", nameof(rule));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO catalog_equivalence_rules(
                id, rule_kind, canonical, alias, dimension, factor, is_default)
            VALUES($id, $kind, $canonical, $alias, $dimension, $factor, $default)
            ON CONFLICT(id) DO UPDATE SET
                rule_kind = excluded.rule_kind, canonical = excluded.canonical,
                alias = excluded.alias, dimension = excluded.dimension,
                factor = excluded.factor, is_default = excluded.is_default;
            """;
        command.Parameters.AddWithValue("$id", rule.Id.ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)rule.Kind);
        command.Parameters.AddWithValue("$canonical", NormalizeRuleText(rule.Canonical));
        command.Parameters.AddWithValue("$alias", NormalizeRuleText(rule.Alias));
        command.Parameters.AddWithValue("$dimension", rule.Dimension.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$factor", (double)rule.Factor);
        command.Parameters.AddWithValue("$default", rule.IsDefault ? 1 : 0);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ArgumentException("Esse alias já pertence a outra regra.", nameof(rule), exception);
        }
    }

    public async Task DeleteEquivalenceRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM catalog_equivalence_rules WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetDefaultEquivalenceRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM catalog_equivalence_rules WHERE is_default = 1;";
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var rule in DefaultRules())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO catalog_equivalence_rules(
                    id, rule_kind, canonical, alias, dimension, factor, is_default)
                VALUES($id, $kind, $canonical, $alias, $dimension, $factor, 1)
                ON CONFLICT(alias) DO UPDATE SET
                    id = excluded.id, rule_kind = excluded.rule_kind,
                    canonical = excluded.canonical, dimension = excluded.dimension,
                    factor = excluded.factor, is_default = 1;
                """;
            command.Parameters.AddWithValue("$id", rule.Id.ToString("N"));
            command.Parameters.AddWithValue("$kind", (int)rule.Kind);
            command.Parameters.AddWithValue("$canonical", rule.Canonical);
            command.Parameters.AddWithValue("$alias", rule.Alias);
            command.Parameters.AddWithValue("$dimension", rule.Dimension);
            command.Parameters.AddWithValue("$factor", (double)rule.Factor);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildEntryFilters(SqliteCommand command, CatalogSearchQuery query, bool useAlias)
    {
        var prefix = useAlias ? "e." : string.Empty;
        var filters = new List<string> { $"{prefix}active = 1" };
        if (query.Kind is { } kind)
        {
            filters.Add($"{prefix}catalog_kind = $kind");
            command.Parameters.AddWithValue("$kind", (int)kind);
        }

        var hierarchy = query.Hierarchy;
        foreach (var (value, column, parameter) in new[]
                 {
                     (hierarchy?.Level1Code, "level1_code", "$level1"),
                     (hierarchy?.Level2Code, "level2_code", "$level2"),
                     (hierarchy?.Level3Code, "level3_code", "$level3"),
                     (hierarchy?.Level4Code, "level4_code", "$level4"),
                     (hierarchy?.Level5Code, "level5_code", "$level5")
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            filters.Add($"{prefix}{column} = {parameter}");
            command.Parameters.AddWithValue(parameter, value);
        }

        return string.Join(" AND ", filters);
    }

    private static void AddEntryParameters(SqliteCommand command)
    {
        foreach (var name in new[]
                 {
                     "$generation", "$kind", "$code", "$description", "$level1Code", "$level1Name",
                     "$level2Code", "$level2Name", "$level3Code", "$level3Name", "$level4Code",
                     "$level4Name", "$level5Code", "$level5Name", "$ncm", "$sustainable", "$exclusive",
                     "$updated", "$searchText"
                 })
        {
            command.Parameters.Add(new SqliteParameter(name, null));
        }
    }

    private static void BindEntry(SqliteCommand command, CatalogEntry entry)
    {
        command.Parameters["$kind"].Value = (int)entry.Kind;
        command.Parameters["$code"].Value = entry.Code;
        command.Parameters["$description"].Value = entry.Description;
        command.Parameters["$level1Code"].Value = entry.Level1Code;
        command.Parameters["$level1Name"].Value = entry.Level1Name;
        command.Parameters["$level2Code"].Value = entry.Level2Code;
        command.Parameters["$level2Name"].Value = entry.Level2Name;
        command.Parameters["$level3Code"].Value = entry.Level3Code;
        command.Parameters["$level3Name"].Value = entry.Level3Name;
        command.Parameters["$level4Code"].Value = entry.Level4Code;
        command.Parameters["$level4Name"].Value = entry.Level4Name;
        command.Parameters["$level5Code"].Value = entry.Level5Code;
        command.Parameters["$level5Name"].Value = entry.Level5Name;
        command.Parameters["$ncm"].Value = entry.NcmCode;
        command.Parameters["$sustainable"].Value = entry.Sustainable ? 1 : 0;
        command.Parameters["$exclusive"].Value = entry.ExclusiveCentralPurchasing ? 1 : 0;
        command.Parameters["$updated"].Value = entry.RemoteUpdatedAt is null
            ? DBNull.Value
            : FormatDateTime(entry.RemoteUpdatedAt.Value);
        command.Parameters["$searchText"].Value = entry.SearchText;
    }

    private static CatalogEntry ReadEntry(SqliteDataReader reader) => new()
    {
        Kind = (CatalogKind)reader.GetInt32(0), Code = reader.GetString(1), Description = reader.GetString(2),
        Active = reader.GetInt64(3) == 1,
        Level1Code = reader.GetString(4), Level1Name = reader.GetString(5),
        Level2Code = reader.GetString(6), Level2Name = reader.GetString(7),
        Level3Code = reader.GetString(8), Level3Name = reader.GetString(9),
        Level4Code = reader.GetString(10), Level4Name = reader.GetString(11),
        Level5Code = reader.GetString(12), Level5Name = reader.GetString(13),
        NcmCode = reader.GetString(14), Sustainable = reader.GetInt64(15) == 1,
        ExclusiveCentralPurchasing = reader.GetInt64(16) == 1,
        RemoteUpdatedAt = reader.IsDBNull(17) ? null : ParseDateTime(reader.GetString(17)),
        SearchText = reader.GetString(18)
    };

    private static CatalogSyncState ReadSyncState(SqliteDataReader reader) => new()
    {
        Kind = (CatalogKind)reader.GetInt32(0), Status = (CatalogSyncStatus)reader.GetInt32(1),
        Generation = reader.GetString(2), NextPage = reader.GetInt32(3), TotalPages = reader.GetInt32(4),
        TotalRecords = reader.GetInt64(5), StagedRecords = reader.GetInt64(6), ActiveRecords = reader.GetInt64(7),
        StartedAt = reader.IsDBNull(8) ? null : ParseDateTime(reader.GetString(8)),
        CompletedAt = reader.IsDBNull(9) ? null : ParseDateTime(reader.GetString(9)),
        LastError = reader.GetString(10)
    };

    private static CatalogEquivalenceRule ReadRule(SqliteDataReader reader) => new()
    {
        Id = Guid.ParseExact(reader.GetString(0), "N"), Kind = (CatalogRuleKind)reader.GetInt32(1),
        Canonical = reader.GetString(2), Alias = reader.GetString(3), Dimension = reader.GetString(4),
        Factor = Convert.ToDecimal(reader.GetDouble(5), CultureInfo.InvariantCulture), IsDefault = reader.GetInt64(6) == 1
    };

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        return await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeRuleText(string value) => value.Trim().ToUpperInvariant();
    private static string FormatDateTime(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDateTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static IReadOnlyList<CatalogEquivalenceRule> DefaultRules()
    {
        var values = new (string Canonical, string Alias, CatalogRuleKind Kind, string Dimension, decimal Factor)[]
        {
            ("POL", "\"", CatalogRuleKind.UnitConversion, "length", 25.4m),
            ("POL", "POL", CatalogRuleKind.UnitConversion, "length", 25.4m),
            ("POL", "POLEGADA", CatalogRuleKind.UnitConversion, "length", 25.4m),
            ("MM", "MM", CatalogRuleKind.UnitConversion, "length", 1m),
            ("CM", "CM", CatalogRuleKind.UnitConversion, "length", 10m),
            ("M", "M", CatalogRuleKind.UnitConversion, "length", 1000m),
            ("MG", "MG", CatalogRuleKind.UnitConversion, "mass", .001m),
            ("G", "G", CatalogRuleKind.UnitConversion, "mass", 1m),
            ("KG", "KG", CatalogRuleKind.UnitConversion, "mass", 1000m),
            ("ML", "ML", CatalogRuleKind.UnitConversion, "volume", 1m),
            ("L", "L", CatalogRuleKind.UnitConversion, "volume", 1000m),
            ("MM2", "MM2", CatalogRuleKind.UnitConversion, "area", 1m),
            ("CM2", "CM2", CatalogRuleKind.UnitConversion, "area", 100m),
            ("M2", "M2", CatalogRuleKind.UnitConversion, "area", 1_000_000m),
            ("UNIDADE", "UN", CatalogRuleKind.Alias, "", 1m),
            ("UNIDADE", "UND", CatalogRuleKind.Alias, "", 1m),
            ("UNIDADE", "UNIDADE", CatalogRuleKind.Alias, "", 1m),
            ("CAIXA", "CX", CatalogRuleKind.Alias, "", 1m),
            ("CAIXA", "CAIXA", CatalogRuleKind.Alias, "", 1m),
            ("PACOTE", "PCT", CatalogRuleKind.Alias, "", 1m),
            ("PACOTE", "PACOTE", CatalogRuleKind.Alias, "", 1m),
            ("ROLO", "RL", CatalogRuleKind.Alias, "", 1m),
            ("ROLO", "ROLO", CatalogRuleKind.Alias, "", 1m),
            ("JOGO", "JG", CatalogRuleKind.Alias, "", 1m),
            ("JOGO", "JOGO", CatalogRuleKind.Alias, "", 1m)
        };
        return values.Select((value, index) => new CatalogEquivalenceRule
        {
            Id = Guid.ParseExact($"{index + 1:x32}", "N"), Kind = value.Kind,
            Canonical = value.Canonical, Alias = value.Alias, Dimension = value.Dimension,
            Factor = value.Factor, IsDefault = true
        }).ToArray();
    }

    private const string SyncStateSelect = """
        SELECT catalog_kind, status, generation, next_page, total_pages,
               total_records, staged_records, active_records,
               started_at, completed_at, last_error
          FROM catalog_sync_state
        """;

    private const string EntryColumns = """
        catalog_kind, code, description, active,
        level1_code, level1_name, level2_code, level2_name,
        level3_code, level3_name, level4_code, level4_name,
        level5_code, level5_name, ncm_code, sustainable,
        exclusive_central, remote_updated_at, search_text
        """;
    private const string EntrySelect = "SELECT " + EntryColumns + " FROM catalog_entries";
    private const string EntrySelectWithFts = """
        SELECT e.catalog_kind, e.code, e.description, e.active,
               e.level1_code, e.level1_name, e.level2_code, e.level2_name,
               e.level3_code, e.level3_name, e.level4_code, e.level4_name,
               e.level5_code, e.level5_name, e.ncm_code, e.sustainable,
               e.exclusive_central, e.remote_updated_at, e.search_text
          FROM catalog_entries_fts
          JOIN catalog_entries e ON e.rowid = catalog_entries_fts.rowid
        """;
    private const string EntrySelectWithDescriptionFts = """
        SELECT e.catalog_kind, e.code, e.description, e.active,
               e.level1_code, e.level1_name, e.level2_code, e.level2_name,
               e.level3_code, e.level3_name, e.level4_code, e.level4_name,
               e.level5_code, e.level5_name, e.ncm_code, e.sustainable,
               e.exclusive_central, e.remote_updated_at, e.search_text
          FROM catalog_description_fts
          JOIN catalog_entries e ON e.rowid = catalog_description_fts.rowid
        """;
}
