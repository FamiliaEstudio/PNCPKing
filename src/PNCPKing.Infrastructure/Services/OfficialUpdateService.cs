using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Infrastructure.Services;

public sealed record OfficialUpdateManifest(int Format, int Schema, string BaseId, string Origin,
    long Revision, bool InitialBase, string PackageId, string Sha256, long Units);
public sealed record OfficialUpdateResult(long Applied, long Skipped, long Conflicts);

/// <summary>Logical, cumulative transfer. Only fixed official tables are accepted;
/// user data and operational settings never enter the package.</summary>
public sealed class OfficialUpdateService
{
    private readonly ISqliteConnectionFactory _connections;
    private static readonly JsonSerializerOptions Json = new();
    private static readonly string[] Tables = ["", "contracts", "items", "item_results", "catalog_entries", "coverage_day_modalities"];
    private static readonly string[][] Columns = [[],
        "pncp_id,cnpj,purchase_year,purchase_sequence,object,additional_information,process,organization,unit,municipality,uf,modality_id,modality_name,status,publication_date,global_updated_at,total_homologated_scaled,search_text,municipality_ibge_code,distance_from_ribeirao_km,municipality_distance_rank,state_proximity_rank,geo_layer,random_order_key".Split(','),
        "contract_id,item_number,description,unit,status,has_result,source_updated_at,search_text,requested_quantity_scaled,additional_information,item_category,ncm_nbs_code,ncm_nbs_description,catalog_code,catalog_name,catalog_category".Split(','),
        "contract_id,item_number,result_sequence,supplier_tax_id,supplier_name,quantity_scaled,unit_value_scaled,total_value_scaled,result_date,result_status_id,result_status_name,supplier_type,supplier_municipality,supplier_uf".Split(','),
        "catalog_kind,code,description,active,level1_code,level1_name,level2_code,level2_name,level3_code,level3_name,level4_code,level4_name,level5_code,level5_name,ncm_code,sustainable,exclusive_central,remote_updated_at,search_text".Split(','),
        "coverage_date,modality_id,uf,status,records_count".Split(',')];
    private sealed record Unit(int Kind, string Key1, string Key2, string? Version, string? ParentVersion,
        List<Dictionary<string, JsonElement>> Rows);
    private const string AllKeys = """
        SELECT 1 kind,pncp_id key1,'' key2 FROM contracts
        UNION ALL SELECT 2,contract_id,'' FROM contract_item_snapshots
        UNION ALL SELECT 3,contract_id,CAST(item_number AS TEXT) FROM items WHERE hydration_status=2
        UNION ALL SELECT 4,CAST(catalog_kind AS TEXT),code FROM catalog_entries
        UNION ALL SELECT 5,coverage_date,CAST(modality_id AS TEXT)||'|'||uf FROM coverage_day_modalities WHERE status=3
        """;

    public OfficialUpdateService(ISqliteConnectionFactory connections) => _connections = connections;
    public OfficialUpdateService(string databasePath) : this(new SqliteConnectionFactory(databasePath)) { }

    public async Task<OfficialUpdateManifest> ExportAsync(string path, bool newBase = false,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        string baseId;
        await using (var lease = await _connections.WorkCoordinator.EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken))
        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using var tx = connection.BeginTransaction();
            if (Convert.ToInt64(await ScalarAsync(connection, tx, "SELECT COUNT(*) FROM official_imports WHERE initial_base=1 AND completed=0", cancellationToken)) > 0)
                throw new InvalidOperationException("Conclua a importação da base inicial antes de exportar atualizações.");
            var existing = await ScalarAsync(connection, tx, "SELECT base_id FROM official_transfer_state WHERE id=1", cancellationToken);
            if (newBase || existing is null)
            {
                baseId = Guid.NewGuid().ToString("N");
                await ExecuteAsync(connection, tx, "UPDATE official_transfer_state SET base_id=$a,base_ready=0 WHERE id=1; DELETE FROM official_changes; DELETE FROM official_imports; DELETE FROM official_conflicts;", cancellationToken, baseId);
            }
            else baseId = (string)existing;
            tx.Commit();
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var payloadPath = temporary + ".db";
        try
        {
            OfficialUpdateManifest manifest;
            await using (var source = await OpenAsync(cancellationToken))
            await using (var snapshot = source.BeginTransaction(deferred: true))
            await using (var payload = await OpenPayloadAsync(payloadPath, false, cancellationToken))
            {
                using var sourceInterruption = SqliteConnectionFactory.InterruptOnCancellation(source, cancellationToken);
                using var payloadInterruption = SqliteConnectionFactory.InterruptOnCancellation(payload, cancellationToken);
                var origin = (string)(await ScalarAsync(source, snapshot, "SELECT origin FROM official_transfer_state WHERE id=1", cancellationToken))!;
                var revision = Convert.ToInt64(await ScalarAsync(source, snapshot, "SELECT revision FROM official_transfer_state WHERE id=1", cancellationToken));
                var initial = Convert.ToInt64(await ScalarAsync(source, snapshot, "SELECT base_ready FROM official_transfer_state WHERE id=1", cancellationToken)) == 0;
                if (!Equals(baseId, await ScalarAsync(source, snapshot, "SELECT base_id FROM official_transfer_state WHERE id=1", cancellationToken)))
                    throw new InvalidOperationException("A base mudou durante a exportação. Tente novamente.");
                await ExecuteAsync(payload, null, "PRAGMA journal_mode=DELETE; CREATE TABLE units(sequence INTEGER PRIMARY KEY,kind INTEGER NOT NULL,key1 TEXT NOT NULL,key2 TEXT NOT NULL,payload TEXT NOT NULL,hash TEXT NOT NULL,UNIQUE(kind,key1,key2));", cancellationToken);
                await using var output = payload.BeginTransaction();
                await using var keys = source.CreateCommand();
                keys.Transaction = snapshot;
                keys.CommandText = initial ? $"SELECT * FROM ({AllKeys}) ORDER BY kind,key1,key2" : "SELECT kind,key1,key2 FROM official_changes ORDER BY kind,key1,key2";
                long count = 0;
                await using var reader = await keys.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var unit = await ReadUnitAsync(source, snapshot, reader.GetInt32(0), reader.GetString(1), reader.GetString(2), cancellationToken);
                    if (unit is null) continue; // Retention/cache eviction is not an official deletion.
                    var json = JsonSerializer.Serialize(unit, Json);
                    await ExecuteAsync(payload, output, "INSERT INTO units VALUES($a,$b,$c,$d,$e,$f)", cancellationToken,
                        ++count, unit.Kind, unit.Key1, unit.Key2, json, Hash(json));
                    if (count % 128 == 0) progress?.Report($"Exportando {(initial ? "base inicial" : "atualizações")}: {count:N0} unidades completas…");
                }
                output.Commit();
                manifest = new(1, SqliteContractRepository.CurrentSchemaVersion, baseId, origin, revision, initial,
                    Guid.NewGuid().ToString("N"), "", count);
            }
            manifest = manifest with { Sha256 = await FileHashAsync(payloadPath, cancellationToken) };
            await using (var file = File.Create(temporary))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                await using (var metadata = zip.CreateEntry("manifest.json").Open())
                    await JsonSerializer.SerializeAsync(metadata, manifest, Json, cancellationToken);
                await using var entry = zip.CreateEntry("updates.db", CompressionLevel.Fastest).Open();
                await using var input = File.OpenRead(payloadPath);
                await input.CopyToAsync(entry, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            await using (var lease = await _connections.WorkCoordinator.EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken))
            await using (var connection = await OpenAsync(cancellationToken))
                await ExecuteAsync(connection, null, "UPDATE official_transfer_state SET base_ready=1 WHERE id=1 AND base_id=$a", cancellationToken, baseId);
            progress?.Report($"{(manifest.InitialBase ? "Base inicial" : "Pacote cumulativo")} exportado: {manifest.Units:N0} unidades.");
            return manifest;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 9 && cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Exportação interrompida.", e, cancellationToken); }
        finally { DeleteTemporary(temporary); DeleteTemporary(payloadPath); }
    }

    public async Task<OfficialUpdateResult> ImportAsync(string path, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, bool replaceBase = false)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(_connections.DatabasePath)!, ".pncpupdate-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            OfficialUpdateManifest manifest;
            progress?.Report("Validando o pacote de atualizações…");
            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.Entries.Count != 2 || zip.Entries.Count(e => e.FullName == "manifest.json") != 1 || zip.Entries.Count(e => e.FullName == "updates.db") != 1)
                    throw new InvalidDataException("Conteúdo do pacote inválido.");
                var metadata = zip.GetEntry("manifest.json")!;
                if (metadata.Length > 65536) throw new InvalidDataException("Manifesto inválido.");
                await using (var input = metadata.Open())
                    manifest = await JsonSerializer.DeserializeAsync<OfficialUpdateManifest>(input, Json, cancellationToken) ?? throw new InvalidDataException("Manifesto ausente.");
                if (manifest.Format != 1 || manifest.Schema != SqliteContractRepository.CurrentSchemaVersion ||
                    !Guid.TryParseExact(manifest.BaseId, "N", out _) || !Guid.TryParseExact(manifest.PackageId, "N", out _) ||
                    !Guid.TryParseExact(manifest.Origin, "N", out _) || manifest.Revision < 0 || manifest.Units < 0)
                    throw new InvalidDataException("Formato ou versão do pacote incompatível.");
                var entry = zip.GetEntry("updates.db")!;
                var available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(temporary))!).AvailableFreeSpace;
                if (entry.Length > available - 512L * 1024 * 1024) throw new IOException("Espaço insuficiente para validar o pacote.");
                await using var inputDb = entry.Open();
                await using var output = File.Create(temporary);
                await inputDb.CopyToAsync(output, cancellationToken);
            }
            if (!StringComparer.OrdinalIgnoreCase.Equals(manifest.Sha256, await FileHashAsync(temporary, cancellationToken)))
                throw new InvalidDataException("Checksum divergente; nenhum dado foi importado.");
            await using var payload = await OpenPayloadAsync(temporary, true, cancellationToken);
            using var payloadInterruption = SqliteConnectionFactory.InterruptOnCancellation(payload, cancellationToken);
            if (!Equals("ok", await ScalarAsync(payload, null, "PRAGMA integrity_check", cancellationToken)))
                throw new InvalidDataException("Banco do pacote corrompido.");
            await ValidatePayloadAsync(payload, manifest.Units, cancellationToken);

            // A fully validated package may now establish the common base. Existing destination
            // entities are recorded once, so newer/different local observations remain exportable.
            await using (var lease = await _connections.WorkCoordinator.EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken))
            await using (var destination = await OpenAsync(cancellationToken))
            {
                using var interruption = SqliteConnectionFactory.InterruptOnCancellation(destination, cancellationToken);
                await using var tx = destination.BeginTransaction();
                var baseId = await ScalarAsync(destination, tx, "SELECT base_id FROM official_transfer_state WHERE id=1", cancellationToken) as string;
                if (replaceBase && !manifest.InitialBase) throw new InvalidDataException("A troca de base exige o arquivo inicial completo.");
                if (replaceBase && baseId is not null && baseId != manifest.BaseId)
                {
                    await ExecuteAsync(destination, tx, "DELETE FROM official_changes; DELETE FROM official_imports; DELETE FROM official_conflicts; UPDATE official_transfer_state SET base_id=NULL,base_ready=0 WHERE id=1;", cancellationToken);
                    baseId = null;
                }
                if (baseId is not null && baseId != manifest.BaseId || baseId is null && !manifest.InitialBase)
                    throw new InvalidDataException("Base incompatível. Importe primeiro a base inicial correspondente.");
                if (!manifest.InitialBase && Convert.ToInt64(await ScalarAsync(destination, tx,
                    "SELECT COUNT(*) FROM official_imports WHERE initial_base=1 AND completed=0", cancellationToken)) > 0)
                    throw new InvalidOperationException("Retome primeiro a importação da base inicial.");
                if (baseId is null)
                {
                    await ExecuteAsync(destination, tx, "UPDATE official_transfer_state SET base_id=$a,base_ready=0,revision=revision+1 WHERE id=1", cancellationToken, manifest.BaseId);
                }
                var checksum = await ScalarAsync(destination, tx, "SELECT checksum FROM official_imports WHERE package_id=$a", cancellationToken, manifest.PackageId) as string;
                if (checksum is not null && checksum != manifest.Sha256) throw new InvalidDataException("Identidade do pacote reutilizada com conteúdo diferente.");
                await ExecuteAsync(destination, tx, "INSERT OR IGNORE INTO official_imports(package_id,checksum,initial_base,inventory_required) SELECT $a,$b,$c,EXISTS(SELECT 1 FROM contracts) OR EXISTS(SELECT 1 FROM catalog_entries) OR EXISTS(SELECT 1 FROM coverage_day_modalities)", cancellationToken, manifest.PackageId, manifest.Sha256, manifest.InitialBase ? 1 : 0);
                tx.Commit();
            }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long last;
                OfficialUpdateResult result;
                var complete = false;
                await using (var lease = await _connections.WorkCoordinator.EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken))
                await using (var destination = await OpenAsync(cancellationToken))
                {
                    using var interruption = SqliteConnectionFactory.InterruptOnCancellation(destination, cancellationToken);
                    await using var tx = destination.BeginTransaction();
                    last = Convert.ToInt64(await ScalarAsync(destination, tx, "SELECT last_sequence FROM official_imports WHERE package_id=$a", cancellationToken, manifest.PackageId));
                    if (manifest.InitialBase)
                        await ExecuteAsync(destination, tx, "UPDATE official_transfer_state SET journal_suspended=1 WHERE id=1", cancellationToken);
                    var batch = Stopwatch.StartNew();
                    var processed = 0;
                    var affectedContracts = new HashSet<string>(StringComparer.Ordinal);
                    long applied = 0, skipped = 0, conflicts = 0;
                    await using var command = payload.CreateCommand();
                    command.CommandText = "SELECT sequence,payload FROM units WHERE sequence>$last ORDER BY sequence LIMIT 32";
                    command.Parameters.AddWithValue("$last", last);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var unit = JsonSerializer.Deserialize<Unit>(reader.GetString(1), Json)!;
                        var disposition = await ApplyUnitAsync(destination, tx, unit, manifest.PackageId, cancellationToken);
                        if (manifest.InitialBase && disposition is 0 or 1)
                            await ExecuteAsync(destination, tx, "DELETE FROM official_changes WHERE kind=$a AND key1=$b AND key2=$c", cancellationToken, unit.Kind, unit.Key1, unit.Key2);
                        if (manifest.InitialBase && disposition is 2 or 3)
                            await RecordDifferenceAsync(destination, tx, unit.Kind, unit.Key1, unit.Key2, cancellationToken);
                        if (disposition == 1 && unit.Kind is 2 or 3) affectedContracts.Add(unit.Key1);
                        if (disposition == 1) applied++; else if (disposition == 2) conflicts++; else skipped++;
                        last = reader.GetInt64(0);
                        processed++;
                        if (batch.ElapsedMilliseconds >= 250) break;
                    }
                    foreach (var contractId in affectedContracts)
                        await SqliteContractRepository.RefreshOfficialPriceIndexAsync(destination, tx, contractId, cancellationToken);
                    complete = processed == 0 || last == manifest.Units;
                    await ExecuteAsync(destination, tx, "UPDATE official_imports SET last_sequence=$b,applied=applied+$c,skipped=skipped+$d,conflicts=conflicts+$e WHERE package_id=$a", cancellationToken,
                        manifest.PackageId, last, applied, skipped, conflicts);
                    await ExecuteAsync(destination, tx, "UPDATE official_transfer_state SET journal_suspended=0 WHERE id=1", cancellationToken);
                    tx.Commit();
                    result = new(
                        Convert.ToInt64(await ScalarAsync(destination, null, "SELECT applied FROM official_imports WHERE package_id=$a", cancellationToken, manifest.PackageId)),
                        Convert.ToInt64(await ScalarAsync(destination, null, "SELECT skipped FROM official_imports WHERE package_id=$a", cancellationToken, manifest.PackageId)),
                        Convert.ToInt64(await ScalarAsync(destination, null, "SELECT COUNT(*) FROM official_conflicts WHERE package_id=$a", cancellationToken, manifest.PackageId)));
                }
                progress?.Report($"Importação: {last:N0}/{manifest.Units:N0}; aplicadas: {result.Applied:N0}; preservadas: {result.Skipped:N0}; revalidar: {result.Conflicts:N0}.");
                if (complete)
                {
                    if (manifest.InitialBase)
                        await RecordInitialExtrasAsync(manifest.PackageId, temporary, progress, cancellationToken);
                    progress?.Report("Conferindo a retenção de 11 meses…");
                    var retention = await new SqliteContractRepository(_connections).MaintainRetentionAsync(
                        DateOnly.FromDateTime(DateTime.Today), cancellationToken: cancellationToken, progress: progress);
                    progress?.Report(retention.Message);
                    await using var lease = await _connections.WorkCoordinator.EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken);
                    await using var destination = await OpenAsync(cancellationToken);
                    await using var tx = destination.BeginTransaction();
                    await ExecuteAsync(destination, tx, "UPDATE official_imports SET completed=1 WHERE package_id=$a; UPDATE official_transfer_state SET base_ready=1 WHERE id=1;", cancellationToken, manifest.PackageId);
                    tx.Commit();
                    return result;
                }
                await Task.Yield(); // release the writer between independently resumable batches
            }
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 9 && cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Importação interrompida; lotes concluídos preservados.", e, cancellationToken); }
        finally { DeleteTemporary(temporary); }
    }

    private async Task RecordInitialExtrasAsync(string packageId, string payloadPath, IProgress<string>? progress, CancellationToken ct)
    {
        // Read the destination snapshot without reserving its writer. Only entities absent
        // from the complete source need to be registered; matching base data is not rewritten.
        await using var snapshot = await OpenPayloadAsync(_connections.DatabasePath, true, ct);
        using var interruption = SqliteConnectionFactory.InterruptOnCancellation(snapshot, ct);
        if (Convert.ToInt64(await ScalarAsync(snapshot, null,
            "SELECT inventory_required AND NOT completed FROM official_imports WHERE package_id=$a", ct, packageId)) == 0) return;
        progress?.Report("Conferindo diferenças próprias do destino para futuras exportações…");
        await ExecuteAsync(snapshot, null, "ATTACH DATABASE $a AS transfer_source", ct, payloadPath);
        await using var tx = snapshot.BeginTransaction(deferred: true);
        await using var command = snapshot.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"""
            SELECT kind,key1,key2 FROM ({AllKeys}) local
            WHERE NOT EXISTS(SELECT 1 FROM transfer_source.units source
                WHERE source.kind=local.kind AND source.key1=local.key1 AND source.key2=local.key2)
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var batch = new List<(int Kind, string Key1, string Key2)>(32);
        while (await reader.ReadAsync(ct))
        {
            batch.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            if (batch.Count == 32) { await SaveBatchAsync(); batch.Clear(); }
        }
        if (batch.Count > 0) await SaveBatchAsync();

        async Task SaveBatchAsync()
        {
            await using var lease = await _connections.WorkCoordinator.EnterWriterAsync(SqliteWorkPriority.Background, ct);
            await using var connection = await OpenAsync(ct);
            await using var transaction = connection.BeginTransaction();
            foreach (var key in batch)
                await RecordDifferenceAsync(connection, transaction, key.Kind, key.Key1, key.Key2, ct);
            transaction.Commit();
        }
    }

    private static Task RecordDifferenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        int kind, string key1, string key2, CancellationToken ct) => ExecuteAsync(connection, transaction,
        "INSERT OR IGNORE INTO official_changes SELECT $a,$b,$c,revision FROM official_transfer_state WHERE id=1",
        ct, kind, key1, key2);

    // Called only by the explicit Atualizar action. Invalidating a completion proof
    // keeps the visible data while making the existing download services revalidate it.
    public async Task PrepareRevalidationAsync(CancellationToken cancellationToken = default)
    {
        string after = "";
        while (true)
        {
            await using var lease = await _connections.WorkCoordinator.EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken);
            await using var connection = await OpenAsync(cancellationToken);
            await using var tx = connection.BeginTransaction();
            var ids = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "SELECT DISTINCT key1 FROM official_conflicts WHERE kind IN(2,3) AND key1>$a ORDER BY key1 LIMIT 32";
                command.Parameters.AddWithValue("$a", after);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
            }
            foreach (var id in ids)
                await ExecuteAsync(connection, tx, """
                    DELETE FROM contract_item_snapshots WHERE contract_id=$a
                        AND EXISTS(SELECT 1 FROM official_conflicts WHERE kind=2 AND key1=$a);
                    UPDATE items SET hydration_status=4 WHERE contract_id=$a AND EXISTS(
                        SELECT 1 FROM official_conflicts WHERE kind=3 AND key1=$a AND key2=CAST(items.item_number AS TEXT));
                    UPDATE price_cache_contracts SET
                        status=CASE WHEN EXISTS(SELECT 1 FROM official_conflicts WHERE kind=2 AND key1=$a) THEN 0 ELSE status END,
                        next_retry_at=NULL,price_index_status=0,price_index_next_retry_at=NULL
                        WHERE contract_id=$a;
                    """, cancellationToken, id);
            tx.Commit();
            if (ids.Count == 0) return;
            after = ids[^1];
        }
    }

    private static async Task<Unit?> ReadUnitAsync(SqliteConnection c, SqliteTransaction? tx, int kind,
        string key1, string key2, CancellationToken ct)
    {
        string? version = null, parent = null;
        if (kind is 2 or 3)
        {
            var exists = await ScalarAsync(c, tx, "SELECT COUNT(*) FROM contracts WHERE pncp_id=$a", ct, key1);
            if (Convert.ToInt64(exists) == 0) return null;
            parent = await ScalarAsync(c, tx, "SELECT global_updated_at FROM contracts WHERE pncp_id=$a", ct, key1) as string;
            if (kind == 2)
            {
                var complete = await ScalarAsync(c, tx, "SELECT COUNT(*) FROM contract_item_snapshots WHERE contract_id=$a AND source_global_updated_at IS $b", ct, key1, parent);
                if (Convert.ToInt64(complete) == 0) return null;
                version = parent;
            }
            else
            {
                var complete = await ScalarAsync(c, tx, """
                    SELECT COUNT(*) FROM items i JOIN contract_item_snapshots s ON s.contract_id=i.contract_id
                    WHERE i.contract_id=$a AND i.item_number=$b AND i.hydration_status=2
                      AND s.source_global_updated_at IS $c
                      AND NOT EXISTS(SELECT 1 FROM official_result_snapshots r
                        WHERE r.contract_id=i.contract_id AND r.item_number=i.item_number
                          AND (r.parent_version IS NOT $c OR r.item_version IS NOT i.source_updated_at))
                    """, ct, key1, key2, parent);
                if (Convert.ToInt64(complete) == 0) return null;
                version = await ScalarAsync(c, tx, "SELECT source_updated_at FROM items WHERE contract_id=$a AND item_number=$b", ct, key1, key2) as string;
            }
        }
        var where = kind switch { 1 => "pncp_id=$a", 2 => "contract_id=$a", 3 => "contract_id=$a AND item_number=$b", 4 => "catalog_kind=$a AND code=$b", 5 => "coverage_date=$a AND CAST(modality_id AS TEXT)||'|'||uf=$b AND status=3", _ => throw new InvalidDataException("Unidade desconhecida.") };
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {string.Join(',', Columns[kind])} FROM {Tables[kind]} WHERE {where}" +
            (kind == 2 ? " ORDER BY item_number" : kind == 3 ? " ORDER BY result_sequence" : "");
        command.Parameters.AddWithValue("$a", key1);
        command.Parameters.AddWithValue("$b", key2);
        var rows = new List<Dictionary<string, JsonElement>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), JsonSerializer.SerializeToElement(reader.IsDBNull(i) ? null : reader.GetValue(i)));
            rows.Add(row);
        }
        if (kind is 1 or 4)
        {
            if (rows.Count == 0) return null;
            version = String(rows[0], kind == 1 ? "global_updated_at" : "remote_updated_at");
        }
        if (kind == 2 && Convert.ToInt64(await ScalarAsync(c, tx,
            "SELECT item_count FROM contract_item_snapshots WHERE contract_id=$a", ct, key1)) != rows.Count) return null;
        if (kind == 5 && rows.Count == 0) return null;
        return new(kind, key1, key2, version, parent, rows);
    }

    private static async Task ValidatePayloadAsync(SqliteConnection payload, long expected, CancellationToken ct)
    {
        if (Convert.ToInt64(await ScalarAsync(payload, null,
            "SELECT EXISTS(SELECT 1 FROM units GROUP BY kind,key1,key2 HAVING COUNT(*)>1)", ct)) != 0)
            throw new InvalidDataException("Unidades duplicadas no pacote.");
        await using var command = payload.CreateCommand();
        command.CommandText = "SELECT sequence,kind,key1,key2,payload,hash FROM units ORDER BY sequence";
        await using var reader = await command.ExecuteReaderAsync(ct);
        long count = 0;
        int previousKind = 0;
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(4);
            var unit = JsonSerializer.Deserialize<Unit>(json, Json) ?? throw new InvalidDataException("Unidade vazia.");
            if (reader.GetInt64(0) != ++count || unit.Kind is < 1 or > 5 || unit.Kind < previousKind ||
                unit.Kind != reader.GetInt32(1) || unit.Key1 != reader.GetString(2) || unit.Key2 != reader.GetString(3) ||
                Hash(json) != reader.GetString(5) || string.IsNullOrWhiteSpace(unit.Key1) || unit.Rows is null ||
                unit.Kind is 1 or 4 or 5 && unit.Rows.Count != 1)
                throw new InvalidDataException("Unidade inválida ou fora de ordem.");
            previousKind = unit.Kind;
            if (unit.Kind is 1 or 2 && unit.Key2 != "" ||
                unit.Kind == 3 && (!long.TryParse(unit.Key2, NumberStyles.None, CultureInfo.InvariantCulture, out var itemNumber) || itemNumber < 1))
                throw new InvalidDataException("Chave da unidade inválida.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in unit.Rows)
            {
                if (row.Count != Columns[unit.Kind].Length || Columns[unit.Kind].Any(name => !row.ContainsKey(name)) ||
                    row.Values.Any(v => v.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null)))
                    throw new InvalidDataException("Colunas inválidas no pacote.");
                var nullable = new HashSet<string>(StringComparer.Ordinal) { "publication_date", "global_updated_at", "total_homologated_scaled",
                    "municipality_ibge_code", "distance_from_ribeirao_km", "municipality_distance_rank", "state_proximity_rank", "source_updated_at", "requested_quantity_scaled",
                    "quantity_scaled", "unit_value_scaled", "total_value_scaled", "result_date", "remote_updated_at", "records_count" };
                var integers = new HashSet<string>(StringComparer.Ordinal) { "purchase_year", "purchase_sequence", "modality_id", "municipality_distance_rank",
                    "state_proximity_rank", "geo_layer", "random_order_key", "item_number", "has_result", "result_sequence", "result_status_id",
                    "catalog_kind", "active", "sustainable", "exclusive_central", "records_count" };
                if (unit.Kind == 5) integers.Add("status");
                foreach (var (name, value) in row)
                {
                    if (value.ValueKind == JsonValueKind.Null)
                    { if (!nullable.Contains(name)) throw new InvalidDataException("Campo obrigatório ausente: " + name); continue; }
                    if (integers.Contains(name) || name.EndsWith("_scaled", StringComparison.Ordinal))
                    { if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _)) throw new InvalidDataException("Inteiro inválido: " + name); }
                    else if (name == "distance_from_ribeirao_km")
                    { if (value.ValueKind != JsonValueKind.Number) throw new InvalidDataException("Distância inválida."); }
                    else if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Texto inválido: " + name);
                }
                if (unit.Kind == 4 && row["catalog_kind"].GetInt64() is not (1 or 2)) throw new InvalidDataException("Catálogo inválido.");
                var key = unit.Kind switch { 1 => String(row, "pncp_id"), 2 or 3 => String(row, "contract_id"), 4 => row["catalog_kind"].ToString(), _ => String(row, "coverage_date") };
                var subkey = unit.Kind switch { 1 => "", 2 => row["item_number"].ToString(), 3 => row["result_sequence"].ToString(), 4 => String(row, "code"), _ => row["modality_id"].ToString() + "|" + String(row, "uf") };
                if (key != unit.Key1 || !keys.Add(subkey ?? "") ||
                    unit.Kind == 3 && row["item_number"].ToString() != unit.Key2 ||
                    unit.Kind is 4 or 5 && subkey != unit.Key2)
                    throw new InvalidDataException("Chaves divergentes ou duplicadas no pacote.");
            }
            if (unit.Kind == 5 && (unit.Rows[0]["status"].GetInt64() != 3 || !DateOnly.TryParseExact(unit.Key1, "yyyy-MM-dd", out _)))
                throw new InvalidDataException("Comprovação de cobertura inválida.");
            if (unit.Kind == 2 && unit.Version != unit.ParentVersion ||
                unit.Kind is 1 or 4 && unit.Version != String(unit.Rows[0], unit.Kind == 1 ? "global_updated_at" : "remote_updated_at"))
                throw new InvalidDataException("Versão divergente no pacote.");
        }
        if (count != expected) throw new InvalidDataException("Quantidade de unidades divergente.");
    }

    private static async Task<int> ApplyUnitAsync(SqliteConnection c, SqliteTransaction tx, Unit incoming,
        string packageId, CancellationToken ct)
    {
        var kind = incoming.Kind;
        if (kind == 5)
        {
            if (string.CompareOrdinal(incoming.Key1, DataWindow.Start(DateOnly.FromDateTime(DateTime.Today)).ToString("yyyy-MM-dd")) < 0) return 0;
            var row = incoming.Rows[0];
            if (Convert.ToInt64(await ScalarAsync(c, tx, "SELECT COUNT(*) FROM coverage_day_modalities WHERE coverage_date=$a AND modality_id=$b AND uf=$c AND status=3", ct,
                incoming.Key1, Value(row["modality_id"]), Value(row["uf"]))) > 0) return 0;
            await ExecuteAsync(c, tx, """
                INSERT INTO coverage_day_modalities(coverage_date,modality_id,uf,status,records_count,updated_at)
                VALUES($a,$b,$c,3,$d,$e) ON CONFLICT(coverage_date,modality_id,uf) DO UPDATE SET
                    status=3,records_count=excluded.records_count,updated_at=excluded.updated_at,last_error=NULL
                """, ct, incoming.Key1, Value(row["modality_id"]), Value(row["uf"]), Value(row["records_count"]), DateTimeOffset.UtcNow.ToString("O"));
            return 1;
        }
        if (kind == 1 && String(incoming.Rows[0], "publication_date") is { Length: >= 10 } date &&
            string.CompareOrdinal(date[..10], DataWindow.Start(DateOnly.FromDateTime(DateTime.Today)).ToString("yyyy-MM-dd")) < 0) return 0;
        var local = await ReadUnitAsync(c, tx, kind, incoming.Key1, incoming.Key2, ct);
        if (local is not null && Hash(JsonSerializer.Serialize(local, Json)) == Hash(JsonSerializer.Serialize(incoming, Json))) return 0;
        if (kind is 2 or 3)
        {
            var parentExists = Convert.ToInt64(await ScalarAsync(c, tx, "SELECT COUNT(*) FROM contracts WHERE pncp_id=$a AND global_updated_at IS $b", ct, incoming.Key1, incoming.ParentVersion)) > 0;
            if (!parentExists) return await ConflictAsync("Versão da contratação divergente ou ausente.");
            if (kind == 3 && Convert.ToInt64(await ScalarAsync(c, tx, "SELECT COUNT(*) FROM items WHERE contract_id=$a AND item_number=$b AND source_updated_at IS $c", ct, incoming.Key1, incoming.Key2, incoming.Version)) == 0)
                return await ConflictAsync("Versão do item divergente ou ausente.");
        }
        if (local is not null)
        {
            if (!DateTimeOffset.TryParse(local.Version, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localVersion) ||
                !DateTimeOffset.TryParse(incoming.Version, CultureInfo.InvariantCulture, DateTimeStyles.None, out var remoteVersion) || localVersion == remoteVersion)
                return await ConflictAsync("Conteúdos diferentes sem versão oficial que determine qual é mais recente.");
            if (remoteVersion < localVersion) return 3;
        }
        if (kind == 2)
        {
            await ExecuteAsync(c, tx, "CREATE TEMP TABLE IF NOT EXISTS update_item_keys(number INTEGER PRIMARY KEY); DELETE FROM update_item_keys;", ct);
            foreach (var row in incoming.Rows)
                await ExecuteAsync(c, tx, "INSERT INTO update_item_keys VALUES($a)", ct, Value(row["item_number"]));
            await ExecuteAsync(c, tx, "DELETE FROM items WHERE contract_id=$a AND item_number NOT IN(SELECT number FROM update_item_keys)", ct, incoming.Key1);
        }
        if (kind == 3)
            await ExecuteAsync(c, tx, "DELETE FROM item_results WHERE contract_id=$a AND item_number=$b", ct, incoming.Key1, incoming.Key2);
        var newContract = kind == 1 && local is null;
        foreach (var row in incoming.Rows)
        {
            // The column list is compiled into the application, never taken from archive SQL.
            var columns = Columns[kind];
            var primary = kind switch { 1 => new[] { "pncp_id" }, 2 => ["contract_id", "item_number"], 3 => ["contract_id", "item_number", "result_sequence"], _ => ["catalog_kind", "code"] };
            var sql = $"INSERT INTO {Tables[kind]}({string.Join(',', columns)}) VALUES({string.Join(',', columns.Select((_, i) => "$p" + i))}) ON CONFLICT({string.Join(',', primary)}) DO UPDATE SET " +
                string.Join(',', columns.Except(primary).Select(name => name + "=excluded." + name));
            await using var upsert = c.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = sql;
            for (var i = 0; i < columns.Length; i++) upsert.Parameters.AddWithValue("$p" + i, Value(row[columns[i]]) ?? DBNull.Value);
            await upsert.ExecuteNonQueryAsync(ct);
        }
        if (newContract)
            await ExecuteAsync(c, tx, "UPDATE dataset_statistics SET contract_count=contract_count+1 WHERE id=1", ct);
        if (kind == 2)
        {
            await ExecuteAsync(c, tx, "DELETE FROM item_results WHERE contract_id=$a AND item_number IN(SELECT item_number FROM items WHERE contract_id=$a AND has_result=0)", ct, incoming.Key1);
            await ExecuteAsync(c, tx, """
                INSERT INTO contract_item_snapshots(contract_id,fetched_at,item_count,source_global_updated_at)
                VALUES($a,$b,$c,$d) ON CONFLICT(contract_id) DO UPDATE SET fetched_at=excluded.fetched_at,
                    item_count=excluded.item_count,source_global_updated_at=excluded.source_global_updated_at
                """, ct, incoming.Key1, DateTimeOffset.UtcNow.ToString("O"), incoming.Rows.Count, incoming.ParentVersion);
        }
        if (kind == 3)
        {
            await ExecuteAsync(c, tx, "UPDATE items SET hydration_status=2,last_error=NULL,cache_updated_at=$c WHERE contract_id=$a AND item_number=$b", ct, incoming.Key1, incoming.Key2, DateTimeOffset.UtcNow.ToString("O"));
            await SqliteContractRepository.RecordOfficialResultsAsync(c, tx, incoming.Key1, long.Parse(incoming.Key2, CultureInfo.InvariantCulture), ct);
        }
        await ExecuteAsync(c, tx, "DELETE FROM official_conflicts WHERE kind=$a AND key1=$b AND key2=$c", ct, kind, incoming.Key1, incoming.Key2);
        return 1;

        async Task<int> ConflictAsync(string message)
        {
            await ExecuteAsync(c, tx, "INSERT INTO official_conflicts VALUES($a,$b,$c,$d,$e) ON CONFLICT(kind,key1,key2) DO UPDATE SET package_id=excluded.package_id,message=excluded.message", ct,
                kind, incoming.Key1, incoming.Key2, packageId, message);
            return 2;
        }
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken ct) => _connections.OpenAsync(ct);
    private static async Task<SqliteConnection> OpenPayloadAsync(string path, bool readOnly, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
        }.ToString());
        try { await connection.OpenAsync(ct); await ExecuteAsync(connection, null, "PRAGMA temp_store=FILE; PRAGMA cache_size=-32768; PRAGMA trusted_schema=OFF;", ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$" + (char)('a' + i), values[i] ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<object?> ScalarAsync(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$" + (char)('a' + i), values[i] ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(ct); return result is DBNull ? null : result;
    }
    private static object? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? (object)integer : value.GetDouble(),
        _ => throw new InvalidDataException("Valor inválido.")
    };
    private static string? String(Dictionary<string, JsonElement> row, string name) => row[name].ValueKind == JsonValueKind.Null ? null : row[name].GetString();
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static async Task<string> FileHashAsync(string path, CancellationToken ct)
    { await using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)); }
    private static void DeleteTemporary(string path)
    { foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
}
