using System.Diagnostics;
using System.Text.Json;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class MainSearchLocalSequencePerformanceTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task SyntheticComparisonKeepsTheSamePagesAsTheExistingReader()
    {
        var report = Environment.GetEnvironmentVariable("PNCPKING_MAIN_CRITERIA_REPORT");
        if (string.IsNullOrWhiteSpace(report)) return;
        var samples = new List<object>();
        foreach (var count in new[] { 50, 1000, 10000 })
        {
            await using var database = await MainSearchLocalSequenceTests.SeedAsync(count / 2, longDescriptions: true);
            var repository = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
            var query = new SearchQuery("café + açúcar", GeoScope.All);
            var expressions = SearchText.ParseMainCriteria(query.Text);
            string[]? expected = null;
            // Alternating order reduces systematic cache bias; this is not a cold-HDD benchmark.
            for (var round = 0; round < 3; round++)
                foreach (var combined in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    using var process = Process.GetCurrentProcess();
                    var cpu = process.TotalProcessorTime;
                    var allocated = GC.GetTotalAllocatedBytes();
                    var watch = Stopwatch.StartNew();
                    var rows = new List<ItemSearchRow>();
                    double firstPageMs = 0;
                    var sequence = combined ? new MainSearchLocalSequence(repository, query, expressions) : null;
                    for (var part = 0; part < expressions.Count; part++)
                    {
                        var page = combined
                            ? await sequence!.LoadNextAsync(null, null, (_, _) => { })
                            : await repository.SearchLocalAfterAsync(query with { Text = expressions[part].OriginalText },
                                expressions[part], null, null, null, 50, PriceCacheLocalReadOrder.Discovery, new IgnoreProgress());
                        rows.AddRange(page!.Rows!);
                        if (part == 0) firstPageMs = watch.Elapsed.TotalMilliseconds;
                    }
                    var elapsed = watch.Elapsed.TotalMilliseconds;
                    var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                    var bytes = GC.GetTotalAllocatedBytes() - allocated;
                    process.Refresh();
                    var keys = rows.Select(row => $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}").ToArray();
                    expected ??= keys;
                    Assert.Equal(expected, keys);
                    samples.Add(new { count, round, combined, firstPageMs, elapsedMs = elapsed, cpuMs,
                        allocatedBytes = bytes, workingSetBytes = process.WorkingSet64, rows = keys.Length });
                }
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                synthetic = true, userDatabaseAccessed = false,
                note = "Leitor atual e sequência usam as mesmas páginas e descrições longas. Cache do SO não foi limpo; máquina de teste não representa um HD antigo.",
                samples
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed class IgnoreProgress : IProgress<PriceCacheLocalProgress>
    {
        public void Report(PriceCacheLocalProgress value) { }
    }
}
