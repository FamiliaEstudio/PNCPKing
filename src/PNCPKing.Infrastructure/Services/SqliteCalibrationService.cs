using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Infrastructure.Services;

public sealed record CalibrationMeasurement(
    SqliteSearchTuning Tuning, string Scenario, int Round, bool FirstAccess,
    double? FirstRowMs, double? TenRowsMs, double PageMs, double NextPageMs,
    double QueueMs, double PreparationMs, long PeakProcessBytes, long MinimumFreeBytes,
    bool MemoryPressure, bool Completed, string Fingerprint, string Outcome);

public sealed record SqliteCalibrationResult(
    SqliteSearchTuning Current, SqliteSearchTuning? Recommended,
    IReadOnlyList<CalibrationMeasurement> Measurements, string Message,
    SavedSqliteCalibration? SavedRecommendation = null);

public sealed class SqliteCalibrationService(
    SqliteConnectionFactory currentConnections,
    ISystemResourceProbe? resourceProbe = null)
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);
    public const long MinimumFreeMemory = 768L * 1024 * 1024;
    private readonly ISystemResourceProbe _resources = resourceProbe ?? new SystemResourceProbe();
    internal TimeSpan DurationLimit { get; init; } = MaximumDuration;
    public SqliteConnectionFactory Connections => currentConnections;

    public async Task<SqliteCalibrationResult> EvaluateAsync(
        IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        var initial = _resources.GetSnapshot();
        var current = currentConnections.SearchTuning;
        var measurements = new List<CalibrationMeasurement>();
        if (UnsafeMemory(initial) || !current.FitsMemory(initial))
            return new(current, null, measurements, "Avaliação adiada: memória livre insuficiente. Perfil atual preservado.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (DurationLimit <= TimeSpan.Zero) deadline.Cancel();
        else deadline.CancelAfter(DurationLimit);
        var creation = File.GetCreationTimeUtc(currentConnections.DatabasePath);
        var schema = 0;
        var completed = false;
        var reason = "";
        try
        {
            await using (var connection = await ReadOnly(current, initial).OpenAsync(deadline.Token).ConfigureAwait(false))
            {
                using var interruption = SqliteConnectionFactory.InterruptOnCancellation(connection, deadline.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT version FROM schema_info;";
                schema = Convert.ToInt32(await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false));
                command.CommandText = "SELECT result_count FROM dataset_statistics WHERE id=1;";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false)) < 50)
                    return new(current, null, measurements, "São necessários pelo menos 50 preços locais para comparar configurações. Perfil atual preservado.");
            }
            var candidates = BuildCandidates(current, initial, currentConnections.ProfileName == "Restrito");
            var today = DateOnly.FromDateTime(DateTime.Today);
            var scenarios = new[]
            {
                (Name: "cafe-descoberta", Text: "Café -máquina -cápsula -cafeteira \"pacote \"unidade"),
                (Name: "limpeza-diaria-descoberta", Text: "Serviço limpeza -odontológico \"diária"),
                (Name: "limpeza-descoberta", Text: "Serviço limpeza -odontológico")
            };
            var order = 0;
            for (var round = 0; round < 2; round++)
            {
                foreach (var scenario in scenarios)
                {
                    foreach (var candidate in round == 0 ? candidates : candidates.Reverse())
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (UnsafeMemory(_resources.GetSnapshot()) || !candidate.FitsMemory(_resources.GetSnapshot()))
                        {
                            reason = "Avaliação interrompida por redução da memória livre.";
                            throw new OperationCanceledException(reason);
                        }
                        progress?.Report($"{candidate.Description}\n{scenario.Name} — rodada {round + 1}/2. " +
                            "Até cinco minutos no total; nenhuma configuração será aplicada automaticamente.");
                        var query = new SearchQuery(scenario.Text, GeoScope.All, DataWindow.Start(today), today);
                        var measurement = await MeasureAsync(candidate, initial, scenario.Name, query,
                            round, order++ == 0, deadline.Token).ConfigureAwait(false);
                        measurements.Add(measurement);
                        if (measurement.MemoryPressure)
                        {
                            reason = "Avaliação interrompida por pressão de memória.";
                            throw new OperationCanceledException(reason);
                        }
                    }
                }
            }
            completed = true;
        }
        catch (Exception exception) when (exception is OperationCanceledException ||
            exception is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 9 } && deadline.IsCancellationRequested)
        {
            if (reason.Length == 0)
                reason = cancellationToken.IsCancellationRequested
                    ? "Avaliação cancelada."
                    : "O prazo de cinco minutos terminou antes de concluir as comparações.";
        }
        if (!completed)
            return new(current, null, measurements, reason + " Perfil atual preservado; medições parciais não geram recomendação.");
        var recommended = SelectRecommendation(current, measurements);
        var saved = recommended is null ? null : new SavedSqliteCalibration(
            Path.GetFullPath(currentConnections.DatabasePath), creation, schema,
            initial.TotalPhysicalMemoryBytes, initial.LogicalProcessors, recommended,
            ResourceProfile: currentConnections.SelectedResourceProfile);
        return new(current, recommended, measurements,
            recommended is null
                ? "Não houve ganho consistente suficiente para recomendar uma alteração. Perfil atual preservado."
                : $"{recommended.Name} recomendada pelas medições deste PC. Aplicação opcional na próxima abertura.", saved);
    }

    internal static IReadOnlyList<SqliteSearchTuning> BuildCandidates(
        SqliteSearchTuning current, SystemResourceSnapshot resources, bool constrained) =>
        new[] { current }.Concat(new[] { 32, 64, 96 }.Select(cache => current with { CacheMiB = cache }))
            .Distinct().Where(candidate => candidate.FitsMemory(resources)).ToArray();

    private SqliteConnectionFactory ReadOnly(SqliteSearchTuning tuning, SystemResourceSnapshot profile) =>
        currentConnections.ReadOnlyProfile(tuning, new FixedProbe(profile));

    private async Task<CalibrationMeasurement> MeasureAsync(
        SqliteSearchTuning tuning, SystemResourceSnapshot profile, string scenario, SearchQuery query,
        int round, bool firstAccess, CancellationToken cancellationToken)
    {
        using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        queryCancellation.CancelAfter(QueryTimeout);
        using var process = Process.GetCurrentProcess();
        var sampleGate = new object();
        long peak = 0;
        long minimumFree = long.MaxValue;
        var pressure = false;
        void Sample()
        {
            lock (sampleGate)
            {
                process.Refresh();
                peak = Math.Max(peak, process.PrivateMemorySize64);
                var snapshot = _resources.GetSnapshot();
                minimumFree = Math.Min(minimumFree, snapshot.AvailablePhysicalMemoryBytes);
                pressure |= UnsafeMemory(snapshot) || !tuning.FitsMemory(snapshot);
                if (pressure) queryCancellation.Cancel();
            }
        }
        Sample();
        using var timer = new Timer(_ => Sample(), null, 250, 250);
        var telemetry = new CalibrationTelemetry();
        var repository = new SqlitePriceCacheRepository(ReadOnly(tuning, profile), telemetry);
        var watch = Stopwatch.StartNew();
        double? first = null, ten = null;
        var rowCount = 0;
        double pageMs = 0, nextMs = 0;
        var success = false;
        var fingerprint = "";
        var outcome = "Concluída";
        var rows = new List<ItemSearchRow>();
        try
        {
            var progress = new InlineProgress<PriceCacheLocalProgress>(value =>
            {
                rowCount += value.Rows.Count;
                if (rowCount > 0) first ??= watch.Elapsed.TotalMilliseconds;
                if (rowCount >= 10) ten ??= watch.Elapsed.TotalMilliseconds;
            });
            var page = await repository.SearchLocalAfterAsync(query, SearchText.Parse(query.Text), null, null,
                null, 50, PriceCacheLocalReadOrder.Discovery, progress, queryCancellation.Token).ConfigureAwait(false);
            pageMs = watch.Elapsed.TotalMilliseconds;
            rows.AddRange(page.Rows ?? []);
            if (page.HasMore)
            {
                queryCancellation.CancelAfter(QueryTimeout);
                var nextStart = Stopwatch.GetTimestamp();
                var next = await repository.SearchLocalAfterAsync(query, SearchText.Parse(query.Text), null, null,
                    page.Cursor, 50, PriceCacheLocalReadOrder.Discovery,
                    cancellationToken: queryCancellation.Token).ConfigureAwait(false);
                nextMs = Stopwatch.GetElapsedTime(nextStart).TotalMilliseconds;
                rows.AddRange(next.Rows ?? []);
            }
            var keys = rows.Select(row => $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}").ToArray();
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', keys))));
            success = rowCount >= 50 && keys.Length == keys.Distinct().Count();
            if (!success) outcome = "Amostra insuficiente ou resultados inconsistentes";
            if (HasQueueInterference(telemetry.QueueMs, pageMs + nextMs))
            {
                success = false;
                outcome = "Interferência de outra operação na fila SQLite";
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = pressure ? "Pressão de memória" : "Consulta interrompida pelo limite de 30 segundos";
        }
        finally
        {
            await timer.DisposeAsync().ConfigureAwait(false);
            Sample();
        }
        return new(tuning, scenario, round, firstAccess, first, ten,
            pageMs > 0 ? pageMs : watch.Elapsed.TotalMilliseconds, nextMs,
            telemetry.QueueMs, telemetry.PreparationMs, peak, minimumFree, pressure, success, fingerprint, outcome);
    }

    internal static SqliteSearchTuning? SelectRecommendation(
        SqliteSearchTuning current, IReadOnlyList<CalibrationMeasurement> measurements)
    {
        var baseline = measurements.Where(value => value.Tuning == current).ToArray();
        var scenarios = baseline.Select(value => value.Scenario).Distinct().ToArray();
        if (scenarios.Length != 3 || scenarios.Any(scenario => !Valid(baseline.Where(value => value.Scenario == scenario).ToArray())))
            return null;
        var eligible = new List<(SqliteSearchTuning Tuning, double Score)>();
        foreach (var group in measurements.Where(value => value.Tuning != current).GroupBy(value => value.Tuning))
        {
            var score = 0d;
            var qualifies = true;
            foreach (var scenario in scenarios)
            {
                var expected = baseline.Where(value => value.Scenario == scenario).ToArray();
                var actual = group.Where(value => value.Scenario == scenario).ToArray();
                if (!Valid(actual) || actual.Any(value => value.Fingerprint != expected[0].Fingerprint))
                { qualifies = false; break; }
                var firstRatio = Median(actual.Select(value => value.FirstRowMs!.Value)) / Math.Max(0.1, Median(expected.Select(value => value.FirstRowMs!.Value)));
                var tenRatio = Median(actual.Select(value => value.TenRowsMs!.Value)) / Math.Max(0.1, Median(expected.Select(value => value.TenRowsMs!.Value)));
                if (firstRatio > 0.8 || tenRatio > 0.8 ||
                    Median(actual.Select(value => value.PageMs)) > Median(expected.Select(value => value.PageMs)) * 1.1 ||
                    Median(actual.Select(value => value.NextPageMs)) > Math.Max(1, Median(expected.Select(value => value.NextPageMs))) * 1.1)
                { qualifies = false; break; }
                score += (firstRatio + tenRatio) / 2;
            }
            if (qualifies) eligible.Add((group.Key, score / scenarios.Length));
        }
        if (eligible.Count == 0) return null;
        var best = eligible.Min(value => value.Score);
        return eligible.Where(value => value.Score <= best * 1.1)
            .OrderBy(value => measurements.Where(sample => sample.Tuning == value.Tuning)
                .Max(sample => sample.PeakProcessBytes))
            .ThenBy(value => value.Tuning.CacheMiB).ThenBy(value => value.Tuning.OrderedItemBatches)
            .ThenBy(value => value.Score).First().Tuning;
    }

    private static bool Valid(CalibrationMeasurement[] values) => values.Length == 2 &&
        values.Select(value => value.Round).Distinct().Count() == 2 &&
        values.All(value => value.Completed && !value.MemoryPressure && value.FirstRowMs is not null && value.TenRowsMs is not null &&
            !HasQueueInterference(value.QueueMs, value.PageMs + value.NextPageMs)) &&
        values[0].Fingerprint == values[1].Fingerprint;
    private static bool HasQueueInterference(double queueMs, double elapsedMs) =>
        queueMs > Math.Max(100, elapsedMs * 0.1);
    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }
    private static bool UnsafeMemory(SystemResourceSnapshot snapshot) =>
        snapshot.AvailablePhysicalMemoryBytes < MinimumFreeMemory || snapshot.Pressure == SystemResourcePressure.Critical;
    private sealed class FixedProbe(SystemResourceSnapshot snapshot) : ISystemResourceProbe
    { public SystemResourceSnapshot GetSnapshot() => snapshot; }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
    private sealed class CalibrationTelemetry : IPerformanceTelemetry
    {
        public double QueueMs { get; private set; }
        public double PreparationMs { get; private set; }
        public PerformanceSpan Begin(string operation, string phase = "total") => new(this, operation, phase);
        public void Record(string operation, string phase, TimeSpan duration, long rows = 0, long bytes = 0,
            bool succeeded = true, string? errorKind = null)
        {
            if (phase == "sqlite-queue") QueueMs += duration.TotalMilliseconds;
            if (phase == "item-index-order") PreparationMs += duration.TotalMilliseconds;
        }
        public PerformanceReport CreateReport() => throw new NotSupportedException();
    }
}
