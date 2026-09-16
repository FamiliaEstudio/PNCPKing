using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

public sealed class MainSearchPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("limpeza-diaria-recente", "Serviço limpeza -odontológico \"diária", SearchSort.Newest)]
    [InlineData("limpeza-ampla-recente", "Serviço limpeza -odontológico", SearchSort.Newest)]
    [InlineData("limpeza-ampla-proximidade", "Serviço limpeza -odontológico", SearchSort.Nearest)]
    [Trait("Category", "Performance")]
    public async Task IsolatedCopy_MeasuresIndexedMainSearchWithoutModifyingDatabase(
        string scenario, string text, SearchSort sort)
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
        var today = DateOnly.FromDateTime(DateTime.Today);
        var query = new SearchQuery(text, GeoScope.All, DataWindow.Start(today), today, Sort: sort);
        var expression = SearchText.Parse(text);
        var measurements = new List<object>();
        var rounds = int.Parse(Environment.GetEnvironmentVariable("PNCPKING_MAIN_SEARCH_ROUNDS") ?? "1");
        var expectedFingerprints = new List<string>();
        var reportPath = Environment.GetEnvironmentVariable("PNCPKING_MAIN_SEARCH_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            reportPath = Path.Combine(Path.GetDirectoryName(reportPath)!,
                $"{Path.GetFileNameWithoutExtension(reportPath)}-{scenario}{Path.GetExtension(reportPath)}");
        }
        for (var round = 0; round < rounds; round++)
        {
            var keys = new HashSet<(string, long, long)>();
            PriceCacheLocalCursor? cursor = null;
            for (var page = 1; page <= 2; page++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
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
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
                    result.Rows.Select(row => $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}")))))
                    .ToLowerInvariant();
                if (round == 0) expectedFingerprints.Add(fingerprint);
                else Assert.Equal(expectedFingerprints[page - 1], fingerprint);
                measurements.Add(new { round, page, milliseconds = timer.Elapsed.TotalMilliseconds,
                    rows = result.Rows.Count, fingerprint, result.HasMore, result.Cursor });
                var report = JsonSerializer.Serialize(new { scenario, text, query.Sort, query.StartDate,
                    query.EndDate, size, rounds, measurements }, new JsonSerializerOptions { WriteIndented = true });
                if (!string.IsNullOrWhiteSpace(reportPath)) await File.WriteAllTextAsync(reportPath, report);
                output.WriteLine($"{scenario}, rodada {round}, página {page}: {result.Rows.Count} preços em {timer.Elapsed.TotalMilliseconds:N0} ms.");
                cursor = result.Cursor;
                if (!result.HasMore) break;
            }
        }
        var after = new FileInfo(path);
        Assert.Equal(size, after.Length);
        Assert.Equal(modified, after.LastWriteTimeUtc);
    }

    internal sealed class ReadOnlyConnections(string path) : ISqliteConnectionFactory
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
            if (cancellationToken.CanBeCanceled)
            {
                SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000,
                    _ => cancellationToken.IsCancellationRequested ? 1 : 0, null);
            }
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON; PRAGMA cache_size=-65536; PRAGMA mmap_size=134217728;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
    }
}
