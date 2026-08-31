using System.Globalization;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Data;

public sealed partial class SqlitePriceCacheRepository : IPriceCacheRepository
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
        var prepared = false;
        await using (var state = connection.CreateCommand())
        {
            state.CommandText = """
                SELECT prepared_window_start, prepared_window_end,
                       indexed_contract_count, indexed_complete_count
                  FROM price_cache_control
                 WHERE id = 1;
                """;
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                ParseDate(reader, 0) == startDate &&
                ParseDate(reader, 1) == endDate)
            {
                contracts = reader.GetInt64(2);
                complete = reader.GetInt64(3);
                prepared = true;
            }
            else
            {
                contracts = 0;
                complete = 0;
            }
        }

        if (!prepared)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*),
                       SUM(CASE WHEN s.contract_id IS NOT NULL
                                      AND COALESCE(s.source_global_updated_at, '') =
                                          COALESCE(c.global_updated_at, '')
                                THEN 1 ELSE 0 END)
                  FROM contracts c
                  LEFT JOIN contract_item_snapshots s ON s.contract_id = c.pncp_id
                 WHERE c.publication_date >= $start
                   AND c.publication_date < $endExclusive;
                """;
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
                   authorized_at = CASE WHEN $authorized = 1 THEN $now ELSE authorized_at END,
                   prepared_window_start = CASE WHEN $authorized = 1 THEN NULL ELSE prepared_window_start END,
                   prepared_window_end = CASE WHEN $authorized = 1 THEN NULL ELSE prepared_window_end END,
                   indexed_contract_count = CASE WHEN $authorized = 1 THEN 0 ELSE indexed_contract_count END,
                   indexed_complete_count = CASE WHEN $authorized = 1 THEN 0 ELSE indexed_complete_count END,
                   indexed_pending_count = CASE WHEN $authorized = 1 THEN 0 ELSE indexed_pending_count END,
                   indexed_failed_count = CASE WHEN $authorized = 1 THEN 0 ELSE indexed_failed_count END,
                   indexed_item_count = CASE WHEN $authorized = 1 THEN 0 ELSE indexed_item_count END,
                   indexed_active_result_count = CASE WHEN $authorized = 1 THEN 0 ELSE indexed_active_result_count END,
                   indexed_cancelled_result_count = CASE WHEN $authorized = 1 THEN 0 ELSE indexed_cancelled_result_count END,
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
        var prepared = false;
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = (SqliteTransaction)transaction;
            state.CommandText = """
                SELECT prepared_window_start, prepared_window_end
                  FROM price_cache_control
                 WHERE id = 1;
                """;
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            prepared = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                       ParseDate(reader, 0) == startDate &&
                       ParseDate(reader, 1) == endDate;
        }

        await using (var control = connection.CreateCommand())
        {
            control.Transaction = (SqliteTransaction)transaction;
            control.CommandText = """
                UPDATE price_cache_control
                   SET window_start = $start,
                       window_end = $end,
                       statistics_suspended = $suspended,
                       updated_at = $now
                 WHERE id = 1;
                """;
            control.Parameters.AddWithValue("$start", FormatDate(startDate));
            control.Parameters.AddWithValue("$end", FormatDate(endDate));
            control.Parameters.AddWithValue("$suspended", prepared ? 0 : 1);
            control.Parameters.AddWithValue("$now", now);
            await control.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var recovery = connection.CreateCommand())
        {
            recovery.Transaction = (SqliteTransaction)transaction;
            recovery.CommandText = """
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
                   AND next_retry_at IS NOT NULL
                   AND last_error LIKE 'PNCP respondeu 404 (%';
                """;
            recovery.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
            recovery.Parameters.AddWithValue("$downloading", (int)PriceCacheContractStatus.Downloading);
            recovery.Parameters.AddWithValue("$failed", (int)PriceCacheContractStatus.Failed);
            recovery.Parameters.AddWithValue("$now", now);
            await recovery.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (prepared)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = (SqliteTransaction)transaction;
            upsert.CommandText = """
                UPDATE price_cache_contracts
                   SET publication_date = COALESCE((
                           SELECT c.publication_date
                             FROM contracts c
                            WHERE c.pncp_id = price_cache_contracts.contract_id), ''),
                       updated_at = $now
                 WHERE publication_date IS NOT COALESCE((
                           SELECT c.publication_date
                             FROM contracts c
                            WHERE c.pncp_id = price_cache_contracts.contract_id), '');

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

                UPDATE price_cache_contracts
                   SET source_global_updated_at = (
                           SELECT s.source_global_updated_at
                             FROM contract_item_snapshots s
                            WHERE s.contract_id = price_cache_contracts.contract_id),
                       status = $complete,
                       item_count = COALESCE((
                           SELECT s.item_count
                             FROM contract_item_snapshots s
                            WHERE s.contract_id = price_cache_contracts.contract_id), 0),
                       last_error = '',
                       next_retry_at = NULL,
                       completed_at = COALESCE(completed_at, $now),
                       updated_at = $now
                 WHERE EXISTS(
                       SELECT 1
                         FROM contracts c
                         JOIN contract_item_snapshots s ON s.contract_id = c.pncp_id
                        WHERE c.pncp_id = price_cache_contracts.contract_id
                          AND c.publication_date >= $start
                          AND c.publication_date < $endExclusive
                          AND COALESCE(s.source_global_updated_at, '') =
                              COALESCE(c.global_updated_at, ''))
                   AND (status <> $complete
                        OR COALESCE(source_global_updated_at, '') <> COALESCE((
                               SELECT s.source_global_updated_at
                                 FROM contract_item_snapshots s
                                WHERE s.contract_id = price_cache_contracts.contract_id), '')
                        OR item_count <> COALESCE((
                               SELECT s.item_count
                                 FROM contract_item_snapshots s
                                WHERE s.contract_id = price_cache_contracts.contract_id), 0));

                INSERT INTO price_cache_contracts(
                    contract_id, publication_date, source_global_updated_at, status, item_count,
                    active_result_count, cancelled_result_count, background_owned,
                    user_pinned, completed_at, updated_at)
                SELECT c.pncp_id,
                       COALESCE(c.publication_date, ''),
                       s.source_global_updated_at,
                       CASE WHEN s.contract_id IS NOT NULL
                                      AND COALESCE(s.source_global_updated_at, '') = COALESCE(c.global_updated_at, '')
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
            upsert.Parameters.AddWithValue("$complete", (int)PriceCacheContractStatus.Complete);
            upsert.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
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
                 WHERE (pc.publication_date < $start OR pc.publication_date >= $endExclusive)
                   AND pc.background_owned = 1
                   AND pc.user_pinned = 0
                   AND NOT EXISTS(
                       SELECT 1 FROM quotation_references qr
                        WHERE qr.contract_id = pc.contract_id);

                DELETE FROM items
                 WHERE contract_id IN (SELECT contract_id FROM price_cache_prune_ids);
                DELETE FROM price_cache_contracts
                 WHERE publication_date < $start OR publication_date >= $endExclusive;
                """;
            prune.Parameters.AddWithValue("$start", FormatDate(startDate));
            prune.Parameters.AddWithValue("$endExclusive", FormatDate(endDate.AddDays(1)));
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var statistics = connection.CreateCommand())
        {
            statistics.Transaction = (SqliteTransaction)transaction;
            statistics.CommandText = """
                WITH totals AS (
                    SELECT COUNT(*) AS contracts,
                           SUM(CASE WHEN status = $complete THEN 1 ELSE 0 END) AS complete,
                           SUM(CASE WHEN status IN ($pending, $downloading) THEN 1 ELSE 0 END) AS pending,
                           SUM(CASE WHEN status = $failed THEN 1 ELSE 0 END) AS failed,
                           COALESCE(SUM(item_count), 0) AS items,
                           COALESCE(SUM(active_result_count), 0) AS active,
                           COALESCE(SUM(cancelled_result_count), 0) AS cancelled
                      FROM price_cache_contracts
                     WHERE publication_date >= $start
                       AND publication_date < $endExclusive
                )
                UPDATE price_cache_control
                   SET prepared_window_start = $start,
                       prepared_window_end = $end,
                       indexed_contract_count = (SELECT contracts FROM totals),
                       indexed_complete_count = COALESCE((SELECT complete FROM totals), 0),
                       indexed_pending_count = COALESCE((SELECT pending FROM totals), 0),
                       indexed_failed_count = COALESCE((SELECT failed FROM totals), 0),
                       indexed_item_count = (SELECT items FROM totals),
                       indexed_active_result_count = (SELECT active FROM totals),
                       indexed_cancelled_result_count = (SELECT cancelled FROM totals),
                       statistics_suspended = 0,
                       updated_at = $now
                 WHERE id = 1;
                """;
            statistics.Parameters.AddWithValue("$complete", (int)PriceCacheContractStatus.Complete);
            statistics.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
            statistics.Parameters.AddWithValue("$downloading", (int)PriceCacheContractStatus.Downloading);
            statistics.Parameters.AddWithValue("$failed", (int)PriceCacheContractStatus.Failed);
            statistics.Parameters.AddWithValue("$start", FormatDate(startDate));
            statistics.Parameters.AddWithValue("$end", FormatDate(endDate));
            statistics.Parameters.AddWithValue("$endExclusive", FormatDate(endDate.AddDays(1)));
            statistics.Parameters.AddWithValue("$now", now);
            await statistics.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            WITH policy AS (
                SELECT authorized, enabled, paused, window_start,
                       date(window_end, '+1 day') AS window_end_exclusive
                  FROM price_cache_control
                 WHERE id = 1
            ),
            pending_candidate AS (
                SELECT pc.contract_id, pc.publication_date
                  FROM price_cache_contracts pc
                  CROSS JOIN policy
                 WHERE pc.status = $pending
                   AND pc.publication_date >= policy.window_start
                   AND pc.publication_date < policy.window_end_exclusive
                 ORDER BY pc.publication_date DESC, pc.contract_id
                 LIMIT 1
            ),
            failed_candidate AS (
                SELECT pc.contract_id, pc.publication_date
                  FROM price_cache_contracts pc
                  CROSS JOIN policy
                 WHERE pc.status = $failed
                   AND (pc.next_retry_at IS NULL OR pc.next_retry_at <= $now)
                   AND pc.publication_date >= policy.window_start
                   AND pc.publication_date < policy.window_end_exclusive
                 ORDER BY pc.publication_date DESC, pc.contract_id
                 LIMIT 1
            ),
            next_candidate AS (
                SELECT contract_id, publication_date FROM pending_candidate
                UNION ALL
                SELECT contract_id, publication_date FROM failed_candidate
                ORDER BY publication_date DESC, contract_id
                LIMIT 1
            )
            SELECT c.pncp_id, c.cnpj, c.purchase_year, c.purchase_sequence, c.object,
                   c.additional_information, c.process, c.organization, c.unit, c.municipality,
                   c.municipality_ibge_code, c.uf, c.modality_id, c.modality_name, c.status,
                   c.publication_date, c.global_updated_at, c.total_homologated_scaled,
                   c.distance_from_ribeirao_km,
                   pc.status, pc.source_global_updated_at, pc.item_count,
                   pc.active_result_count, pc.cancelled_result_count, pc.attempts,
                   pc.next_retry_at, pc.last_error, pc.background_owned, pc.user_pinned
              FROM next_candidate next
              JOIN price_cache_contracts pc ON pc.contract_id = next.contract_id
              JOIN contracts c ON c.pncp_id = pc.contract_id
              JOIN policy ON policy.authorized = 1 AND policy.enabled = 1 AND policy.paused = 0;
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
                   price_index_eligible_item_count =
                       (SELECT COUNT(*) FROM items
                         WHERE contract_id = $contractId AND has_result = 1),
                   price_index_completed_item_count =
                       (SELECT COUNT(*) FROM items
                         WHERE contract_id = $contractId AND has_result = 1
                           AND hydration_status = $itemComplete),
                   price_index_priced_item_count =
                       (SELECT COUNT(*) FROM items i
                         WHERE i.contract_id = $contractId AND i.has_result = 1
                           AND i.hydration_status = $itemComplete
                           AND EXISTS(
                               SELECT 1 FROM item_results r
                                WHERE r.contract_id = i.contract_id
                                  AND r.item_number = i.item_number
                                  AND r.result_status_id = 1 AND r.unit_value_scaled > 0)),
                   price_index_result_count =
                       (SELECT COUNT(*) FROM item_results
                         WHERE contract_id = $contractId
                           AND result_status_id = 1 AND unit_value_scaled > 0),
                   price_index_status = CASE WHEN EXISTS(
                       SELECT 1 FROM items
                        WHERE contract_id = $contractId AND has_result = 1
                          AND hydration_status <> $itemComplete)
                       THEN $pricePending ELSE $complete END,
                   price_index_attempts = CASE WHEN EXISTS(
                       SELECT 1 FROM items
                        WHERE contract_id = $contractId AND has_result = 1
                          AND hydration_status <> $itemComplete)
                       THEN 0 ELSE price_index_attempts END,
                   price_index_last_error = '',
                   price_index_next_retry_at = NULL,
                   last_error = '', next_retry_at = NULL,
                   completed_at = $now, updated_at = $now
             WHERE contract_id = $contractId;
            """;
        command.Parameters.AddWithValue("$source", DbValue(sourceGlobalUpdatedAt));
        command.Parameters.AddWithValue("$complete", (int)PriceCacheContractStatus.Complete);
        command.Parameters.AddWithValue("$pricePending", (int)PriceCacheContractStatus.Pending);
        command.Parameters.AddWithValue("$itemComplete", (int)ItemHydrationStatus.Complete);
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
                   price_index_status = $complete,
                   price_index_eligible_item_count = 0,
                   price_index_completed_item_count = 0,
                   price_index_priced_item_count = 0,
                   price_index_result_count = 0,
                   price_index_last_error = $reason,
                   price_index_next_retry_at = NULL,
                   price_index_completed_at = $now,
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
            INSERT INTO price_cache_contracts(
                contract_id, publication_date, status, user_pinned, updated_at)
            VALUES($contractId,
                   COALESCE((SELECT publication_date FROM contracts WHERE pncp_id = $contractId), ''),
                   $status, 1, $now)
            ON CONFLICT(contract_id) DO UPDATE SET user_pinned = 1, background_owned = 0, updated_at = $now;
            """,
            contractId,
            (command, _) => command.Parameters.AddWithValue("$status", (int)PriceCacheContractStatus.Pending),
            cancellationToken);

    public async Task<PriceCacheProgress> GetProgressAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-364);
        var end = today;
        var status = PriceCacheStatus.Disabled;
        var message = string.Empty;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = 0, complete = 0, pending = 0, failed = 0, items = 0, active = 0, cancelled = 0;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, window_start, window_end, last_error,
                   indexed_contract_count, indexed_complete_count,
                   indexed_pending_count, indexed_failed_count,
                   indexed_item_count, indexed_active_result_count,
                   indexed_cancelled_result_count
              FROM price_cache_control
             WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            status = (PriceCacheStatus)reader.GetInt32(0);
            start = ParseDate(reader, 1) ?? start;
            end = ParseDate(reader, 2) ?? end;
            message = reader.GetString(3);
            total = reader.GetInt64(4);
            complete = reader.GetInt64(5);
            pending = reader.GetInt64(6);
            failed = reader.GetInt64(7);
            items = reader.GetInt64(8);
            active = reader.GetInt64(9);
            cancelled = reader.GetInt64(10);
        }

        var occupied = checked(items * 900 + (active + cancelled) * 750);
        return new PriceCacheProgress
        {
            Status = status,
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
            Message = message
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

        var itemSource = itemMatch.Length > 0
            ? """
              items_fts
              CROSS JOIN items i ON i.rowid = items_fts.rowid
              """
            : "items i";
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
        if (filters.Sort != SearchSort.Relevance && explicitMatch.Length == 0 &&
            await ShouldUseContractChunksAsync(itemMatch, cancellationToken).ConfigureAwait(false))
        {
            var chunked = await SearchLocalByContractChunksAsync(
                    filters,
                    expression,
                    minimumUnitPrice,
                    maximumUnitPrice,
                    cursor,
                    page,
                    pageSize,
                    cancellationToken)
                .ConfigureAwait(false);
            sqlSpan.Complete(chunked.Rows?.Count ?? 0);
            span.Complete(chunked.Rows?.Count ?? 0);
            return chunked;
        }
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
                  FROM {itemSource}
                  CROSS JOIN contracts c ON c.pncp_id = i.contract_id
                  CROSS JOIN contract_item_snapshots s ON s.contract_id = i.contract_id
                  CROSS JOIN item_results r ON r.contract_id = i.contract_id AND r.item_number = i.item_number
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

    private async Task<bool> ShouldUseContractChunksAsync(
        string itemMatch,
        CancellationToken cancellationToken)
    {
        if (itemMatch.Length == 0)
        {
            return true;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
              FROM (
                  SELECT rowid
                    FROM items_fts
                   WHERE items_fts MATCH $itemMatch
                   LIMIT 20001
              );
            """;
        command.Parameters.AddWithValue("$itemMatch", itemMatch);
        var occurrences = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return occurrences > 20_000;
    }

    private async Task<PriceCacheLocalPage> SearchLocalByContractChunksAsync(
        SearchQuery filters,
        SearchExpression expression,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        PriceCacheLocalCursor? initialCursor,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        using var span = _performance.Begin("price-search", "local-contract-chunks");
        var chunkSize = string.Equals(_connections.ProfileName, "Restrito", StringComparison.Ordinal)
            ? 128
            : 256;
        var matches = new List<(ItemSearchHit Hit, ItemSearchRow Row, PriceCacheLocalCursor Cursor)>(
            pageSize + 1);
        var scanCursor = initialCursor;
        var hasMoreContracts = true;

        while (matches.Count <= pageSize && hasMoreContracts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkStarted = Stopwatch.GetTimestamp();
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.CreateFunction<string?, string?, bool>(
                "pncp_item_matches",
                (description, unit) => expression.MatchesItem(description, unit),
                isDeterministic: true);

            var contractConditions = new List<string>();
            switch (filters.EffectiveGeoFilter.Kind)
            {
                case SearchGeoFilterKind.Southeast:
                    contractConditions.Add("c.uf IN ('ES','MG','RJ','SP')");
                    break;
                case SearchGeoFilterKind.State:
                    contractConditions.Add("c.uf = $uf");
                    break;
                case SearchGeoFilterKind.NearRibeirao:
                    contractConditions.Add("c.geo_layer = 0");
                    break;
            }
            if (filters.StartDate is not null)
            {
                contractConditions.Add("c.publication_date >= $startDate");
            }
            if (filters.EndDate is not null)
            {
                contractConditions.Add("c.publication_date < $endDateExclusive");
            }

            var primaryRank = filters.Sort == SearchSort.Nearest
                ? "CAST(COALESCE(c.geo_layer, 1) AS REAL)"
                : "0.0";
            var secondaryRank = filters.Sort == SearchSort.Nearest
                ? "CAST(COALESCE(c.municipality_distance_rank, 999999) AS REAL)"
                : "0.0";
            if (scanCursor is not null)
            {
                var inclusiveContract = scanCursor.ItemNumber != long.MaxValue;
                var contractComparison = inclusiveContract ? ">=" : ">";
                contractConditions.Add($"""
                    (({primaryRank}) > $cursorPrimary
                     OR (({primaryRank}) = $cursorPrimary AND ({secondaryRank}) > $cursorSecondary)
                     OR (({primaryRank}) = $cursorPrimary AND ({secondaryRank}) = $cursorSecondary
                         AND COALESCE(c.publication_date, '') < $cursorPublication)
                     OR (({primaryRank}) = $cursorPrimary AND ({secondaryRank}) = $cursorSecondary
                         AND COALESCE(c.publication_date, '') = $cursorPublication
                         AND c.pncp_id {contractComparison} $cursorContract))
                    """);
            }

            var contractWhere = contractConditions.Count == 0
                ? string.Empty
                : "WHERE " + string.Join(" AND ", contractConditions);
            var contracts = new List<(string Id, double Primary, double Secondary, string Publication)>(
                chunkSize + 1);
            await using (var contractCommand = connection.CreateCommand())
            {
                contractCommand.CommandText = $"""
                    SELECT c.pncp_id, {primaryRank}, {secondaryRank},
                           COALESCE(c.publication_date, '')
                      FROM contracts c
                      {contractWhere}
                     ORDER BY 2, 3, 4 DESC, c.pncp_id
                     LIMIT $contractLimit;
                    """;
                contractCommand.Parameters.AddWithValue("$contractLimit", chunkSize + 1);
                AddFilterParameters(contractCommand, filters);
                if (scanCursor is not null)
                {
                    contractCommand.Parameters.AddWithValue("$cursorPrimary", scanCursor.PrimaryRank);
                    contractCommand.Parameters.AddWithValue("$cursorSecondary", scanCursor.SecondaryRank);
                    contractCommand.Parameters.AddWithValue("$cursorPublication", scanCursor.PublicationDate);
                    contractCommand.Parameters.AddWithValue("$cursorContract", scanCursor.ContractId);
                }

                await using var reader = await contractCommand.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    contracts.Add((
                        reader.GetString(0),
                        reader.GetDouble(1),
                        reader.GetDouble(2),
                        reader.GetString(3)));
                }
            }

            hasMoreContracts = contracts.Count > chunkSize;
            if (hasMoreContracts)
            {
                contracts.RemoveAt(contracts.Count - 1);
            }
            if (contracts.Count == 0)
            {
                break;
            }

            await using var command = connection.CreateCommand();
            var contractParameters = new string[contracts.Count];
            for (var index = 0; index < contracts.Count; index++)
            {
                contractParameters[index] = $"$contract{index}";
                command.Parameters.AddWithValue(contractParameters[index], contracts[index].Id);
            }

            var itemConditions = new List<string>
            {
                $"c.pncp_id IN ({string.Join(", ", contractParameters)})",
                "i.hydration_status = $complete",
                "COALESCE(s.source_global_updated_at, '') = COALESCE(c.global_updated_at, '')",
                "pncp_item_matches(i.description, i.unit) = 1",
                "r.result_status_id = 1",
                "r.unit_value_scaled > 0"
            };
            if (minimumUnitPrice is not null)
            {
                itemConditions.Add("r.unit_value_scaled >= $minimum");
                command.Parameters.AddWithValue(
                    "$minimum",
                    DecimalScale.ToScaled(minimumUnitPrice.Value)!.Value);
            }
            if (maximumUnitPrice is not null)
            {
                itemConditions.Add("r.unit_value_scaled <= $maximum");
                command.Parameters.AddWithValue(
                    "$maximum",
                    DecimalScale.ToScaled(maximumUnitPrice.Value)!.Value);
            }
            if (scanCursor is not null)
            {
                itemConditions.Add("""
                    ((primary_rank > $itemCursorPrimary)
                     OR (primary_rank = $itemCursorPrimary AND secondary_rank > $itemCursorSecondary)
                     OR (primary_rank = $itemCursorPrimary AND secondary_rank = $itemCursorSecondary
                         AND publication_date < $itemCursorPublication)
                     OR (primary_rank = $itemCursorPrimary AND secondary_rank = $itemCursorSecondary
                         AND publication_date = $itemCursorPublication AND pncp_id > $itemCursorContract)
                     OR (primary_rank = $itemCursorPrimary AND secondary_rank = $itemCursorSecondary
                         AND publication_date = $itemCursorPublication AND pncp_id = $itemCursorContract
                         AND item_number > $itemCursorItem)
                     OR (primary_rank = $itemCursorPrimary AND secondary_rank = $itemCursorSecondary
                         AND publication_date = $itemCursorPublication AND pncp_id = $itemCursorContract
                         AND item_number = $itemCursorItem AND result_sequence > $itemCursorResult))
                    """);
                command.Parameters.AddWithValue("$itemCursorPrimary", scanCursor.PrimaryRank);
                command.Parameters.AddWithValue("$itemCursorSecondary", scanCursor.SecondaryRank);
                command.Parameters.AddWithValue("$itemCursorPublication", scanCursor.PublicationDate);
                command.Parameters.AddWithValue("$itemCursorContract", scanCursor.ContractId);
                command.Parameters.AddWithValue("$itemCursorItem", scanCursor.ItemNumber);
                command.Parameters.AddWithValue("$itemCursorResult", scanCursor.ResultSequence);
            }

            var remaining = pageSize + 1 - matches.Count;
            command.CommandText = $"""
                WITH ranked_items AS (
                    SELECT c.pncp_id, c.cnpj, c.purchase_year, c.purchase_sequence, c.object,
                           c.additional_information, c.process, c.organization, c.unit, c.municipality,
                           c.municipality_ibge_code, c.uf, c.modality_id, c.modality_name, c.status,
                           COALESCE(c.publication_date, '') AS publication_date,
                           c.global_updated_at, c.total_homologated_scaled, c.distance_from_ribeirao_km,
                           i.contract_id, i.item_number, i.description, i.unit, i.requested_quantity_scaled,
                           i.additional_information, i.item_category, i.ncm_nbs_code, i.ncm_nbs_description,
                           i.catalog_code, i.catalog_name, i.catalog_category, i.status, i.has_result,
                           i.source_updated_at, i.hydration_status, i.last_error,
                           0 AS sort_priority, {primaryRank} AS primary_rank,
                           {secondaryRank} AS secondary_rank,
                           r.result_sequence, r.supplier_tax_id, r.supplier_name, r.supplier_type,
                           r.supplier_municipality, r.supplier_uf, r.quantity_scaled,
                           r.unit_value_scaled, r.total_value_scaled, r.result_date,
                           r.result_status_id, r.result_status_name
                      FROM contracts c
                      CROSS JOIN items i ON i.contract_id = c.pncp_id
                      CROSS JOIN contract_item_snapshots s ON s.contract_id = c.pncp_id
                      CROSS JOIN item_results r
                        ON r.contract_id = i.contract_id AND r.item_number = i.item_number
                     WHERE {string.Join(" AND ", itemConditions.Where(value => !value.Contains("primary_rank", StringComparison.Ordinal)))}
                )
                SELECT * FROM ranked_items
                 WHERE {string.Join(" AND ", itemConditions.Where(value => value.Contains("primary_rank", StringComparison.Ordinal)).DefaultIfEmpty("1 = 1"))}
                 ORDER BY sort_priority, primary_rank, secondary_rank,
                          publication_date DESC, pncp_id, item_number, result_sequence
                 LIMIT $resultLimit;
                """;
            command.Parameters.AddWithValue("$complete", (int)ItemHydrationStatus.Complete);
            command.Parameters.AddWithValue("$resultLimit", remaining);

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var contract = ReadContract(reader);
                    var item = ReadItem(reader, 19);
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
                    var rowCursor = new PriceCacheLocalCursor(
                        page,
                        0,
                        reader.GetDouble(37),
                        reader.GetDouble(38),
                        reader.GetString(15),
                        contract.PncpId,
                        item.ItemNumber,
                        result.ResultSequence);
                    matches.Add((
                        new ItemSearchHit(contract, item),
                        new ItemSearchRow(
                            contract,
                            item,
                            result,
                            ItemSearchPriceState.Homologated,
                            "Preço homologado do cache local",
                            false),
                        rowCursor));
                    scanCursor = rowCursor;
                }
            }

            if (matches.Count > pageSize)
            {
                break;
            }

            var lastContract = contracts[^1];
            scanCursor = new PriceCacheLocalCursor(
                page,
                0,
                lastContract.Primary,
                lastContract.Secondary,
                lastContract.Publication,
                lastContract.Id,
                long.MaxValue,
                long.MaxValue);
            var duration = Stopwatch.GetElapsedTime(chunkStarted);
            if (duration < TimeSpan.FromMilliseconds(150))
            {
                chunkSize = Math.Min(512, chunkSize * 2);
            }
            else if (duration > TimeSpan.FromMilliseconds(500))
            {
                chunkSize = Math.Max(64, chunkSize / 2);
            }
        }

        var selected = matches.Take(pageSize).ToArray();
        var hits = selected
            .Select(value => value.Hit)
            .DistinctBy(hit => (hit.Contract.PncpId, hit.Item.ItemNumber))
            .ToArray();
        var hasMore = matches.Count > pageSize;
        var continuation = hasMore
            ? selected[^1].Cursor
            : scanCursor ?? selected.LastOrDefault().Cursor ?? initialCursor;
        span.Complete(selected.Length);
        return new PriceCacheLocalPage(
            hits,
            page,
            pageSize,
            hasMore,
            hits.Length,
            selected.Select(value => value.Row).ToArray(),
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
