using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

public sealed class MainSearchPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task IsolatedCopy_MeasuresIndexedMainSearchWithoutModifyingDatabase()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_MAIN_SEARCH_COPY");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("Opt-in: PNCPKING_MAIN_SEARCH_COPY deve apontar para uma cópia isolada do banco.");
            return;
        }

        Assert.True(File.Exists(path));
        Assert.Contains("benchmark", Path.GetDirectoryName(Path.GetFullPath(path))!, StringComparison.OrdinalIgnoreCase);
        var before = new FileInfo(path);
        var size = before.Length;
        var modified = before.LastWriteTimeUtc;
        var cache = new SqlitePriceCacheRepository(new ReadOnlyConnections(path));
        const string text = "Serviço limpeza -odontológico \"diária";
        var today = DateOnly.FromDateTime(DateTime.Today);
        var query = new SearchQuery(text, GeoScope.All, DataWindow.Start(today), today, Sort: SearchSort.Newest);
        var expression = SearchText.Parse(text);
        var measurements = new List<object>();
        var keys = new HashSet<(string, long, long)>();
        PriceCacheLocalCursor? cursor = null;
        for (var page = 1; page <= 2; page++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var timer = Stopwatch.StartNew();
            var result = await cache.SearchLocalAfterAsync(
                query, expression, null, null, cursor, 50, timeout.Token);
            timer.Stop();
            Assert.NotEmpty(result.Rows!);
            foreach (var row in result.Rows!)
            {
                Assert.True(expression.MatchesItem(row.Item.Description, row.Item.Unit));
                Assert.True(keys.Add((row.Contract.PncpId, row.Item.ItemNumber, row.Result!.ResultSequence)));
            }
            measurements.Add(new { page, milliseconds = timer.Elapsed.TotalMilliseconds,
                rows = result.Rows.Count, result.HasMore, result.Cursor });
            output.WriteLine($"Página {page}: {result.Rows.Count} preços em {timer.Elapsed.TotalMilliseconds:N0} ms.");
            cursor = result.Cursor;
            if (!result.HasMore) break;
        }

        var report = JsonSerializer.Serialize(new { text, query.StartDate, query.EndDate, size, measurements },
            new JsonSerializerOptions { WriteIndented = true });
        var reportPath = Environment.GetEnvironmentVariable("PNCPKING_MAIN_SEARCH_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath)) await File.WriteAllTextAsync(reportPath, report);
        output.WriteLine(report);
        var after = new FileInfo(path);
        Assert.Equal(size, after.Length);
        Assert.Equal(modified, after.LastWriteTimeUtc);
    }

    private sealed class ReadOnlyConnections(string path) : ISqliteConnectionFactory
    {
        public string DatabasePath => path;
        public ISqliteWorkCoordinator WorkCoordinator { get; } = new SqliteWorkCoordinator();
        public int MigrationCacheKib => 64 * 1024;
        public long MmapBytes => 128L * 1024 * 1024;
        public int WorkerThreads => 2;
        public string ProfileName => "Amplo";

        public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON; PRAGMA cache_size=-65536; PRAGMA mmap_size=134217728;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
    }
}
