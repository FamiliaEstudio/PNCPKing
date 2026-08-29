using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Infrastructure.Services;

public sealed record GuardWorkerInput(string Name, int Weight);

public sealed record GuardCampaignResult(
    string CampaignId,
    string MasterId,
    int ContractCount,
    IReadOnlyList<(string WorkerName, string WorkerId, int ContractCount)> Workers);

public sealed record GuardImportResult(
    int PackageFiles,
    int ImportedPackages,
    int DuplicatePackages,
    int RejectedPackages,
    int ImportedContracts,
    int MissingContracts,
    int DivergentContracts,
    int OlderContracts,
    IReadOnlyList<string> Errors);

public sealed class GuardMasterService
{
    private readonly ISqliteConnectionFactory _connections;
    private readonly Action? _beforePackageCommit;

    public GuardMasterService(ISqliteConnectionFactory connections)
        : this(connections, beforePackageCommit: null)
    {
    }

    internal GuardMasterService(
        ISqliteConnectionFactory connections,
        Action? beforePackageCommit)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _beforePackageCommit = beforePackageCommit;
    }

    public async Task<GuardCampaignResult> CreateOrReplaceCampaignAsync(
        string driveRoot,
        IReadOnlyList<GuardWorkerInput> requestedWorkers,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(driveRoot);
        var inputs = NormalizeWorkers(requestedWorkers);
        Directory.CreateDirectory(Path.Combine(root, "plans"));
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        Directory.CreateDirectory(Path.Combine(root, "acks"));
        Directory.CreateDirectory(Path.Combine(root, "status"));

        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var now = DateTimeOffset.UtcNow;
        var masterId = await GetOrCreateMasterIdAsync(connection, sqliteTransaction, now, cancellationToken)
            .ConfigureAwait(false);
        var workers = await ResolveWorkersAsync(connection, sqliteTransaction, inputs, now, cancellationToken)
            .ConfigureAwait(false);
        var contracts = await ReadCampaignContractsAsync(connection, sqliteTransaction, cancellationToken)
            .ConfigureAwait(false);
        var assignments = GuardPartitioner.AssignPartitions(workers);
        var campaignId = Guid.NewGuid().ToString("D");
        var campaignDirectory = Path.Combine(root, "plans", campaignId);
        Directory.CreateDirectory(campaignDirectory);
        var controlWorkers = new List<GuardControlWorker>(workers.Count);
        var counts = new List<(string WorkerName, string WorkerId, int ContractCount)>(workers.Count);

        foreach (var worker in workers)
        {
            var assigned = contracts
                .Where(contract => assignments[GuardPartitioner.GetPartition(contract.PncpId)] == worker.Id)
                .ToArray();
            var plan = new GuardWorkerPlan
            {
                CampaignId = campaignId,
                MasterId = masterId,
                Worker = worker,
                CreatedAt = now,
                Contracts = assigned
            };
            var relativePath = Path.Combine("plans", campaignId, worker.Id + GuardFormat.PlanExtension);
            await GuardFileCodec.WriteJsonAtomicAsync(
                    Path.Combine(root, relativePath),
                    plan,
                    overwrite: false,
                    cancellationToken)
                .ConfigureAwait(false);
            controlWorkers.Add(new GuardControlWorker
            {
                WorkerId = worker.Id,
                PlanRelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/')
            });
            counts.Add((worker.Name, worker.Id, assigned.Length));
        }

        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = sqliteTransaction;
            deactivate.CommandText = "UPDATE guard_campaigns SET active = 0 WHERE active = 1;";
            await deactivate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = sqliteTransaction;
            insert.CommandText = """
                INSERT INTO guard_campaigns(campaign_id, master_id, created_at, root_path, active)
                VALUES($campaign, $master, $created, $root, 1);
                """;
            insert.Parameters.AddWithValue("$campaign", campaignId);
            insert.Parameters.AddWithValue("$master", masterId);
            insert.Parameters.AddWithValue("$created", FormatDateTime(now));
            insert.Parameters.AddWithValue("$root", root);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var control = new GuardControl
        {
            CampaignId = campaignId,
            MasterId = masterId,
            CreatedAt = now,
            Workers = controlWorkers
        };
        await GuardFileCodec.WriteJsonAtomicAsync(
                Path.Combine(root, "control.json"),
                control,
                overwrite: true,
                cancellationToken)
            .ConfigureAwait(false);
        return new GuardCampaignResult(campaignId, masterId, contracts.Count, counts);
    }

    public async Task<GuardImportResult> ImportPackagesAsync(
        string selectedFolder,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(selectedFolder);
        var packagesFolder = Path.Combine(root, "packages");
        var activeCampaign = await ReadActiveCampaignIdAsync(cancellationToken).ConfigureAwait(false);
        var activeFolder = activeCampaign is null ? null : Path.Combine(packagesFolder, activeCampaign);
        var packageRoot = activeFolder is not null && Directory.Exists(activeFolder)
            ? activeFolder
            : Directory.Exists(packagesFolder)
                ? packagesFolder
                : root;
        var packageFiles = Directory.Exists(packageRoot)
            ? Directory.EnumerateFiles(packageRoot, "*" + GuardFormat.PackageExtension, SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var importedPackages = 0;
        var duplicates = 0;
        var rejected = 0;
        var importedContracts = 0;
        var missingContracts = 0;
        var divergentContracts = 0;
        var olderContracts = 0;
        var errors = new List<string>();

        foreach (var path in packageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GuardPackage package;
            try
            {
                package = await GuardFileCodec.ReadPackageAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                rejected++;
                errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
                continue;
            }

            try
            {
                var outcome = await ImportPackageAsync(package, cancellationToken).ConfigureAwait(false);
                if (outcome.Duplicate)
                {
                    duplicates++;
                }
                else
                {
                    importedPackages++;
                    importedContracts += outcome.Imported;
                    missingContracts += outcome.Missing;
                    divergentContracts += outcome.Divergent;
                    olderContracts += outcome.Older;
                }

                await WriteAckAsync(root, package, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or SqliteException or IOException)
            {
                rejected++;
                errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return new GuardImportResult(
            packageFiles.Length,
            importedPackages,
            duplicates,
            rejected,
            importedContracts,
            missingContracts,
            divergentContracts,
            olderContracts,
            errors);
    }

    private async Task<PackageImportOutcome> ImportPackageAsync(
        GuardPackage package,
        CancellationToken cancellationToken)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;

        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = sqliteTransaction;
            duplicate.CommandText = "SELECT package_sha256 FROM guard_imported_packages WHERE package_id = $id;";
            duplicate.Parameters.AddWithValue("$id", package.Manifest.PackageId);
            var existingHash = await duplicate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (existingHash is not null)
            {
                if (!string.Equals(existingHash, package.FileSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"O UUID {package.Manifest.PackageId} já foi importado com outro checksum.");
                }

                return new PackageImportOutcome(true, 0, 0, 0, 0);
            }
        }

        await using (var activeCampaign = connection.CreateCommand())
        {
            activeCampaign.Transaction = sqliteTransaction;
            activeCampaign.CommandText = "SELECT campaign_id FROM guard_campaigns WHERE active = 1;";
            var active = await activeCampaign.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (!string.Equals(active, package.Manifest.CampaignId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("O pacote pertence a uma campanha substituída ou desconhecida.");
            }
        }

        var imported = 0;
        var missing = 0;
        var divergent = 0;
        var older = 0;
        foreach (var snapshot in package.Payload.Contracts)
        {
            var masterVersion = await ReadMasterContractVersionAsync(
                    connection,
                    sqliteTransaction,
                    snapshot.Contract.PncpId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!masterVersion.Exists)
            {
                missing++;
                continue;
            }

            if (!SameInstant(masterVersion.GlobalUpdatedAt, snapshot.Contract.GlobalUpdatedAt))
            {
                divergent++;
                continue;
            }

            var stored = await ReadStoredSnapshotAsync(
                    connection,
                    sqliteTransaction,
                    snapshot.Contract.PncpId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stored is not null && SameInstant(stored.Value.Version, snapshot.Contract.GlobalUpdatedAt) &&
                stored.Value.FetchedAt >= snapshot.CollectedAt)
            {
                older++;
                continue;
            }

            await ReplaceContractSnapshotAsync(
                    connection,
                    sqliteTransaction,
                    snapshot,
                    masterVersion.RawGlobalUpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            imported++;
        }

        await using (var record = connection.CreateCommand())
        {
            record.Transaction = sqliteTransaction;
            record.CommandText = """
                INSERT INTO guard_imported_packages(
                    package_id, package_sha256, campaign_id, worker_id, imported_at,
                    imported_contracts, skipped_contracts)
                VALUES($id, $sha, $campaign, $worker, $now, $imported, $skipped);
                """;
            record.Parameters.AddWithValue("$id", package.Manifest.PackageId);
            record.Parameters.AddWithValue("$sha", package.FileSha256);
            record.Parameters.AddWithValue("$campaign", package.Manifest.CampaignId);
            record.Parameters.AddWithValue("$worker", package.Manifest.WorkerId);
            record.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
            record.Parameters.AddWithValue("$imported", imported);
            record.Parameters.AddWithValue("$skipped", missing + divergent + older);
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _beforePackageCommit?.Invoke();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PackageImportOutcome(false, imported, missing, divergent, older);
    }

    private async Task<string?> ReadActiveCampaignIdAsync(CancellationToken cancellationToken)
    {
        await using var reader = await _connections.WorkCoordinator
            .EnterReaderAsync(SqliteWorkPriority.Visible, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT campaign_id FROM guard_campaigns WHERE active = 1;";
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task ReplaceContractSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GuardContractSnapshot snapshot,
        string? masterGlobalUpdatedAt,
        CancellationToken cancellationToken)
    {
        await using (var prepare = connection.CreateCommand())
        {
            prepare.Transaction = transaction;
            prepare.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS guard_incoming_items(
                    item_number INTEGER PRIMARY KEY
                ) WITHOUT ROWID;
                DELETE FROM guard_incoming_items;
                """;
            await prepare.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var incoming = connection.CreateCommand();
        incoming.Transaction = transaction;
        incoming.CommandText = "INSERT INTO guard_incoming_items(item_number) VALUES($number);";
        incoming.Parameters.Add("$number", SqliteType.Integer);

        await using var upsertItem = connection.CreateCommand();
        upsertItem.Transaction = transaction;
        upsertItem.CommandText = """
            INSERT INTO items(
                contract_id, item_number, description, unit, requested_quantity_scaled,
                additional_information, status, has_result, hydration_status, last_error,
                cache_updated_at, search_text)
            VALUES($contract, $number, $description, $unit, $quantity,
                   $additional, '', $hasResult, $hydrationStatus, NULL, $collected, $searchText)
            ON CONFLICT(contract_id, item_number) DO UPDATE SET
                description = excluded.description,
                unit = excluded.unit,
                requested_quantity_scaled = excluded.requested_quantity_scaled,
                additional_information = excluded.additional_information,
                has_result = excluded.has_result,
                hydration_status = CASE
                    WHEN items.hydration_status = 2 THEN 2
                    ELSE excluded.hydration_status
                END,
                last_error = NULL,
                cache_updated_at = excluded.cache_updated_at,
                search_text = excluded.search_text;
            """;
        upsertItem.Parameters.Add("$contract", SqliteType.Text);
        upsertItem.Parameters.Add("$number", SqliteType.Integer);
        upsertItem.Parameters.Add("$description", SqliteType.Text);
        upsertItem.Parameters.Add("$unit", SqliteType.Text);
        upsertItem.Parameters.Add("$quantity", SqliteType.Integer);
        upsertItem.Parameters.Add("$additional", SqliteType.Text);
        upsertItem.Parameters.Add("$hasResult", SqliteType.Integer);
        upsertItem.Parameters.Add("$hydrationStatus", SqliteType.Integer);
        upsertItem.Parameters.Add("$collected", SqliteType.Text);
        upsertItem.Parameters.Add("$searchText", SqliteType.Text);

        foreach (var item in snapshot.Items)
        {
            incoming.Parameters["$number"].Value = item.ItemNumber;
            await incoming.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var description = item.Description.Trim();
            upsertItem.Parameters["$contract"].Value = snapshot.Contract.PncpId;
            upsertItem.Parameters["$number"].Value = item.ItemNumber;
            upsertItem.Parameters["$description"].Value = description;
            upsertItem.Parameters["$unit"].Value = item.Unit.Trim();
            upsertItem.Parameters["$quantity"].Value = DbValue(item.RequestedQuantityScaled);
            upsertItem.Parameters["$additional"].Value = item.AdditionalInformation.Trim();
            upsertItem.Parameters["$hasResult"].Value = item.HasResult ? 1 : 0;
            upsertItem.Parameters["$hydrationStatus"].Value = item.HasResult
                ? (int)ItemHydrationStatus.NotLoaded
                : (int)ItemHydrationStatus.Complete;
            upsertItem.Parameters["$collected"].Value = FormatDateTime(snapshot.CollectedAt);
            upsertItem.Parameters["$searchText"].Value = SearchText.Normalize(
                description + " " + item.AdditionalInformation);
            await upsertItem.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var reconcile = connection.CreateCommand())
        {
            reconcile.Transaction = transaction;
            reconcile.CommandText = """
                DELETE FROM items
                 WHERE contract_id = $contract
                   AND item_number NOT IN (SELECT item_number FROM guard_incoming_items);
                """;
            reconcile.Parameters.AddWithValue("$contract", snapshot.Contract.PncpId);
            await reconcile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insertResult = connection.CreateCommand();
        insertResult.Transaction = transaction;
        insertResult.CommandText = """
            INSERT INTO item_results(
                contract_id, item_number, result_sequence, supplier_tax_id, supplier_name,
                supplier_type, supplier_municipality, supplier_uf, quantity_scaled,
                unit_value_scaled, total_value_scaled, result_date, result_status_id,
                result_status_name)
            VALUES($contract, $item, $sequence, $taxId, $name, $type, $municipality,
                   $uf, $quantity, $unitValue, $totalValue, $date, $statusId, $statusName)
            ON CONFLICT(contract_id, item_number, result_sequence) DO UPDATE SET
                supplier_tax_id = excluded.supplier_tax_id,
                supplier_name = excluded.supplier_name,
                supplier_type = excluded.supplier_type,
                supplier_municipality = excluded.supplier_municipality,
                supplier_uf = excluded.supplier_uf,
                quantity_scaled = excluded.quantity_scaled,
                unit_value_scaled = excluded.unit_value_scaled,
                total_value_scaled = excluded.total_value_scaled,
                result_date = excluded.result_date,
                result_status_id = excluded.result_status_id,
                result_status_name = excluded.result_status_name;
            """;
        foreach (var name in new[]
                 {
                     "$contract", "$item", "$sequence", "$taxId", "$name", "$type", "$municipality",
                     "$uf", "$quantity", "$unitValue", "$totalValue", "$date", "$statusId", "$statusName"
                 })
        {
            insertResult.Parameters.Add(name, name is "$item" or "$sequence" or "$quantity" or "$unitValue" or "$totalValue" or "$statusId"
                ? SqliteType.Integer
                : SqliteType.Text);
        }

        foreach (var result in snapshot.Results)
        {
            insertResult.Parameters["$contract"].Value = snapshot.Contract.PncpId;
            insertResult.Parameters["$item"].Value = result.ItemNumber;
            insertResult.Parameters["$sequence"].Value = result.ResultSequence;
            insertResult.Parameters["$taxId"].Value = result.SupplierTaxId.Trim();
            insertResult.Parameters["$name"].Value = result.SupplierName.Trim();
            insertResult.Parameters["$type"].Value = result.SupplierType.Trim();
            insertResult.Parameters["$municipality"].Value = result.SupplierMunicipality.Trim();
            insertResult.Parameters["$uf"].Value = result.SupplierUf.Trim();
            insertResult.Parameters["$quantity"].Value = DbValue(result.HomologatedQuantityScaled);
            insertResult.Parameters["$unitValue"].Value = DbValue(result.HomologatedUnitValueScaled);
            insertResult.Parameters["$totalValue"].Value = DbValue(result.HomologatedTotalValueScaled);
            insertResult.Parameters["$date"].Value = result.ResultDate is null
                ? DBNull.Value
                : result.ResultDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            insertResult.Parameters["$statusId"].Value = result.ResultStatusId;
            insertResult.Parameters["$statusName"].Value = result.ResultStatusName.Trim();
            await insertResult.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.Results.Count > 0)
        {
            await using var markImportedResultsComplete = connection.CreateCommand();
            markImportedResultsComplete.Transaction = transaction;
            markImportedResultsComplete.CommandText = """
                UPDATE items
                   SET hydration_status = $complete,
                       last_error = NULL,
                       cache_updated_at = $collected
                 WHERE contract_id = $contract
                   AND item_number = $item;
                """;
            markImportedResultsComplete.Parameters.AddWithValue(
                "$complete",
                (int)ItemHydrationStatus.Complete);
            markImportedResultsComplete.Parameters.AddWithValue(
                "$collected",
                FormatDateTime(snapshot.CollectedAt));
            markImportedResultsComplete.Parameters.AddWithValue("$contract", snapshot.Contract.PncpId);
            markImportedResultsComplete.Parameters.Add("$item", SqliteType.Integer);
            foreach (var itemNumber in snapshot.Results.Select(result => result.ItemNumber).Distinct())
            {
                markImportedResultsComplete.Parameters["$item"].Value = itemNumber;
                await markImportedResultsComplete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var snapshotCommand = connection.CreateCommand())
        {
            snapshotCommand.Transaction = transaction;
            snapshotCommand.CommandText = """
                INSERT INTO contract_item_snapshots(
                    contract_id, fetched_at, item_count, source_global_updated_at)
                VALUES($contract, $collected, $count, $version)
                ON CONFLICT(contract_id) DO UPDATE SET
                    fetched_at = excluded.fetched_at,
                    item_count = excluded.item_count,
                    source_global_updated_at = excluded.source_global_updated_at;

                INSERT INTO price_cache_contracts(
                    contract_id, publication_date, source_global_updated_at, status, item_count,
                    active_result_count, cancelled_result_count, attempts, last_error,
                    next_retry_at, background_owned, user_pinned, completed_at, updated_at)
                VALUES($contract,
                       COALESCE((SELECT publication_date FROM contracts WHERE pncp_id = $contract), ''),
                       $version, 2, $count,
                       (SELECT COUNT(*) FROM item_results
                         WHERE contract_id = $contract AND result_status_id = 1),
                       (SELECT COUNT(*) FROM item_results
                         WHERE contract_id = $contract AND result_status_id <> 1),
                       0, '',
                       NULL, 1, 0, $collected, $collected)
                ON CONFLICT(contract_id) DO UPDATE SET
                    publication_date = excluded.publication_date,
                    source_global_updated_at = excluded.source_global_updated_at,
                    status = 2,
                    item_count = excluded.item_count,
                    active_result_count = excluded.active_result_count,
                    cancelled_result_count = excluded.cancelled_result_count,
                    attempts = 0,
                    last_error = '',
                    next_retry_at = NULL,
                    background_owned = CASE
                        WHEN price_cache_contracts.user_pinned = 1 THEN 0 ELSE 1 END,
                    user_pinned = price_cache_contracts.user_pinned,
                    completed_at = excluded.completed_at,
                    updated_at = excluded.updated_at;
                """;
            snapshotCommand.Parameters.AddWithValue("$contract", snapshot.Contract.PncpId);
            snapshotCommand.Parameters.AddWithValue("$collected", FormatDateTime(snapshot.CollectedAt));
            snapshotCommand.Parameters.AddWithValue("$count", snapshot.Items.Count);
            snapshotCommand.Parameters.AddWithValue("$version", DbValue(masterGlobalUpdatedAt));
            await snapshotCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<MasterContractVersion> ReadMasterContractVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contractId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT global_updated_at FROM contracts WHERE pncp_id = $id;";
        command.Parameters.AddWithValue("$id", contractId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new MasterContractVersion(false, null, null);
        }

        var raw = reader.IsDBNull(0) ? null : reader.GetString(0);
        return new MasterContractVersion(true, ParseDateTime(reader, 0), raw);
    }

    private static async Task<(DateTimeOffset FetchedAt, DateTimeOffset? Version)?> ReadStoredSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contractId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fetched_at, source_global_updated_at
              FROM contract_item_snapshots
             WHERE contract_id = $id;
            """;
        command.Parameters.AddWithValue("$id", contractId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (ParseDateTime(reader, 0) ?? DateTimeOffset.MinValue, ParseDateTime(reader, 1));
    }

    private static async Task<IReadOnlyList<GuardPlanContract>> ReadCampaignContractsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.pncp_id, c.cnpj, c.purchase_year, c.purchase_sequence,
                   c.publication_date, c.global_updated_at
              FROM contracts c
             LEFT JOIN contract_item_snapshots s ON s.contract_id = c.pncp_id
             WHERE s.contract_id IS NULL
                OR COALESCE(s.source_global_updated_at, '') <> COALESCE(c.global_updated_at, '')
             ORDER BY c.pncp_id;
            """;
        var contracts = new List<GuardPlanContract>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            contracts.Add(new GuardPlanContract
            {
                PncpId = reader.GetString(0),
                Cnpj = reader.GetString(1),
                PurchaseYear = reader.GetInt32(2),
                PurchaseSequence = reader.GetInt32(3),
                PublicationDate = ParseDateTime(reader, 4),
                GlobalUpdatedAt = ParseDateTime(reader, 5)
            });
        }

        return contracts;
    }

    private static async Task<string> GetOrCreateMasterIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT master_id FROM guard_master WHERE id = 1;";
            if (await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string existing)
            {
                return existing;
            }
        }

        var id = Guid.NewGuid().ToString("D");
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO guard_master(id, master_id, created_at) VALUES(1, $id, $now);";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$now", FormatDateTime(now));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    private static async Task<IReadOnlyList<GuardWorkerDefinition>> ResolveWorkersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<GuardWorkerInput> inputs,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var workers = new List<GuardWorkerDefinition>(inputs.Count);
        foreach (var input in inputs)
        {
            string? id;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT worker_id FROM guard_workers WHERE name = $name COLLATE NOCASE;";
                read.Parameters.AddWithValue("$name", input.Name);
                id = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            }

            id ??= Guid.NewGuid().ToString("D");
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO guard_workers(worker_id, name, weight, updated_at)
                    VALUES($id, $name, $weight, $now)
                    ON CONFLICT(worker_id) DO UPDATE SET
                        name = excluded.name,
                        weight = excluded.weight,
                        updated_at = excluded.updated_at;
                    """;
                upsert.Parameters.AddWithValue("$id", id);
                upsert.Parameters.AddWithValue("$name", input.Name);
                upsert.Parameters.AddWithValue("$weight", input.Weight);
                upsert.Parameters.AddWithValue("$now", FormatDateTime(now));
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            workers.Add(new GuardWorkerDefinition { Id = id, Name = input.Name, Weight = input.Weight });
        }

        return workers;
    }

    private static async Task WriteAckAsync(
        string root,
        GuardPackage package,
        CancellationToken cancellationToken)
    {
        var ack = new GuardAck
        {
            PackageId = package.Manifest.PackageId,
            PackageSha256 = package.FileSha256,
            CampaignId = package.Manifest.CampaignId,
            ImportedAt = DateTimeOffset.UtcNow
        };
        var path = Path.Combine(root, "acks", package.Manifest.CampaignId, package.Manifest.PackageId + ".ack");
        await GuardFileCodec.WriteJsonAtomicAsync(path, ack, overwrite: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<GuardWorkerInput> NormalizeWorkers(IReadOnlyList<GuardWorkerInput> workers)
    {
        ArgumentNullException.ThrowIfNull(workers);
        var normalized = workers
            .Select(worker => new GuardWorkerInput(worker.Name.Trim(), worker.Weight))
            .ToArray();
        if (normalized.Length == 0 || normalized.Any(worker => worker.Name.Length == 0 || worker.Weight <= 0) ||
            normalized.Select(worker => worker.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
        {
            throw new ArgumentException("Informe nomes únicos e pesos inteiros positivos para os trabalhadores.", nameof(workers));
        }

        return normalized;
    }

    private static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path.Trim());
    }

    private static bool SameInstant(DateTimeOffset? left, DateTimeOffset? right) =>
        left?.ToUniversalTime() == right?.ToUniversalTime();

    private static DateTimeOffset? ParseDateTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.TryParse(
                reader.GetString(ordinal),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var value)
                ? value.ToUniversalTime()
                : null;

    private static object DbValue(DateTimeOffset? value) =>
        value is null ? DBNull.Value : FormatDateTime(value.Value);

    private static object DbValue(long? value) => value is null ? DBNull.Value : value.Value;

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private readonly record struct MasterContractVersion(
        bool Exists,
        DateTimeOffset? GlobalUpdatedAt,
        string? RawGlobalUpdatedAt);
    private readonly record struct PackageImportOutcome(
        bool Duplicate,
        int Imported,
        int Missing,
        int Divergent,
        int Older);
}
