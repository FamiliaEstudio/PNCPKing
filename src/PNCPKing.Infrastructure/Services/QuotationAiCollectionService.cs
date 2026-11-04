using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

/// <summary>
/// Serial orchestration over the existing local reader. The small auxiliary database stores
/// candidates/checkpoints together; the national database schema and query engine are untouched.
/// </summary>
public sealed class QuotationAiCollectionService(
    IPriceCacheRepository prices, IQuotationRepository quotations, string databasePath)
{
    public async Task<QuotationAiPackageManifest> CollectAsync(
        QuotationAutomationRun run, IProgress<QuotationAiProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run.OutputPath);
        var lines = (await quotations.GetLinesAsync(run.ProjectId, cancellationToken).ConfigureAwait(false))
            .Where(line => line.AutomationRunId == run.Id).OrderBy(line => line.DisplayOrder).ToArray();
        if (lines.Length == 0) throw new InvalidOperationException("A coleta não contém itens.");
        foreach (var line in lines)
        {
            _ = SearchText.ParseMainCriteria(line.SearchText);
            if (line.RequestedBatchCount is < 1 or > 100)
                throw new InvalidDataException("O limite de páginas por critério deve ficar entre 1 e 100.");
        }
        var project = (await quotations.GetProjectsAsync(cancellationToken).ConfigureAwait(false))
            .Single(value => value.Id == run.ProjectId);
        var stagingPath = Path.GetFullPath(run.OutputPath) + ".coleta.sqlite";
        if (!File.Exists(stagingPath) && File.Exists(run.OutputPath))
        {
            var exported = await QuotationAiPackage.ReadManifestAsync(run.OutputPath, cancellationToken).ConfigureAwait(false);
            if (exported.RunId == run.Id && exported.ProjectId == run.ProjectId &&
                exported.IsMedication == project.IsMedication && exported.Items.Count == lines.Length &&
                exported.StartDate == run.StartDate && exported.EndDate == run.EndDate &&
                exported.GeoFilterKind == run.GeoFilter.Kind && exported.GeoFilterUf == run.GeoFilter.Uf &&
                lines.All(line => exported.Items.Any(item => item.Line.Id == line.Id &&
                    item.Line.RequestedBatchCount == line.RequestedBatchCount &&
                    item.SpecificationHash == QuotationAiPackage.SpecificationHash(line))))
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(run.OutputPath);
                var verifiedItems = 0;
                long verifiedReferences = 0;
                foreach (var item in exported.Items)
                {
                    await foreach (var reference in QuotationAiPackage.ReadItemReferencesAsync(
                                       archive, exported, item.Line.Id, cancellationToken).ConfigureAwait(false))
                        verifiedReferences++;
                    progress?.Report(new("verify", ++verifiedItems, lines.Length, verifiedReferences,
                        $"Conferindo resultado salvo: {verifiedItems:N0}/{lines.Length:N0} itens."));
                }
                foreach (var line in lines)
                    await ReconcileCollectedItemAsync(line,
                        exported.Items.Single(item => item.Line.Id == line.Id), cancellationToken).ConfigureAwait(false);
                await quotations.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Completed,
                    "Resultado existente validado; a coleta aguarda o arquivo guia.", cancellationToken).ConfigureAwait(false);
                progress?.Report(new("completed", lines.Length, lines.Length,
                    exported.Items.Sum(item => item.CandidateCount), "O resultado desta coleta já está disponível."));
                return exported;
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        await using var staging = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = stagingPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        await staging.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(staging, """
            PRAGMA journal_mode=DELETE;
            PRAGMA synchronous=FULL;
            PRAGMA cache_size=-2048;
            PRAGMA temp_store=FILE;
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS candidates(
                line_id TEXT NOT NULL, id TEXT NOT NULL, data TEXT NOT NULL, matches TEXT NOT NULL,
                PRIMARY KEY(line_id,id));
            CREATE TABLE IF NOT EXISTS queries(
                line_id TEXT NOT NULL, part INTEGER NOT NULL, cursor TEXT, pages INTEGER NOT NULL,
                exhausted INTEGER NOT NULL, PRIMARY KEY(line_id,part));
            CREATE TABLE IF NOT EXISTS counts(line_id TEXT PRIMARY KEY, count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS completed(line_id TEXT PRIMARY KEY, item TEXT NOT NULL);
            """, cancellationToken).ConfigureAwait(false);
        var signature = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            run.Id, run.ProjectId, run.StartDate, run.EndDate, run.GeoFilter.Kind, run.GeoFilter.Uf,
            project.IsMedication,
            Items = lines.Select(line => new { Hash = QuotationAiPackage.SpecificationHash(line), line.RequestedBatchCount })
        }, QuotationAiPackage.JsonOptions)));
        var previousSignature = await ReadMetadataAsync(staging, "signature", cancellationToken).ConfigureAwait(false);
        if (previousSignature is not null && previousSignature != signature)
            throw new InvalidOperationException("A especificação, os filtros ou o limite de páginas mudaram. Inicie uma nova coleta em outro arquivo.");
        await SaveMetadataAsync(staging, "signature", signature, cancellationToken).ConfigureAwait(false);
        var fingerprint = DatabaseFingerprint();
        var previousFingerprint = await ReadMetadataAsync(staging, "database", cancellationToken).ConfigureAwait(false);
        if (previousFingerprint is not null && previousFingerprint != fingerprint)
        {
            // A file/WAL checkpoint can also change the stamp. Restarting only unfinished items
            // is conservative and safe; completed items retain their dated source snapshots.
            await ResetUnfinishedAsync(staging, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("collect", 0, lines.Length, 0,
                "A base foi alterada; o item incompleto será pesquisado novamente. Itens já coletados foram preservados."));
        }
        await quotations.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Running,
            "Coleta local para IA externa em andamento; nenhum documento será baixado.", cancellationToken).ConfigureAwait(false);
        fingerprint = DatabaseFingerprint();
        await SaveMetadataAsync(staging, "database", fingerprint, cancellationToken).ConfigureAwait(false);
        var completedItems = await ReadCompletedAsync(staging, cancellationToken).ConfigureAwait(false);
        long total = await ReadTotalCountAsync(staging, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completedItems.TryGetValue(line.Id, out var completedItem))
                {
                    // The checkpoint commits before the visible state. Reconcile a crash or
                    // cancellation between those writes without repeating the local search.
                    if (await ReconcileCollectedItemAsync(line, completedItem, cancellationToken).ConfigureAwait(false))
                    {
                        fingerprint = DatabaseFingerprint();
                        await SaveMetadataAsync(staging, "database", fingerprint, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }
                var expressions = SearchText.ParseMainCriteria(line.SearchText);
                var states = await ReadQueriesAsync(staging, line.Id, expressions.Count, cancellationToken).ConfigureAwait(false);
                while (states.Any(state => !state.Exhausted && state.Pages < line.RequestedBatchCount))
                {
                    for (var part = 0; part < expressions.Count; part++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (states[part].Exhausted || states[part].Pages >= line.RequestedBatchCount) continue;
                        var observed = DatabaseFingerprint();
                        if (observed != fingerprint)
                        {
                            await ResetItemAsync(staging, line.Id, cancellationToken).ConfigureAwait(false);
                            states = Enumerable.Range(0, expressions.Count).Select(_ => new QueryState()).ToArray();
                            total = await ReadTotalCountAsync(staging, cancellationToken).ConfigureAwait(false);
                            fingerprint = observed;
                            await SaveMetadataAsync(staging, "database", fingerprint, cancellationToken).ConfigureAwait(false);
                            progress?.Report(new("collect", completedItems.Count, lines.Length, total,
                                $"Base alterada: reiniciando somente {line.EffectiveDisplayName}."));
                            break;
                        }
                        var expression = expressions[part];
                        var query = new SearchQuery(expression.OriginalText, run.GeoFilter,
                            run.StartDate, run.EndDate, SearchSort.Nearest, 1, ItemSearchDefaults.ContractsPerBatch);
                        progress?.Report(new("collect", completedItems.Count, lines.Length, total,
                            $"Item {completedItems.Count + 1:N0}/{lines.Length:N0}: {line.EffectiveDisplayName}; " +
                            $"critério {part + 1}/{expressions.Count}, página {states[part].Pages + 1}/{line.RequestedBatchCount}."));
                        var pageProgress = progress is null ? null : new PageProgress(value => progress.Report(
                            new("collect", completedItems.Count, lines.Length, total,
                                $"Item {completedItems.Count + 1:N0}/{lines.Length:N0}: {line.EffectiveDisplayName}; " +
                                $"critério {part + 1}/{expressions.Count}, página {states[part].Pages + 1}/{line.RequestedBatchCount}; " +
                                $"{value.MatchingRows:N0} preço(s) encontrados nesta página.")));
                        // The existing reader may execute synchronously until its first I/O wait.
                        // Offload the page so SQLite never blocks the WPF dispatcher.
                        var page = await Task.Run(() => prices.SearchLocalAfterAsync(query, expression,
                            null, null, states[part].Cursor, ItemSearchDefaults.ContractsPerBatch,
                            PriceCacheLocalReadOrder.Discovery, pageProgress, cancellationToken), cancellationToken)
                            .ConfigureAwait(false);
                        if (DatabaseFingerprint() != fingerprint) continue; // discard page; next pass restarts this item
                        var next = new QueryState { Cursor = page.Cursor, Pages = states[part].Pages + 1, Exhausted = !page.HasMore };
                        total += await CommitPageAsync(staging, line.Id, part, expression.OriginalText,
                            page.Rows ?? [], next, fingerprint, cancellationToken).ConfigureAwait(false);
                        states[part] = next;
                    }
                }
                var count = await ReadItemCountAsync(staging, line.Id, cancellationToken).ConfigureAwait(false);
                var complete = states.All(state => state.Exhausted);
                var item = new QuotationAiPackageItem
                {
                    Line = line, SpecificationHash = QuotationAiPackage.SpecificationHash(line),
                    Criteria = expressions.Select(expression => expression.OriginalText).ToArray(),
                    CandidateCount = count, Complete = complete, CapturedAt = DateTimeOffset.UtcNow,
                    Message = complete ? "Todos os critérios foram esgotados no banco local disponível." :
                        $"Coleta parcial: limite de {line.RequestedBatchCount} página(s) por critério; ainda pode haver resultados."
                };
                await using (var command = staging.CreateCommand())
                {
                    command.CommandText = "INSERT OR REPLACE INTO completed(line_id,item) VALUES($id,$item);";
                    command.Parameters.AddWithValue("$id", line.Id.ToString("N"));
                    command.Parameters.AddWithValue("$item", JsonSerializer.Serialize(item, QuotationAiPackage.JsonOptions));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                completedItems[line.Id] = item;
                await ReconcileCollectedItemAsync(line, item, cancellationToken).ConfigureAwait(false);
                fingerprint = DatabaseFingerprint();
                await SaveMetadataAsync(staging, "database", fingerprint, cancellationToken).ConfigureAwait(false);
            }
            var manifest = new QuotationAiPackageManifest
            {
                PackageId = run.Id, ProjectId = run.ProjectId, RunId = run.Id, IsMedication = project.IsMedication,
                GeoFilterKind = run.GeoFilter.Kind, GeoFilterUf = run.GeoFilter.Uf,
                StartDate = run.StartDate, EndDate = run.EndDate,
                Items = lines.Select(line => completedItems[line.Id]).ToArray()
            };
            manifest = await QuotationAiPackage.WriteAsync(run.OutputPath, manifest,
                (id, token) => ReadStagedReferencesAsync(staging, id, token), progress, cancellationToken).ConfigureAwait(false);
            await quotations.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Completed,
                $"Coleta exportada para IA: {total:N0} candidatos. As cestas aguardam o arquivo guia.", cancellationToken).ConfigureAwait(false);
            // The compressed archive is durable and the run completed. Keep the uncompressed
            // staging database only when interrupted; finished collections need no duplicate copy.
            await staging.CloseAsync().ConfigureAwait(false);
            try { File.Delete(stagingPath); }
            catch (IOException) { /* A later cleanup may remove a temporarily locked staging file. */ }
            catch (UnauthorizedAccessException) { /* Preserve the valid exported result. */ }
            progress?.Report(new("completed", lines.Length, lines.Length, total, "Resultado preparado para triagem pela IA."));
            return manifest;
        }
        catch (OperationCanceledException)
        {
            if (DatabaseFingerprint() != fingerprint)
                await ResetUnfinishedAsync(staging, CancellationToken.None).ConfigureAwait(false);
            await quotations.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Cancelled,
                "Coleta pausada; páginas gravadas e itens concluídos foram preservados.", CancellationToken.None).ConfigureAwait(false);
            await SaveMetadataAsync(staging, "database", DatabaseFingerprint(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            if (DatabaseFingerprint() != fingerprint)
                await ResetUnfinishedAsync(staging, CancellationToken.None).ConfigureAwait(false);
            await quotations.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Failed,
                exception.Message, CancellationToken.None).ConfigureAwait(false);
            await SaveMetadataAsync(staging, "database", DatabaseFingerprint(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> ReconcileCollectedItemAsync(QuotationLine current,
        QuotationAiPackageItem collected, CancellationToken cancellationToken)
    {
        var state = collected.Complete
            ? QuotationAutomationItemState.Completed
            : QuotationAutomationItemState.CompletedWithWarning;
        var message = $"{collected.CandidateCount:N0} candidato(s) para IA. {collected.Message}";
        if (current.AutomationState == state && current.AutomationMessage == message) return false;
        await quotations.UpdateAutomationItemStateAsync(current.Id, state, message, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<long> CommitPageAsync(SqliteConnection connection, Guid lineId, int part,
        string criterion, IReadOnlyList<ItemSearchRow> rows, QueryState state, string fingerprint, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO candidates(line_id,id,data,matches) VALUES($line,$id,$data,$matches);";
        insert.Parameters.AddWithValue("$line", lineId.ToString("N"));
        insert.Parameters.AddWithValue("$id", ""); insert.Parameters.AddWithValue("$data", "");
        insert.Parameters.AddWithValue("$matches", criterion);
        insert.Prepare();
        long added = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.PriceState != ItemSearchPriceState.Homologated || row.Result?.HomologatedUnitValue is not > 0) continue;
            var reference = QuotationService.MapReference(lineId, row);
            insert.Parameters["$id"].Value = reference.Id;
            insert.Parameters["$data"].Value = JsonSerializer.Serialize(reference, QuotationAiPackage.JsonOptions);
            var changed = await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            added += changed;
            if (changed == 0)
            {
                await using var match = connection.CreateCommand();
                match.Transaction = transaction;
                match.CommandText = """
                    UPDATE candidates SET matches=matches || char(10) || $match
                    WHERE line_id=$line AND id=$id AND instr(char(10)||matches||char(10),char(10)||$match||char(10))=0;
                    """;
                match.Parameters.AddWithValue("$line", lineId.ToString("N"));
                match.Parameters.AddWithValue("$id", reference.Id); match.Parameters.AddWithValue("$match", criterion);
                await match.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
        await using var checkpoint = connection.CreateCommand();
        checkpoint.Transaction = transaction;
        checkpoint.CommandText = """
            INSERT OR REPLACE INTO queries(line_id,part,cursor,pages,exhausted) VALUES($line,$part,$cursor,$pages,$exhausted);
            INSERT INTO counts(line_id,count) VALUES($line,$count) ON CONFLICT(line_id) DO UPDATE SET count=count+$count;
            INSERT OR REPLACE INTO metadata(key,value) VALUES('database',$fingerprint);
            """;
        checkpoint.Parameters.AddWithValue("$line", lineId.ToString("N"));
        checkpoint.Parameters.AddWithValue("$part", part);
        checkpoint.Parameters.AddWithValue("$cursor", state.Cursor is null ? DBNull.Value : JsonSerializer.Serialize(state.Cursor, QuotationAiPackage.JsonOptions));
        checkpoint.Parameters.AddWithValue("$pages", state.Pages);
        checkpoint.Parameters.AddWithValue("$exhausted", state.Exhausted ? 1 : 0);
        checkpoint.Parameters.AddWithValue("$count", added); checkpoint.Parameters.AddWithValue("$fingerprint", fingerprint);
        await checkpoint.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return added;
    }

    private static async IAsyncEnumerable<QuotationReference> ReadStagedReferencesAsync(SqliteConnection connection,
        Guid lineId, [EnumeratorCancellation] CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT data,matches FROM candidates WHERE line_id=$line ORDER BY id;";
        command.Parameters.AddWithValue("$line", lineId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            yield return (JsonSerializer.Deserialize<QuotationReference>(reader.GetString(0), QuotationAiPackage.JsonOptions)
                ?? throw new InvalidDataException("Candidato da coleta inválido.")) with { MatchedSearchText = reader.GetString(1) };
        }
    }

    private string DatabaseFingerprint()
    {
        var fullPath = Path.GetFullPath(databasePath);
        var builder = new StringBuilder(fullPath);
        Span<byte> header = stackalloc byte[100];
        foreach (var path in new[] { fullPath, fullPath + "-wal" })
        {
            var info = new FileInfo(path);
            builder.Append('|').Append(info.Exists ? info.Length : -1)
                .Append('|').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
            if (!info.Exists) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var count = stream.Read(header);
            builder.Append('|').Append(Convert.ToHexString(header[..count]));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static async Task<QueryState[]> ReadQueriesAsync(SqliteConnection connection, Guid lineId, int count, CancellationToken token)
    {
        var result = Enumerable.Range(0, count).Select(_ => new QueryState()).ToArray();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT part,cursor,pages,exhausted FROM queries WHERE line_id=$id;";
        command.Parameters.AddWithValue("$id", lineId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var part = reader.GetInt32(0);
            if (part < 0 || part >= count) throw new InvalidDataException("Checkpoint da coleta inválido.");
            result[part] = new QueryState
            {
                Cursor = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<PriceCacheLocalCursor>(reader.GetString(1), QuotationAiPackage.JsonOptions),
                Pages = reader.GetInt32(2), Exhausted = reader.GetInt32(3) != 0
            };
        }
        return result;
    }

    private static async Task<Dictionary<Guid, QuotationAiPackageItem>> ReadCompletedAsync(SqliteConnection connection, CancellationToken token)
    {
        var result = new Dictionary<Guid, QuotationAiPackageItem>();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT item FROM completed;";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var item = JsonSerializer.Deserialize<QuotationAiPackageItem>(reader.GetString(0), QuotationAiPackage.JsonOptions)
                ?? throw new InvalidDataException("Checkpoint de item inválido.");
            result.Add(item.Line.Id, item);
        }
        return result;
    }

    private static async Task ResetItemAsync(SqliteConnection connection, Guid lineId, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM candidates WHERE line_id=$id; DELETE FROM queries WHERE line_id=$id; DELETE FROM counts WHERE line_id=$id;";
        command.Parameters.AddWithValue("$id", lineId.ToString("N"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static async Task ResetUnfinishedAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM candidates WHERE line_id NOT IN (SELECT line_id FROM completed);
            DELETE FROM queries WHERE line_id NOT IN (SELECT line_id FROM completed);
            DELETE FROM counts WHERE line_id NOT IN (SELECT line_id FROM completed);
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static async Task<long> ReadItemCountAsync(SqliteConnection connection, Guid lineId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE((SELECT count FROM counts WHERE line_id=$id),0);";
        command.Parameters.AddWithValue("$id", lineId.ToString("N"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadTotalCountAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT COALESCE(SUM(count),0) FROM counts;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadMetadataAsync(SqliteConnection connection, string key, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM metadata WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
    }

    private static async Task SaveMetadataAsync(SqliteConnection connection, string key, string value, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO metadata(key,value) VALUES($key,$value);";
        command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private sealed record QueryState
    {
        public PriceCacheLocalCursor? Cursor { get; init; }
        public int Pages { get; init; }
        public bool Exhausted { get; init; }
    }

    private sealed class PageProgress(Action<PriceCacheLocalProgress> report) : IProgress<PriceCacheLocalProgress>
    {
        public void Report(PriceCacheLocalProgress value) => report(value);
    }
}
