using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class MainSearchDiscoveryPerformanceTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task IsolatedCopyComparesDiscoveryWithCurrentRestrictedSearch()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_DISCOVERY_COPY");
        if (string.IsNullOrWhiteSpace(path)) return;
        Assert.Contains("benchmark", Path.GetDirectoryName(Path.GetFullPath(path))!, StringComparison.OrdinalIgnoreCase);
        var reportPath = Environment.GetEnvironmentVariable("PNCPKING_DISCOVERY_REPORT");
        Assert.False(string.IsNullOrWhiteSpace(reportPath));
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var measurements = new List<Measurement>();
        var scenarios = new[]
        {
            (Name: "cafe", Text: "Café -máquina -cápsula -cafeteira \"pacote \"unidade", Sort: SearchSort.Newest),
            (Name: "limpeza-diaria", Text: "Serviço limpeza -odontológico \"diária", Sort: SearchSort.Newest),
            (Name: "limpeza-ampla", Text: "Serviço limpeza -odontológico", Sort: SearchSort.Nearest)
        };
        var started = DateTimeOffset.Now;
        try
        {
            for (var round = 0; round < 2; round++)
                foreach (var scenario in scenarios)
                    foreach (var order in round == 0
                        ? new[] { PriceCacheLocalReadOrder.Discovery, PriceCacheLocalReadOrder.RequestedSort }
                        : new[] { PriceCacheLocalReadOrder.RequestedSort, PriceCacheLocalReadOrder.Discovery })
                    {
                        var query = new SearchQuery(scenario.Text, GeoScope.All, DataWindow.Start(today), today, Sort: scenario.Sort);
                        measurements.Add(await MeasureAsync(path, query, scenario.Name, order, round));
                        await File.WriteAllTextAsync(reportPath!, JsonSerializer.Serialize(new
                        {
                            started, profile = "Restrito", cacheMiB = 32, pageDeadlineSeconds = 45,
                            databaseBytes = length, databaseModifiedUtc = modified,
                            note = "Sem limpeza do cache do sistema; primeira rodada não representa cache frio controlado. Tempos de entrega pelo repositório, não de renderização.",
                            executor = new SystemResourceProbe().GetSnapshot(), measurements
                        }, new JsonSerializerOptions { WriteIndented = true }));
                    }
            foreach (var group in measurements.GroupBy(value => (value.Scenario, value.Order)))
            {
                var longest = group.OrderByDescending(value => value.Keys.Length).First().Keys;
                foreach (var sample in group)
                    Assert.Equal(longest.Take(sample.Keys.Length), sample.Keys);
            }
        }
        finally
        {
            var after = new FileInfo(path);
            Assert.Equal(length, after.Length);
            Assert.Equal(modified, after.LastWriteTimeUtc);
        }
    }

    private static async Task<Measurement> MeasureAsync(string path, SearchQuery query, string scenario,
        PriceCacheLocalReadOrder order, int round)
    {
        var factory = new SqliteConnectionFactory(path, resourceProbe: new SqliteCalibrationTests.Probe(),
            tuning: new(32, true), readOnly: true);
        var repository = new SqlitePriceCacheRepository(factory);
        var expression = SearchText.Parse(query.Text);
        using var process = Process.GetCurrentProcess();
        var initialCpu = process.TotalProcessorTime;
        var initialAllocated = GC.GetTotalAllocatedBytes();
        long peakPrivateBytes = 0, peakWorkingSetBytes = 0;
        var gate = new object();
        void Sample()
        {
            lock (gate)
            {
                process.Refresh();
                peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
                peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.WorkingSet64);
            }
        }
        Sample();
        await using var timer = new Timer(_ => Sample(), null, 50, 50);
        var watch = Stopwatch.StartNew();
        double? first = null, ten = null, fifty = null, pageMs = null, nextMs = null;
        var keys = new List<string>();
        var completed = 0;
        string? error = null;
        PriceCacheLocalCursor? cursor = null;
        for (var pageNumber = 0; pageNumber < 2; pageNumber++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var countBefore = keys.Count;
            var pageStart = watch.Elapsed.TotalMilliseconds;
            try
            {
                var page = await repository.SearchLocalAfterAsync(query, expression, null, null, cursor, 50,
                    order, new InlineProgress(value =>
                    {
                        foreach (var row in value.Rows)
                        {
                            Assert.True(expression.MatchesItem(row.Item.Description, row.Item.Unit));
                            Assert.True(row.Result!.IsActive && row.HomologatedUnitValue > 0);
                            Assert.InRange(DateOnly.FromDateTime(row.Contract.PublicationDate!.Value.Date),
                                query.StartDate!.Value, query.EndDate!.Value);
                            keys.Add(Key(row));
                        }
                        if (pageNumber != 0) return;
                        if (keys.Count > 0) first ??= watch.Elapsed.TotalMilliseconds;
                        if (keys.Count >= 10) ten ??= watch.Elapsed.TotalMilliseconds;
                        if (keys.Count >= 50) fifty ??= watch.Elapsed.TotalMilliseconds;
                    }), deadline.Token);
                Assert.Equal(page.Rows!.Select(Key), keys.Skip(countBefore));
                Assert.Equal(keys.Count, keys.Distinct().Count());
                if (pageNumber == 0) pageMs = watch.Elapsed.TotalMilliseconds;
                else nextMs = watch.Elapsed.TotalMilliseconds - pageStart;
                cursor = page.Cursor;
                completed++;
                if (!page.HasMore) break;
            }
            catch (Exception exception) when (deadline.IsCancellationRequested &&
                exception is OperationCanceledException or SqliteException { SqliteErrorCode: 9 })
            {
                error = $"Página {pageNumber + 1}: limite de 45 segundos";
                break;
            }
        }
        await timer.DisposeAsync();
        Sample();
        return new(scenario, order.ToString(), round, first, ten, fifty, pageMs, nextMs,
            watch.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - initialCpu).TotalMilliseconds,
            GC.GetTotalAllocatedBytes() - initialAllocated, peakPrivateBytes, peakWorkingSetBytes,
            completed, error, keys.ToArray());
    }

    private static string Key(ItemSearchRow row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}")));
    private sealed record Measurement(string Scenario, string Order, int Round, double? FirstMs, double? TenMs,
        double? FiftyMs, double? ConfirmedPageMs, double? NextPageMs, double ElapsedMs, double CpuMs,
        long AllocatedBytes, long PeakPrivateBytes, long PeakWorkingSetBytes, int CompletedPages, string? Error, string[] Keys);
    private sealed class InlineProgress(Action<PriceCacheLocalProgress> report) : IProgress<PriceCacheLocalProgress>
    { public void Report(PriceCacheLocalProgress value) => report(value); }
}
