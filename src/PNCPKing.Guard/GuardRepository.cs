using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;

namespace PNCPKing.Guard;

internal sealed record GuardLocalWork(
    GuardPlanContract Contract,
    int Attempts);

internal sealed record GuardLocalPackage(
    string PackageId,
    string CampaignId,
    string WorkerId,
    string Sha256,
    string LocalPath,
    string? PublishedPath);

internal sealed record GuardLocalStatus(
    int Pending,
    int Complete,
    int Packaged,
    int Acknowledged,
    int FailedWaiting);

internal sealed class GuardRepository
{
    private readonly string _connectionString;

    public GuardRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS metadata(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS work(
                contract_id TEXT PRIMARY KEY,
                campaign_id TEXT NOT NULL,
                cnpj TEXT NOT NULL,
                purchase_year INTEGER NOT NULL,
                purchase_sequence INTEGER NOT NULL,
                publication_date TEXT,
                global_updated_at TEXT,
                state INTEGER NOT NULL DEFAULT 0,
                attempts INTEGER NOT NULL DEFAULT 0,
                next_retry_at TEXT,
                last_error TEXT NOT NULL DEFAULT '',
                collected_at TEXT,
                package_id TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_guard_work_next
                ON work(state, next_retry_at, contract_id);

            CREATE TABLE IF NOT EXISTS items(
                contract_id TEXT NOT NULL REFERENCES work(contract_id) ON DELETE CASCADE,
                item_number INTEGER NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                additional_information TEXT NOT NULL DEFAULT '',
                requested_quantity_scaled INTEGER,
                unit TEXT NOT NULL DEFAULT '',
                has_result INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(contract_id, item_number)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS results(
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
                    REFERENCES items(contract_id, item_number) ON DELETE CASCADE
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS packages(
                package_id TEXT PRIMARY KEY,
                campaign_id TEXT NOT NULL,
                worker_id TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                local_path TEXT NOT NULL,
                published_path TEXT,
                created_at TEXT NOT NULL
            ) WITHOUT ROWID;

            UPDATE work SET state = 0 WHERE state = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ApplyPlanAsync(GuardWorkerPlan plan, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadMetadataAsync(connection, (SqliteTransaction)transaction, "campaign_id", cancellationToken)
            .ConfigureAwait(false);
        var changed = !string.Equals(current, plan.CampaignId, StringComparison.Ordinal);
        if (changed)
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = """
                DELETE FROM packages;
                DELETE FROM work;
                DELETE FROM metadata;
                """;
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO work(
                contract_id, campaign_id, cnpj, purchase_year, purchase_sequence,
                publication_date, global_updated_at, state)
            VALUES($id, $campaign, $cnpj, $year, $sequence, $publication, $updated, 0)
            ON CONFLICT(contract_id) DO NOTHING;
            """;
        insert.Parameters.Add("$id", SqliteType.Text);
        insert.Parameters.Add("$campaign", SqliteType.Text);
        insert.Parameters.Add("$cnpj", SqliteType.Text);
        insert.Parameters.Add("$year", SqliteType.Integer);
        insert.Parameters.Add("$sequence", SqliteType.Integer);
        insert.Parameters.Add("$publication", SqliteType.Text);
        insert.Parameters.Add("$updated", SqliteType.Text);
        foreach (var contract in plan.Contracts)
        {
            insert.Parameters["$id"].Value = contract.PncpId;
            insert.Parameters["$campaign"].Value = plan.CampaignId;
            insert.Parameters["$cnpj"].Value = contract.Cnpj;
            insert.Parameters["$year"].Value = contract.PurchaseYear;
            insert.Parameters["$sequence"].Value = contract.PurchaseSequence;
            insert.Parameters["$publication"].Value = DbValue(contract.PublicationDate);
            insert.Parameters["$updated"].Value = DbValue(contract.GlobalUpdatedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await SetMetadataAsync(connection, (SqliteTransaction)transaction, "campaign_id", plan.CampaignId, cancellationToken)
            .ConfigureAwait(false);
        await SetMetadataAsync(connection, (SqliteTransaction)transaction, "worker_id", plan.Worker.Id, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<GuardLocalWork?> AcquireNextAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        GuardLocalWork? work = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = """
                SELECT contract_id, cnpj, purchase_year, purchase_sequence,
                       publication_date, global_updated_at, attempts
                  FROM work
                 WHERE state = 0
                   AND (next_retry_at IS NULL OR next_retry_at <= $now)
                 ORDER BY COALESCE(next_retry_at, ''), contract_id
                 LIMIT 1;
                """;
            select.Parameters.AddWithValue("$now", FormatDateTime(now));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                work = new GuardLocalWork(
                    new GuardPlanContract
                    {
                        PncpId = reader.GetString(0),
                        Cnpj = reader.GetString(1),
                        PurchaseYear = reader.GetInt32(2),
                        PurchaseSequence = reader.GetInt32(3),
                        PublicationDate = ParseDateTime(reader, 4),
                        GlobalUpdatedAt = ParseDateTime(reader, 5)
                    },
                    reader.GetInt32(6));
            }
        }

        if (work is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE work SET state = 1 WHERE contract_id = $id AND state = 0;";
            update.Parameters.AddWithValue("$id", work.Contract.PncpId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                work = null;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return work;
    }

    public async Task SaveSnapshotAsync(
        GuardContractSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqlTransaction = (SqliteTransaction)transaction;
        await ExecuteAsync(connection, sqlTransaction,
            "DELETE FROM results WHERE contract_id = $id; DELETE FROM items WHERE contract_id = $id;",
            ("$id", snapshot.Contract.PncpId), cancellationToken).ConfigureAwait(false);

        await using var itemCommand = connection.CreateCommand();
        itemCommand.Transaction = sqlTransaction;
        itemCommand.CommandText = """
            INSERT INTO items(
                contract_id, item_number, description, additional_information,
                requested_quantity_scaled, unit, has_result)
            VALUES($contract, $number, $description, $additional, $quantity, $unit, $hasResult);
            """;
        foreach (var name in new[] { "$contract", "$number", "$description", "$additional", "$quantity", "$unit", "$hasResult" })
        {
            itemCommand.Parameters.Add(name, name is "$number" or "$quantity" or "$hasResult"
                ? SqliteType.Integer
                : SqliteType.Text);
        }

        foreach (var item in snapshot.Items)
        {
            itemCommand.Parameters["$contract"].Value = snapshot.Contract.PncpId;
            itemCommand.Parameters["$number"].Value = item.ItemNumber;
            itemCommand.Parameters["$description"].Value = item.Description;
            itemCommand.Parameters["$additional"].Value = item.AdditionalInformation;
            itemCommand.Parameters["$quantity"].Value = DbValue(item.RequestedQuantityScaled);
            itemCommand.Parameters["$unit"].Value = item.Unit;
            itemCommand.Parameters["$hasResult"].Value = item.HasResult ? 1 : 0;
            await itemCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var resultCommand = connection.CreateCommand();
        resultCommand.Transaction = sqlTransaction;
        resultCommand.CommandText = """
            INSERT INTO results(
                contract_id, item_number, result_sequence, supplier_tax_id, supplier_name,
                supplier_type, supplier_municipality, supplier_uf, quantity_scaled,
                unit_value_scaled, total_value_scaled, result_date, result_status_id,
                result_status_name)
            VALUES($contract, $item, $sequence, $taxId, $name, $type, $municipality,
                   $uf, $quantity, $unitValue, $totalValue, $date, $statusId, $statusName);
            """;
        foreach (var name in new[]
                 {
                     "$contract", "$item", "$sequence", "$taxId", "$name", "$type", "$municipality",
                     "$uf", "$quantity", "$unitValue", "$totalValue", "$date", "$statusId", "$statusName"
                 })
        {
            resultCommand.Parameters.Add(name, name is "$item" or "$sequence" or "$quantity" or "$unitValue" or "$totalValue" or "$statusId"
                ? SqliteType.Integer
                : SqliteType.Text);
        }

        foreach (var result in snapshot.Results)
        {
            resultCommand.Parameters["$contract"].Value = snapshot.Contract.PncpId;
            resultCommand.Parameters["$item"].Value = result.ItemNumber;
            resultCommand.Parameters["$sequence"].Value = result.ResultSequence;
            resultCommand.Parameters["$taxId"].Value = result.SupplierTaxId;
            resultCommand.Parameters["$name"].Value = result.SupplierName;
            resultCommand.Parameters["$type"].Value = result.SupplierType;
            resultCommand.Parameters["$municipality"].Value = result.SupplierMunicipality;
            resultCommand.Parameters["$uf"].Value = result.SupplierUf;
            resultCommand.Parameters["$quantity"].Value = DbValue(result.HomologatedQuantityScaled);
            resultCommand.Parameters["$unitValue"].Value = DbValue(result.HomologatedUnitValueScaled);
            resultCommand.Parameters["$totalValue"].Value = DbValue(result.HomologatedTotalValueScaled);
            resultCommand.Parameters["$date"].Value = result.ResultDate is null
                ? DBNull.Value
                : result.ResultDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            resultCommand.Parameters["$statusId"].Value = result.ResultStatusId;
            resultCommand.Parameters["$statusName"].Value = result.ResultStatusName;
            await resultCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, sqlTransaction, """
            UPDATE work
               SET state = 2, attempts = 0, next_retry_at = NULL, last_error = '',
                   collected_at = $collected, package_id = NULL
             WHERE contract_id = $id;
            """,
            ("$collected", FormatDateTime(snapshot.CollectedAt)),
            ("$id", snapshot.Contract.PncpId), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailureAsync(
        string contractId,
        string error,
        DateTimeOffset nextRetryAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE work
               SET state = 0, attempts = attempts + 1, last_error = $error,
                   next_retry_at = $retry
             WHERE contract_id = $id;
            """;
        command.Parameters.AddWithValue("$error", error.Length <= 2000 ? error : error[..2000]);
        command.Parameters.AddWithValue("$retry", FormatDateTime(nextRetryAt));
        command.Parameters.AddWithValue("$id", contractId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReturnToPendingAsync(string contractId)
    {
        await using var connection = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE work SET state = 0 WHERE contract_id = $id AND state = 1;";
        command.Parameters.AddWithValue("$id", contractId);
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GuardContractSnapshot>> ReadReadySnapshotsAsync(
        int maximumContracts,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT contract_id FROM work
                 WHERE state = 2
                 ORDER BY collected_at, contract_id
                 LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", maximumContracts);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var snapshots = new List<GuardContractSnapshot>(ids.Count);
        foreach (var id in ids)
        {
            snapshots.Add(await ReadSnapshotAsync(connection, id, cancellationToken).ConfigureAwait(false));
        }

        return snapshots;
    }

    public async Task RecordPackageAsync(
        GuardPackageManifest manifest,
        string sha256,
        string localPath,
        IReadOnlyList<string> contractIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqlTransaction = (SqliteTransaction)transaction;
        await ExecuteAsync(connection, sqlTransaction, """
            INSERT INTO packages(
                package_id, campaign_id, worker_id, sha256, local_path, created_at)
            VALUES($id, $campaign, $worker, $sha, $path, $created);
            """,
            ("$id", manifest.PackageId),
            ("$campaign", manifest.CampaignId),
            ("$worker", manifest.WorkerId),
            ("$sha", sha256),
            ("$path", localPath),
            ("$created", FormatDateTime(manifest.CreatedAt)), cancellationToken).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = sqlTransaction;
        update.CommandText = "UPDATE work SET state = 3, package_id = $package WHERE contract_id = $contract AND state = 2;";
        update.Parameters.Add("$package", SqliteType.Text);
        update.Parameters.Add("$contract", SqliteType.Text);
        foreach (var contractId in contractIds)
        {
            update.Parameters["$package"].Value = manifest.PackageId;
            update.Parameters["$contract"].Value = contractId;
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("O checkpoint mudou durante a criação do pacote.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GuardLocalPackage>> ReadPackagesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT package_id, campaign_id, worker_id, sha256, local_path, published_path
              FROM packages ORDER BY created_at;
            """;
        var packages = new List<GuardLocalPackage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            packages.Add(new GuardLocalPackage(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return packages;
    }

    public async Task MarkPublishedAsync(string packageId, string path, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE packages SET published_path = $path WHERE package_id = $id;";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$id", packageId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AcknowledgeAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqlTransaction = (SqliteTransaction)transaction;
        await ExecuteAsync(connection, sqlTransaction, """
            DELETE FROM results
             WHERE contract_id IN (SELECT contract_id FROM work WHERE package_id = $id);
            DELETE FROM items
             WHERE contract_id IN (SELECT contract_id FROM work WHERE package_id = $id);
            UPDATE work SET state = 4, package_id = NULL WHERE package_id = $id;
            DELETE FROM packages WHERE package_id = $id;
            """, ("$id", packageId), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GuardLocalStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN state IN (0, 1) THEN 1 ELSE 0 END),
                SUM(CASE WHEN state = 2 THEN 1 ELSE 0 END),
                SUM(CASE WHEN state = 3 THEN 1 ELSE 0 END),
                SUM(CASE WHEN state = 4 THEN 1 ELSE 0 END),
                SUM(CASE WHEN state = 0 AND next_retry_at IS NOT NULL THEN 1 ELSE 0 END)
              FROM work;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new GuardLocalStatus(
            ReadCount(reader, 0), ReadCount(reader, 1), ReadCount(reader, 2),
            ReadCount(reader, 3), ReadCount(reader, 4));
    }

    private static async Task<GuardContractSnapshot> ReadSnapshotAsync(
        SqliteConnection connection,
        string contractId,
        CancellationToken cancellationToken)
    {
        GuardPlanContract contract;
        DateTimeOffset collected;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT contract_id, cnpj, purchase_year, purchase_sequence,
                       publication_date, global_updated_at, collected_at
                  FROM work WHERE contract_id = $id AND state = 2;
                """;
            command.Parameters.AddWithValue("$id", contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Snapshot pronto não encontrado.");
            }

            contract = new GuardPlanContract
            {
                PncpId = reader.GetString(0),
                Cnpj = reader.GetString(1),
                PurchaseYear = reader.GetInt32(2),
                PurchaseSequence = reader.GetInt32(3),
                PublicationDate = ParseDateTime(reader, 4),
                GlobalUpdatedAt = ParseDateTime(reader, 5)
            };
            collected = ParseDateTime(reader, 6) ?? throw new InvalidDataException("Data da coleta ausente.");
        }

        var items = new List<GuardItem>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT item_number, description, additional_information,
                       requested_quantity_scaled, unit, has_result
                  FROM items WHERE contract_id = $id ORDER BY item_number;
                """;
            command.Parameters.AddWithValue("$id", contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new GuardItem
                {
                    ItemNumber = reader.GetInt64(0),
                    Description = reader.GetString(1),
                    AdditionalInformation = reader.GetString(2),
                    RequestedQuantityScaled = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    Unit = reader.GetString(4),
                    HasResult = reader.GetInt64(5) == 1
                });
            }
        }

        var results = new List<GuardResult>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT item_number, result_sequence, supplier_tax_id, supplier_name,
                       supplier_type, supplier_municipality, supplier_uf, quantity_scaled,
                       unit_value_scaled, total_value_scaled, result_date, result_status_id,
                       result_status_name
                  FROM results WHERE contract_id = $id
                 ORDER BY item_number, result_sequence;
                """;
            command.Parameters.AddWithValue("$id", contractId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new GuardResult
                {
                    ItemNumber = reader.GetInt64(0),
                    ResultSequence = reader.GetInt64(1),
                    SupplierTaxId = reader.GetString(2),
                    SupplierName = reader.GetString(3),
                    SupplierType = reader.GetString(4),
                    SupplierMunicipality = reader.GetString(5),
                    SupplierUf = reader.GetString(6),
                    HomologatedQuantityScaled = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    HomologatedUnitValueScaled = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    HomologatedTotalValueScaled = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    ResultDate = reader.IsDBNull(10) ? null : DateOnly.ParseExact(reader.GetString(10), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ResultStatusId = reader.GetInt32(11),
                    ResultStatusName = reader.GetString(12)
                });
            }
        }

        return new GuardContractSnapshot
        {
            Contract = contract,
            CollectedAt = collected,
            Items = items,
            Results = results
        };
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object Value) parameter,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction, sql, [parameter], cancellationToken).ConfigureAwait(false);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object Value) parameter1,
        (string Name, object Value) parameter2,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction, sql, [parameter1, parameter2], cancellationToken).ConfigureAwait(false);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object Value) parameter1,
        (string Name, object Value) parameter2,
        (string Name, object Value) parameter3,
        (string Name, object Value) parameter4,
        (string Name, object Value) parameter5,
        (string Name, object Value) parameter6,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction, sql,
            [parameter1, parameter2, parameter3, parameter4, parameter5, parameter6], cancellationToken).ConfigureAwait(false);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task SetMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO metadata(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int ReadCount(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);

    private static object DbValue(DateTimeOffset? value) =>
        value is null ? DBNull.Value : FormatDateTime(value.Value);

    private static object DbValue(long? value) => value is null ? DBNull.Value : value.Value;

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseDateTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : null;
}
