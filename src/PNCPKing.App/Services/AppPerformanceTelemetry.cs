using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Services;

public sealed class AppPerformanceTelemetry : IPerformanceTelemetry
{
    private const int MaximumMeasurements = 20_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ConcurrentQueue<PerformanceMeasurement> _measurements = new();
    private string? _databasePath;
    private int _measurementCount;

    public void SetDatabasePath(string databasePath) =>
        _databasePath = Path.GetFullPath(databasePath);

    public PerformanceSpan Begin(string operation, string phase = "total")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        return new PerformanceSpan(this, operation, phase);
    }

    public void Record(
        string operation,
        string phase,
        TimeSpan duration,
        long rows = 0,
        long bytes = 0,
        bool succeeded = true,
        string? errorKind = null)
    {
        var measurement = new PerformanceMeasurement
        {
            Operation = SanitizeLabel(operation),
            Phase = SanitizeLabel(phase),
            StartedAt = DateTimeOffset.UtcNow - duration,
            Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
            Rows = Math.Max(0, rows),
            Bytes = Math.Max(0, bytes),
            WorkingSetBytes = GetWorkingSet(),
            Succeeded = succeeded,
            ErrorKind = SanitizeLabel(errorKind ?? string.Empty)
        };
        _measurements.Enqueue(measurement);
        var count = Interlocked.Increment(ref _measurementCount);
        while (count > MaximumMeasurements && _measurements.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _measurementCount);
        }
    }

    public PerformanceReport CreateReport()
    {
        var measurements = _measurements.ToArray();
        var summaries = measurements
            .GroupBy(value => (value.Operation, value.Phase))
            .Select(group =>
            {
                var ordered = group.Select(value => value.Duration.TotalMilliseconds)
                    .OrderBy(value => value)
                    .ToArray();
                return new PerformanceOperationSummary
                {
                    Operation = group.Key.Operation,
                    Phase = group.Key.Phase,
                    Samples = ordered.Length,
                    MedianMilliseconds = Percentile(ordered, 0.50),
                    P95Milliseconds = Percentile(ordered, 0.95),
                    MaximumMilliseconds = ordered.Length == 0 ? 0 : ordered[^1],
                    TotalRows = group.Sum(value => value.Rows),
                    TotalBytes = group.Sum(value => value.Bytes),
                    PeakWorkingSetBytes = group.Max(value => value.WorkingSetBytes)
                };
            })
            .OrderBy(value => value.Operation, StringComparer.Ordinal)
            .ThenBy(value => value.Phase, StringComparer.Ordinal)
            .ToArray();
        var databasePath = _databasePath;
        return new PerformanceReport
        {
            GeneratedAt = DateTimeOffset.Now,
            ApplicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "desconhecida",
            OperatingSystem = RuntimeInformation.OSDescription,
            Framework = RuntimeInformation.FrameworkDescription,
            LogicalProcessors = Environment.ProcessorCount,
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            DatabaseBytes = FileLength(databasePath),
            WalBytes = FileLength(databasePath is null ? null : databasePath + "-wal"),
            Measurements = measurements,
            Summaries = summaries
        };
    }

    public async Task ExportAsync(
        string jsonPath,
        string? baselineJsonPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        var report = CreateReport();
        if (!string.IsNullOrWhiteSpace(baselineJsonPath))
        {
            await using var baselineStream = new FileStream(
                baselineJsonPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var baseline = await JsonSerializer.DeserializeAsync<PerformanceReport>(
                    baselineStream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("Relatório-base inválido.");
            report = WithComparison(report, baseline);
        }
        var fullPath = Path.GetFullPath(jsonPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using (var stream = new FileStream(
                         fullPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        var textPath = Path.ChangeExtension(fullPath, ".txt");
        await File.WriteAllTextAsync(textPath, FormatText(report), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string FormatText(PerformanceReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("PNCP King — relatório de desempenho");
        builder.AppendLine($"Gerado em: {report.GeneratedAt:O}");
        builder.AppendLine($"Versão: {report.ApplicationVersion}");
        builder.AppendLine($"Sistema: {report.OperatingSystem}");
        builder.AppendLine($"Runtime: {report.Framework}");
        builder.AppendLine($"Processadores lógicos: {report.LogicalProcessors}");
        builder.AppendLine($"Memória disponível ao processo: {FormatBytes(report.AvailableMemoryBytes)}");
        builder.AppendLine($"Banco/WAL: {FormatBytes(report.DatabaseBytes)} / {FormatBytes(report.WalBytes)}");
        builder.AppendLine();
        builder.AppendLine("Operação | Fase | Amostras | Mediana ms | P95 ms | Máximo ms | Linhas | Pico RAM");
        foreach (var item in report.Summaries)
        {
            builder.AppendLine(
                $"{item.Operation} | {item.Phase} | {item.Samples} | " +
                $"{item.MedianMilliseconds:N1} | {item.P95Milliseconds:N1} | " +
                $"{item.MaximumMilliseconds:N1} | {item.TotalRows:N0} | " +
                $"{FormatBytes(item.PeakWorkingSetBytes)}");
        }

        if (report.Comparisons.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Base comparada: {report.BaselineApplicationVersion}");
            builder.AppendLine("Operação | Fase | Antes mediana ms | Depois mediana ms | Ganho mediana | Antes p95 ms | Depois p95 ms | Ganho p95 | Ganho registros/s");
            foreach (var item in report.Comparisons)
            {
                builder.AppendLine(
                    $"{item.Operation} | {item.Phase} | {item.BaselineMedianMilliseconds:N1} | " +
                    $"{item.CurrentMedianMilliseconds:N1} | {item.MedianImprovementPercent:N1}% | " +
                    $"{item.BaselineP95Milliseconds:N1} | {item.CurrentP95Milliseconds:N1} | " +
                    $"{item.P95ImprovementPercent:N1}% | {item.ThroughputImprovementPercent:N1}%");
            }
        }

        return builder.ToString();
    }

    private static PerformanceReport WithComparison(
        PerformanceReport current,
        PerformanceReport baseline)
    {
        var baselineByOperation = baseline.Summaries.ToDictionary(
            value => (value.Operation, value.Phase));
        var comparisons = current.Summaries
            .Where(value => baselineByOperation.ContainsKey((value.Operation, value.Phase)))
            .Select(value =>
            {
                var before = baselineByOperation[(value.Operation, value.Phase)];
                return new PerformanceComparison
                {
                    Operation = value.Operation,
                    Phase = value.Phase,
                    BaselineMedianMilliseconds = before.MedianMilliseconds,
                    CurrentMedianMilliseconds = value.MedianMilliseconds,
                    BaselineP95Milliseconds = before.P95Milliseconds,
                    CurrentP95Milliseconds = value.P95Milliseconds,
                    BaselinePeakWorkingSetBytes = before.PeakWorkingSetBytes,
                    CurrentPeakWorkingSetBytes = value.PeakWorkingSetBytes,
                    MedianImprovementPercent = Improvement(before.MedianMilliseconds, value.MedianMilliseconds),
                    P95ImprovementPercent = Improvement(before.P95Milliseconds, value.P95Milliseconds),
                    ThroughputImprovementPercent = ThroughputImprovement(before, value)
                };
            })
            .OrderBy(value => value.Operation, StringComparer.Ordinal)
            .ThenBy(value => value.Phase, StringComparer.Ordinal)
            .ToArray();
        return current with
        {
            BaselineApplicationVersion = baseline.ApplicationVersion,
            Comparisons = comparisons
        };
    }

    private static double Improvement(double before, double after) =>
        before <= 0 ? 0 : (before - after) * 100d / before;

    private static double ThroughputImprovement(
        PerformanceOperationSummary before,
        PerformanceOperationSummary after)
    {
        var beforeThroughput = before.MedianMilliseconds <= 0
            ? 0
            : before.TotalRows / Math.Max(1, before.Samples) * 1000d / before.MedianMilliseconds;
        var afterThroughput = after.MedianMilliseconds <= 0
            ? 0
            : after.TotalRows / Math.Max(1, after.Samples) * 1000d / after.MedianMilliseconds;
        return beforeThroughput <= 0 ? 0 : (afterThroughput - beforeThroughput) * 100d / beforeThroughput;
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(ordered.Count * percentile) - 1, 0, ordered.Count - 1);
        return ordered[index];
    }

    private static string SanitizeLabel(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80];
    }

    private static long GetWorkingSet()
    {
        try
        {
            return Process.GetCurrentProcess().WorkingSet64;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static long FileLength(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return $"{value:N1} {units[unit]}";
    }
}
