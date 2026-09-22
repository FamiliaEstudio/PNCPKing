using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

// Experimental SQL only: none of these strategies is installed in the application.
public sealed class MainSearchBatchCostTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string FullColumns = (string)typeof(SqlitePriceCacheRepository)
        .GetMethod("BuildLocalSearchColumns", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, ["0", "0.0", "0.0"])!;

    [Fact]
    public async Task ReadOnlyBenchmark_InterruptsSqliteWhenDeadlineExpires()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        await using var connection = await new MainSearchPerformanceTests.ReadOnlyConnections(database.Repository.DatabasePath)
            .OpenAsync(cancellation.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000000) SELECT SUM(x) FROM n;";
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));
        var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteScalarAsync());
        Assert.Equal(9, error.SqliteErrorCode); // SQLITE_INTERRUPT from the native progress handler.
    }

    [Theory]
    [InlineData(SearchSort.Newest)]
    [InlineData(SearchSort.Nearest)]
    public async Task CandidateBatches_PreserveFiltersAndResultBoundaries(SearchSort sort)
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var n = 1; n <= 4; n++)
        {
            var contract = PriceCacheTests.RecentContract($"batch-{n}", today.AddDays(n - 5), n);
            await database.Repository.UpsertContractsAsync([contract]);
            var items = Enumerable.Range(1, 60).Select(i => PriceCacheTests.Item(contract, i) with
            {
                Description = i % 5 == 0 ? "Serviço limpeza odontológico" : "Serviço limpeza predial",
                Unit = i % 3 == 0 ? "Diárias" : "Mês",
                HydrationStatus = ItemHydrationStatus.Complete
            }).ToArray();
            await database.Repository.UpsertItemsAsync(contract.PncpId, items, false);
            foreach (var item in items)
                await database.Repository.ReplaceItemResultsAsync(contract.PncpId, item.ItemNumber,
                    [PriceCacheTests.Result(contract, item.ItemNumber, 1, true),
                     PriceCacheTests.Result(contract, item.ItemNumber, 2, true),
                     PriceCacheTests.Result(contract, item.ItemNumber, 3, false)]);
        }
        var query = Query("Serviço limpeza -odontológico \"diária", today, sort);
        var expected = await MeasureAsync(database.Repository.DatabasePath, query, "current", 0, 30);
        Assert.Equal(100, expected.Keys.Count);
        foreach (var engine in new[] { "ordered-items", "filtered-price-queue" })
        {
            var actual = await MeasureAsync(database.Repository.DatabasePath, query, engine, 7, 30);
            Assert.Equal(expected.Keys, actual.Keys);
            Assert.Equal(100, actual.Keys.Distinct().Count());
        }
        var raw = await MeasureAsync(database.Repository.DatabasePath, query, "raw-items", 7, 30);
        Assert.Equal(100, raw.Keys.Count);
        Assert.False(expected.Keys.SequenceEqual(raw.Keys));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task IsolatedCopy_ComparesCandidateBatchCosts()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_BATCH_COST_COPY");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("Opt-in: PNCPKING_BATCH_COST_COPY aponta para uma cópia isolada em diretório de benchmark.");
            return;
        }
        Assert.True(File.Exists(path));
        Assert.Contains("benchmark", Path.GetDirectoryName(Path.GetFullPath(path))!, StringComparison.OrdinalIgnoreCase);
        var info = new FileInfo(path);
        var length = info.Length;
        var modified = info.LastWriteTimeUtc;
        var reportPath = Environment.GetEnvironmentVariable("PNCPKING_BATCH_COST_REPORT")!;
        Assert.False(string.IsNullOrWhiteSpace(reportPath));
        var rounds = int.Parse(Environment.GetEnvironmentVariable("PNCPKING_BATCH_COST_ROUNDS") ?? "3", CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var scenarios = new[]
        {
            (Id: "limpeza-diaria-recente", Query: Query("Serviço limpeza -odontológico \"diária", today, SearchSort.Newest)),
            (Id: "limpeza-ampla-recente", Query: Query("Serviço limpeza -odontológico", today, SearchSort.Newest)),
            (Id: "limpeza-diaria-proximidade", Query: Query("Serviço limpeza -odontológico \"diária", today, SearchSort.Nearest))
        };
        var plans = new[]
        {
            (Engine: "current", Size: 0),
            (Engine: "raw-items", Size: 250), (Engine: "ordered-items", Size: 250),
            (Engine: "raw-items", Size: 1000), (Engine: "ordered-items", Size: 1000),
            (Engine: "raw-items", Size: 5000), (Engine: "ordered-items", Size: 5000),
            (Engine: "filtered-price-queue", Size: 0)
        };
        var measurements = new List<Measurement>();
        var probes = new List<object>();
        var expected = new Dictionary<string, IReadOnlyList<string>>();
        async Task SaveAsync(string message)
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                startedDate = today, databaseBytes = length, databaseModifiedUtc = modified,
                sqlite = typeof(SqliteConnection).Assembly.GetName().Version?.ToString(),
                runtime = Environment.Version.ToString(), logicalProcessors = Environment.ProcessorCount,
                rounds, probes, measurements
            }, JsonOptions));
            await File.AppendAllTextAsync(reportPath + ".progress.txt", $"{DateTimeOffset.Now:O} {message}\n");
        }
        foreach (var scenario in scenarios)
        {
            await using var connection = await new MainSearchPerformanceTests.ReadOnlyConnections(path).OpenAsync();
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM items_fts WHERE items_fts MATCH $text;";
            count.Parameters.AddWithValue("$text", SearchText.Parse(scenario.Query.Text).ItemMatchQuery);
            var timer = Stopwatch.StartNew();
            var matches = Convert.ToInt64(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            probes.Add(new { scenario.Id, scenario.Query.Text, scenario.Query.Sort, matches, milliseconds = timer.Elapsed.TotalMilliseconds });
            await SaveAsync($"{scenario.Id}: {matches} correspondências FTS.");
        }
        for (var round = 0; round < rounds; round++)
        {
            foreach (var scenario in round % 2 == 0 ? scenarios : scenarios.AsEnumerable().Reverse().ToArray())
            {
                var order = round switch
                {
                    0 => plans,
                    1 => plans.AsEnumerable().Reverse().ToArray(),
                    _ => plans.Skip(3).Concat(plans.Take(3)).ToArray()
                };
                foreach (var plan in order)
                {
                    await SaveAsync($"Iniciando rodada {round} {scenario.Id} {plan.Engine}/{plan.Size}.");
                    var result = await MeasureAsync(path, scenario.Query, plan.Engine, plan.Size, 120);
                    result.Scenario = scenario.Id;
                    result.Round = round;
                    if (result.Error is null)
                    {
                        if (plan.Engine == "current") expected.TryAdd(scenario.Id, result.Keys.ToArray());
                        if (expected.TryGetValue(scenario.Id, out var reference))
                        {
                            result.SameOrderedKeysAsCurrent = reference.SequenceEqual(result.Keys);
                            result.KeysInCurrentFirst100 = result.Keys.Intersect(reference).Count();
                            if (plan.Engine != "raw-items") Assert.True(result.SameOrderedKeysAsCurrent, "O protótipo alterou os resultados.");
                        }
                        Assert.Equal(result.Keys.Count, result.Keys.Distinct().Count());
                    }
                    measurements.Add(result);
                    await SaveAsync($"Concluído {scenario.Id} {plan.Engine}/{plan.Size}: " +
                        $"{result.Pages.FirstOrDefault()?.Milliseconds:N1} ms primeira página; " +
                        $"{result.CandidatesRead} candidatos, {result.ItemChecks} avaliações. {result.Error}");
                }
            }
        }
        var after = new FileInfo(path);
        Assert.Equal(length, after.Length);
        Assert.Equal(modified, after.LastWriteTimeUtc);
        foreach (var measurement in measurements.Where(m => m.Error is null))
        {
            var reference = expected[measurement.Scenario];
            measurement.SameOrderedKeysAsCurrent = reference.SequenceEqual(measurement.Keys);
            measurement.KeysInCurrentFirst100 = measurement.Keys.Intersect(reference).Count();
            if (measurement.Engine != "raw-items") Assert.True(measurement.SameOrderedKeysAsCurrent);
        }
        await SaveAsync("Concluído; tamanho e data de modificação do banco preservados.");
    }

    private static SearchQuery Query(string text, DateOnly today, SearchSort sort) =>
        new(text, GeoScope.All, DataWindow.Start(today), today, Sort: sort);

    private static async Task<Measurement> MeasureAsync(string path, SearchQuery query, string engine, int batchSize, int timeoutSeconds)
    {
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(false);
        var timer = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var token = timeout.Token;
        var measurement = new Measurement { Engine = engine, BatchSize = batchSize };
        try
        {
            var expression = SearchText.Parse(query.Text);
            var connections = new MainSearchPerformanceTests.ReadOnlyConnections(path);
            if (engine == "current")
            {
                var repository = new SqlitePriceCacheRepository(connections);
                PriceCacheLocalCursor? cursor = null;
                for (var page = 1; page <= 2; page++)
                {
                    var start = page == 1 ? 0 : timer.Elapsed.TotalMilliseconds;
                    var previousContracts = measurement.ContractsExamined;
                    var result = await repository.SearchLocalAfterAsync(query, expression, null, null, cursor, 50,
                        new InlineProgress<PriceCacheLocalProgress>(p => measurement.ContractsExamined = previousContracts + p.ContractsExamined), token);
                    var keys = result.Rows!.Select(r => $"{r.Contract.PncpId}|{r.Item.ItemNumber}|{r.Result!.ResultSequence}").ToArray();
                    measurement.Keys.AddRange(keys);
                    measurement.Pages.Add(new PageCost(page, timer.Elapsed.TotalMilliseconds - start, keys.Length,
                        0, 0, 0, 0, 0, Fingerprint(keys), result.Rows!.FirstOrDefault()?.Contract.PublicationDate?.ToString("O")));
                    cursor = result.Cursor;
                    if (!result.HasMore) break;
                }
                return measurement;
            }

            await using var connection = await connections.OpenAsync(token);
            connection.CreateFunction<long, bool>("probe_tick", _ => { token.ThrowIfCancellationRequested(); return true; });
            connection.CreateFunction<string?, string?, bool>("pncp_item_matches", (description, unit) =>
            {
                token.ThrowIfCancellationRequested();
                measurement.ItemChecks++;
                return expression.MatchesItem(description, unit);
            });
            var pending = new Queue<PriceKey>();
            var orderedIds = new List<long>();
            if (engine == "ordered-items")
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT i.rowid FROM items_fts
                    CROSS JOIN items i ON i.rowid = items_fts.rowid
                    CROSS JOIN contracts c ON c.pncp_id = i.contract_id
                    WHERE items_fts MATCH $text AND i.hydration_status = 2
                      AND {Period} AND probe_tick(i.rowid)
                    ORDER BY {Order(query)}, i.item_number;
                    """;
                AddParameters(command, query, expression);
                measurement.Plan = await ExplainAsync(command, token);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) orderedIds.Add(reader.GetInt64(0));
                measurement.PreparedCandidates = orderedIds.Count;
                measurement.IdBufferBytes = orderedIds.Capacity * sizeof(long);
            }
            else if (engine == "filtered-price-queue")
            {
                var keys = await FilterAsync(connection, query, expression, null, token);
                foreach (var key in keys) pending.Enqueue(key);
                measurement.PreparedPrices = keys.Count;
            }
            measurement.PreparationMilliseconds = timer.Elapsed.TotalMilliseconds;
            var position = 0;
            var afterRowid = 0L;
            var exhausted = engine == "filtered-price-queue";
            for (var page = 1; page <= 2; page++)
            {
                var start = page == 1 ? 0 : timer.Elapsed.TotalMilliseconds;
                var filteringStart = timer.Elapsed.TotalMilliseconds;
                while (pending.Count < 51 && !exhausted)
                {
                    token.ThrowIfCancellationRequested();
                    long[] ids;
                    if (engine == "ordered-items")
                    {
                        ids = orderedIds.Skip(position).Take(batchSize).ToArray();
                        position += ids.Length;
                        exhausted = position >= orderedIds.Count;
                    }
                    else
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = """
                            SELECT rowid FROM items_fts WHERE items_fts MATCH $text AND rowid > $after
                            ORDER BY rowid LIMIT $limit;
                            """;
                        command.Parameters.AddWithValue("$text", expression.ItemMatchQuery);
                        command.Parameters.AddWithValue("$after", afterRowid);
                        command.Parameters.AddWithValue("$limit", batchSize);
                        if (measurement.Plan.Count == 0) measurement.Plan = await ExplainAsync(command, token);
                        var list = new List<long>(batchSize);
                        await using var reader = await command.ExecuteReaderAsync(token);
                        while (await reader.ReadAsync(token)) list.Add(reader.GetInt64(0));
                        ids = list.ToArray();
                        exhausted = ids.Length < batchSize;
                        if (ids.Length > 0) afterRowid = ids[^1];
                    }
                    if (ids.Length == 0) break;
                    measurement.Batches++;
                    measurement.CandidatesRead += ids.Length;
                    var filtered = await FilterAsync(connection, query, expression, ids, token);
                    foreach (var key in filtered) pending.Enqueue(key);
                    measurement.PreparedPrices += filtered.Count;
                    measurement.MaximumBufferedPrices = Math.Max(measurement.MaximumBufferedPrices, pending.Count);
                }
                var filtering = timer.Elapsed.TotalMilliseconds - filteringStart;
                var selected = new List<PriceKey>(50);
                while (selected.Count < 50 && pending.TryDequeue(out var key)) selected.Add(key);
                var hydrateStart = timer.Elapsed.TotalMilliseconds;
                await HydrateAsync(connection, selected, token);
                var hydration = timer.Elapsed.TotalMilliseconds - hydrateStart;
                var pageKeys = selected.Select(k => k.Key).ToArray();
                measurement.Keys.AddRange(pageKeys);
                measurement.Pages.Add(new PageCost(page, timer.Elapsed.TotalMilliseconds - start, selected.Count,
                    measurement.CandidatesRead, measurement.ItemChecks, measurement.Batches, filtering, hydration,
                    Fingerprint(pageKeys), selected.FirstOrDefault()?.Publication));
                if (exhausted && pending.Count == 0) break;
            }
        }
        catch (Exception ex) when (timeout.IsCancellationRequested && ex is OperationCanceledException or SqliteException)
        {
            measurement.Error = $"Timeout após {timeoutSeconds} segundos ({ex.GetType().Name}).";
        }
        finally
        {
            process.Refresh();
            measurement.TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
            measurement.CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            measurement.ManagedAllocatedBytes = GC.GetTotalAllocatedBytes(false) - allocated;
            measurement.WorkingSetAfterBytes = process.WorkingSet64;
            measurement.PrivateMemoryAfterBytes = process.PrivateMemorySize64;
        }
        return measurement;
    }

    private const string Period = "c.publication_date >= $start AND c.publication_date < $end";
    private static string Order(SearchQuery query) => (query.Sort == SearchSort.Nearest
        ? "COALESCE(c.geo_layer, 1), COALESCE(c.municipality_distance_rank, 999999), " : "") +
        "c.publication_date DESC, c.pncp_id";

    private static void AddParameters(SqliteCommand command, SearchQuery query, SearchExpression expression)
    {
        command.Parameters.AddWithValue("$text", expression.ItemMatchQuery);
        command.Parameters.AddWithValue("$start", query.StartDate!.Value.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$end", query.EndDate!.Value.AddDays(1).ToString("yyyy-MM-dd"));
    }

    private static async Task<List<PriceKey>> FilterAsync(SqliteConnection connection, SearchQuery query,
        SearchExpression expression, long[]? ids, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        var inputs = ids is null ? "" : "inputs(item_rowid, ordinal) AS (VALUES " + string.Join(",",
            ids.Select((id, n) => { command.Parameters.AddWithValue($"$id{n}", id); return $"($id{n},{n})"; })) + "),";
        var source = ids is null
            ? "items_fts CROSS JOIN items i ON i.rowid = items_fts.rowid"
            : "inputs x CROSS JOIN items i ON i.rowid = x.item_rowid";
        var sourceCondition = ids is null ? "items_fts MATCH $text AND " : "";
        var ordinal = ids is null ? "0" : "x.ordinal";
        var order = ids is null ? Order(query) + ", i.item_number, r.result_sequence" : "i.ordinal, r.result_sequence";
        command.CommandText = $"""
            WITH {inputs} matched AS MATERIALIZED (
                SELECT i.rowid AS item_rowid, i.contract_id, i.item_number, {ordinal} AS ordinal
                FROM {source}
                WHERE {sourceCondition} i.hydration_status = 2 AND pncp_item_matches(i.description, i.unit)
            )
            SELECT i.item_rowid, c.rowid, r.rowid, c.pncp_id, i.item_number, r.result_sequence, c.publication_date
            FROM matched i CROSS JOIN contracts c ON c.pncp_id = i.contract_id
            CROSS JOIN contract_item_snapshots s ON s.contract_id = i.contract_id
            CROSS JOIN item_results r ON r.contract_id = i.contract_id AND r.item_number = i.item_number
            WHERE {Period} AND COALESCE(s.source_global_updated_at, '') = COALESCE(c.global_updated_at, '')
              AND r.result_status_id = 1 AND r.unit_value_scaled > 0
            ORDER BY {order};
            """;
        AddParameters(command, query, expression);
        var keys = new List<PriceKey>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            keys.Add(new PriceKey(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                $"{reader.GetString(3)}|{reader.GetInt64(4)}|{reader.GetInt64(5)}", reader.GetString(6)));
        return keys;
    }

    private static async Task HydrateAsync(SqliteConnection connection, List<PriceKey> keys, CancellationToken token)
    {
        if (keys.Count == 0) return;
        await using var command = connection.CreateCommand();
        var values = keys.Select((key, n) =>
        {
            command.Parameters.AddWithValue($"$i{n}", key.ItemRowid);
            command.Parameters.AddWithValue($"$c{n}", key.ContractRowid);
            command.Parameters.AddWithValue($"$r{n}", key.ResultRowid);
            return $"($i{n},$c{n},$r{n},{n})";
        }).ToArray();
        command.CommandText = $"""
            WITH page(item_rowid, contract_rowid, result_rowid, ordinal) AS (VALUES {string.Join(",", values)})
            SELECT {FullColumns} FROM page k
            CROSS JOIN contracts c ON c.rowid = k.contract_rowid
            CROSS JOIN items i ON i.rowid = k.item_rowid
            CROSS JOIN item_results r ON r.rowid = k.result_rowid
            ORDER BY k.ordinal;
            """;
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            // Read the same 51 fields as production; UI/model construction is not benchmarked here.
            var valuesRead = new object[reader.FieldCount];
            reader.GetValues(valuesRead);
            Assert.Equal(keys[count].Key, $"{reader.GetString(0)}|{reader.GetInt64(20)}|{reader.GetInt64(39)}");
            count++;
        }
        Assert.Equal(keys.Count, count);
    }

    private static async Task<List<string>> ExplainAsync(SqliteCommand source, CancellationToken token)
    {
        await using var command = source.Connection!.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + source.CommandText;
        foreach (SqliteParameter parameter in source.Parameters) command.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(reader.GetString(3));
        return rows;
    }

    private static string Fingerprint(IEnumerable<string> keys) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', keys)))).ToLowerInvariant();

    private sealed record PriceKey(long ItemRowid, long ContractRowid, long ResultRowid, string Key, string Publication);
    private sealed record PageCost(int Page, double Milliseconds, int Rows, long CandidatesRead, long ItemChecks,
        int Batches, double FilteringMilliseconds, double HydrationMilliseconds, string Fingerprint, string? FirstPublication);
    private sealed class Measurement
    {
        public string Scenario { get; set; } = "";
        public int Round { get; set; }
        public string Engine { get; init; } = "";
        public int BatchSize { get; init; }
        public double PreparationMilliseconds { get; set; }
        public double TotalMilliseconds { get; set; }
        public double CpuMilliseconds { get; set; }
        public long ManagedAllocatedBytes { get; set; }
        public long WorkingSetAfterBytes { get; set; }
        public long PrivateMemoryAfterBytes { get; set; }
        public long PreparedCandidates { get; set; }
        public long IdBufferBytes { get; set; }
        public long CandidatesRead { get; set; }
        public long ItemChecks { get; set; }
        public int Batches { get; set; }
        public int PreparedPrices { get; set; }
        public int MaximumBufferedPrices { get; set; }
        public long ContractsExamined { get; set; }
        public bool? SameOrderedKeysAsCurrent { get; set; }
        public int? KeysInCurrentFirst100 { get; set; }
        public string? Error { get; set; }
        public List<string> Plan { get; set; } = [];
        public List<PageCost> Pages { get; } = [];
        [JsonIgnore] public List<string> Keys { get; } = [];
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
