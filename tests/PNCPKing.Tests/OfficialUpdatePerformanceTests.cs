using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class OfficialUpdatePerformanceTests
{
    [Fact]
    public async Task IsolatedLargeBaseSmallUpdateBenchmark()
    {
        var report = Environment.GetEnvironmentVariable("PNCPKING_UPDATE_REPORT");
        if (string.IsNullOrWhiteSpace(report)) return;
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        const int population = 20000;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contracts = Enumerable.Range(1, population).Select(i => PriceCacheTests.RecentContract($"benchmark-{i:000000}", today, 1) with { PurchaseSequence = i }).ToArray();
        var watch = Stopwatch.StartNew();
        await source.Repository.UpsertContractsAsync(contracts);
        var seedMs = watch.Elapsed.TotalMilliseconds;
        var modified = contracts.Take(100).Select(c => c with { Object = c.Object + " revisão", GlobalUpdatedAt = c.GlobalUpdatedAt!.Value.AddMinutes(1) }).ToArray();
        watch.Restart(); await source.Repository.UpsertContractsAsync(modified); var writeBeforeJournalMs = watch.Elapsed.TotalMilliseconds;
        var a = new OfficialUpdateService(source.Repository.DatabasePath);
        var b = new OfficialUpdateService(destination.Repository.DatabasePath);
        var baseline = Path.Combine(source.Directory, "base.pncpupdate");
        watch.Restart(); await a.ExportAsync(baseline); var baseExportMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); await b.ImportAsync(baseline); var baseImportMs = watch.Elapsed.TotalMilliseconds;
        modified = modified.Select(c => c with { Object = c.Object + " nova", GlobalUpdatedAt = c.GlobalUpdatedAt!.Value.AddMinutes(1) }).ToArray();
        watch.Restart(); await source.Repository.UpsertContractsAsync(modified); var writeWithJournalMs = watch.Elapsed.TotalMilliseconds;
        var delta = Path.Combine(source.Directory, "delta.pncpupdate");
        watch.Restart(); var package = await a.ExportAsync(delta); var deltaExportMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); var result = await b.ImportAsync(delta); var deltaImportMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); await b.ImportAsync(delta); var repeatedImportMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); var retention = await destination.Repository.MaintainRetentionAsync(today); var sameDayRetentionMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(100, package.Units); Assert.Equal(100, result.Applied); Assert.Equal(0, result.Conflicts); Assert.False(retention.Applied);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            population,
            changes = 100,
            seedMs,
            writeBeforeJournalMs,
            writeWithJournalMs,
            baseExportMs,
            baseImportMs,
            deltaExportMs,
            deltaImportMs,
            repeatedImportMs,
            sameDayRetentionMs,
            databaseBytes = new FileInfo(destination.Repository.DatabasePath).Length,
            baseBytes = new FileInfo(baseline).Length,
            deltaBytes = new FileInfo(delta).Length,
            machine = Environment.MachineName,
            note = "Base sintética isolada; primeiro acesso e repetições sem controle do cache do sistema; não representa o HD do serviço."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    [Fact]
    public async Task IsolatedRealDatabaseSmallUpdateBenchmark()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_UPDATE_COPY");
        var report = Environment.GetEnvironmentVariable("PNCPKING_UPDATE_REAL_REPORT");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(report)) return;
        if (!Path.GetFullPath(path).Contains("manual-update-validation" + Path.DirectorySeparatorChar + "isolated", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O benchmark aceita somente a cópia isolada dedicada.");
        var connections = new SqliteConnectionFactory(path, resourceProbe: new RestrainedProbe());
        var repository = new SqliteContractRepository(connections);
        await File.WriteAllTextAsync(report + ".phase", "Migrando a cópia isolada para o esquema 28");
        var watch = Stopwatch.StartNew(); await repository.InitializeAsync(); var migrationMs = watch.Elapsed.TotalMilliseconds;
        await File.WriteAllTextAsync(report + ".phase", "Conferindo a retenção da cópia isolada");
        watch.Restart(); var firstRetention = await repository.MaintainRetentionAsync(DateOnly.FromDateTime(DateTime.Today)); var firstRetentionMs = watch.Elapsed.TotalMilliseconds;
        await File.WriteAllTextAsync(report + ".phase", "Medindo um pacote de 100 alterações sobre o banco grande");
        var ids = new List<string>();
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pncp_id FROM contracts ORDER BY publication_date DESC LIMIT 100";
            await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        }
        Assert.Equal(100, ids.Count);
        var originals = new List<ContractRecord>();
        foreach (var id in ids) originals.Add((await repository.GetContractAsync(id))!);
        await using var source = await TestDatabase.CreateAsync();
        var service = new OfficialUpdateService(source.Repository.DatabasePath);
        var baseline = await service.ExportAsync(Path.Combine(source.Directory, "base.pncpupdate"));
        // Fixture: the isolated full copy represents an already adopted base. Do not
        // export/copy its millions of unchanged entities to measure a small update.
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE official_transfer_state SET base_id=$id,base_ready=1 WHERE id=1";
            command.Parameters.AddWithValue("$id", baseline.BaseId); await command.ExecuteNonQueryAsync();
        }
        var updated = originals.Select(c => c with { Object = c.Object + " [benchmark isolado]", GlobalUpdatedAt = (c.GlobalUpdatedAt ?? DateTimeOffset.Now).AddHours(1) }).ToArray();
        await source.Repository.UpsertContractsAsync(updated);
        var package = Path.Combine(source.Directory, "delta.pncpupdate");
        watch.Restart(); var manifest = await service.ExportAsync(package); var exportMs = watch.Elapsed.TotalMilliseconds;
        var destination = new OfficialUpdateService(connections);
        watch.Restart(); var imported = await destination.ImportAsync(package); var importMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); await destination.ImportAsync(package); var repeatedImportMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); var repeatedRetention = await repository.MaintainRetentionAsync(DateOnly.FromDateTime(DateTime.Today)); var repeatedRetentionMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(100, manifest.Units); Assert.Equal(100, imported.Applied); Assert.Equal(0, imported.Conflicts); Assert.False(repeatedRetention.Applied);
        var counts = await repository.GetCountsAsync();
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            databaseBytes = new FileInfo(path).Length,
            migrationMs,
            firstRetentionMs,
            firstRetention.RemovedContracts,
            firstRetention.RemovedReferences,
            exportMs,
            importMs,
            repeatedImportMs,
            repeatedRetentionMs,
            profile = connections.ProfileName,
            packageBytes = new FileInfo(package).Length,
            counts = new { counts.Contracts, counts.Items, counts.Results },
            note = "Cópia isolada de banco real grande, com linhagem semeada para medir somente o delta; não é medição do PC do serviço nem de cache frio controlado."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class RestrainedProbe : ISystemResourceProbe
    {
        public SystemResourceSnapshot GetSnapshot() => SystemResourceProbe.CreateSnapshot(6L * 1024 * 1024 * 1024, 1200L * 1024 * 1024, 80, 4);
    }

}
