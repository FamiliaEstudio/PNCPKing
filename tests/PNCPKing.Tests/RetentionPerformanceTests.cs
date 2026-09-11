using System.Diagnostics;
using System.Text.Json;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

public sealed class RetentionPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task IsolatedCopy_ReportsRetentionSizeAndEquivalentSearches()
    {
        var baselinePath = Environment.GetEnvironmentVariable("PNCPKING_RETENTION_BENCHMARK_BASELINE");
        if (string.IsNullOrWhiteSpace(baselinePath))
        {
            output.WriteLine("Opt-in: informe PNCPKING_RETENTION_BENCHMARK_BASELINE com retention-baseline.db, uma cópia isolada.");
            return;
        }
        Assert.Equal("retention-baseline.db", Path.GetFileName(baselinePath));
        var folder = Path.GetDirectoryName(Path.GetFullPath(baselinePath))!;
        var candidatePath = Path.Combine(folder, "retention-candidate.db");
        Assert.False(File.Exists(candidatePath), "Use uma pasta nova de validação; a candidata não será sobrescrita.");
        File.Copy(baselinePath, candidatePath);
        var candidate = new SqliteContractRepository(candidatePath);
        var baseline = new SqliteContractRepository(baselinePath);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var before = await baseline.GetCountsAsync();
        var timer = Stopwatch.StartNew();
        await candidate.InitializeAsync();
        var retention = await candidate.MaintainRetentionAsync(today, compact: true);
        var elapsed = timer.Elapsed.TotalSeconds;
        var after = await candidate.GetCountsAsync();
        var measurements = new List<object>();
        var baselinePrices = new SqlitePriceCacheRepository(baselinePath);
        var candidatePrices = new SqlitePriceCacheRepository(candidatePath);
        foreach (var term in new[] { "café", "papel", "luva", "manutenção" })
        {
            var query = new SearchQuery(term, GeoScope.All, DataWindow.Start(today), today, PageSize: 50, Sort: SearchSort.Newest);
            var expression = SearchText.Parse(term);
            for (var round = 0; round < 3; round++)
            {
                foreach (var isCandidate in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    timer.Restart();
                    var contracts = await (isCandidate ? candidate : baseline).SearchAsync(query);
                    var contractMilliseconds = timer.Elapsed.TotalMilliseconds;
                    timer.Restart();
                    var prices = await (isCandidate ? candidatePrices : baselinePrices).SearchLocalAfterAsync(query, expression, null, null, null, 50);
                    measurements.Add(new { term, round, candidate = isCandidate, contractMilliseconds,
                        priceMilliseconds = timer.Elapsed.TotalMilliseconds, contracts = contracts.Results.Count,
                        prices = prices.Rows?.Count ?? 0 });
                }
            }
            Assert.Equal((await baseline.SearchAsync(query)).Results.Select(c => c.PncpId),
                (await candidate.SearchAsync(query)).Results.Select(c => c.PncpId));
        }
        var report = new { today, cutoff = DataWindow.Start(today), retention, elapsedSeconds = elapsed,
            before = new { before.Contracts, before.Items, before.Results },
            after = new { after.Contracts, after.Items, after.Results }, measurements };
        await File.WriteAllTextAsync(Path.Combine(folder, "retention-comparison.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(retention.Compacted, retention.Message);
        Assert.True(retention.BytesAfter < retention.BytesBefore);
        Assert.True(after.Contracts < before.Contracts);
        Assert.True(after.Items <= before.Items);
        output.WriteLine(retention.Message);
    }
}
