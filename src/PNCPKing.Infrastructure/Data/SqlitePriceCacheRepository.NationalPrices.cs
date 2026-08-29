using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Data;

public sealed partial class SqlitePriceCacheRepository
{
    private const long MinimumNationalPriceBytesPerItem = 250;
    private const long MaximumNationalPriceBytesPerItem = 750;
    private const long EstimatedNetworkBytesPerResultCall = 1_240;
    private const long NationalPriceMinimumSafetyReserve = 2L * 1024 * 1024 * 1024;

    public async Task<NationalPriceIndexPolicy> GetNationalPriceIndexPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT authorized, enabled, paused, status, window_start, window_end,
                   authorized_at, last_started_at, last_completed_at, last_error
              FROM national_price_index_control WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new NationalPriceIndexPolicy();
        }

        return new NationalPriceIndexPolicy
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

    public async Task<NationalPriceIndexEstimate> EstimateNationalPriceIndexAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (startDate > endDate)
        {
            throw new ArgumentException("A data inicial deve ser anterior ou igual à final.");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long eligible = 0;
        long complete = 0;
        var prepared = false;
        await using (var state = connection.CreateCommand())
        {
            state.CommandText = """
                SELECT prepared_window_start, prepared_window_end,
                       eligible_item_count, completed_item_count
                  FROM national_price_index_control WHERE id = 1;
                """;
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                ParseDate(reader, 0) == startDate && ParseDate(reader, 1) == endDate)
            {
                eligible = reader.GetInt64(2);
                complete = reader.GetInt64(3);
                prepared = true;
            }
        }

        if (!prepared)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*),
                       SUM(CASE WHEN i.hydration_status = $complete THEN 1 ELSE 0 END)
                  FROM items i
                  JOIN contracts c ON c.pncp_id = i.contract_id
                  JOIN contract_item_snapshots s ON s.contract_id = i.contract_id
                 WHERE i.has_result = 1
                   AND c.publication_date >= $start
                   AND c.publication_date < $endExclusive
                   AND COALESCE(s.source_global_updated_at, '') = COALESCE(c.global_updated_at, '');
                """;
            command.Parameters.AddWithValue("$complete", (int)ItemHydrationStatus.Complete);
            command.Parameters.AddWithValue("$start", FormatDate(startDate));
            command.Parameters.AddWithValue("$endExclusive", FormatDate(endDate.AddDays(1)));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            eligible = reader.GetInt64(0);
            complete = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        }
        var remaining = Math.Max(0, eligible - complete);
        var minimumBytes = SaturatingMultiply(remaining, MinimumNationalPriceBytesPerItem);
        var maximumBytes = SaturatingMultiply(remaining, MaximumNationalPriceBytesPerItem);
        var reserve = Math.Max(
            NationalPriceMinimumSafetyReserve,
            (long)Math.Ceiling(maximumBytes * 0.20d));

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

        return new NationalPriceIndexEstimate
        {
            StartDate = startDate,
            EndDate = endDate,
            EligibleItems = eligible,
            CompletedItems = complete,
            EstimatedMinimumBytes = minimumBytes,
            EstimatedMaximumBytes = maximumBytes,
            EstimatedNetworkBytes = SaturatingMultiply(remaining, EstimatedNetworkBytesPerResultCall),
            AvailableFreeBytes = available,
            SafetyReserveBytes = reserve,
            EstimatedMinimumDuration = TimeSpan.FromSeconds(
                Math.Min(TimeSpan.MaxValue.TotalSeconds, remaining / 100d)),
            EstimatedMaximumDuration = TimeSpan.FromSeconds(
                Math.Min(TimeSpan.MaxValue.TotalSeconds, remaining / 10d))
        };
    }

    public async Task SetNationalPriceIndexAuthorizationAsync(
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
            UPDATE national_price_index_control
               SET authorized = $authorized,
                   enabled = $authorized,
                   paused = 0,
                   status = $status,
                   window_start = $start,
                   window_end = $end,
                   authorized_at = CASE WHEN $authorized = 1 THEN $now ELSE authorized_at END,
                   prepared_window_start = CASE WHEN $authorized = 1 THEN NULL ELSE prepared_window_start END,
                   prepared_window_end = CASE WHEN $authorized = 1 THEN NULL ELSE prepared_window_end END,
                   eligible_item_count = CASE WHEN $authorized = 1 THEN 0 ELSE eligible_item_count END,
                   completed_item_count = CASE WHEN $authorized = 1 THEN 0 ELSE completed_item_count END,
                   priced_item_count = CASE WHEN $authorized = 1 THEN 0 ELSE priced_item_count END,
                   result_row_count = CASE WHEN $authorized = 1 THEN 0 ELSE result_row_count END,
                   pending_contract_count = CASE WHEN $authorized = 1 THEN 0 ELSE pending_contract_count END,
                   failed_contract_count = CASE WHEN $authorized = 1 THEN 0 ELSE failed_contract_count END,
                   last_error = '',
                   updated_at = $now
             WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$authorized", authorized ? 1 : 0);
        command.Parameters.AddWithValue(
            "$status",
            (int)(authorized ? PriceCacheStatus.Idle : PriceCacheStatus.Disabled));
        command.Parameters.AddWithValue("$start", FormatDate(startDate));
        command.Parameters.AddWithValue("$end", FormatDate(endDate));
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetNationalPriceIndexPausedAsync(
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
            UPDATE national_price_index_control
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
        command.Parameters.AddWithValue(
            "$space",
            reason?.Contains("espaço", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0);
        command.Parameters.AddWithValue("$insufficient", (int)PriceCacheStatus.InsufficientSpace);
        command.Parameters.AddWithValue("$pausedStatus", (int)PriceCacheStatus.Paused);
        command.Parameters.AddWithValue("$idle", (int)PriceCacheStatus.Idle);
        command.Parameters.AddWithValue("$reason", reason?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetNationalPriceIndexStatusAsync(
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
            UPDATE national_price_index_control
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

    public async Task PrepareNationalPriceIndexAsync(
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
                  FROM national_price_index_control WHERE id = 1;
                """;
            await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            prepared = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                       ParseDate(reader, 0) == startDate && ParseDate(reader, 1) == endDate;
        }

        await using (var control = connection.CreateCommand())
        {
            control.Transaction = (SqliteTransaction)transaction;
            control.CommandText = """
                UPDATE national_price_index_control
                   SET window_start = $start, window_end = $end,
                       statistics_suspended = $suspended, updated_at = $now
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
                UPDATE items SET hydration_status = $notLoaded,
                                 last_error = 'Consulta interrompida; item pendente para retomada.'
                 WHERE hydration_status = $loading AND has_result = 1;
                UPDATE price_cache_contracts
                   SET price_index_status = $pending,
                       price_index_last_error = '',
                       price_index_next_retry_at = NULL,
                       updated_at = $now
                 WHERE price_index_status = $downloading;
                """;
            recovery.Parameters.AddWithValue("$notLoaded", (int)ItemHydrationStatus.NotLoaded);
            recovery.Parameters.AddWithValue("$loading", (int)ItemHydrationStatus.Loading);
            recovery.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
            recovery.Parameters.AddWithValue("$downloading", (int)PriceCacheContractStatus.Downloading);
            recovery.Parameters.AddWithValue("$now", now);
            await recovery.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (prepared)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (var checkpoints = connection.CreateCommand())
        {
            checkpoints.Transaction = (SqliteTransaction)transaction;
            checkpoints.CommandText = """
                UPDATE price_cache_contracts
                   SET price_index_eligible_item_count =
                           (SELECT COUNT(*) FROM items
                             WHERE contract_id = price_cache_contracts.contract_id AND has_result = 1),
                       price_index_completed_item_count =
                           (SELECT COUNT(*) FROM items
                             WHERE contract_id = price_cache_contracts.contract_id AND has_result = 1
                               AND hydration_status = $itemComplete),
                       price_index_priced_item_count =
                           (SELECT COUNT(*) FROM items i
                             WHERE i.contract_id = price_cache_contracts.contract_id
                               AND i.has_result = 1 AND i.hydration_status = $itemComplete
                               AND EXISTS(
                                   SELECT 1 FROM item_results r
                                    WHERE r.contract_id = i.contract_id
                                      AND r.item_number = i.item_number
                                      AND r.result_status_id = 1 AND r.unit_value_scaled > 0)),
                       price_index_result_count =
                           (SELECT COUNT(*) FROM item_results
                             WHERE contract_id = price_cache_contracts.contract_id
                               AND result_status_id = 1 AND unit_value_scaled > 0),
                       price_index_status = CASE WHEN EXISTS(
                           SELECT 1 FROM items
                            WHERE contract_id = price_cache_contracts.contract_id
                              AND has_result = 1 AND hydration_status <> $itemComplete)
                           THEN $pending ELSE $complete END,
                       price_index_attempts = 0,
                       price_index_last_error = '',
                       price_index_next_retry_at = NULL,
                       price_index_started_at = NULL,
                       price_index_completed_at = CASE WHEN EXISTS(
                           SELECT 1 FROM items
                            WHERE contract_id = price_cache_contracts.contract_id
                              AND has_result = 1 AND hydration_status <> $itemComplete)
                           THEN NULL ELSE COALESCE(price_index_completed_at, $now) END,
                       updated_at = $now
                 WHERE publication_date >= $start AND publication_date < $endExclusive;
                """;
            checkpoints.Parameters.AddWithValue("$itemComplete", (int)ItemHydrationStatus.Complete);
            checkpoints.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
            checkpoints.Parameters.AddWithValue("$complete", (int)PriceCacheContractStatus.Complete);
            checkpoints.Parameters.AddWithValue("$start", FormatDate(startDate));
            checkpoints.Parameters.AddWithValue("$endExclusive", FormatDate(endDate.AddDays(1)));
            checkpoints.Parameters.AddWithValue("$now", now);
            await checkpoints.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var statistics = connection.CreateCommand())
        {
            statistics.Transaction = (SqliteTransaction)transaction;
            statistics.CommandText = """
                WITH totals AS (
                    SELECT COALESCE(SUM(price_index_eligible_item_count), 0) AS eligible,
                           COALESCE(SUM(price_index_completed_item_count), 0) AS completed,
                           COALESCE(SUM(price_index_priced_item_count), 0) AS priced,
                           COALESCE(SUM(price_index_result_count), 0) AS results,
                           SUM(CASE WHEN price_index_status IN ($pending, $downloading) THEN 1 ELSE 0 END) AS pending,
                           SUM(CASE WHEN price_index_status = $failed THEN 1 ELSE 0 END) AS failed
                      FROM price_cache_contracts
                     WHERE publication_date >= $start AND publication_date < $endExclusive
                )
                UPDATE national_price_index_control
                   SET prepared_window_start = $start,
                       prepared_window_end = $end,
                       eligible_item_count = (SELECT eligible FROM totals),
                       completed_item_count = (SELECT completed FROM totals),
                       priced_item_count = (SELECT priced FROM totals),
                       result_row_count = (SELECT results FROM totals),
                       pending_contract_count = COALESCE((SELECT pending FROM totals), 0),
                       failed_contract_count = COALESCE((SELECT failed FROM totals), 0),
                       statistics_suspended = 0,
                       updated_at = $now
                 WHERE id = 1;
                """;
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

    internal const string NationalPriceWorkSelectionSql = """
            WITH policy AS (
                SELECT authorized, enabled, paused, window_start,
                       date(window_end, '+1 day') AS window_end_exclusive
                  FROM national_price_index_control WHERE id = 1
            ),
            pending_candidate AS (
                SELECT pc.contract_id, pc.publication_date, 0 AS queue_priority
                  FROM price_cache_contracts pc INDEXED BY idx_price_cache_contracts_price_work
                  CROSS JOIN policy
                 WHERE pc.status = $listComplete
                   AND pc.publication_date >= policy.window_start
                   AND pc.publication_date < policy.window_end_exclusive
                   AND pc.price_index_status = $pending
                 ORDER BY pc.price_index_next_retry_at,
                          pc.publication_date DESC, pc.contract_id
                 LIMIT 1
            ),
            failed_candidate AS (
                SELECT pc.contract_id, pc.publication_date, 1 AS queue_priority
                  FROM price_cache_contracts pc INDEXED BY idx_price_cache_contracts_price_work
                  CROSS JOIN policy
                 WHERE pc.status = $listComplete
                   AND pc.publication_date >= policy.window_start
                   AND pc.publication_date < policy.window_end_exclusive
                   AND pc.price_index_status = $failed
                   AND (pc.price_index_next_retry_at IS NULL OR pc.price_index_next_retry_at <= $now)
                 ORDER BY pc.price_index_next_retry_at,
                          pc.publication_date DESC, pc.contract_id
                 LIMIT 1
            ),
            next_candidate AS (
                SELECT contract_id, publication_date, queue_priority FROM pending_candidate
                UNION ALL
                SELECT contract_id, publication_date, queue_priority FROM failed_candidate
                ORDER BY queue_priority, publication_date DESC, contract_id
                LIMIT 1
            )
            SELECT c.pncp_id, c.cnpj, c.purchase_year, c.purchase_sequence, c.object,
                   c.additional_information, c.process, c.organization, c.unit, c.municipality,
                   c.municipality_ibge_code, c.uf, c.modality_id, c.modality_name, c.status,
                   c.publication_date, c.global_updated_at, c.total_homologated_scaled,
                   c.distance_from_ribeirao_km,
                   pc.price_index_status, pc.price_index_attempts,
                   pc.price_index_next_retry_at, pc.price_index_last_error,
                   pc.price_index_eligible_item_count, pc.price_index_completed_item_count,
                   pc.price_index_priced_item_count, pc.price_index_result_count
              FROM next_candidate next
              JOIN price_cache_contracts pc ON pc.contract_id = next.contract_id
              JOIN contracts c ON c.pncp_id = pc.contract_id
              JOIN policy ON policy.authorized = 1 AND policy.enabled = 1 AND policy.paused = 0;
            """;

    public async Task<NationalPriceIndexWorkItem?> GetNextNationalPriceWorkAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = NationalPriceWorkSelectionSql;
        command.Parameters.AddWithValue("$listComplete", (int)PriceCacheContractStatus.Complete);
        command.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
        command.Parameters.AddWithValue("$failed", (int)PriceCacheContractStatus.Failed);
        command.Parameters.AddWithValue("$now", FormatDateTime(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var contract = ReadContract(reader);
        return new NationalPriceIndexWorkItem(contract, new NationalPriceIndexCheckpoint
        {
            ContractId = contract.PncpId,
            Status = (PriceCacheContractStatus)reader.GetInt32(19),
            Attempts = reader.GetInt32(20),
            NextRetryAt = ParseDateTime(reader, 21),
            LastError = reader.GetString(22),
            EligibleItems = reader.GetInt32(23),
            CompletedItems = reader.GetInt32(24),
            PricedItems = reader.GetInt32(25),
            ResultRows = reader.GetInt32(26)
        });
    }

    public Task MarkNationalPriceContractDownloadingAsync(
        string contractId,
        CancellationToken cancellationToken = default) =>
        ExecuteContractUpdateAsync(
            """
            UPDATE price_cache_contracts
               SET price_index_status = $status,
                   price_index_attempts = price_index_attempts + 1,
                   price_index_started_at = $now,
                   price_index_last_error = '',
                   price_index_next_retry_at = NULL,
                   updated_at = $now
             WHERE contract_id = $contractId;
            """,
            contractId,
            (command, _) => command.Parameters.AddWithValue(
                "$status", (int)PriceCacheContractStatus.Downloading),
            cancellationToken);

    public async Task MarkNationalPriceContractCompleteAsync(
        string contractId,
        CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE price_cache_contracts
               SET price_index_status = $complete,
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
                   price_index_last_error = '',
                   price_index_next_retry_at = NULL,
                   price_index_completed_at = $now,
                   updated_at = $now
             WHERE contract_id = $contractId;
            """;
        command.Parameters.AddWithValue("$contractId", contractId);
        command.Parameters.AddWithValue("$complete", (int)PriceCacheContractStatus.Complete);
        command.Parameters.AddWithValue("$itemComplete", (int)ItemHydrationStatus.Complete);
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task MarkNationalPriceContractFailedAsync(
        string contractId,
        string error,
        DateTimeOffset nextRetryAt,
        CancellationToken cancellationToken = default) =>
        ExecuteContractUpdateAsync(
            """
            UPDATE price_cache_contracts
               SET price_index_status = $status,
                   price_index_last_error = $error,
                   price_index_next_retry_at = $retry,
                   updated_at = $now
             WHERE contract_id = $contractId;
            """,
            contractId,
            (command, _) =>
            {
                command.Parameters.AddWithValue("$status", (int)PriceCacheContractStatus.Failed);
                command.Parameters.AddWithValue("$error", error.Trim());
                command.Parameters.AddWithValue("$retry", FormatDateTime(nextRetryAt));
            },
            cancellationToken);

    public Task MarkNationalPriceContractPendingAsync(
        string contractId,
        string? message = null,
        CancellationToken cancellationToken = default) =>
        ExecuteContractUpdateAsync(
            """
            UPDATE price_cache_contracts
               SET price_index_status = $status,
                   price_index_last_error = $message,
                   price_index_next_retry_at = NULL,
                   updated_at = $now
             WHERE contract_id = $contractId;
            """,
            contractId,
            (command, _) =>
            {
                command.Parameters.AddWithValue("$status", (int)PriceCacheContractStatus.Pending);
                command.Parameters.AddWithValue("$message", message?.Trim() ?? string.Empty);
            },
            cancellationToken);

    public async Task<NationalPriceIndexProgress> GetNationalPriceIndexProgressAsync(
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-364);
        var end = today;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, window_start, window_end, last_error,
                   eligible_item_count, completed_item_count, priced_item_count,
                   result_row_count, pending_contract_count, failed_contract_count
              FROM national_price_index_control WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new NationalPriceIndexProgress
            {
                Status = PriceCacheStatus.NotAuthorized,
                StartDate = start,
                EndDate = end,
                EligibleItems = 0,
                CompletedItems = 0,
                PricedItems = 0,
                ResultRows = 0,
                NoPriceItems = 0,
                PendingContracts = 0,
                FailedContracts = 0,
                OccupiedBytes = 0
            };
        }

        var status = (PriceCacheStatus)reader.GetInt32(0);
        start = ParseDate(reader, 1) ?? start;
        end = ParseDate(reader, 2) ?? end;
        var message = reader.GetString(3);
        var eligible = reader.GetInt64(4);
        var completed = reader.GetInt64(5);
        var priced = reader.GetInt64(6);
        var results = reader.GetInt64(7);
        return new NationalPriceIndexProgress
        {
            Status = status,
            StartDate = start,
            EndDate = end,
            EligibleItems = eligible,
            CompletedItems = completed,
            PricedItems = priced,
            ResultRows = results,
            NoPriceItems = Math.Max(0, completed - priced),
            PendingContracts = reader.GetInt64(8),
            FailedContracts = reader.GetInt64(9),
            OccupiedBytes = SaturatingMultiply(results, MinimumNationalPriceBytesPerItem),
            Message = message
        };
    }

    public async Task RemoveBackgroundPricesAsync(CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS national_price_remove_ids(
                contract_id TEXT PRIMARY KEY
            ) WITHOUT ROWID;
            DELETE FROM national_price_remove_ids;
            INSERT INTO national_price_remove_ids(contract_id)
            SELECT contract_id FROM price_cache_contracts pc
             WHERE background_owned = 1 AND user_pinned = 0
               AND NOT EXISTS(
                   SELECT 1 FROM quotation_references qr
                    WHERE qr.contract_id = pc.contract_id);

            DELETE FROM item_results
             WHERE contract_id IN (SELECT contract_id FROM national_price_remove_ids);
            UPDATE items
               SET hydration_status = CASE WHEN has_result = 1 THEN $notLoaded ELSE hydration_status END,
                   last_error = NULL
             WHERE contract_id IN (SELECT contract_id FROM national_price_remove_ids);
            UPDATE price_cache_contracts
               SET price_index_status = $pending,
                   price_index_attempts = 0,
                   price_index_last_error = '',
                   price_index_next_retry_at = NULL,
                   price_index_started_at = NULL,
                   price_index_completed_at = NULL,
                   price_index_completed_item_count = 0,
                   price_index_priced_item_count = 0,
                   price_index_result_count = 0,
                   updated_at = $now
             WHERE contract_id IN (SELECT contract_id FROM national_price_remove_ids);
            UPDATE national_price_index_control
               SET authorized = 0, enabled = 0, paused = 0, status = $disabled,
                   prepared_window_start = NULL, prepared_window_end = NULL,
                   eligible_item_count = 0, completed_item_count = 0,
                   priced_item_count = 0, result_row_count = 0,
                   pending_contract_count = 0, failed_contract_count = 0,
                   last_error = '', updated_at = $now
             WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$notLoaded", (int)ItemHydrationStatus.NotLoaded);
        command.Parameters.AddWithValue("$pending", (int)PriceCacheContractStatus.Pending);
        command.Parameters.AddWithValue("$disabled", (int)PriceCacheStatus.Disabled);
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long SaturatingMultiply(long left, long right)
    {
        if (left <= 0 || right <= 0)
        {
            return 0;
        }

        return left > long.MaxValue / right ? long.MaxValue : left * right;
    }
}
