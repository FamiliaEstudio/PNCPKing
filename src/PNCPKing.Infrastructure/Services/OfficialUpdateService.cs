using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Infrastructure.Services;

public sealed record OfficialUpdateChunk(
    string Key,
    string Kind,
    string Entry,
    DateOnly? Date,
    long ExpandedSize,
    string Sha256,
    string ContentDigest,
    long Contracts,
    long ItemSnapshots,
    long Items,
    long ResultSnapshots,
    long Results,
    long CoverageCells);

public sealed record OfficialUpdateManifest(
    int Format,
    int Schema,
    DateOnly StartDate,
    DateOnly EndDate,
    DateTimeOffset GeneratedAt,
    DateTimeOffset IntegrityValidatedAt,
    string PackageId,
    IReadOnlyList<OfficialUpdateChunk> Chunks);

public sealed record OfficialUpdateResult(long Applied, long Skipped, long Conflicts);
public sealed record OfficialImportReceipt(string Checksum, bool Completed);
public sealed record OfficialTransferStatus(IReadOnlyDictionary<string, OfficialImportReceipt> Imports);

/// <summary>
/// Creates and applies self-contained ten-day official-data packages. Export performs the
/// expensive completeness and SQLite checks; import only authenticates bytes it must extract
/// and merges one typed block per transaction.
/// </summary>
public sealed class OfficialUpdateService
{
    public const int CurrentFormat = 2;
    // The v2 official-data payload is unchanged by migrations of local quotations.
    public const int PayloadSchemaVersion = 29;
    public const int WindowDays = 10;
    private const string DayKind = "publication-day";
    private const string LateKind = "late-changes";
    private const int CompleteCoverage = (int)CoverageStatus.Complete;
    private const int CompleteItem = (int)ItemHydrationStatus.Complete;
    private const int ManifestLimit = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] ContractColumns =
        "pncp_id,cnpj,purchase_year,purchase_sequence,object,additional_information,process,organization,unit,municipality,uf,modality_id,modality_name,status,publication_date,global_updated_at,total_homologated_scaled,search_text,municipality_ibge_code,distance_from_ribeirao_km,municipality_distance_rank,state_proximity_rank,geo_layer,random_order_key".Split(',');
    private static readonly string[] ItemColumns =
        "contract_id,item_number,description,unit,status,has_result,source_updated_at,search_text,requested_quantity_scaled,additional_information,item_category,ncm_nbs_code,ncm_nbs_description,catalog_code,catalog_name,catalog_category".Split(',');
    private static readonly string[] ResultColumns =
        "contract_id,item_number,result_sequence,supplier_tax_id,supplier_name,quantity_scaled,unit_value_scaled,total_value_scaled,result_date,result_status_id,result_status_name,supplier_type,supplier_municipality,supplier_uf".Split(',');

    private readonly ISqliteConnectionFactory _connections;

    public OfficialUpdateService(ISqliteConnectionFactory connections) => _connections = connections;
    public OfficialUpdateService(string databasePath) : this(new SqliteConnectionFactory(databasePath)) { }

    public static async Task<(OfficialUpdateManifest Manifest, long ExpandedSize)> ReadManifestAsync(
        string path, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(path).Length >= GitHubUpdateValidation.AssetLimit)
            throw new InvalidDataException("O arquivo .pncpupdate atingiu o limite de 2 GiB e não pode ser importado.");

        using var zip = ZipFile.OpenRead(path);
        var (manifest, _) = await ReadAndValidateManifestAsync(zip, cancellationToken).ConfigureAwait(false);
        return (manifest, manifest.Chunks.Sum(chunk => chunk.ExpandedSize));
    }

    public async Task<OfficialTransferStatus> GetTransferStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT package_id,manifest_digest,completed FROM official_update_packages";
        var imports = new Dictionary<string, OfficialImportReceipt>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            imports[reader.GetString(0)] = new(reader.GetString(1), reader.GetInt64(2) != 0);
        return new(imports);
    }

    public async Task<OfficialUpdateManifest> ExportAsync(
        string path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var endDate = DateOnly.FromDateTime(DateTime.Now);
        var startDate = endDate.AddDays(-(WindowDays - 1));
        var outputPath = Path.GetFullPath(path);
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Destino inválido para a atualização.");
        Directory.CreateDirectory(outputDirectory);
        var workDirectory = Path.Combine(outputDirectory, ".pncpupdate-" + Guid.NewGuid().ToString("N"));
        var archivePath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(workDirectory);

        try
        {
            var chunks = new List<OfficialUpdateChunk>(WindowDays + 1);
            await using var readerLease = await _connections.WorkCoordinator
                .EnterReaderAsync(SqliteWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await using var source = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var sourceInterruption = SqliteConnectionFactory.InterruptOnCancellation(source, cancellationToken);
            await using var snapshot = source.BeginTransaction(deferred: true);

            await ValidateCoverageAsync(source, snapshot, startDate, endDate, cancellationToken).ConfigureAwait(false);
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                progress?.Report($"Validando e empacotando {date:dd/MM/yyyy}…");
                var key = "day:" + FormatDate(date);
                var payloadPath = Path.Combine(workDirectory, FormatDate(date) + ".db");
                chunks.Add(await BuildChunkAsync(source, snapshot, payloadPath, key, DayKind, date,
                    startDate, endDate, cancellationToken).ConfigureAwait(false));
            }

            progress?.Report("Validando contratações antigas alteradas na janela…");
            var latePath = Path.Combine(workDirectory, "late-changes.db");
            var late = await BuildChunkAsync(source, snapshot, latePath, LateKind, LateKind, null,
                startDate, endDate, cancellationToken).ConfigureAwait(false);
            if (late.Contracts > 0) chunks.Add(late);
            else DeleteTemporary(latePath);

            var validatedAt = DateTimeOffset.UtcNow;
            var manifest = new OfficialUpdateManifest(
                CurrentFormat,
                PayloadSchemaVersion,
                startDate,
                endDate,
                validatedAt,
                validatedAt,
                Guid.NewGuid().ToString("N"),
                chunks);

            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                await using (var metadata = zip.CreateEntry("manifest.json", CompressionLevel.SmallestSize).Open())
                    await JsonSerializer.SerializeAsync(metadata, manifest, Json, cancellationToken).ConfigureAwait(false);

                foreach (var chunk in chunks)
                {
                    var entry = zip.CreateEntry(chunk.Entry, CompressionLevel.SmallestSize);
                    await using var destination = entry.Open();
                    await using var input = new FileStream(Path.Combine(workDirectory, Path.GetFileName(chunk.Entry)),
                        FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(archivePath).Length >= GitHubUpdateValidation.AssetLimit)
                throw new InvalidOperationException("A atualização resultou em 2 GiB ou mais e foi recusada. Conclua Atualizar no computador exportador e tente novamente.");

            File.Move(archivePath, outputPath, overwrite: true);
            progress?.Report($"Atualização v2 exportada: {startDate:dd/MM/yyyy} a {endDate:dd/MM/yyyy}, {chunks.Count} blocos.");
            return manifest;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 9 && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Exportação interrompida.", exception, cancellationToken);
        }
        finally
        {
            DeleteTemporary(archivePath);
            DeleteDirectory(workDirectory);
        }
    }

    public async Task<OfficialUpdateResult> ImportAsync(
        string path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var packagePath = Path.GetFullPath(path);
        if (new FileInfo(packagePath).Length >= GitHubUpdateValidation.AssetLimit)
            throw new InvalidDataException("O arquivo .pncpupdate atingiu o limite de 2 GiB e não pode ser importado.");

        OfficialUpdateManifest manifest;
        string manifestDigest;
        using (var zip = ZipFile.OpenRead(packagePath))
            (manifest, manifestDigest) = await ReadAndValidateManifestAsync(zip, cancellationToken).ConfigureAwait(false);

        var existing = await GetTransferStatusAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Imports.TryGetValue(manifest.PackageId, out var completed) && completed.Completed)
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(completed.Checksum, manifestDigest))
                throw new InvalidDataException("A identidade do pacote já foi usada por outro manifesto.");
            progress?.Report("Este pacote já foi importado integralmente.");
            return new(0, manifest.Chunks.Sum(CountRows), 0);
        }

        await RegisterPackageAsync(manifest.PackageId, manifestDigest, cancellationToken).ConfigureAwait(false);
        long applied = 0;
        long skipped = 0;

        foreach (var chunk in manifest.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await HasChunkReceiptAsync(chunk, cancellationToken).ConfigureAwait(false))
            {
                skipped += CountRows(chunk);
                progress?.Report($"Bloco {chunk.Key} já concluído; extração dispensada.");
                continue;
            }

            var temporary = Path.Combine(Path.GetDirectoryName(_connections.DatabasePath)!,
                ".pncpupdate-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                progress?.Report($"Lendo {chunk.Key}…");
                await ExtractAndHashAsync(packagePath, chunk, temporary, cancellationToken).ConfigureAwait(false);
                var result = await ApplyChunkAsync(temporary, manifest.PackageId, chunk, cancellationToken)
                    .ConfigureAwait(false);
                applied += result.Applied;
                skipped += result.Skipped;
            }
            finally
            {
                DeleteTemporary(temporary);
            }
        }

        await CompletePackageAsync(manifest.PackageId, applied, skipped, cancellationToken).ConfigureAwait(false);
        progress?.Report($"Atualização concluída: {applied:N0} registros aplicados, {skipped:N0} preservados.");
        return new(applied, skipped, 0);
    }

    // Kept as a compatibility seam for the view model. V2 never schedules PNCP revalidation.
    public Task PrepareRevalidationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static async Task ValidateCoverageAsync(SqliteConnection source, SqliteTransaction snapshot,
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand();
        command.Transaction = snapshot;
        command.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT coverage_date),
                   COALESCE(SUM(CASE WHEN status=$complete THEN 0 ELSE 1 END),0)
              FROM coverage_day_modalities
             WHERE coverage_date BETWEEN $start AND $end AND uf='ALL';
            """;
        command.Parameters.AddWithValue("$complete", CompleteCoverage);
        command.Parameters.AddWithValue("$start", FormatDate(startDate));
        command.Parameters.AddWithValue("$end", FormatDate(endDate));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (reader.GetInt64(0) == 0 || reader.GetInt64(1) != WindowDays || reader.GetInt64(2) != 0)
            throw IncompleteExport("a cobertura oficial dos dez dias não está completa");
    }

    private static async Task<OfficialUpdateChunk> BuildChunkAsync(
        SqliteConnection source,
        SqliteTransaction snapshot,
        string payloadPath,
        string key,
        string kind,
        DateOnly? date,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var selector = kind == DayKind
            ? "substr(c.publication_date,1,10)=$date"
            : "substr(c.publication_date,1,10)<$start AND substr(c.global_updated_at,1,10) BETWEEN $start AND $end";

        var incompleteLists = await CountAsync(source, snapshot, $"""
            SELECT COUNT(*) FROM contracts c
             WHERE {selector}
               AND NOT EXISTS(
                   SELECT 1 FROM contract_item_snapshots s
                    WHERE s.contract_id=c.pncp_id
                      AND s.source_global_updated_at IS c.global_updated_at
                      AND s.item_count=(SELECT COUNT(*) FROM items i WHERE i.contract_id=c.pncp_id));
            """, date, startDate, endDate, cancellationToken).ConfigureAwait(false);
        if (incompleteLists != 0)
            throw IncompleteExport($"{incompleteLists:N0} contratação(ões) do bloco {key} têm lista de itens incompleta");

        var incompleteResults = await CountAsync(source, snapshot, $"""
            SELECT COUNT(*)
              FROM contracts c JOIN items i ON i.contract_id=c.pncp_id
             WHERE {selector} AND i.has_result=1
               AND (i.hydration_status<>$itemComplete OR NOT EXISTS(
                   SELECT 1 FROM official_result_snapshots s
                    WHERE s.contract_id=i.contract_id AND s.item_number=i.item_number
                      AND s.parent_version IS c.global_updated_at
                      AND s.item_version IS i.source_updated_at
                      AND (s.result_count IS NULL OR s.result_count=(
                          SELECT COUNT(*) FROM item_results r
                           WHERE r.contract_id=i.contract_id AND r.item_number=i.item_number))));
            """, date, startDate, endDate, cancellationToken, includeItemComplete: true).ConfigureAwait(false);
        if (incompleteResults != 0)
            throw IncompleteExport($"{incompleteResults:N0} item(ns) do bloco {key} têm resultados incompletos");

        await using var payload = await OpenPayloadAsync(payloadPath, readOnly: false, cancellationToken).ConfigureAwait(false);
        await CreatePayloadSchemaAsync(payload, cancellationToken).ConfigureAwait(false);
        await using var transaction = payload.BeginTransaction();

        var contracts = await CopyAsync(source, snapshot, payload, transaction,
            $"SELECT {Join("c", ContractColumns)} FROM contracts c WHERE {selector} ORDER BY c.pncp_id",
            "contracts", ContractColumns, date, startDate, endDate, cancellationToken).ConfigureAwait(false);
        var itemSnapshots = await CopyAsync(source, snapshot, payload, transaction,
            $"""
             SELECT s.contract_id,s.source_global_updated_at,s.item_count
               FROM contract_item_snapshots s JOIN contracts c ON c.pncp_id=s.contract_id
              WHERE {selector} ORDER BY s.contract_id
             """, "item_snapshots", ["contract_id", "parent_version", "item_count"], date, startDate, endDate,
            cancellationToken).ConfigureAwait(false);
        var items = await CopyAsync(source, snapshot, payload, transaction,
            $"""
             SELECT {Join("i", ItemColumns)} FROM items i JOIN contracts c ON c.pncp_id=i.contract_id
              WHERE {selector} ORDER BY i.contract_id,i.item_number
             """, "items", ItemColumns, date, startDate, endDate, cancellationToken).ConfigureAwait(false);
        var resultSnapshots = await CopyAsync(source, snapshot, payload, transaction,
            $"""
             SELECT i.contract_id,i.item_number,c.global_updated_at,i.source_updated_at,
                    (SELECT COUNT(*) FROM item_results r
                      WHERE r.contract_id=i.contract_id AND r.item_number=i.item_number)
               FROM items i JOIN contracts c ON c.pncp_id=i.contract_id
              WHERE {selector} ORDER BY i.contract_id,i.item_number
             """, "result_snapshots", ["contract_id", "item_number", "parent_version", "item_version", "result_count"],
            date, startDate, endDate, cancellationToken).ConfigureAwait(false);
        var results = await CopyAsync(source, snapshot, payload, transaction,
            $"""
             SELECT {Join("r", ResultColumns)}
               FROM item_results r JOIN contracts c ON c.pncp_id=r.contract_id
              WHERE {selector} ORDER BY r.contract_id,r.item_number,r.result_sequence
             """, "item_results", ResultColumns, date, startDate, endDate, cancellationToken).ConfigureAwait(false);
        var coverage = kind == DayKind
            ? await CopyAsync(source, snapshot, payload, transaction,
                """
                SELECT coverage_date,modality_id,uf,status,records_count
                  FROM coverage_day_modalities
                 WHERE coverage_date=$date AND uf='ALL' AND status=$coverageComplete
                 ORDER BY modality_id,uf
                """, "coverage", ["coverage_date", "modality_id", "uf", "status", "records_count"],
                date, startDate, endDate, cancellationToken, includeCoverageComplete: true).ConfigureAwait(false)
            : 0;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        await using (var foreignKeys = payload.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            await using var invalid = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await invalid.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException($"O bloco {key} falhou na verificação de chaves estrangeiras.");
        }
        await using (var integrity = payload.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if (!Equals("ok", await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
                throw new InvalidDataException($"O bloco {key} falhou na verificação de integridade.");
        }

        var logicalDigest = await ComputeLogicalDigestAsync(payload, cancellationToken).ConfigureAwait(false);
        await payload.CloseAsync().ConfigureAwait(false);
        var size = new FileInfo(payloadPath).Length;
        var sha256 = await FileHashAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        return new(key, kind, kind == DayKind ? "days/" + FormatDate(date!.Value) + ".db" : "late-changes.db",
            date, size, sha256, logicalDigest, contracts, itemSnapshots, items, resultSnapshots, results, coverage);
    }

    private static async Task<long> CopyAsync(
        SqliteConnection source,
        SqliteTransaction snapshot,
        SqliteConnection payload,
        SqliteTransaction payloadTransaction,
        string selectSql,
        string table,
        IReadOnlyList<string> columns,
        DateOnly? date,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken,
        bool includeCoverageComplete = false)
    {
        await using var select = source.CreateCommand();
        select.Transaction = snapshot;
        select.CommandText = selectSql;
        AddSelectorParameters(select, date, startDate, endDate);
        if (includeCoverageComplete) select.Parameters.AddWithValue("$coverageComplete", CompleteCoverage);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var insert = payload.CreateCommand();
        insert.Transaction = payloadTransaction;
        insert.CommandText = $"INSERT INTO {table}({string.Join(',', columns)}) VALUES({string.Join(',', Enumerable.Range(0, columns.Count).Select(i => "$p" + i))})";
        for (var index = 0; index < columns.Count; index++) insert.Parameters.Add(new SqliteParameter("$p" + index, DBNull.Value));

        long count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var index = 0; index < columns.Count; index++)
                insert.Parameters[index].Value = reader.IsDBNull(index) ? DBNull.Value : reader.GetValue(index);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    private static async Task<long> CountAsync(SqliteConnection source, SqliteTransaction snapshot, string sql,
        DateOnly? date, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken,
        bool includeItemComplete = false)
    {
        await using var command = source.CreateCommand();
        command.Transaction = snapshot;
        command.CommandText = sql;
        AddSelectorParameters(command, date, startDate, endDate);
        if (includeItemComplete) command.Parameters.AddWithValue("$itemComplete", CompleteItem);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static void AddSelectorParameters(SqliteCommand command, DateOnly? date, DateOnly startDate, DateOnly endDate)
    {
        if (date is not null) command.Parameters.AddWithValue("$date", FormatDate(date.Value));
        command.Parameters.AddWithValue("$start", FormatDate(startDate));
        command.Parameters.AddWithValue("$end", FormatDate(endDate));
    }

    private static async Task CreatePayloadSchemaAsync(SqliteConnection payload, CancellationToken cancellationToken)
    {
        await using var command = payload.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=DELETE;
            PRAGMA synchronous=FULL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE contracts(
                pncp_id TEXT PRIMARY KEY,cnpj TEXT NOT NULL,purchase_year INTEGER NOT NULL,purchase_sequence INTEGER NOT NULL,
                object TEXT NOT NULL,additional_information TEXT NOT NULL,process TEXT NOT NULL,organization TEXT NOT NULL,
                unit TEXT NOT NULL,municipality TEXT NOT NULL,uf TEXT NOT NULL,modality_id INTEGER NOT NULL,
                modality_name TEXT NOT NULL,status TEXT NOT NULL,publication_date TEXT,global_updated_at TEXT,
                total_homologated_scaled INTEGER,search_text TEXT NOT NULL,municipality_ibge_code TEXT,
                distance_from_ribeirao_km REAL,municipality_distance_rank INTEGER,state_proximity_rank INTEGER,
                geo_layer INTEGER,random_order_key INTEGER);
            CREATE TABLE item_snapshots(
                contract_id TEXT PRIMARY KEY,parent_version TEXT,item_count INTEGER NOT NULL,
                FOREIGN KEY(contract_id) REFERENCES contracts(pncp_id) ON DELETE CASCADE);
            CREATE TABLE items(
                contract_id TEXT NOT NULL,item_number INTEGER NOT NULL,description TEXT NOT NULL,unit TEXT NOT NULL,
                status TEXT NOT NULL,has_result INTEGER NOT NULL,source_updated_at TEXT,search_text TEXT NOT NULL,
                requested_quantity_scaled INTEGER,additional_information TEXT NOT NULL,item_category TEXT NOT NULL,
                ncm_nbs_code TEXT NOT NULL,ncm_nbs_description TEXT NOT NULL,catalog_code TEXT NOT NULL,
                catalog_name TEXT NOT NULL,catalog_category TEXT NOT NULL,PRIMARY KEY(contract_id,item_number),
                FOREIGN KEY(contract_id) REFERENCES contracts(pncp_id) ON DELETE CASCADE);
            CREATE TABLE result_snapshots(
                contract_id TEXT NOT NULL,item_number INTEGER NOT NULL,parent_version TEXT,item_version TEXT,
                result_count INTEGER NOT NULL,PRIMARY KEY(contract_id,item_number),
                FOREIGN KEY(contract_id,item_number) REFERENCES items(contract_id,item_number) ON DELETE CASCADE);
            CREATE TABLE item_results(
                contract_id TEXT NOT NULL,item_number INTEGER NOT NULL,result_sequence INTEGER NOT NULL,
                supplier_tax_id TEXT NOT NULL,supplier_name TEXT NOT NULL,quantity_scaled INTEGER,unit_value_scaled INTEGER,
                total_value_scaled INTEGER,result_date TEXT,result_status_id INTEGER NOT NULL,result_status_name TEXT NOT NULL,
                supplier_type TEXT NOT NULL,supplier_municipality TEXT NOT NULL,supplier_uf TEXT NOT NULL,
                PRIMARY KEY(contract_id,item_number,result_sequence),
                FOREIGN KEY(contract_id,item_number) REFERENCES items(contract_id,item_number) ON DELETE CASCADE);
            CREATE TABLE coverage(
                coverage_date TEXT NOT NULL,modality_id INTEGER NOT NULL,uf TEXT NOT NULL,status INTEGER NOT NULL,
                records_count INTEGER,PRIMARY KEY(coverage_date,modality_id,uf));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeLogicalDigestAsync(SqliteConnection payload, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (table, order) in new[]
                 {
                     ("contracts", "pncp_id"), ("item_snapshots", "contract_id"),
                     ("items", "contract_id,item_number"), ("result_snapshots", "contract_id,item_number"),
                     ("item_results", "contract_id,item_number,result_sequence"),
                     ("coverage", "coverage_date,modality_id,uf")
                 })
        {
            Append(hash, table);
            await using var command = payload.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY {order}";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    if (reader.IsDBNull(index)) { Append(hash, "N"); continue; }
                    var value = reader.GetValue(index);
                    Append(hash, value switch
                    {
                        byte[] bytes => "B:" + Convert.ToHexString(bytes),
                        double number => "R:" + number.ToString("R", CultureInfo.InvariantCulture),
                        float number => "R:" + number.ToString("R", CultureInfo.InvariantCulture),
                        _ when value is sbyte or byte or short or ushort or int or uint or long or ulong =>
                            "I:" + Convert.ToString(value, CultureInfo.InvariantCulture),
                        _ => "T:" + Convert.ToString(value, CultureInfo.InvariantCulture)
                    });
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static async Task<(OfficialUpdateManifest Manifest, string Digest)> ReadAndValidateManifestAsync(
        ZipArchive zip, CancellationToken cancellationToken)
    {
        var metadataEntries = zip.Entries.Where(entry => entry.FullName == "manifest.json").ToArray();
        if (metadataEntries.Length != 1 || metadataEntries[0].Length <= 0 || metadataEntries[0].Length > ManifestLimit)
            throw new InvalidDataException("Manifesto do pacote ausente ou inválido.");

        byte[] bytes;
        await using (var input = metadataEntries[0].Open())
        await using (var memory = new MemoryStream())
        {
            await input.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            bytes = memory.ToArray();
        }

        using (var document = JsonDocument.Parse(bytes))
        {
            if (!document.RootElement.TryGetProperty("format", out var format) &&
                !document.RootElement.TryGetProperty("Format", out format))
                throw new InvalidDataException("Formato do pacote não identificado.");
            if (format.GetInt32() != CurrentFormat)
                throw new InvalidDataException("Pacote .pncpupdate v1 incompatível. Gere uma atualização v2 no PNCP King 1.2.0 ou posterior.");
        }

        var manifest = JsonSerializer.Deserialize<OfficialUpdateManifest>(bytes, Json)
            ?? throw new InvalidDataException("Manifesto do pacote inválido.");
        ValidateManifest(manifest, zip);
        return (manifest, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static void ValidateManifest(OfficialUpdateManifest manifest, ZipArchive zip)
    {
        if (manifest.Format != CurrentFormat || manifest.Schema != PayloadSchemaVersion)
            throw new InvalidDataException("Formato ou esquema do pacote incompatível.");
        if (manifest.EndDate.DayNumber - manifest.StartDate.DayNumber != WindowDays - 1 ||
            !Guid.TryParseExact(manifest.PackageId, "N", out _) || manifest.Chunks is null)
            throw new InvalidDataException("Período ou identidade do pacote inválido.");
        if (manifest.GeneratedAt == default || manifest.IntegrityValidatedAt == default ||
            manifest.IntegrityValidatedAt < manifest.GeneratedAt)
            throw new InvalidDataException("Registro de validação do exportador inválido.");

        var expectedDates = Enumerable.Range(0, WindowDays)
            .Select(offset => manifest.StartDate.AddDays(offset)).ToHashSet();
        var daily = manifest.Chunks.Where(chunk => chunk.Kind == DayKind).ToArray();
        if (daily.Length != WindowDays || daily.Any(chunk => chunk.Date is null) ||
            !daily.Select(chunk => chunk.Date!.Value).ToHashSet().SetEquals(expectedDates) ||
            manifest.Chunks.Count(chunk => chunk.Kind == LateKind) > 1 ||
            manifest.Chunks.Any(chunk => chunk.Kind is not DayKind and not LateKind))
            throw new InvalidDataException("A janela do pacote não contém exatamente os dez blocos diários.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in manifest.Chunks)
        {
            if (!keys.Add(chunk.Key) || !names.Add(chunk.Entry) || chunk.ExpandedSize <= 0 ||
                chunk.ExpandedSize >= GitHubUpdateValidation.AssetLimit || !GitHubUpdateValidation.IsHash(chunk.Sha256) ||
                !GitHubUpdateValidation.IsHash(chunk.ContentDigest) || CountRows(chunk) < 0 ||
                chunk.Entry.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(chunk.Entry))
                throw new InvalidDataException("Descritor de bloco inválido.");
            var entry = zip.GetEntry(chunk.Entry);
            if (entry is null || entry.Length != chunk.ExpandedSize)
                throw new InvalidDataException($"Bloco ausente ou com tamanho divergente: {chunk.Key}.");
        }

        if (zip.Entries.Count != manifest.Chunks.Count + 1 ||
            zip.Entries.Any(entry => entry.FullName != "manifest.json" && !names.Contains(entry.FullName)))
            throw new InvalidDataException("O pacote contém entradas não declaradas.");
    }

    private async Task RegisterPackageAsync(string packageId, string digest, CancellationToken cancellationToken)
    {
        await using var lease = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken).ConfigureAwait(false);
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT manifest_digest FROM official_update_packages WHERE package_id=$id";
        query.Parameters.AddWithValue("$id", packageId);
        var existing = await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (existing is not null && !StringComparer.OrdinalIgnoreCase.Equals(existing, digest))
            throw new InvalidDataException("A identidade do pacote já foi usada por outro manifesto.");
        if (existing is null)
        {
            query.CommandText = "INSERT INTO official_update_packages(package_id,manifest_digest) VALUES($id,$digest)";
            query.Parameters.AddWithValue("$digest", digest);
            await query.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasChunkReceiptAsync(OfficialUpdateChunk chunk, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM official_update_chunks
             WHERE chunk_key=$key AND content_digest=$digest AND completed=1)
            """;
        command.Parameters.AddWithValue("$key", chunk.Key);
        command.Parameters.AddWithValue("$digest", chunk.ContentDigest);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task ExtractAndHashAsync(string packagePath, OfficialUpdateChunk chunk, string destination,
        CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        var entry = zip.GetEntry(chunk.Entry) ?? throw new InvalidDataException($"Bloco ausente: {chunk.Key}.");
        if (entry.Length != chunk.ExpandedSize) throw new InvalidDataException($"Tamanho divergente no bloco {chunk.Key}.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        {
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!StringComparer.OrdinalIgnoreCase.Equals(digest, chunk.Sha256))
        {
            DeleteTemporary(destination);
            throw new InvalidDataException($"Checksum divergente no bloco {chunk.Key}; nenhum dado desse bloco foi importado.");
        }
        File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
    }

    private async Task<OfficialUpdateResult> ApplyChunkAsync(string payloadPath, string packageId,
        OfficialUpdateChunk chunk, CancellationToken cancellationToken)
    {
        await using var lease = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken).ConfigureAwait(false);
        await using var destination = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interruption = SqliteConnectionFactory.InterruptOnCancellation(destination, cancellationToken);
        await using (var attach = destination.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $path AS incoming";
            attach.Parameters.AddWithValue("$path", Path.GetFullPath(payloadPath));
            await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var transaction = destination.BeginTransaction();
            await using var command = destination.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = MergeSql;
            command.Parameters.AddWithValue("$package", packageId);
            command.Parameters.AddWithValue("$key", chunk.Key);
            command.Parameters.AddWithValue("$digest", chunk.ContentDigest);
            command.Parameters.AddWithValue("$sha", chunk.Sha256);
            command.Parameters.AddWithValue("$complete", CompleteItem);
            command.Parameters.AddWithValue("$coverageComplete", CompleteCoverage);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var counts = destination.CreateCommand();
            counts.Transaction = transaction;
            counts.CommandText = """
                SELECT (SELECT COUNT(*) FROM accepted_contracts)
                     + (SELECT COUNT(*) FROM accepted_items)
                     + (SELECT COUNT(*) FROM accepted_results),
                       (SELECT COUNT(*) FROM incoming.contracts)
                     + (SELECT COUNT(*) FROM incoming.items)
                     + (SELECT COUNT(*) FROM incoming.item_results);
                """;
            await using var reader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var applied = reader.GetInt64(0);
            var skipped = Math.Max(0, reader.GetInt64(1) - applied);

            command.CommandText = """
                UPDATE official_update_chunks SET applied=$applied,skipped=$skipped
                 WHERE chunk_key=$key AND content_digest=$digest;
                """;
            command.Parameters.AddWithValue("$applied", applied);
            command.Parameters.AddWithValue("$skipped", skipped);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(applied, skipped, 0);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 9 && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Importação interrompida; o bloco atual foi revertido.", exception, cancellationToken);
        }
        finally
        {
            await using var detach = destination.CreateCommand();
            detach.CommandText = "DETACH DATABASE incoming";
            await detach.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private const string MergeSql = """
        DROP TABLE IF EXISTS temp.accepted_contracts;
        DROP TABLE IF EXISTS temp.accepted_lists;
        DROP TABLE IF EXISTS temp.accepted_items;
        DROP TABLE IF EXISTS temp.accepted_results;
        CREATE TEMP TABLE accepted_contracts(pncp_id TEXT PRIMARY KEY,is_new INTEGER NOT NULL) WITHOUT ROWID;
        INSERT INTO accepted_contracts
        SELECT n.pncp_id,d.pncp_id IS NULL
          FROM incoming.contracts n LEFT JOIN contracts d ON d.pncp_id=n.pncp_id
         WHERE d.pncp_id IS NULL OR (
             n.global_updated_at IS NOT NULL AND d.global_updated_at IS NOT NULL
             AND julianday(n.global_updated_at)>julianday(d.global_updated_at));

        INSERT INTO contracts(
            pncp_id,cnpj,purchase_year,purchase_sequence,object,additional_information,process,organization,unit,
            municipality,uf,modality_id,modality_name,status,publication_date,global_updated_at,total_homologated_scaled,
            search_text,municipality_ibge_code,distance_from_ribeirao_km,municipality_distance_rank,
            state_proximity_rank,geo_layer,random_order_key)
        SELECT n.pncp_id,n.cnpj,n.purchase_year,n.purchase_sequence,n.object,n.additional_information,n.process,
               n.organization,n.unit,n.municipality,n.uf,n.modality_id,n.modality_name,n.status,n.publication_date,
               n.global_updated_at,n.total_homologated_scaled,n.search_text,n.municipality_ibge_code,
               n.distance_from_ribeirao_km,n.municipality_distance_rank,n.state_proximity_rank,n.geo_layer,n.random_order_key
          FROM incoming.contracts n JOIN accepted_contracts a ON a.pncp_id=n.pncp_id
         WHERE 1=1
        ON CONFLICT(pncp_id) DO UPDATE SET
            cnpj=excluded.cnpj,purchase_year=excluded.purchase_year,purchase_sequence=excluded.purchase_sequence,
            object=excluded.object,additional_information=excluded.additional_information,process=excluded.process,
            organization=excluded.organization,unit=excluded.unit,municipality=excluded.municipality,uf=excluded.uf,
            modality_id=excluded.modality_id,modality_name=excluded.modality_name,status=excluded.status,
            publication_date=excluded.publication_date,global_updated_at=excluded.global_updated_at,
            total_homologated_scaled=excluded.total_homologated_scaled,search_text=excluded.search_text,
            municipality_ibge_code=excluded.municipality_ibge_code,
            distance_from_ribeirao_km=excluded.distance_from_ribeirao_km,
            municipality_distance_rank=excluded.municipality_distance_rank,state_proximity_rank=excluded.state_proximity_rank,
            geo_layer=excluded.geo_layer,random_order_key=excluded.random_order_key;

        UPDATE dataset_statistics
           SET contract_count=contract_count+(SELECT COUNT(*) FROM accepted_contracts WHERE is_new=1),updated_at=$now
         WHERE id=1;

        CREATE TEMP TABLE accepted_lists(contract_id TEXT PRIMARY KEY,replace_all INTEGER NOT NULL) WITHOUT ROWID;
        INSERT INTO accepted_lists
        SELECT s.contract_id,COALESCE(a.pncp_id IS NOT NULL,0)
          FROM incoming.item_snapshots s
          JOIN contracts d ON d.pncp_id=s.contract_id AND d.global_updated_at IS s.parent_version
          LEFT JOIN accepted_contracts a ON a.pncp_id=s.contract_id
          LEFT JOIN contract_item_snapshots old ON old.contract_id=s.contract_id
         WHERE a.pncp_id IS NOT NULL
            OR (old.contract_id IS NULL AND (SELECT COUNT(*) FROM items i WHERE i.contract_id=s.contract_id)<=s.item_count)
            OR (old.source_global_updated_at IS s.parent_version AND old.item_count=s.item_count);

        DELETE FROM items
         WHERE contract_id IN(SELECT contract_id FROM accepted_lists WHERE replace_all=1)
           AND NOT EXISTS(SELECT 1 FROM incoming.items n
                           WHERE n.contract_id=items.contract_id AND n.item_number=items.item_number);

        CREATE TEMP TABLE accepted_items(contract_id TEXT NOT NULL,item_number INTEGER NOT NULL,
            PRIMARY KEY(contract_id,item_number)) WITHOUT ROWID;
        INSERT INTO accepted_items
        SELECT n.contract_id,n.item_number
          FROM incoming.items n JOIN accepted_lists l ON l.contract_id=n.contract_id
          LEFT JOIN items d ON d.contract_id=n.contract_id AND d.item_number=n.item_number
         WHERE l.replace_all=1 OR d.contract_id IS NULL OR (
             n.source_updated_at IS NOT NULL AND d.source_updated_at IS NOT NULL
             AND julianday(n.source_updated_at)>julianday(d.source_updated_at));

        INSERT INTO items(contract_id,item_number,description,unit,status,has_result,source_updated_at,hydration_status,
            last_error,cache_updated_at,search_text,requested_quantity_scaled,additional_information,item_category,
            ncm_nbs_code,ncm_nbs_description,catalog_code,catalog_name,catalog_category)
        SELECT n.contract_id,n.item_number,n.description,n.unit,n.status,n.has_result,n.source_updated_at,
               CASE WHEN n.has_result=1 THEN 4 ELSE $complete END,NULL,NULL,n.search_text,n.requested_quantity_scaled,
               n.additional_information,n.item_category,n.ncm_nbs_code,n.ncm_nbs_description,n.catalog_code,
               n.catalog_name,n.catalog_category
          FROM incoming.items n JOIN accepted_items a
            ON a.contract_id=n.contract_id AND a.item_number=n.item_number
         WHERE 1=1
        ON CONFLICT(contract_id,item_number) DO UPDATE SET
            description=excluded.description,unit=excluded.unit,status=excluded.status,has_result=excluded.has_result,
            source_updated_at=excluded.source_updated_at,hydration_status=excluded.hydration_status,last_error=NULL,
            cache_updated_at=NULL,search_text=excluded.search_text,requested_quantity_scaled=excluded.requested_quantity_scaled,
            additional_information=excluded.additional_information,item_category=excluded.item_category,
            ncm_nbs_code=excluded.ncm_nbs_code,ncm_nbs_description=excluded.ncm_nbs_description,
            catalog_code=excluded.catalog_code,catalog_name=excluded.catalog_name,catalog_category=excluded.catalog_category;

        INSERT INTO contract_item_snapshots(contract_id,fetched_at,item_count,source_global_updated_at)
        SELECT s.contract_id,$now,s.item_count,s.parent_version FROM incoming.item_snapshots s
        JOIN accepted_lists l ON l.contract_id=s.contract_id
        WHERE (SELECT COUNT(*) FROM items i WHERE i.contract_id=s.contract_id)=s.item_count
        ON CONFLICT(contract_id) DO UPDATE SET fetched_at=excluded.fetched_at,item_count=excluded.item_count,
            source_global_updated_at=excluded.source_global_updated_at;

        CREATE TEMP TABLE accepted_results(contract_id TEXT NOT NULL,item_number INTEGER NOT NULL,
            PRIMARY KEY(contract_id,item_number)) WITHOUT ROWID;
        INSERT INTO accepted_results
        SELECT s.contract_id,s.item_number
          FROM incoming.result_snapshots s
          JOIN contracts c ON c.pncp_id=s.contract_id AND c.global_updated_at IS s.parent_version
          JOIN items i ON i.contract_id=s.contract_id AND i.item_number=s.item_number
                      AND i.source_updated_at IS s.item_version
          LEFT JOIN accepted_items ai ON ai.contract_id=s.contract_id AND ai.item_number=s.item_number
          LEFT JOIN official_result_snapshots old
                 ON old.contract_id=s.contract_id AND old.item_number=s.item_number
         WHERE ai.contract_id IS NOT NULL
            OR (old.contract_id IS NULL AND (SELECT COUNT(*) FROM item_results r
                 WHERE r.contract_id=s.contract_id AND r.item_number=s.item_number)<=s.result_count)
            OR (old.parent_version IS s.parent_version AND old.item_version IS s.item_version
                AND old.result_count=s.result_count);

        DELETE FROM item_results WHERE EXISTS(SELECT 1 FROM accepted_results a
            WHERE a.contract_id=item_results.contract_id AND a.item_number=item_results.item_number);
        INSERT INTO item_results(contract_id,item_number,result_sequence,supplier_tax_id,supplier_name,quantity_scaled,
            unit_value_scaled,total_value_scaled,result_date,result_status_id,result_status_name,supplier_type,
            supplier_municipality,supplier_uf)
        SELECT r.contract_id,r.item_number,r.result_sequence,r.supplier_tax_id,r.supplier_name,r.quantity_scaled,
               r.unit_value_scaled,r.total_value_scaled,r.result_date,r.result_status_id,r.result_status_name,
               r.supplier_type,r.supplier_municipality,r.supplier_uf
          FROM incoming.item_results r JOIN accepted_results a
            ON a.contract_id=r.contract_id AND a.item_number=r.item_number;
        UPDATE items SET hydration_status=$complete,last_error=NULL,cache_updated_at=$now
         WHERE EXISTS(SELECT 1 FROM accepted_results a
            WHERE a.contract_id=items.contract_id AND a.item_number=items.item_number);
        INSERT INTO official_result_snapshots(contract_id,item_number,parent_version,item_version,result_count)
        SELECT s.contract_id,s.item_number,s.parent_version,s.item_version,s.result_count
          FROM incoming.result_snapshots s JOIN accepted_results a
            ON a.contract_id=s.contract_id AND a.item_number=s.item_number
         WHERE 1=1
        ON CONFLICT(contract_id,item_number) DO UPDATE SET parent_version=excluded.parent_version,
            item_version=excluded.item_version,result_count=excluded.result_count;

        INSERT INTO coverage_day_modalities(coverage_date,modality_id,uf,status,records_count,updated_at,last_error)
        SELECT coverage_date,modality_id,uf,$coverageComplete,records_count,$now,NULL FROM incoming.coverage
         WHERE 1=1
        ON CONFLICT(coverage_date,modality_id,uf) DO UPDATE SET status=$coverageComplete,
            records_count=excluded.records_count,updated_at=excluded.updated_at,last_error=NULL;

        INSERT INTO official_update_chunks(chunk_key,content_digest,sha256,package_id,completed,imported_at)
        VALUES($key,$digest,$sha,$package,1,$now)
        ON CONFLICT(chunk_key,content_digest) DO UPDATE SET sha256=excluded.sha256,package_id=excluded.package_id,
            completed=1,imported_at=excluded.imported_at;
        """;

    private async Task CompletePackageAsync(string packageId, long applied, long skipped,
        CancellationToken cancellationToken)
    {
        await using var lease = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Background, cancellationToken).ConfigureAwait(false);
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE official_update_packages
               SET applied=$applied,skipped=$skipped,completed=1,imported_at=$now
             WHERE package_id=$id;
            """;
        command.Parameters.AddWithValue("$id", packageId);
        command.Parameters.AddWithValue("$applied", applied);
        command.Parameters.AddWithValue("$skipped", skipped);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenPayloadAsync(string path, bool readOnly,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<string> FileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string Join(string alias, IEnumerable<string> columns) =>
        string.Join(',', columns.Select(column => alias + "." + column));

    private static long CountRows(OfficialUpdateChunk chunk) => checked(
        chunk.Contracts + chunk.ItemSnapshots + chunk.Items + chunk.ResultSnapshots + chunk.Results + chunk.CoverageCells);

    private static InvalidOperationException IncompleteExport(string detail) => new(
        $"Não foi possível criar a atualização: {detail}. Conclua Atualizar no computador exportador e tente novamente.");

    private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void DeleteTemporary(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
