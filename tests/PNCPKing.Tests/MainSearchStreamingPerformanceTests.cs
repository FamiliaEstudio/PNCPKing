using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class MainSearchStreamingPerformanceTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task IsolatedCopyComparesProgressAndTwoPagePrefixes()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_STREAM_COPY");
        if (string.IsNullOrWhiteSpace(path)) return;
        Assert.Contains("benchmark", Path.GetDirectoryName(Path.GetFullPath(path))!, StringComparison.OrdinalIgnoreCase);
        var reportPath = Environment.GetEnvironmentVariable("PNCPKING_STREAM_REPORT");
        Assert.False(string.IsNullOrWhiteSpace(reportPath));
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var scenarios = new[]
        {
            (Name: "cafe-recente", Text: "Café -máquina -cápsula -cafeteira \"pacote \"unidade", Sort: SearchSort.Newest),
            (Name: "limpeza-diaria-recente", Text: "Serviço limpeza -odontológico \"diária", Sort: SearchSort.Newest),
            (Name: "limpeza-proximidade", Text: "Serviço limpeza -odontológico", Sort: SearchSort.Nearest)
        };
        var measurements = new List<Measurement>();
        var started = DateTimeOffset.Now;
        for (var round = 0; round < 2; round++)
        {
            foreach (var scenario in scenarios)
            {
                var engines = scenario.Sort == SearchSort.Newest
                    ? new[] { "item-index", "progressive", "contract-chunks" }
                    : new[] { "item-index", "progressive" };
                if (round == 1) Array.Reverse(engines);
                foreach (var engine in engines)
                {
                    var query = new SearchQuery(scenario.Text, GeoScope.All, DataWindow.Start(today), today, Sort: scenario.Sort);
                    measurements.Add(await MeasureAsync(path, query, scenario.Name, engine, round));
                    await File.WriteAllTextAsync(reportPath!, JsonSerializer.Serialize(new
                    {
                        started, profile = "Restrito", cacheMiB = 32, pageDeadlineSeconds = 45,
                        databaseBytes = length, databaseModifiedUtc = modified,
                        executor = new SystemResourceProbe().GetSnapshot(), measurements
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        foreach (var scenario in measurements.GroupBy(value => value.Scenario))
        {
            var reference = scenario.OrderByDescending(value => value.Keys.Length).First().Keys;
            Assert.NotEmpty(reference);
            foreach (var sample in scenario)
            {
                Assert.Equal(reference.Take(sample.Keys.Length), sample.Keys);
                Assert.Equal(sample.Keys.Length, sample.Keys.Distinct().Count());
            }
        }
        var after = new FileInfo(path);
        Assert.Equal(length, after.Length);
        Assert.Equal(modified, after.LastWriteTimeUtc);
    }

    private static async Task<Measurement> MeasureAsync(string path, SearchQuery query, string scenario, string engine, int round)
    {
        var factory = new SqliteConnectionFactory(path, resourceProbe: new SqliteCalibrationTests.Probe(),
            tuning: new(32, engine == "progressive"), readOnly: true);
        Assert.Equal("Restrito", factory.ProfileName);
        var repository = new SqlitePriceCacheRepository(factory);
        var expression = SearchText.Parse(query.Text);
        var watch = Stopwatch.StartNew();
        double? first = null, ten = null, pageMs = null, nextMs = null;
        string? error = null;
        var keys = new List<string>();
        var pages = 0;
        PriceCacheLocalCursor? cursor = null;
        for (var pageIndex = 0; pageIndex < 2; pageIndex++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var progress = new InlineProgress(value =>
            {
                foreach (var row in value.Rows)
                    keys.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}"))));
                if (pageIndex == 0 && keys.Count > 0) first ??= watch.Elapsed.TotalMilliseconds;
                if (pageIndex == 0 && keys.Count >= 10) ten ??= watch.Elapsed.TotalMilliseconds;
            });
            var countBefore = keys.Count;
            var pageStarted = watch.Elapsed.TotalMilliseconds;
            try
            {
                PriceCacheLocalPage page;
                if (engine == "contract-chunks")
                {
                    page = await (Task<PriceCacheLocalPage>)typeof(SqlitePriceCacheRepository)
                        .GetMethod("SearchLocalByContractChunksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(repository, [query, expression, null, null, cursor, pageIndex + 1, 50, progress, deadline.Token])!;
                }
                else
                    page = await repository.SearchLocalAfterAsync(query, expression, null, null, cursor, 50, progress, deadline.Token);
                Assert.Equal(page.Rows!.Select(row => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}")))), keys.Skip(countBefore));
                if (pageIndex == 0) pageMs = watch.Elapsed.TotalMilliseconds;
                else nextMs = watch.Elapsed.TotalMilliseconds - pageStarted;
                pages++;
                cursor = page.Cursor;
                if (!page.HasMore) break;
            }
            catch (Exception exception) when (deadline.IsCancellationRequested &&
                (exception is OperationCanceledException || exception is SqliteException { SqliteErrorCode: 9 }))
            {
                error = $"page {pageIndex + 1}: deadline";
                break;
            }
        }
        return new(scenario, engine, round, first, ten, pageMs, nextMs, watch.Elapsed.TotalMilliseconds, pages, error, keys.ToArray());
    }

    private sealed record Measurement(string Scenario, string Engine, int Round, double? FirstMs, double? TenMs,
        double? PageMs, double? NextPageMs, double ElapsedMs, int CompletedPages, string? Error, string[] Keys);
    private sealed class InlineProgress(Action<PriceCacheLocalProgress> action) : IProgress<PriceCacheLocalProgress>
    { public void Report(PriceCacheLocalProgress value) => action(value); }
}
