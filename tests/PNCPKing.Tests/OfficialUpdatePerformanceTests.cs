using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class OfficialUpdatePerformanceTests
{
    [Fact]
    public async Task TenDayPackageBenchmarkRecordsSizeExtractionsWritesAndRepeatedManifestOnlyCost()
    {
        var report = Environment.GetEnvironmentVariable("PNCPKING_UPDATE_REPORT");
        if (string.IsNullOrWhiteSpace(report)) return;

        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        const int population = 20_000;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-9);
        await source.Repository.EnsureCoverageWindowAsync(start, today, [6]);
        await source.Repository.SetCoverageStatusAsync(start, today, 6, "ALL", CoverageStatus.Complete, 0);
        var contracts = Enumerable.Range(1, population)
            .Select(index => PriceCacheTests.RecentContract($"benchmark-{index:000000}",
                today.AddDays(-(index % 10)), index % 27 + 1) with { PurchaseSequence = index })
            .ToArray();
        await source.Repository.UpsertContractsAsync(contracts);
        await using (var connection = new SqliteConnection($"Data Source={source.Repository.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO contract_item_snapshots(contract_id,fetched_at,item_count,source_global_updated_at)
                SELECT pncp_id,strftime('%Y-%m-%dT%H:%M:%fZ','now'),0,global_updated_at FROM contracts;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var exporter = new OfficialUpdateService(source.Repository.DatabasePath);
        var importer = new OfficialUpdateService(destination.Repository.DatabasePath);
        var package = Path.Combine(source.Directory, "benchmark.pncpupdate");
        var watch = Stopwatch.StartNew();
        var manifest = await exporter.ExportAsync(package);
        var exportMs = watch.Elapsed.TotalMilliseconds;
        var beforeBytes = new FileInfo(destination.Repository.DatabasePath).Length;
        var firstProgress = new CounterProgress();
        watch.Restart();
        var imported = await importer.ImportAsync(package, firstProgress);
        var importMs = watch.Elapsed.TotalMilliseconds;
        var writtenBytes = Math.Max(0, new FileInfo(destination.Repository.DatabasePath).Length - beforeBytes);
        var repeatProgress = new CounterProgress();
        watch.Restart();
        var repeated = await importer.ImportAsync(package, repeatProgress);
        var repeatedImportMs = watch.Elapsed.TotalMilliseconds;

        Assert.Equal(population, imported.Applied);
        Assert.Equal(0, repeated.Applied);
        Assert.Equal(manifest.Chunks.Count, firstProgress.Extractions);
        Assert.Equal(0, repeatProgress.Extractions);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            population,
            compressedBytes = new FileInfo(package).Length,
            expandedBytes = manifest.Chunks.Sum(chunk => chunk.ExpandedSize),
            firstExtractions = firstProgress.Extractions,
            repeatedExtractions = repeatProgress.Extractions,
            bytesWritten = writtenBytes,
            exportMs,
            importMs,
            repeatedImportMs
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class CounterProgress : IProgress<string>
    {
        public int Extractions { get; private set; }
        public void Report(string value)
        {
            if (value.StartsWith("Lendo ", StringComparison.Ordinal)) Extractions++;
        }
    }
}
