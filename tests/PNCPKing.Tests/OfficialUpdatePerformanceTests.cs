using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class OfficialUpdatePerformanceTests
{
    [Fact]
    public async Task SmallImportDoesNotScanUnrelatedLocalContractsItemsAndResults()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var small = await TestDatabase.CreateAsync();
        await using var large = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = PriceCacheTests.RecentContract("incoming-price", today, 1);
        await source.Repository.UpsertContractsAsync([contract]);
        await source.Repository.UpsertItemsAsync(contract.PncpId, [PriceCacheTests.Item(contract, 1)], false);
        await source.Repository.ReplaceItemResultsAsync(contract.PncpId, 1,
            [PriceCacheTests.Result(contract, 1, 1, true)]);
        var package = Path.Combine(source.Directory, "small.pncpupdate");
        await new OfficialUpdateService(source.Repository.DatabasePath).ExportAsync(package);
        var unrelated = PriceCacheTests.RecentContract("unrelated", today.AddDays(-100), 1);
        await large.Repository.UpsertContractsAsync([unrelated]);
        await using (var connection = new SqliteConnection($"Data Source={large.Repository.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<10000)
                INSERT INTO contracts(pncp_id,cnpj,purchase_year,purchase_sequence,modality_id,
                                      publication_date,global_updated_at)
                SELECT 'unrelated-' || n,c.cnpj,c.purchase_year,n,c.modality_id,c.publication_date,c.global_updated_at
                  FROM numbers CROSS JOIN contracts c WHERE c.pncp_id='unrelated';
                UPDATE dataset_statistics SET contract_count=contract_count+10000 WHERE id=1;
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<10000)
                INSERT INTO items(contract_id,item_number,description,has_result,hydration_status)
                SELECT 'unrelated',n,'Preço antigo',1,2 FROM numbers;
                INSERT INTO item_results(contract_id,item_number,result_sequence,supplier_name,unit_value_scaled)
                SELECT contract_id,item_number,1,'Fornecedor antigo',10000 FROM items;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var smallConnections = new MeasuredConnections(small.Repository.DatabasePath);
        var largeConnections = new MeasuredConnections(large.Repository.DatabasePath);
        var smallResult = await new OfficialUpdateService(smallConnections).ImportAsync(package);
        var largeResult = await new OfficialUpdateService(largeConnections).ImportAsync(package);

        Assert.Equal(smallResult, largeResult);
        Assert.True(largeConnections.Steps <= smallConnections.Steps + 10000,
            $"Import SQL grew from {smallConnections.Steps:N0} to {largeConnections.Steps:N0} steps for unrelated rows.");
        Assert.Single((await large.Repository.GetCachedItemResultsAsync(contract.PncpId, 1))!.Results);
        Assert.Equal((10002, 10001, 10001), await large.Repository.GetCountsAsync());
    }

    [Fact]
    public async Task TwentyDayPackageBenchmarkRecordsSizeExtractionsWritesAndRepeatedManifestOnlyCost()
    {
        var report = Environment.GetEnvironmentVariable("PNCPKING_UPDATE_REPORT");
        if (string.IsNullOrWhiteSpace(report)) return;

        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        const int population = 20_000;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-(OfficialUpdateService.WindowDays - 1));
        await source.Repository.EnsureCoverageWindowAsync(start, today, [6]);
        await source.Repository.SetCoverageStatusAsync(start, today, 6, "ALL", CoverageStatus.Complete, 0);
        var contracts = Enumerable.Range(1, population)
            .Select(index => PriceCacheTests.RecentContract($"benchmark-{index:000000}",
                today.AddDays(-(index % OfficialUpdateService.WindowDays)), index % 27 + 1) with { PurchaseSequence = index })
            .ToArray();
        await source.Repository.UpsertContractsAsync(contracts);
        await using (var connection = new SqliteConnection($"Data Source={source.Repository.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO items(contract_id,item_number,description,unit,has_result,source_updated_at,hydration_status,search_text)
                SELECT pncp_id,n,'Preço do benchmark','UN',1,global_updated_at,2,'preco benchmark'
                  FROM contracts CROSS JOIN (SELECT 1 AS n UNION ALL SELECT 2);
                INSERT INTO item_results(contract_id,item_number,result_sequence,supplier_name,unit_value_scaled,result_status_id)
                SELECT contract_id,item_number,n,'Fornecedor do benchmark',1000000,1
                  FROM items CROSS JOIN (SELECT 1 AS n UNION ALL SELECT 2);
                INSERT INTO official_result_snapshots(contract_id,item_number,parent_version,item_version,result_count)
                SELECT contract_id,item_number,source_updated_at,source_updated_at,2 FROM items;
                INSERT INTO contract_item_snapshots(contract_id,fetched_at,item_count,source_global_updated_at)
                SELECT pncp_id,strftime('%Y-%m-%dT%H:%M:%fZ','now'),2,global_updated_at FROM contracts;
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
        var originProgress = new CounterProgress();
        watch.Restart();
        var originRepeated = await exporter.ImportAsync(package, originProgress);
        var originImportMs = watch.Elapsed.TotalMilliseconds;

        Assert.Equal(population * 5, imported.Applied);
        Assert.Equal((population, population * 2, population * 4), await destination.Repository.GetCountsAsync());
        Assert.Equal(20, manifest.Chunks.Count);
        Assert.Equal(0, repeated.Applied);
        Assert.Equal(manifest.Chunks.Count, firstProgress.Extractions);
        Assert.Equal(0, repeatProgress.Extractions);
        Assert.Equal(0, originRepeated.Applied);
        Assert.Equal(0, originProgress.Extractions);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            population,
            items = population * 2,
            results = population * 4,
            compressedBytes = new FileInfo(package).Length,
            expandedBytes = manifest.Chunks.Sum(chunk => chunk.ExpandedSize),
            firstExtractions = firstProgress.Extractions,
            repeatedExtractions = repeatProgress.Extractions,
            originExtractions = originProgress.Extractions,
            bytesWritten = writtenBytes,
            exportMs,
            importMs,
            repeatedImportMs,
            originImportMs
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class MeasuredConnections(string path) : ISqliteConnectionFactory
    {
        public string DatabasePath => path;
        public ISqliteWorkCoordinator WorkCoordinator { get; } = new SqliteWorkCoordinator();
        public int MigrationCacheKib => 64 * 1024;
        public long MmapBytes => 0;
        public int WorkerThreads => 1;
        public string ProfileName => "Restrito";
        public long Steps { get; private set; }

        public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = new SqliteConnection($"Data Source={path};Foreign Keys=True;Pooling=False");
            await connection.OpenAsync(cancellationToken);
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, _ => { Steps += 1000; return 0; }, null);
            return connection;
        }
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
