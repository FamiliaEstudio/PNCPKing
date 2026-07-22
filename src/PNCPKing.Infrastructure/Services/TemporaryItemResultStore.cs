using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

internal sealed record TemporaryItemResultEntry(
    bool Succeeded,
    string? Error,
    IReadOnlyList<HomologationResult> Results);

/// <summary>
/// Search-session prices deliberately live outside the user's permanent index.
/// The file has no pooling and is deleted on reset/disposal so a crash residue can
/// be discarded before the next session starts.
/// </summary>
internal sealed class TemporaryItemResultStore(string databasePath) : IAsyncDisposable
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
        ForeignKeys = true
    }.ToString();

    public void ClearAbandonedSession() => DeleteFiles();

    public async Task ResetAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        DeleteFiles();
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE session_info(
                id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL
            );

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

            INSERT INTO session_info(id, started_at) VALUES($id, $startedAt);
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
        command.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

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
                    quantity_scaled, unit_value_scaled, total_value_scaled, result_date,
                    result_status_id, result_status_name)
                VALUES($contractId, $itemNumber, $sequence, $taxId, $supplier, $quantity,
                       $unitValue, $totalValue, $resultDate, $statusId, $statusName);
                """;
            insert.Parameters.Add("$contractId", SqliteType.Text);
            insert.Parameters.Add("$itemNumber", SqliteType.Integer);
            insert.Parameters.Add("$sequence", SqliteType.Integer);
            insert.Parameters.Add("$taxId", SqliteType.Text);
            insert.Parameters.Add("$supplier", SqliteType.Text);
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
        DeleteFiles();
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
        HomologatedQuantityScaled = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        HomologatedUnitValueScaled = reader.IsDBNull(6) ? null : reader.GetInt64(6),
        HomologatedTotalValueScaled = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        ResultDate = reader.IsDBNull(8) || !DateOnly.TryParse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? null
            : date,
        ResultStatusId = reader.GetInt32(9),
        ResultStatusName = reader.GetString(10)
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
