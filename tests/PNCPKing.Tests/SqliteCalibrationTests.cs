using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class SqliteCalibrationTests
{
    internal sealed class Probe : ISystemResourceProbe
    {
        public SystemResourceSnapshot Snapshot { get; set; } = SystemResourceProbe.CreateSnapshot(
            6L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024, 65, 4);
        public SystemResourceSnapshot GetSnapshot() => Snapshot;
    }

    [Fact]
    public async Task CalibratedCacheCanExceedRestrictedDefaultButFallsBackWhenMemoryDrops()
    {
        await using var database = await TestDatabase.CreateAsync();
        var probe = new Probe();
        var connections = new SqliteConnectionFactory(database.Repository.DatabasePath,
            resourceProbe: probe, tuning: new(64, true));
        Assert.Equal("Restrito", connections.ProfileName);
        Assert.True(connections.SearchTuning.OrderedItemBatches);
        await using (var connection = await connections.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cache_size;";
            Assert.Equal(-65536L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        probe.Snapshot = probe.Snapshot with { AvailablePhysicalMemoryBytes = 500L * 1024 * 1024, Pressure = SystemResourcePressure.Critical };
        await using (var connection = await connections.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cache_size;";
            Assert.Equal(-32768L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.CommandText = "PRAGMA temp_store;";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        connections.ResetCalibration();
        Assert.Equal(new SqliteSearchTuning(32, true), connections.SearchTuning);
    }

    [Fact]
    public void CandidateBudgetAccountsForFourConnectionsAndDoesNotChangeSpaciousSearchStrategy()
    {
        var resources = new Probe().Snapshot with { AvailablePhysicalMemoryBytes = 1200L * 1024 * 1024 };
        var choices = SqliteCalibrationService.BuildCandidates(new(32), resources, true);
        Assert.Contains(new SqliteSearchTuning(64, true), choices);
        Assert.DoesNotContain(choices, value => value.CacheMiB == 96);
        Assert.All(SqliteCalibrationService.BuildCandidates(new(64), new Probe().Snapshot, false),
            value => Assert.False(value.OrderedItemBatches));
    }

    [Fact]
    public void RecommendationPrefersMediumCacheWhenItIsWithinTenPercentOfFasterAlternative()
    {
        var current = new SqliteSearchTuning(32);
        var medium = new SqliteSearchTuning(64, true);
        var large = new SqliteSearchTuning(96, true);
        var samples = Samples(current, 100, 200, 1000)
            .Concat(Samples(medium, 50, 100, 600)).Concat(Samples(large, 48, 96, 580)).ToArray();
        Assert.Equal(medium, SqliteCalibrationService.SelectRecommendation(current, samples));
    }

    [Fact]
    public void RecommendationUsesMeasuredMemoryToBreakCloseSpeedResults()
    {
        var current = new SqliteSearchTuning(32);
        var medium = new SqliteSearchTuning(64, true);
        var large = new SqliteSearchTuning(96, true);
        var samples = Samples(current, 100, 200, 1000)
            .Concat(Samples(medium, 50, 100, 600).Select(value => value with { PeakProcessBytes = 300_000_000 }))
            .Concat(Samples(large, 48, 96, 580).Select(value => value with { PeakProcessBytes = 250_000_000 })).ToArray();
        Assert.Equal(large, SqliteCalibrationService.SelectRecommendation(current, samples));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("pressure")]
    [InlineData("changed-results")]
    [InlineData("slow-page")]
    [InlineData("no-early-gain")]
    [InlineData("queue-interference")]
    public void RecommendationRejectsInsufficientOrRegressingEvidence(string problem)
    {
        var current = new SqliteSearchTuning(32);
        var candidate = Samples(new(64, true), 50, 100, 600).ToArray();
        candidate[0] = problem switch
        {
            "incomplete" => candidate[0] with { Completed = false },
            "pressure" => candidate[0] with { MemoryPressure = true },
            "changed-results" => candidate[0] with { Fingerprint = "different" },
            "slow-page" => candidate[0] with { PageMs = 2000 },
            "queue-interference" => candidate[0] with { QueueMs = 500 },
            _ => candidate[0] with { FirstRowMs = 120 }
        };
        Assert.Null(SqliteCalibrationService.SelectRecommendation(current,
            Samples(current, 100, 200, 1000).Concat(candidate).ToArray()));
    }

    [Fact]
    public async Task EvaluationWithLowMemoryDoesNotOpenDatabaseOrProposeConfiguration()
    {
        var probe = new Probe { Snapshot = new Probe().Snapshot with { AvailablePhysicalMemoryBytes = 600L * 1024 * 1024 } };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var service = new SqliteCalibrationService(new SqliteConnectionFactory(path, resourceProbe: probe), probe);
        var result = await service.EvaluateAsync(null);
        Assert.Null(result.Recommended);
        Assert.Empty(result.Measurements);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SavedCalibrationRejectsDatabaseReplacementEnvironmentAndVersionChanges()
    {
        var path = Path.GetFullPath("calibration.db");
        var created = DateTime.UtcNow;
        var resources = new Probe().Snapshot;
        var saved = new SavedSqliteCalibration(path, created, 27, resources.TotalPhysicalMemoryBytes,
            resources.LogicalProcessors, new(64, true));
        Assert.True(saved.AppliesTo(path, created, 27, resources));
        Assert.False(saved.AppliesTo(path, created.AddSeconds(1), 27, resources));
        Assert.False(saved.AppliesTo(path + "other", created, 27, resources));
        Assert.False(saved.AppliesTo(path, created, 28, resources));
        Assert.False((saved with { Version = 1 }).AppliesTo(path, created, 27, resources));
        Assert.False(saved.AppliesTo(path, created, 27, resources with { LogicalProcessors = 8 }));
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"DataFolder\":\"data\",\"IsConfigured\":true}")!;
        Assert.Null(settings.SqliteCalibration);
        Assert.Equal(saved, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings with { SqliteCalibration = saved }))!.SqliteCalibration);
        Assert.Null((settings with { SqliteCalibration = null }).SqliteCalibration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrExpiredEvaluationKeepsCurrentProfile(bool expired)
    {
        await using var database = await TestDatabase.CreateAsync();
        var probe = new Probe();
        var service = new SqliteCalibrationService(new SqliteConnectionFactory(database.Repository.DatabasePath,
            resourceProbe: probe), probe)
        { DurationLimit = expired ? TimeSpan.Zero : SqliteCalibrationService.MaximumDuration };
        using var cancellation = new CancellationTokenSource();
        if (!expired) cancellation.Cancel();
        var result = await service.EvaluateAsync(null, cancellation.Token);
        Assert.Null(result.Recommended);
        Assert.Null(result.SavedRecommendation);
        Assert.Empty(result.Measurements);
        Assert.Contains(expired ? "prazo" : "cancelada", result.Message);
        Assert.Equal(new SqliteSearchTuning(32, true), service.Connections.SearchTuning);
    }

    [Fact]
    public async Task NativeCancellationRollsBackWriteAndDoesNotPoisonPooledConnection()
    {
        await using var database = await TestDatabase.CreateAsync();
        var factory = new SqliteConnectionFactory(database.Repository.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        await using (var connection = await factory.OpenAsync())
        {
            await using var setup = connection.CreateCommand();
            setup.CommandText = "CREATE TABLE cancellation_test(value INTEGER);";
            await setup.ExecuteNonQueryAsync();
            using var interruption = SqliteConnectionFactory.InterruptOnCancellation(connection, cancellation.Token);
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO cancellation_test VALUES(42);";
            await command.ExecuteNonQueryAsync();
            connection.CreateFunction("cancel_now", () => { cancellation.Cancel(); return 1; });
            command.CommandText = "WITH RECURSIVE n(x) AS (VALUES(cancel_now()) UNION ALL SELECT x+1 FROM n WHERE x<100000000) INSERT INTO cancellation_test SELECT x FROM n;";
            var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(9, error.SqliteErrorCode);
        }
        await using var reopened = await factory.OpenAsync();
        await using var check = reopened.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM cancellation_test;";
        Assert.Equal(0L, Convert.ToInt64(await check.ExecuteScalarAsync()));
        cancellation.Cancel();
        check.CommandText = "SELECT 42;";
        Assert.Equal(42L, Convert.ToInt64(await check.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ReadOnlyCalibrationConnectionCannotModifyTheDatabase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var factory = new SqliteConnectionFactory(database.Repository.DatabasePath, readOnly: true);
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM contracts;";
        Assert.Equal(8, (await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync())).SqliteErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedHydrationPreservesPreviouslyCommittedItemsAndPrices(bool replacingPrices)
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([RepositorySearchTests.Contract("cancelled", "Café", "SP", 1)]);
        var item = new ProcurementItem { ContractId = "cancelled", ItemNumber = 1, Description = "Café original", Unit = "kg", HasResult = true };
        var price = new HomologationResult { ContractId = "cancelled", ItemNumber = 1, ResultSequence = 1,
            SupplierName = "Original", HomologatedUnitValueScaled = DecimalScale.ToScaled(10), ResultStatusId = 1 };
        await database.Repository.UpsertItemsAsync("cancelled", [item], false);
        await database.Repository.ReplaceItemResultsAsync("cancelled", 1, [price]);
        using var cancellation = new CancellationTokenSource();
        var connections = new CancellingConnections(new(database.Repository.DatabasePath), cancellation);
        await using (var connection = await connections.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TRIGGER interrupt_hydration BEFORE INSERT ON {(replacingPrices ? "item_results" : "items")}
                BEGIN
                    SELECT cancel_now();
                    SELECT (WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<10000000) SELECT SUM(x) FROM n);
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var repository = new SqliteContractRepository(connections);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacingPrices
            ? repository.ReplaceBackgroundItemResultsAsync("cancelled", 1, [price with { SupplierName = "Novo" }], cancellation.Token)
            : repository.UpsertItemsAsync("cancelled", [item with { Description = "Alterado" }], true, cancellation.Token));
        Assert.Equal("Café original", (await database.Repository.GetItemAsync("cancelled", 1))!.Description);
        Assert.Equal("Original", Assert.Single((await database.Repository.GetCachedItemResultsAsync("cancelled", 1))!.Results).SupplierName);
    }

    private sealed class CancellingConnections(SqliteConnectionFactory source, CancellationTokenSource cancellation) : ISqliteConnectionFactory
    {
        public string DatabasePath => source.DatabasePath;
        public ISqliteWorkCoordinator WorkCoordinator => source.WorkCoordinator;
        public int MigrationCacheKib => source.MigrationCacheKib;
        public long MmapBytes => source.MmapBytes;
        public int WorkerThreads => source.WorkerThreads;
        public string ProfileName => source.ProfileName;
        public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = await source.OpenAsync(cancellationToken);
            connection.CreateFunction("cancel_now", () => { cancellation.Cancel(); return 1; });
            return connection;
        }
    }

    [Theory]
    [InlineData("Restrito")]
    [InlineData("Amplo")]
    [Trait("Category", "Performance")]
    public async Task IsolatedCopyEvaluatesProfilesWithoutChangingDatabase(string profile)
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_CALIBRATION_COPY");
        if (string.IsNullOrWhiteSpace(path)) return;
        Assert.Contains("benchmark", Path.GetDirectoryName(Path.GetFullPath(path))!, StringComparison.OrdinalIgnoreCase);
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        var probe = new Probe();
        if (profile == "Amplo") probe.Snapshot = SystemResourceProbe.CreateSnapshot(
            32L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024, 50, 16);
        var factory = new SqliteConnectionFactory(path, resourceProbe: probe, readOnly: true);
        Assert.Equal(profile, factory.ProfileName);
        var service = new SqliteCalibrationService(factory, new SystemResourceProbe());
        var result = await service.EvaluateAsync(null);
        Assert.NotEmpty(result.Measurements);
        if (result.Recommended is not null)
        {
            Assert.All(result.Measurements.Where(value => value.Tuning == result.Recommended), value => Assert.True(value.Completed));
            Assert.NotNull(result.SavedRecommendation);
        }
        var reportRoot = Environment.GetEnvironmentVariable("PNCPKING_CALIBRATION_REPORT_DIR");
        if (!string.IsNullOrWhiteSpace(reportRoot))
        {
            Directory.CreateDirectory(reportRoot);
            await File.WriteAllTextAsync(Path.Combine(reportRoot, $"calibration-{profile}.json"),
                JsonSerializer.Serialize(result with { SavedRecommendation = null }, new JsonSerializerOptions { WriteIndented = true }));
        }
        var after = new FileInfo(path);
        Assert.Equal(length, after.Length);
        Assert.Equal(modified, after.LastWriteTimeUtc);
    }

    private static IEnumerable<CalibrationMeasurement> Samples(SqliteSearchTuning tuning, double first, double ten, double page)
    {
        foreach (var scenario in new[] { "cafe", "limpeza", "proximidade" })
        for (var round = 0; round < 2; round++)
            yield return new(tuning, scenario, round, round == 0, first, ten, page, 100,
                0, 0, 200_000_000, 1_000_000_000, false, true, scenario, "Concluída");
    }
}
