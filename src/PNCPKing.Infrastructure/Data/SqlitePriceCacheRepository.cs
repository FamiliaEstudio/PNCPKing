using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Data;

public sealed class SqlitePriceCacheRepository : IPriceCacheRepository
{
    private const long MinimumBytesPerContract = 14_000;
    private const long MaximumBytesPerContract = 28_000;
    private const long MinimumSafetyReserve = 2L * 1024 * 1024 * 1024;
    private readonly ISqliteConnectionFactory _connections;
    private readonly string _databasePath;
    private readonly IPerformanceTelemetry _performance;

    public SqlitePriceCacheRepository(
        string databasePath,
        IPerformanceTelemetry? performance = null)
        : this(new SqliteConnectionFactory(databasePath), performance)
    {
    }

    public SqlitePriceCacheRepository(
        ISqliteConnectionFactory connections,
        IPerformanceTelemetry? performance = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _databasePath = connections.DatabasePath;
        _performance = performance ?? NullPerformanceTelemetry.Instance;
    }

    public async Task<PriceCachePolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT authorized, enabled, paused, status, window_start, window_end,
                   authorized_at, last_started_at, last_completed_at, last_error
              FROM price_cache_control WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new PriceCachePolicy();
        }

        return new PriceCachePolicy
        {
            Authorized = reader.GetInt64(0) == 1,
            Enabled = reader.GetInt64(1) == 1,
            Paused = reader.GetInt64(2) == 1,
            Status = (PriceCacheStatus)reader.GetInt32(3),
            WindowStart = ParseDate(reader, 4),
            WindowEnd = ParseDate(reader, 5),
            AuthorizedAt = ParseDateTime(reader, 6),
            LastStartedAt = ParseDateTime(reader, 7),
            LastCompletedAt = ParseDateTime(reader, 8),
            LastError = reader.GetString(9)
        };
    }

    public async Task<PriceCacheEstimate> EstimateAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (startDate > endDate)
        {
            throw new ArgumentException("A data inicial deve ser anterior ou igual à final.");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long contracts;
        long complete;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*),
                       SUM(CASE WHEN pc.status = $complete THEN 1 ELSE 0 END)
                  FROM contracts c
                  LEFT JOIN price_cache_contracts pc ON pc.contract_id = c.pncp_id
                 WHERE c.publication_date >= $start
                   AND c.publication_date < $endExclusive;
                """;
            command.Parameters.AddWithValue("$complete", (int)PriceCacheContractStatus.Complete);
            command.Parameters.AddWithValue("$start", FormatDate(startDate));
            command.Parameters.AddWithValue("$endExclusive", FormatDate(endDate.AddDays(1)));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            contracts = reader.GetInt64(0);
            complete = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        }

        var remaining = Math.Max(0, contracts - complete);
        var minimumBytes = checked(remaining * MinimumBytesPerContract);
        var maximumBytes = checked(remaining * MaximumBytesPerContract);
        var reserve = Math.Max(MinimumSafetyReserve, (long)Math.Ceiling(maximumBytes * 0.20d));
        long available;
        try
        {
            var root = Path.GetPathRoot(_databasePath) ?? _databasePath;
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            available = long.MaxValue;
        }

        return new PriceCacheEstimate
        {
            StartDate = startDate,
            EndDate = endDate,
            ContractCount = contracts,
            AlreadyCompleteContracts = complete,
            EstimatedMinimumBytes = minimumBytes,
            EstimatedMaximumBytes = maximumBytes,
            AvailableFreeBytes = available,
            SafetyReserveBytes = reserve,
            EstimatedMinimumDuration = TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds, remaining * 4d)),
            EstimatedMaximumDuration = TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds, remaining * 24d))
        };
    }

    public async Task SetAuthorizationAsync(
        bool authorized,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE price_cache_control
               SET authorized = $authorized,
                   enabled = $authorized,
                   paused = 0,
                   status = $status,
                   window_start = $start,
                   window_end = $end,
                   authorized_at = CASE WHEN $authorized = 1 THEN COALESCE(authorized_at, $now) ELSE authorized_at END,
                   last_error = '',
                   updated_at = $now
             WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$authorized", authorized ? 1 : 0);
        command.Parameters.AddWithValue("$status", (int)(authorized ? PriceCacheStatus.Idle : PriceCacheStatus.Disabled));
        command.Parameters.AddWithValue("$start", FormatDate(startDate));
        command.Parameters.AddWithValue("$end", FormatDate(endDate));
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPausedAsync(
        bool paused,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE price_cache_control
               SET paused = $paused,
                   status = CASE
                       WHEN $paused = 1 AND $space = 1 THEN $insufficient
                       WHEN $paused = 1 THEN $pausedStatus
                       ELSE $idle END,
                   last_error = $reason,
                   updated_at = $now
             WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$paused", paused ? 1 : 0);
        command.Parameters.AddWithValue("$space", reason?.Contains("espaço", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0);
        command.Parameters.AddWithValue("$insufficient", (int)PriceCacheStatus.InsufficientSpace);
        command.Parameters.AddWithValue("$pausedStatus", (int)PriceCacheStatus.Paused);
        command.Parameters.AddWithValue("$idle", (int)PriceCacheStatus.Idle);
        command.Parameters.AddWithValue("$reason", reason?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetStatusAsync(
        PriceCacheStatus status,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE price_cache_control
               SET status = $status,
                   last_error = $message,
                   last_started_at = CASE WHEN $status = $downloading THEN $now ELSE last_started_at END,
                   last_completed_at = CASE WHEN $status = $complete THEN $now ELSE last_completed_at END,
                   updated_at = $now
             WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$message", message?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$downloading", (int)PriceCacheStatus.Downloading);
        command.Parameters.AddWithValue("$complete", (int)PriceCacheStatus.Complete);
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareWindowAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = FormatDateTime(DateTimeOffset.UtcNow);
        await using (var control = connection.CreateCommand())
        {
            control.Transaction = (SqliteTransaction)transaction;
            control.CommandText = """
                UPDATE price_cache_control
                   SET window_start = $start, window_end = $end, updated_at = $now
                 WHERE id = 1;
                """;
            control.Parameters.AddWithValue("$start", FormatDate(startDate));
            control.Parameters.AddWithValue("$end", FormatDate(endDate));
            control.Parameters.AddWithValue("$now", now);
            await control.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = (SqliteTransaction)transaction;
            upsert.CommandText = """
                UPDATE price_cache_contracts
                   SET status = $pending,
                       last_error = '',
                       next_retry_at = NULL,
                       updated_at = $now
                 WHERE status = $downloading;

                UPDATE price_cache_contracts
                   SET next_retry_at = NULL,
                       updated_at = $now
                 WHERE status = $failed
                   AND last_error LIKE 'PNCP respondeu 404 (%';

                UPDATE price_cache_contracts
                   SET status = $pending,
                       last_error = '',
                       next_retry_at = NULL,
                       updated_at = $now
                 WHERE status = $complete
                   AND EXISTS(
                       SELECT 1
                         FROM contracts c
                        WHERE c.pncp_id = price_cache_contracts.contract_id
                          AND c.publication_date >= $start
                          AND c.publication_date < $endExclusive
                          AND COALESCE(price_cache_contracts.source_global_updated_at, '') <>
                              COALESCE(c.global_updated_at, ''));

                INSERT INTO price_cache_contracts(
                    contract_id, source_global_updated_at, status, item_count,
                    active_result_count, cancelled_result_count, background_owned,
                    user_pinned, completed_at, updated_at)
                SELECT c.pncp_id,
                       s.source_global_updated_at,
                       CASE WHEN s.contract_id IS NOT NULL
                                      AND COALESCE(s.source_global_updated_at, '') = COALESCE(c.global_updated_at, '')
                                      AND NOT EXISTS(
                                          SELECT 1 FROM items pending
                                           WHERE pending.contract_id = c.pncp_id
                                             AND pending.has_result = 1
                                             AND pending.hydration_status <> $itemComplete)
                            THEN $complete ELSE $pending END,
                       COALESCE(s.item_count, 0),
                       (SELECT COUNT(*) FROM item_results r
                         WHERE r.contract_id = c.pncp_id AND r.result_status_id = 1),
                       (SELECT COUNT(*) FROM item_results r
                         WHERE r.contract_id = c.pncp_id AND r.result_status_id <> 1),
                       0,
                       CASE WHEN s.contract_id IS NULL THEN 0 ELSE 1 END,
                       CASE WHEN s.contract_id IS NULL THEN NULL ELSE s.fetched_at END,
                       $now
                  FROM contracts c
                  LEFT JOIN contract_item_snapshots s ON s.contract_id = c.pncp_id
                 WHERE c.publication_date >= $start
                   AND c.publication_date < $endExclusive
                   AND NOT EXISTS(
                       SELECT 1 FROM price_cache_contracts pc
                        WHERE pc.contract_id = c.pncp_id);
                """;
            upsert.Parameters.AddWithValue("$itemComplete", (int)ItemHydrationStatus.Complete);
            upsert.Parameters.AddWithValue("$complete", (int)PriceCacheContractStatus.Complete);
            upsert.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
            upsert.Parameters.AddWithValue("$downloading", (int)PriceCacheContractStatus.Downloading);
            upsert.Parameters.AddWithValue("$failed", (int)PriceCacheContractStatus.Failed);
            upsert.Parameters.AddWithValue("$start", FormatDate(startDate));
            upsert.Parameters.AddWithValue("$endExclusive", FormatDate(endDate.AddDays(1)));
            upsert.Parameters.AddWithValue("$now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = (SqliteTransaction)transaction;
            prune.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS price_cache_prune_ids(
                    contract_id TEXT PRIMARY KEY
                ) WITHOUT ROWID;
                DELETE FROM price_cache_prune_ids;
                INSERT INTO price_cache_prune_ids(contract_id)
                SELECT pc.contract_id
                  FROM price_cache_contracts pc
                  JOIN contracts c ON c.pncp_id = pc.contract_id
                 WHERE (c.publication_date < $start OR c.publication_date >= $endExclusive)
                   AND pc.background_owned = 1
                   AND pc.user_pinned = 0
                   AND NOT EXISTS(
                       SELECT 1 FROM quotation_references qr
                        WHERE qr.contract_id = pc.contract_id);

                DELETE FROM items
                 WHERE contract_id IN (SELECT contract_id FROM price_cache_prune_ids);
                DELETE FROM price_cache_contracts
                 WHERE contract_id IN (
                     SELECT c.pncp_id FROM contracts c
                      WHERE c.publication_date < $start OR c.publication_date >= $endExclusive);
                """;
            prune.Parameters.AddWithValue("$start", FormatDate(startDate));
            prune.Parameters.AddWithValue("$endExclusive", FormatDate(endDate.AddDays(1)));
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PriceCacheWorkItem?> GetNextWorkAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.pncp_id, c.cnpj, c.purchase_year, c.purchase_sequence, c.object,
                   c.additional_information, c.process, c.organization, c.unit, c.municipality,
                   c.municipality_ibge_code, c.uf, c.modality_id, c.modality_name, c.status,
                   c.publication_date, c.global_updated_at, c.total_homologated_scaled,
                   c.distance_from_ribeirao_km,
                   pc.status, pc.source_global_updated_at, pc.item_count,
                   pc.active_result_count, pc.cancelled_result_count, pc.attempts,
                   pc.next_retry_at, pc.last_error, pc.background_owned, pc.user_pinned
              FROM price_cache_contracts pc
              JOIN contracts c ON c.pncp_id = pc.contract_id
              JOIN price_cache_control ctl ON ctl.id = 1
             WHERE ctl.authorized = 1 AND ctl.enabled = 1 AND ctl.paused = 0
               AND c.publication_date >= ctl.window_start
               AND c.publication_date < date(ctl.window_end, '+1 day')
               AND (pc.status = $pending OR
                    (pc.status = $failed AND (pc.next_retry_at IS NULL OR pc.next_retry_at <= $now)))
             ORDER BY c.publication_date DESC, c.pncp_id
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
        command.Parameters.AddWithValue("$failed", (int)PriceCacheContractStatus.Failed);
        command.Parameters.AddWithValue("$now", FormatDateTime(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var contract = ReadContract(reader);
        return new PriceCacheWorkItem(contract, new PriceCacheCheckpoint
        {
            ContractId = contract.PncpId,
            Status = (PriceCacheContractStatus)reader.GetInt32(19),
            SourceGlobalUpdatedAt = ParseDateTime(reader, 20),
            ItemCount = reader.GetInt32(21),
            ActiveResultCount = reader.GetInt32(22),
            CancelledResultCount = reader.GetInt32(23),
            Attempts = reader.GetInt32(24),
            NextRetryAt = ParseDateTime(reader, 25),
            LastError = reader.GetString(26),
            BackgroundOwned = reader.GetInt64(27) == 1,
            UserPinned = reader.GetInt64(28) == 1
        });
    }

    public Task MarkContractDownloadingAsync(
        string contractId,
        bool backgroundOwned,
        CancellationToken cancellationToken = default) =>
        ExecuteContractUpdateAsync(
            """
            UPDATE price_cache_contracts
               SET status = $status,
                   background_owned = CASE WHEN user_pinned = 1 THEN background_owned ELSE MAX(background_owned, $owned) END,
                   attempts = attempts + 1,
                   started_at = $now,
                   last_error = '', next_retry_at = NULL, updated_at = $now
             WHERE contract_id = $contractId;
            """,
            contractId,
            (command, now) =>
            {
                command.Parameters.AddWithValue("$status", (int)PriceCacheContractStatus.Downloading);
                command.Parameters.AddWithValue("$owned", backgroundOwned ? 1 : 0);
            },
            cancellationToken);

    public async Task MarkContractCompleteAsync(
        string contractId,
        DateTimeOffset? sourceGlobalUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE price_cache_contracts
               SET source_global_updated_at = $source,
                   status = $complete,
                   item_count = (SELECT COUNT(*) FROM items WHERE contract_id = $contractId),
                   active_result_count = (SELECT COUNT(*) FROM item_results
                                           WHERE contract_id = $contractId AND result_status_id = 1),
                   cancelled_result_count = (SELECT COUNT(*) FROM item_results
                                              WHERE contract_id = $contractId AND result_status_id <> 1),
                   last_error = '', next_retry_at = NULL,
                   completed_at = $now, updated_at = $now
             WHERE contract_id = $contractId;
            """;
        command.Parameters.AddWithValue("$source", DbValue(sourceGlobalUpdatedAt));
        command.Parameters.AddWithValue("$complete", (int)PriceCacheContractStatus.Complete);
        command.Parameters.AddWithValue("$contractId", contractId);
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkContractUnavailableAsync(
        string contractId,
        DateTimeOffset? sourceGlobalUpdatedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE price_cache_contracts
               SET source_global_updated_at = $source,
                   status = $complete,
                   item_count = (SELECT COUNT(*) FROM items WHERE contract_id = $contractId),
                   active_result_count = (SELECT COUNT(*) FROM item_results
                                           WHERE contract_id = $contractId AND result_status_id = 1),
                   cancelled_result_count = (SELECT COUNT(*) FROM item_results
                                              WHERE contract_id = $contractId AND result_status_id <> 1),
                   last_error = $reason,
                   next_retry_at = NULL,
                   completed_at = $now,
                   updated_at = $now
             WHERE contract_id = $contractId;
            """;
        command.Parameters.AddWithValue("$source", DbValue(sourceGlobalUpdatedAt));
        command.Parameters.AddWithValue("$complete", (int)PriceCacheContractStatus.Complete);
        command.Parameters.AddWithValue("$contractId", contractId);
        command.Parameters.AddWithValue("$reason", SearchText.Sanitize(reason));
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task MarkContractFailedAsync(
        string contractId,
        string error,
        DateTimeOffset nextRetryAt,
        CancellationToken cancellationToken = default) =>
        ExecuteContractUpdateAsync(
            """
            UPDATE price_cache_contracts
               SET status = $status, last_error = $error, next_retry_at = $retry, updated_at = $now
             WHERE contract_id = $contractId;
            """,
            contractId,
            (command, _) =>
            {
                command.Parameters.AddWithValue("$status", (int)PriceCacheContractStatus.Failed);
                command.Parameters.AddWithValue("$error", SearchText.Sanitize(error));
                command.Parameters.AddWithValue("$retry", FormatDateTime(nextRetryAt));
            },
            cancellationToken);

    public Task MarkContractPendingAsync(
        string contractId,
        string? message = null,
        CancellationToken cancellationToken = default) =>
        ExecuteContractUpdateAsync(
            """
            UPDATE price_cache_contracts
               SET status = $status, last_error = $message, next_retry_at = NULL, updated_at = $now
             WHERE contract_id = $contractId;
            """,
            contractId,
            (command, _) =>
            {
                command.Parameters.AddWithValue("$status", (int)PriceCacheContractStatus.Pending);
                command.Parameters.AddWithValue("$message", message?.Trim() ?? string.Empty);
            },
            cancellationToken);

    public Task MarkContractPinnedAsync(
        string contractId,
        CancellationToken cancellationToken = default) =>
        ExecuteContractUpdateAsync(
            """
            INSERT INTO price_cache_contracts(contract_id, status, user_pinned, updated_at)
            VALUES($contractId, $status, 1, $now)
            ON CONFLICT(contract_id) DO UPDATE SET user_pinned = 1, background_owned = 0, updated_at = $now;
            """,
            contractId,
            (command, _) => command.Parameters.AddWithValue("$status", (int)PriceCacheContractStatus.Pending),
            cancellationToken);

    public async Task<PriceCacheProgress> GetProgressAsync(CancellationToken cancellationToken = default)
    {
        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var start = policy.WindowStart ?? DateOnly.FromDateTime(DateTime.Today).AddDays(-89);
        var end = policy.WindowEnd ?? DateOnly.FromDateTime(DateTime.Today);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = 0, complete = 0, pending = 0, failed = 0, items = 0, active = 0, cancelled = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*),
                       SUM(CASE WHEN pc.status = $complete THEN 1 ELSE 0 END),
                       SUM(CASE WHEN pc.status IN ($pending, $downloading) THEN 1 ELSE 0 END),
                       SUM(CASE WHEN pc.status = $failed THEN 1 ELSE 0 END),
                       COALESCE(SUM(pc.item_count), 0),
                       COALESCE(SUM(pc.active_result_count), 0),
                       COALESCE(SUM(pc.cancelled_result_count), 0)
                  FROM price_cache_contracts pc
                  JOIN contracts c ON c.pncp_id = pc.contract_id
                 WHERE c.publication_date >= $start
                   AND c.publication_date < $endExclusive;
                """;
            command.Parameters.AddWithValue("$complete", (int)PriceCacheContractStatus.Complete);
            command.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
            command.Parameters.AddWithValue("$downloading", (int)PriceCacheContractStatus.Downloading);
            command.Parameters.AddWithValue("$failed", (int)PriceCacheContractStatus.Failed);
            command.Parameters.AddWithValue("$start", FormatDate(start));
            command.Parameters.AddWithValue("$endExclusive", FormatDate(end.AddDays(1)));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                total = reader.GetInt64(0);
                complete = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                pending = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                failed = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                items = reader.GetInt64(4);
                active = reader.GetInt64(5);
                cancelled = reader.GetInt64(6);
            }
        }

        var occupied = checked(items * 900 + (active + cancelled) * 750);
        return new PriceCacheProgress
        {
            Status = policy.Status,
            StartDate = start,
            EndDate = end,
            TotalContracts = total,
            CompletedContracts = complete,
            PendingContracts = pending,
            FailedContracts = failed,
            ItemCount = items,
            ActiveResultCount = active,
            CancelledResultCount = cancelled,
            OccupiedBytes = occupied,
            Message = policy.LastError
        };
    }

    public async Task RemoveBackgroundCacheAsync(CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS price_cache_remove_ids(
                contract_id TEXT PRIMARY KEY
            ) WITHOUT ROWID;
            DELETE FROM price_cache_remove_ids;
            INSERT INTO price_cache_remove_ids(contract_id)
            SELECT contract_id FROM price_cache_contracts pc
             WHERE background_owned = 1 AND user_pinned = 0
               AND NOT EXISTS(
                   SELECT 1 FROM quotation_references qr
                    WHERE qr.contract_id = pc.contract_id);
            DELETE FROM items WHERE contract_id IN (SELECT contract_id FROM price_cache_remove_ids);
            DELETE FROM price_cache_contracts WHERE background_owned = 1;
            UPDATE price_cache_control
               SET authorized = 0, enabled = 0, paused = 0, status = $disabled,
                   last_error = '', updated_at = $now
             WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$disabled", (int)PriceCacheStatus.Disabled);
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PriceCacheLocalPage> SearchLocalAsync(
        SearchQuery filters,
        SearchExpression expression,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        PriceCacheLocalCursor? cursor = null;
        PriceCacheLocalPage result = new([], 1, pageSize, false, 0);
        for (var currentPage = 1; currentPage <= page; currentPage++)
        {
            result = await SearchLocalAfterAsync(
                    filters,
                    expression,
                    minimumUnitPrice,
                    maximumUnitPrice,
                    cursor,
                    pageSize,
                    cancellationToken)
                .ConfigureAwait(false);
            cursor = result.Cursor;
            if (!result.HasMore)
            {
                break;
            }
        }

        return result;
    }

    public async Task<PriceCacheLocalPage> SearchLocalAfterAsync(
        SearchQuery filters,
        SearchExpression expression,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        PriceCacheLocalCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        using var span = _performance.Begin("price-search", "local-page");
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(expression);
        var page = (cursor?.Page ?? 0) + 1;
        pageSize = Math.Clamp(pageSize, 1, 200);
        if (expression.IsEmpty)
        {
            return new PriceCacheLocalPage([], page, pageSize, false, 0);
        }


        using var queueSpan = _performance.Begin("price-search", "sqlite-queue");
        await using var readerLease = await _connections.WorkCoordinator
            .EnterReaderAsync(SqliteWorkPriority.Visible, cancellationToken)
            .ConfigureAwait(false);
        queueSpan.Complete();
        using var sqlSpan = _performance.Begin("price-search", "sql-execution");

        var itemMatch = expression.ItemMatchQuery;
        var explicitMatch = expression.ExplicitContractMatchQuery;
        var conditions = new List<string>
        {
            "i.hydration_status = $complete",
            "COALESCE(s.source_global_updated_at, '') = COALESCE(c.global_updated_at, '')"
        };
        if (itemMatch.Length > 0)
        {
            conditions.Add("items_fts MATCH $itemMatch");
        }

        switch (filters.EffectiveGeoFilter.Kind)
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
            conditions.Add("c.publication_date >= $startDate");
        }
        if (filters.EndDate is not null)
        {
            conditions.Add("c.publication_date < $endDateExclusive");
        }
        var activePriceConditions = new List<string>
        {
            "r.result_status_id = 1",
            "r.unit_value_scaled > 0"
        };
        if (minimumUnitPrice is not null)
        {
            activePriceConditions.Add("r.unit_value_scaled >= $minimum");
        }
        if (maximumUnitPrice is not null)
        {
            activePriceConditions.Add("r.unit_value_scaled <= $maximum");
        }
        conditions.AddRange(activePriceConditions);

        var itemJoin = itemMatch.Length > 0
            ? "JOIN items_fts ON items_fts.rowid = i.rowid"
            : string.Empty;
        var explicitPriority = explicitMatch.Length > 0
            ? "CASE WHEN c.rowid IN (SELECT rowid FROM contracts_fts WHERE contracts_fts MATCH $explicitMatch) THEN 0 ELSE 1 END"
            : "0";
        var (primaryRank, secondaryRank) = filters.Sort switch
        {
            SearchSort.Nearest => ("CAST(COALESCE(c.geo_layer, 1) AS REAL)",
                "CAST(COALESCE(c.municipality_distance_rank, 999999) AS REAL)"),
            SearchSort.Relevance when itemMatch.Length > 0 => ("bm25(items_fts)", "0.0"),
            _ => ("0.0", "0.0")
        };
        var cursorWhere = cursor is null
            ? string.Empty
            : """
              WHERE sort_priority > $cursorPriority
                 OR (sort_priority = $cursorPriority AND primary_rank > $cursorPrimary)
                 OR (sort_priority = $cursorPriority AND primary_rank = $cursorPrimary
                     AND secondary_rank > $cursorSecondary)
                 OR (sort_priority = $cursorPriority AND primary_rank = $cursorPrimary
                     AND secondary_rank = $cursorSecondary AND publication_date < $cursorPublication)
                 OR (sort_priority = $cursorPriority AND primary_rank = $cursorPrimary
                     AND secondary_rank = $cursorSecondary AND publication_date = $cursorPublication
                     AND pncp_id > $cursorContract)
                 OR (sort_priority = $cursorPriority AND primary_rank = $cursorPrimary
                     AND secondary_rank = $cursorSecondary AND publication_date = $cursorPublication
                     AND pncp_id = $cursorContract AND item_number > $cursorItem)
                 OR (sort_priority = $cursorPriority AND primary_rank = $cursorPrimary
                     AND secondary_rank = $cursorSecondary AND publication_date = $cursorPublication
                     AND pncp_id = $cursorContract AND item_number = $cursorItem
                     AND result_sequence > $cursorResult)
              """;
        var scanLimit = Math.Min(10_000, Math.Max(pageSize * 4, pageSize + 1));

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH ranked_items AS (
                SELECT c.pncp_id, c.cnpj, c.purchase_year, c.purchase_sequence, c.object,
                       c.additional_information, c.process, c.organization, c.unit, c.municipality,
                       c.municipality_ibge_code, c.uf, c.modality_id, c.modality_name, c.status,
                       c.publication_date, c.global_updated_at, c.total_homologated_scaled,
                       c.distance_from_ribeirao_km,
                       i.contract_id, i.item_number, i.description, i.unit, i.requested_quantity_scaled,
                       i.additional_information, i.item_category, i.ncm_nbs_code, i.ncm_nbs_description,
                       i.catalog_code, i.catalog_name, i.catalog_category, i.status, i.has_result,
                       i.source_updated_at, i.hydration_status, i.last_error,
                       {explicitPriority} AS sort_priority,
                       {primaryRank} AS primary_rank,
                       {secondaryRank} AS secondary_rank,
                       r.result_sequence, r.supplier_tax_id, r.supplier_name, r.supplier_type,
                       r.supplier_municipality, r.supplier_uf, r.quantity_scaled,
                       r.unit_value_scaled, r.total_value_scaled, r.result_date,
                       r.result_status_id, r.result_status_name
                  FROM items i
                  JOIN contracts c ON c.pncp_id = i.contract_id
                  JOIN contract_item_snapshots s ON s.contract_id = i.contract_id
                  JOIN item_results r ON r.contract_id = i.contract_id AND r.item_number = i.item_number
                  {itemJoin}
                 WHERE {string.Join(" AND ", conditions)}
            )
            SELECT * FROM ranked_items
            {cursorWhere}
             ORDER BY sort_priority, primary_rank, secondary_rank,
                      publication_date DESC, pncp_id, item_number, result_sequence
             LIMIT $scanLimit;
            """;
        command.Parameters.AddWithValue("$complete", (int)ItemHydrationStatus.Complete);
        command.Parameters.AddWithValue("$scanLimit", scanLimit);
        if (itemMatch.Length > 0)
        {
            command.Parameters.AddWithValue("$itemMatch", itemMatch);
        }
        if (explicitMatch.Length > 0)
        {
            command.Parameters.AddWithValue("$explicitMatch", explicitMatch);
        }
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$cursorPriority", cursor.ExplicitPriority);
            command.Parameters.AddWithValue("$cursorPrimary", cursor.PrimaryRank);
            command.Parameters.AddWithValue("$cursorSecondary", cursor.SecondaryRank);
            command.Parameters.AddWithValue("$cursorPublication", cursor.PublicationDate);
            command.Parameters.AddWithValue("$cursorContract", cursor.ContractId);
            command.Parameters.AddWithValue("$cursorItem", cursor.ItemNumber);
            command.Parameters.AddWithValue("$cursorResult", cursor.ResultSequence);
        }
        AddFilterParameters(command, filters);
        if (minimumUnitPrice is not null)
        {
            command.Parameters.AddWithValue("$minimum", DecimalScale.ToScaled(minimumUnitPrice.Value)!.Value);
        }
        if (maximumUnitPrice is not null)
        {
            command.Parameters.AddWithValue("$maximum", DecimalScale.ToScaled(maximumUnitPrice.Value)!.Value);
        }

        var matches = new List<(ItemSearchHit Hit, ItemSearchRow Row, PriceCacheLocalCursor Cursor)>(pageSize + 1);
        var scanned = 0;
        PriceCacheLocalCursor? lastScannedCursor = cursor;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scanned++;
            var item = ReadItem(reader, 19);
            var rowCursor = new PriceCacheLocalCursor(
                page,
                reader.GetInt32(36),
                reader.GetDouble(37),
                reader.GetDouble(38),
                reader.GetString(15),
                reader.GetString(0),
                reader.GetInt64(20),
                reader.GetInt64(39));
            lastScannedCursor = rowCursor;
            if (expression.MatchesItem(item.Description, item.Unit))
            {
                var contract = ReadContract(reader);
                var result = new HomologationResult
                {
                    ContractId = contract.PncpId,
                    ItemNumber = item.ItemNumber,
                    ResultSequence = reader.GetInt64(39),
                    SupplierTaxId = reader.GetString(40),
                    SupplierName = reader.GetString(41),
                    SupplierType = reader.GetString(42),
                    SupplierMunicipality = reader.GetString(43),
                    SupplierUf = reader.GetString(44),
                    HomologatedQuantityScaled = reader.IsDBNull(45) ? null : reader.GetInt64(45),
                    HomologatedUnitValueScaled = reader.IsDBNull(46) ? null : reader.GetInt64(46),
                    HomologatedTotalValueScaled = reader.IsDBNull(47) ? null : reader.GetInt64(47),
                    ResultDate = ParseDate(reader, 48),
                    ResultStatusId = reader.GetInt32(49),
                    ResultStatusName = reader.GetString(50)
                };
                var hit = new ItemSearchHit(contract, item);
                matches.Add((
                    hit,
                    new ItemSearchRow(
                        contract,
                        item,
                        result,
                        ItemSearchPriceState.Homologated,
                        "Preço homologado do cache local",
                        false),
                    rowCursor));
                if (matches.Count > pageSize)
                {
                    break;
                }
            }
        }

        var selectedMatches = matches.Take(pageSize).ToArray();
        var hits = selectedMatches
            .Select(value => value.Hit)
            .DistinctBy(hit => (hit.Contract.PncpId, hit.Item.ItemNumber))
            .ToArray();
        var rows = selectedMatches.Select(value => value.Row).ToArray();
        var hasMore = matches.Count > pageSize || scanned >= scanLimit;
        var continuation = matches.Count > pageSize
            ? selectedMatches[^1].Cursor
            : hasMore
                ? lastScannedCursor
                : selectedMatches.LastOrDefault().Cursor ?? cursor;
        sqlSpan.Complete(rows.Length);
        span.Complete(rows.Length);
        return new PriceCacheLocalPage(
            hits,
            page,
            pageSize,
            hasMore,
            hits.Length,
            rows,
            continuation);
    }

    private static async Task<IReadOnlyList<ItemSearchRow>> LoadLocalRowsAsync(
        SqliteConnection connection,
        IReadOnlyList<ItemSearchHit> hits,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        CancellationToken cancellationToken)
    {
        if (hits.Count == 0)
        {
            return [];
        }

        var byKey = hits.ToDictionary(
            hit => (hit.Contract.PncpId, hit.Item.ItemNumber),
            hit => hit);
        var keyConditions = new string[hits.Count];
        await using var command = connection.CreateCommand();
        for (var index = 0; index < hits.Count; index++)
        {
            keyConditions[index] = $"(r.contract_id = $contract{index} AND r.item_number = $item{index})";
            command.Parameters.AddWithValue($"$contract{index}", hits[index].Contract.PncpId);
            command.Parameters.AddWithValue($"$item{index}", hits[index].Item.ItemNumber);
        }

        var priceConditions = new List<string>
        {
            "r.result_status_id = 1",
            "r.unit_value_scaled > 0"
        };
        if (minimumUnitPrice is not null)
        {
            priceConditions.Add("r.unit_value_scaled >= $minimum");
            command.Parameters.AddWithValue("$minimum", DecimalScale.ToScaled(minimumUnitPrice.Value)!.Value);
        }

        if (maximumUnitPrice is not null)
        {
            priceConditions.Add("r.unit_value_scaled <= $maximum");
            command.Parameters.AddWithValue("$maximum", DecimalScale.ToScaled(maximumUnitPrice.Value)!.Value);
        }

        command.CommandText = $"""
            SELECT r.contract_id, r.item_number, r.result_sequence,
                   r.supplier_tax_id, r.supplier_name, r.supplier_type,
                   r.supplier_municipality, r.supplier_uf,
                   r.quantity_scaled, r.unit_value_scaled, r.total_value_scaled,
                   r.result_date, r.result_status_id, r.result_status_name
              FROM item_results r
             WHERE ({string.Join(" OR ", keyConditions)})
               AND {string.Join(" AND ", priceConditions)}
             ORDER BY r.contract_id, r.item_number, r.result_sequence;
            """;
        var rows = new List<ItemSearchRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (reader.GetString(0), reader.GetInt64(1));
            if (!byKey.TryGetValue(key, out var hit))
            {
                continue;
            }

            var result = new HomologationResult
            {
                ContractId = key.Item1,
                ItemNumber = key.Item2,
                ResultSequence = reader.GetInt64(2),
                SupplierTaxId = reader.GetString(3),
                SupplierName = reader.GetString(4),
                SupplierType = reader.GetString(5),
                SupplierMunicipality = reader.GetString(6),
                SupplierUf = reader.GetString(7),
                HomologatedQuantityScaled = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                HomologatedUnitValueScaled = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                HomologatedTotalValueScaled = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                ResultDate = ParseDate(reader, 11),
                ResultStatusId = reader.GetInt32(12),
                ResultStatusName = reader.GetString(13)
            };
            rows.Add(new ItemSearchRow(
                hit.Contract,
                hit.Item,
                result,
                ItemSearchPriceState.Homologated,
                "Preço homologado do cache local",
                false));
        }

        return rows;
    }

    private async Task ExecuteContractUpdateAsync(
        string sql,
        string contractId,
        Action<SqliteCommand, DateTimeOffset> addParameters,
        CancellationToken cancellationToken)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$contractId", contractId);
        command.Parameters.AddWithValue("$now", FormatDateTime(now));
        addParameters(command, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        return await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddFilterParameters(SqliteCommand command, SearchQuery query)
    {
        if (query.EffectiveGeoFilter.Kind == SearchGeoFilterKind.State)
        {
            command.Parameters.AddWithValue("$uf", query.EffectiveGeoFilter.Uf!);
        }
        if (query.StartDate is not null)
        {
            command.Parameters.AddWithValue("$startDate", FormatDate(query.StartDate.Value));
        }
        if (query.EndDate is not null)
        {
            command.Parameters.AddWithValue("$endDateExclusive", FormatDate(query.EndDate.Value.AddDays(1)));
        }
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
        DistanceFromRibeiraoKilometers = reader.IsDBNull(18) ? null : reader.GetDouble(18)
    };

    private static ProcurementItem ReadItem(SqliteDataReader reader, int offset) => new()
    {
        ContractId = reader.GetString(offset),
        ItemNumber = reader.GetInt64(offset + 1),
        Description = reader.GetString(offset + 2),
        Unit = reader.GetString(offset + 3),
        RequestedQuantityScaled = ReadNullableLong(reader, offset + 4),
        AdditionalInformation = reader.GetString(offset + 5),
        Category = reader.GetString(offset + 6),
        NcmNbsCode = reader.GetString(offset + 7),
        NcmNbsDescription = reader.GetString(offset + 8),
        CatalogCode = reader.GetString(offset + 9),
        CatalogName = reader.GetString(offset + 10),
        CatalogCategory = reader.GetString(offset + 11),
        Status = reader.GetString(offset + 12),
        HasResult = reader.GetInt64(offset + 13) == 1,
        UpdatedAt = ParseDateTime(reader, offset + 14),
        HydrationStatus = (ItemHydrationStatus)reader.GetInt32(offset + 15),
        LastError = reader.IsDBNull(offset + 16) ? null : reader.GetString(offset + 16)
    };

    private static DateOnly? ParseDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ||
        !DateOnly.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? null
            : value;

    private static DateTimeOffset? ParseDateTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ||
        !DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? null
            : value;

    private static long? ReadNullableLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static object DbValue(object? value) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset dateTime => FormatDateTime(dateTime),
        _ => value
    };
}
