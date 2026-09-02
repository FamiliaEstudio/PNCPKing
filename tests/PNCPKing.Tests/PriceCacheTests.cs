using Microsoft.Data.Sqlite;
using System.Net;
using System.Reflection;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class PriceCacheTests
{
    [Fact]
    public async Task CacheMeasurements_UseExplicitOrCachedAggregates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("dbstat-aggregate", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [Item(contract, 1)], false);
        await database.Repository.ReplaceItemResultsAsync(
            contract.PncpId,
            1,
            [Result(contract, 1, 1, true)]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);

        var itemCacheBytes = await database.Repository.GetCacheSizeBytesAsync();
        var priceCacheBytes = (await cache.GetProgressAsync()).OccupiedBytes;

        Assert.True(itemCacheBytes > 0, $"item cache: {itemCacheBytes}; price cache: {priceCacheBytes}");
        Assert.True(priceCacheBytes > 0, $"item cache: {itemCacheBytes}; price cache: {priceCacheBytes}");
    }

    [Fact]
    public async Task Migration14ToCurrent_CreatesControlStatisticsAndRecognizesExistingSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("existing", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [Item(contract, 1)], false);
        await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [Result(contract, 1, 1, true)]);

        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TRIGGER contracts_invalidate_price_cache;
                DROP TABLE price_cache_contracts;
                DROP TABLE price_cache_control;
                UPDATE schema_info SET version = 14 WHERE id = 1;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        await database.Repository.InitializeAsync();
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var policy = await cache.GetPolicyAsync();
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);
        var progress = await cache.GetProgressAsync();

        Assert.False(policy.Authorized);
        Assert.Equal(1, progress.TotalContracts);
        Assert.Equal(1, progress.CompletedContracts);
        Assert.Equal(26, SqliteContractRepository.CurrentSchemaVersion);
        Assert.Equal((1L, 1L, 1L), await database.Repository.GetCountsAsync());
    }

    [Fact]
    public async Task Window_IsInclusiveNewestFirstAndExcludesTheThreeHundredSixtyFifthPreviousDay()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        await database.Repository.UpsertContractsAsync([
            RecentContract("newest", today, 1),
            RecentContract("edge", today.AddDays(-364), 2),
            RecentContract("outside", today.AddDays(-365), 3)
        ]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);

        var progress = await cache.GetProgressAsync();
        var next = await cache.GetNextWorkAsync(DateTimeOffset.UtcNow);

        Assert.Equal(2, progress.TotalContracts);
        Assert.NotNull(next);
        Assert.Equal("newest", next.Contract.PncpId);
    }

    [Fact]
    public async Task LocalSearch_ReturnsOnlyCurrentActivePricesAndHonorsFilters()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("coffee", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);
        await cache.MarkContractDownloadingAsync(contract.PncpId, true);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [Item(contract, 1)], false);
        await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [
            Result(contract, 1, 1, true) with { HomologatedUnitValueScaled = DecimalScale.ToScaled(25m) },
            Result(contract, 1, 2, false) with { HomologatedUnitValueScaled = DecimalScale.ToScaled(10m) }
        ]);
        await cache.MarkContractCompleteAsync(contract.PncpId, contract.GlobalUpdatedAt);

        var query = new SearchQuery(
            "cafe",
            SearchGeoFilter.State("SP"),
            today.AddDays(-30),
            today,
            SearchSort.Newest);
        var found = await cache.SearchLocalAsync(
            query,
            SearchText.Parse("cafe"),
            20m,
            30m,
            1,
            50);
        var excluded = await cache.SearchLocalAsync(
            query with { Text = "cafe -torrado" },
            SearchText.Parse("cafe -torrado"),
            null,
            null,
            1,
            50);

        Assert.Single(found.Hits);
        Assert.Equal("coffee", found.Hits[0].Contract.PncpId);
        Assert.Empty(excluded.Hits);
    }

    [Fact]
    public async Task ContractChunkSearch_UsesCursorWithoutOverlapForUnitOnlyExpression()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contracts = new[]
        {
            RecentContract("chunk-newest", today, 1),
            RecentContract("chunk-middle", today.AddDays(-1), 2),
            RecentContract("chunk-oldest", today.AddDays(-2), 3)
        };
        await database.Repository.UpsertContractsAsync(contracts);
        foreach (var contract in contracts)
        {
            await database.Repository.UpsertItemsAsync(contract.PncpId, [
                Item(contract, 1) with
                {
                    Unit = "PACOTE",
                    HydrationStatus = ItemHydrationStatus.Complete
                }
            ], false);
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [
                Result(contract, 1, 1, true)
            ]);
        }

        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var expression = SearchText.Parse("\"pacote");
        var query = new SearchQuery(
            "\"pacote",
            GeoScope.All,
            today.AddDays(-30),
            today,
            Sort: SearchSort.Newest);
        var first = await cache.SearchLocalAfterAsync(query, expression, null, null, null, 1);
        var second = await cache.SearchLocalAfterAsync(
            query,
            expression,
            null,
            null,
            first.Cursor,
            1);
        var third = await cache.SearchLocalAfterAsync(
            query,
            expression,
            null,
            null,
            second.Cursor,
            1);

        Assert.Equal("chunk-newest", Assert.Single(first.Hits).Contract.PncpId);
        Assert.Equal("chunk-middle", Assert.Single(second.Hits).Contract.PncpId);
        Assert.Equal("chunk-oldest", Assert.Single(third.Hits).Contract.PncpId);
        Assert.Equal(
            3,
            first.Hits.Concat(second.Hits).Concat(third.Hits)
                .Select(hit => hit.Contract.PncpId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.False(third.HasMore);
    }

    [Fact]
    public async Task RelevanceSearch_ExpandsRankTiesAndPreservesFiltersAndPagesWithoutCardinalityCount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("fts-candidates", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await SeedHighCardinalityItemsAsync(database.Repository.DatabasePath, contract.PncpId);
        var telemetry = new RecordingSearchTelemetry();
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath, telemetry);
        const string text = "\"cafe comum\" -torrado";
        var expression = SearchText.Parse(text);
        var query = new SearchQuery(
            text,
            SearchGeoFilter.State("SP"),
            today.AddDays(-1),
            today,
            Sort: SearchSort.Relevance);

        var first = await cache.SearchLocalAfterAsync(
            query,
            expression,
            20m,
            30m,
            cursor: null,
            pageSize: 25);
        var second = await cache.SearchLocalAfterAsync(
            query,
            expression,
            20m,
            30m,
            first.Cursor,
            pageSize: 25);
        var legacy = await ReadLegacyHighCardinalityPageAsync(
            database.Repository.DatabasePath,
            expression,
            today);

        var expected = Enumerable.Range(1, 30_001)
            .Where(number => number % 2 == 0 && number % 4 != 0)
            .Take(50)
            .Select(number => (long)number)
            .ToArray();
        var optimizedKeys = first.Rows!.Concat(second.Rows!)
            .Select(row => (
                row.Contract.PncpId,
                row.Item.ItemNumber,
                row.Result!.ResultSequence))
            .ToArray();
        Assert.Equal(legacy, optimizedKeys);
        Assert.Equal(expected[..25], first.Rows!.Select(row => row.Item.ItemNumber));
        Assert.Equal(expected[25..], second.Rows!.Select(row => row.Item.ItemNumber));
        Assert.True(first.HasMore);
        Assert.Equal(
            50,
            first.Rows!.Concat(second.Rows!).Select(row => row.Item.ItemNumber).Distinct().Count());
        Assert.Contains(telemetry.Measurements, value =>
            value.Operation == "price-search" && value.Phase == "sqlite-queue" && value.Succeeded);
        Assert.Contains(telemetry.Measurements, value =>
            value.Operation == "price-search" && value.Phase == "sql-execution" && value.Succeeded);
        Assert.DoesNotContain(telemetry.Measurements, value =>
            value.Operation == "price-search" && value.Phase == "fts-cardinality");
        Assert.Contains(telemetry.Measurements, value =>
            value.Operation == "price-search" && value.Phase == "fts-candidate-batch" &&
            value.Rows == 1_000 && value.Succeeded);
        Assert.Contains(telemetry.Measurements, value =>
            value.Operation == "price-search" && value.Phase == "fts-candidate-batch" &&
            value.Rows == 4_000 && value.Succeeded);
        Assert.Contains(telemetry.Measurements, value =>
            value.Operation == "price-search" && value.Phase == "fts-candidate-batch" &&
            value.Rows == 16_000 && value.Succeeded);
        Assert.Contains(telemetry.Measurements, value =>
            value.Operation == "price-search" && value.Phase == "fts-candidate-batch" &&
            value.Rows == 22_501 && value.Succeeded);

        using var cancellation = new CancellationTokenSource();
        telemetry.CancelAtCandidateBatch = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SearchLocalAfterAsync(
            query,
            expression,
            20m,
            30m,
            cursor: null,
            pageSize: 25,
            cancellation.Token));
        Assert.Contains(telemetry.Measurements, value =>
            value.Phase == "fts-candidate-batch" && !value.Succeeded &&
            value.ErrorKind is nameof(OperationCanceledException) or nameof(TaskCanceledException));
        Assert.Contains(telemetry.Measurements, value =>
            value.Phase == "sql-execution" && !value.Succeeded &&
            value.ErrorKind is nameof(OperationCanceledException) or nameof(TaskCanceledException));

        using var queueCancellation = new CancellationTokenSource();
        queueCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SearchLocalAfterAsync(
            query,
            expression,
            20m,
            30m,
            cursor: null,
            pageSize: 25,
            queueCancellation.Token));
        Assert.Contains(telemetry.Measurements, value =>
            value.Phase == "sqlite-queue" && !value.Succeeded &&
            value.ErrorKind is nameof(OperationCanceledException) or nameof(TaskCanceledException));
    }

    [Fact]
    public async Task RelevanceSearch_RecordsCandidateSqlErrorsWithoutQueryText()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE items_fts;";
            await command.ExecuteNonQueryAsync();
        }
        var telemetry = new RecordingSearchTelemetry();
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath, telemetry);
        var expression = SearchText.Parse("segredo-nao-deve-ser-registrado");
        var query = new SearchQuery(
            "segredo-nao-deve-ser-registrado",
            GeoScope.All,
            Sort: SearchSort.Relevance);

        await Assert.ThrowsAsync<SqliteException>(() => cache.SearchLocalAfterAsync(
            query,
            expression,
            null,
            null,
            null,
            25));

        var failure = Assert.Single(
            telemetry.Measurements,
            value => value.Phase == "fts-candidate-batch" && !value.Succeeded);
        Assert.Equal(nameof(SqliteException), failure.ErrorKind);
        Assert.DoesNotContain(telemetry.Measurements, value => value.Phase == "fts-cardinality");
        Assert.Contains(telemetry.Measurements, value =>
            value.Phase == "sql-execution" && !value.Succeeded &&
            value.ErrorKind == nameof(SqliteException));
        Assert.DoesNotContain("segredo", failure.Operation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("segredo", failure.Phase, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelevanceSearch_SelectsAdaptiveInitialCandidateLimits()
    {
        var selector = typeof(SqlitePriceCacheRepository).GetMethod(
            "SelectInitialFtsCandidateLimit",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(selector);

        static long Select(
            MethodInfo method,
            SearchQuery query,
            SearchExpression expression,
            decimal? minimum = null,
            decimal? maximum = null,
            int pageSize = 25) => Assert.IsType<long>(method.Invoke(
                null,
                [query, expression, minimum, maximum, pageSize]));

        var simple = new SearchQuery("cafe", GeoScope.All, Sort: SearchSort.Relevance);
        Assert.Equal(250, Select(selector, simple, SearchText.Parse(simple.Text)));

        var filtered = new SearchQuery(
            "cafe",
            SearchGeoFilter.State("SP"),
            Sort: SearchSort.Relevance);
        Assert.Equal(1_000, Select(selector, filtered, SearchText.Parse(filtered.Text)));
        Assert.Equal(1_000, Select(selector, simple, SearchText.Parse(simple.Text), minimum: 1m));

        var postFiltered = new SearchQuery("cafe %500 g", GeoScope.All, Sort: SearchSort.Relevance);
        Assert.Equal(5_000, Select(
            selector,
            postFiltered,
            SearchText.Parse(postFiltered.Text)));
        Assert.Equal(5_000, Select(selector, simple, SearchText.Parse(simple.Text), pageSize: 200));
    }

    [Fact]
    public async Task CandidateQueryPlan_MaterializesFtsBeforeDetailTableSearches()
    {
        await using var database = await TestDatabase.CreateAsync();
        var builder = typeof(SqlitePriceCacheRepository).GetMethod(
            "BuildFtsCandidateSearchSql",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(builder);
        var sql = Assert.IsType<string>(builder.Invoke(null, [
            new[]
            {
                "i.hydration_status = $complete",
                "COALESCE(s.source_global_updated_at, '') = COALESCE(c.global_updated_at, '')",
                "r.result_status_id = 1",
                "r.unit_value_scaled > 0"
            },
            string.Empty
        ]));
        await using var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("$itemMatch", "cafe*");
        command.Parameters.AddWithValue("$candidateLimit", 5_000);
        command.Parameters.AddWithValue("$candidateWindow", 5_001);
        command.Parameters.AddWithValue("$complete", (int)ItemHydrationStatus.Complete);
        command.Parameters.AddWithValue("$scanLimit", 200);
        var details = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                details.Add(reader.GetString(3));
            }
        }

        var materializeWindow = details.FindIndex(value => value.Contains(
            "MATERIALIZE fts_window",
            StringComparison.OrdinalIgnoreCase));
        var materializeCandidates = details.FindIndex(value => value.Contains(
            "MATERIALIZE fts_candidates",
            StringComparison.OrdinalIgnoreCase));
        var rankedFtsScan = details.FindIndex(value =>
            value.Contains("SCAN items_fts VIRTUAL TABLE INDEX 32:", StringComparison.OrdinalIgnoreCase));
        var firstDetailSearch = details.FindIndex(value =>
            value.Contains("SEARCH i ", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("SEARCH c ", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("SEARCH s ", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("SEARCH r ", StringComparison.OrdinalIgnoreCase));
        Assert.True(materializeWindow >= 0, string.Join(Environment.NewLine, details));
        Assert.True(materializeCandidates >= 0, string.Join(Environment.NewLine, details));
        Assert.True(rankedFtsScan >= 0, string.Join(Environment.NewLine, details));
        Assert.True(materializeWindow < firstDetailSearch, string.Join(Environment.NewLine, details));
        Assert.True(firstDetailSearch > materializeCandidates, string.Join(Environment.NewLine, details));
    }

    [Fact]
    public async Task SelectiveRemoval_DeletesBackgroundDataButPreservesPinnedContract()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var removable = RecentContract("removable", today, 1);
        var pinned = RecentContract("pinned", today, 2);
        await database.Repository.UpsertContractsAsync([removable, pinned]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);
        foreach (var contract in new[] { removable, pinned })
        {
            await cache.MarkContractDownloadingAsync(contract.PncpId, true);
            await database.Repository.UpsertItemsAsync(contract.PncpId, [Item(contract, 1)], false);
            await cache.MarkContractCompleteAsync(contract.PncpId, contract.GlobalUpdatedAt);
        }
        await database.Repository.ReplaceItemResultsAsync(
            pinned.PncpId,
            1,
            [Result(pinned, 1, 1, true)]);

        await cache.RemoveBackgroundCacheAsync();

        Assert.Null(await database.Repository.GetItemAsync(removable.PncpId, 1));
        Assert.NotNull(await database.Repository.GetItemAsync(pinned.PncpId, 1));
        Assert.False((await cache.GetPolicyAsync()).Enabled);
    }

    [Fact]
    public async Task Synchronization_IsolatesFailureAndResumesOnlyThePendingContract()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var failing = RecentContract("failing", today, 1);
        var healthy = RecentContract("healthy", today.AddDays(-1), 2);
        await database.Repository.UpsertContractsAsync([failing, healthy]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        var client = new CacheClient(failing.PncpId);
        var service = new PriceCacheService(
            client,
            database.Repository,
            new CompleteCoverageRepository(),
            cache);

        await service.SynchronizeAsync();
        var afterFailure = await cache.GetProgressAsync();

        Assert.Equal(1, afterFailure.CompletedContracts);
        Assert.Equal(1, afterFailure.FailedContracts);
        Assert.NotNull(await database.Repository.GetItemAsync(healthy.PncpId, 1));
        Assert.Null(await database.Repository.GetItemAsync(failing.PncpId, 1));

        await cache.MarkContractPendingAsync(failing.PncpId);
        await service.SynchronizeAsync();
        var completed = await cache.GetProgressAsync();

        Assert.Equal(2, completed.CompletedContracts);
        Assert.Equal(0, completed.FailedContracts);
        Assert.NotNull(await database.Repository.GetItemAsync(failing.PncpId, 1));
        Assert.Equal(3, client.ItemListCalls);
        Assert.Equal(0, client.ResultCalls);
    }

    [Fact]
    public async Task PrepareWindow_RecoversContractInterruptedWhileDownloading()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("interrupted", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);
        await cache.MarkContractDownloadingAsync(contract.PncpId, true);

        await cache.PrepareWindowAsync(today.AddDays(-364), today);
        var recovered = await cache.GetNextWorkAsync(DateTimeOffset.UtcNow);

        Assert.NotNull(recovered);
        Assert.Equal(contract.PncpId, recovered.Contract.PncpId);
        Assert.Equal(PriceCacheContractStatus.Pending, recovered.Checkpoint.Status);
    }

    [Fact]
    public async Task PrepareWindow_ReusesPreparedWindowAndTracksNewContractsIncrementally()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var first = RecentContract("prepared-first", today.AddDays(-1), 1);
        await database.Repository.UpsertContractsAsync([first]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);

        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var sentinel = connection.CreateCommand();
            sentinel.CommandText = """
                UPDATE price_cache_contracts SET updated_at = 'sentinel'
                 WHERE contract_id = $contract;
                """;
            sentinel.Parameters.AddWithValue("$contract", first.PncpId);
            await sentinel.ExecuteNonQueryAsync();
        }

        await cache.PrepareWindowAsync(today.AddDays(-364), today);
        var second = RecentContract("prepared-second", today, 2);
        await database.Repository.UpsertContractsAsync([second]);

        await using var verify = new SqliteConnection($"Data Source={database.Repository.DatabasePath}");
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = """
            SELECT updated_at FROM price_cache_contracts WHERE contract_id = $first;
            """;
        command.Parameters.AddWithValue("$first", first.PncpId);
        Assert.Equal("sentinel", Convert.ToString(await command.ExecuteScalarAsync()));

        var progress = await cache.GetProgressAsync();
        var next = await cache.GetNextWorkAsync(DateTimeOffset.UtcNow);
        Assert.Equal(2, progress.TotalContracts);
        Assert.Equal(2, progress.PendingContracts);
        Assert.NotNull(next);
        Assert.Equal(second.PncpId, next.Contract.PncpId);
    }

    [Fact]
    public async Task ProgressCountersFollowCheckpointAndSnapshotChanges()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var completed = RecentContract("statistics-complete", today, 1);
        var failed = RecentContract("statistics-failed", today.AddDays(-1), 2);
        await database.Repository.UpsertContractsAsync([completed, failed]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);

        await cache.MarkContractDownloadingAsync(completed.PncpId, true);
        await database.Repository.UpsertItemsAsync(completed.PncpId, [Item(completed, 1)], false);
        await cache.MarkContractCompleteAsync(completed.PncpId, completed.GlobalUpdatedAt);
        await cache.MarkContractFailedAsync(
            failed.PncpId,
            "falha esperada",
            DateTimeOffset.UtcNow.AddMinutes(1));

        var progress = await cache.GetProgressAsync();
        Assert.Equal(2, progress.TotalContracts);
        Assert.Equal(1, progress.CompletedContracts);
        Assert.Equal(0, progress.PendingContracts);
        Assert.Equal(1, progress.FailedContracts);
        Assert.Equal(1, progress.ItemCount);
    }

    [Fact]
    public async Task AuthorizationRecordsTheCurrentConfirmationTime()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var oldAuthorization = connection.CreateCommand();
            oldAuthorization.CommandText = """
                UPDATE price_cache_control SET authorized_at = '2025-01-01T00:00:00Z'
                 WHERE id = 1;
                """;
            await oldAuthorization.ExecuteNonQueryAsync();
        }

        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        var policy = await cache.GetPolicyAsync();

        Assert.NotNull(policy.AuthorizedAt);
        Assert.True(policy.AuthorizedAt.GetValueOrDefault() >= before);
    }

    [Fact]
    public async Task Synchronization_TimesOutUnresponsiveContractAndContinues()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var hanging = RecentContract("hanging", today, 1);
        var healthy = RecentContract("healthy-after-timeout", today.AddDays(-1), 2);
        await database.Repository.UpsertContractsAsync([hanging, healthy]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        var service = new PriceCacheService(
            new TimeoutCacheClient(hanging.PncpId),
            database.Repository,
            new CompleteCoverageRepository(),
            cache,
            TimeSpan.FromMilliseconds(25));

        await service.SynchronizeAsync();
        var progress = await cache.GetProgressAsync();

        Assert.Equal(1, progress.CompletedContracts);
        Assert.Equal(1, progress.FailedContracts);
        Assert.NotNull(await database.Repository.GetItemAsync(healthy.PncpId, 1));
        Assert.Null(await database.Repository.GetItemAsync(hanging.PncpId, 1));
    }

    [Fact]
    public async Task Synchronization_TreatsMissingItemListAsUnavailableWithoutRetryLoop()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("missing-list", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        var client = new NotFoundCacheClient(failItemList: true);
        var service = new PriceCacheService(
            client,
            database.Repository,
            new CompleteCoverageRepository(),
            cache);

        await service.SynchronizeAsync();
        await service.SynchronizeAsync();
        var progress = await cache.GetProgressAsync();

        Assert.Equal(1, progress.CompletedContracts);
        Assert.Equal(0, progress.FailedContracts);
        Assert.Equal(1, client.ItemListCalls);
    }

    [Fact]
    public async Task Synchronization_DoesNotConsultResultEndpoints()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contract = RecentContract("missing-results", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        var service = new PriceCacheService(
            new NotFoundCacheClient(failItemList: false),
            database.Repository,
            new CompleteCoverageRepository(),
            cache);

        await service.SynchronizeAsync();
        var progress = await cache.GetProgressAsync();
        var item = await database.Repository.GetItemAsync(contract.PncpId, 1);

        Assert.Equal(1, progress.CompletedContracts);
        Assert.Equal(0, progress.FailedContracts);
        Assert.NotNull(item);
        Assert.Equal(ItemHydrationStatus.NotLoaded, item.HydrationStatus);
    }

    [Fact]
    public async Task AggressiveSynchronization_ProcessesDistinctContractsInParallelWithoutResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contracts = Enumerable.Range(1, 8)
            .Select(sequence => RecentContract($"aggressive-{sequence}", today, sequence))
            .ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        var client = new ConcurrentCacheClient();
        var service = new PriceCacheService(
            client,
            database.Repository,
            new CompleteCoverageRepository(),
            cache);

        await service.SynchronizeAggressivelyAsync(maximumParallelContracts: 4);

        var progress = await cache.GetProgressAsync();
        Assert.Equal(8, progress.CompletedContracts);
        Assert.Equal(8, client.ItemListCalls);
        Assert.Equal(0, client.ResultCalls);
        Assert.True(client.MaximumConcurrentCalls >= 2);
        Assert.All(client.CallsByContract.Values, calls => Assert.Equal(1, calls));
    }

    [Fact]
    public async Task AggressiveSynchronization_CancellationRestoresEveryClaimedCheckpoint()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var contracts = Enumerable.Range(1, 6)
            .Select(sequence => RecentContract($"aggressive-cancel-{sequence}", today, sequence))
            .ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        var client = new BlockingConcurrentCacheClient(expectedConcurrentCalls: 3);
        var service = new PriceCacheService(
            client,
            database.Repository,
            new CompleteCoverageRepository(),
            cache);
        using var cancellation = new CancellationTokenSource();
        var run = service.SynchronizeAggressivelyAsync(3, cancellationToken: cancellation.Token);

        await client.WaitUntilConcurrentAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var progress = await cache.GetProgressAsync();
        Assert.Equal(0, progress.CompletedContracts);
        Assert.Equal(6, progress.PendingContracts);
        Assert.Equal(0, progress.FailedContracts);
    }

    [Fact]
    public async Task WindowPruning_RemovesReconstructibleContractsButPreservesPinnedResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var removable = RecentContract("old-removable", today, 1);
        var pinned = RecentContract("old-pinned", today, 2);
        await database.Repository.UpsertContractsAsync([removable, pinned]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);
        foreach (var contract in new[] { removable, pinned })
        {
            await cache.MarkContractDownloadingAsync(contract.PncpId, true);
            await database.Repository.UpsertItemsAsync(contract.PncpId, [Item(contract, 1)], false);
            await cache.MarkContractCompleteAsync(contract.PncpId, contract.GlobalUpdatedAt);
        }
        await database.Repository.ReplaceItemResultsAsync(
            pinned.PncpId,
            1,
            [Result(pinned, 1, 1, true)]);
        var oldDate = today.AddDays(-400).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
        await database.Repository.UpsertContractsAsync([
            removable with { PublicationDate = oldDate },
            pinned with { PublicationDate = oldDate }
        ]);

        await database.Repository.PruneContractsBeforeAsync(today.AddDays(-364));

        Assert.Null(await database.Repository.GetContractAsync(removable.PncpId));
        Assert.NotNull(await database.Repository.GetContractAsync(pinned.PncpId));
        Assert.Single((await database.Repository.GetCachedItemResultsAsync(pinned.PncpId, 1))!.Results);
    }

    private static async Task SeedHighCardinalityItemsAsync(string path, string contractId)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            WITH RECURSIVE numbers(value) AS (
                VALUES(1)
                UNION ALL
                SELECT value + 1 FROM numbers WHERE value < 30001
            )
            INSERT INTO items(
                contract_id, item_number, description, unit, status, has_result,
                hydration_status, cache_updated_at, search_text)
            SELECT $contract, value,
                   'Cafe comum lote ' || value ||
                       CASE WHEN value % 4 = 0 THEN ' torrado' ELSE '' END,
                   'KG', '', 1, $complete, $now,
                   'cafe comum lote ' || value ||
                       CASE WHEN value % 4 = 0 THEN ' torrado' ELSE '' END
              FROM numbers;

            WITH RECURSIVE numbers(value) AS (
                VALUES(1)
                UNION ALL
                SELECT value + 1 FROM numbers WHERE value < 30001
            )
            INSERT INTO item_results(
                contract_id, item_number, result_sequence, supplier_name,
                unit_value_scaled, result_status_id, result_status_name)
            SELECT $contract, value, 1, 'Fornecedor',
                   CASE WHEN value % 2 = 0 THEN $includedPrice ELSE $excludedPrice END,
                   1, 'Ativo'
              FROM numbers;

            INSERT INTO contract_item_snapshots(
                contract_id, fetched_at, item_count, source_global_updated_at)
            SELECT pncp_id, $now, 30001, global_updated_at
              FROM contracts
             WHERE pncp_id = $contract;
            """;
        command.Parameters.AddWithValue("$contract", contractId);
        command.Parameters.AddWithValue("$complete", (int)ItemHydrationStatus.Complete);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$includedPrice", DecimalScale.ToScaled(25m)!.Value);
        command.Parameters.AddWithValue("$excludedPrice", DecimalScale.ToScaled(10m)!.Value);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<IReadOnlyList<(string ContractId, long ItemNumber, long ResultSequence)>>
        ReadLegacyHighCardinalityPageAsync(
            string path,
            SearchExpression expression,
            DateOnly today)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.pncp_id, i.item_number, r.result_sequence, i.description, i.unit
              FROM items_fts
              CROSS JOIN items i ON i.rowid = items_fts.rowid
              CROSS JOIN contracts c ON c.pncp_id = i.contract_id
              CROSS JOIN contract_item_snapshots s ON s.contract_id = i.contract_id
              CROSS JOIN item_results r
                ON r.contract_id = i.contract_id AND r.item_number = i.item_number
             WHERE items_fts MATCH $itemMatch
               AND i.hydration_status = $complete
               AND COALESCE(s.source_global_updated_at, '') = COALESCE(c.global_updated_at, '')
               AND c.uf = 'SP'
               AND c.publication_date >= $start
               AND c.publication_date < $endExclusive
               AND r.result_status_id = 1
               AND r.unit_value_scaled >= $minimum
               AND r.unit_value_scaled <= $maximum
             ORDER BY bm25(items_fts), c.publication_date DESC,
                      c.pncp_id, i.item_number, r.result_sequence
             LIMIT 800;
            """;
        command.Parameters.AddWithValue("$itemMatch", expression.ItemMatchQuery);
        command.Parameters.AddWithValue("$complete", (int)ItemHydrationStatus.Complete);
        command.Parameters.AddWithValue("$start", today.AddDays(-1).ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$endExclusive", today.AddDays(1).ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$minimum", DecimalScale.ToScaled(20m)!.Value);
        command.Parameters.AddWithValue("$maximum", DecimalScale.ToScaled(30m)!.Value);
        var rows = new List<(string ContractId, long ItemNumber, long ResultSequence)>(50);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!expression.MatchesItem(reader.GetString(3), reader.GetString(4)))
            {
                continue;
            }
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            if (rows.Count == 50)
            {
                break;
            }
        }
        return rows;
    }

    internal static ContractRecord RecentContract(string id, DateOnly date, int sequence) =>
        RepositorySearchTests.Contract(id, $"Compra de café {id}", "SP", sequence) with
        {
            PublicationDate = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            GlobalUpdatedAt = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)
        };

    internal static ProcurementItem Item(ContractRecord contract, long number) => new()
    {
        ContractId = contract.PncpId,
        ItemNumber = number,
        Description = "Café torrado em grãos",
        Unit = "KG",
        HasResult = true,
        HydrationStatus = ItemHydrationStatus.NotLoaded
    };

    internal static HomologationResult Result(
        ContractRecord contract,
        long itemNumber,
        long sequence,
        bool active) => new()
    {
        ContractId = contract.PncpId,
        ItemNumber = itemNumber,
        ResultSequence = sequence,
        SupplierName = $"Fornecedor {sequence}",
        HomologatedUnitValueScaled = DecimalScale.ToScaled(25m),
        ResultStatusId = active ? 1 : 2,
        ResultStatusName = active ? "Ativo" : "Cancelado"
    };

    private sealed class RecordingSearchTelemetry : IPerformanceTelemetry
    {
        public List<SearchMeasurement> Measurements { get; } = [];

        public CancellationTokenSource? CancelAtCandidateBatch { get; set; }

        public PerformanceSpan Begin(string operation, string phase = "total")
        {
            if (phase == "fts-candidate-batch" && CancelAtCandidateBatch is not null)
            {
                CancelAtCandidateBatch.Cancel();
                CancelAtCandidateBatch = null;
            }
            return new PerformanceSpan(this, operation, phase);
        }

        public void Record(
            string operation,
            string phase,
            TimeSpan duration,
            long rows = 0,
            long bytes = 0,
            bool succeeded = true,
            string? errorKind = null) =>
            Measurements.Add(new SearchMeasurement(operation, phase, rows, succeeded, errorKind));

        public PerformanceReport CreateReport() => throw new NotSupportedException();
    }

    private sealed record SearchMeasurement(
        string Operation,
        string Phase,
        long Rows,
        bool Succeeded,
        string? ErrorKind);

    private sealed class CacheClient(string failOnceContractId) : IPncpClient
    {
        private bool _failed;

        public int ItemListCalls { get; private set; }
        public int ResultCalls { get; private set; }

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([]);

        public Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default)
        {
            ItemListCalls++;
            if (!_failed && contract.PncpId == failOnceContractId)
            {
                _failed = true;
                throw new HttpRequestException("429 simulado");
            }

            return Task.FromResult<IReadOnlyList<ProcurementItem>>([Item(contract, 1)]);
        }

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default)
        {
            ResultCalls++;
            return Task.FromResult<IReadOnlyList<HomologationResult>>([
                Result(contract, itemNumber, 1, true)
            ]);
        }
    }

    private sealed class ConcurrentCacheClient : IPncpClient
    {
        private int _activeCalls;
        private int _itemListCalls;
        private int _maximumConcurrentCalls;
        private int _resultCalls;

        public int ItemListCalls => Volatile.Read(ref _itemListCalls);
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximumConcurrentCalls);
        public int ResultCalls => Volatile.Read(ref _resultCalls);
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> CallsByContract { get; } = new();

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([]);

        public Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => Task.FromResult(1);

        public async Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _itemListCalls);
            CallsByContract.AddOrUpdate(contract.PncpId, 1, (_, current) => current + 1);
            var active = Interlocked.Increment(ref _activeCalls);
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumConcurrentCalls);
                if (active <= maximum ||
                    Interlocked.CompareExchange(ref _maximumConcurrentCalls, active, maximum) == maximum)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken);
                return [Item(contract, 1)];
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resultCalls);
            return Task.FromResult<IReadOnlyList<HomologationResult>>([]);
        }
    }

    private sealed class BlockingConcurrentCacheClient(int expectedConcurrentCalls) : IPncpClient
    {
        private readonly TaskCompletionSource _concurrent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task WaitUntilConcurrentAsync() => _concurrent.Task;

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([]);

        public Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => Task.FromResult(1);

        public async Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) >= expectedConcurrentCalls)
            {
                _concurrent.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("O modo agressivo não deve consultar resultados.");
    }

    private sealed class TimeoutCacheClient(string hangingContractId) : IPncpClient
    {
        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([]);

        public Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => Task.FromResult(1);

        public async Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default)
        {
            if (contract.PncpId == hangingContractId)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return [Item(contract, 1)];
        }

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HomologationResult>>([
                Result(contract, itemNumber, 1, true)
            ]);
    }

    private sealed class NotFoundCacheClient(bool failItemList) : IPncpClient
    {
        public int ItemListCalls { get; private set; }

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([]);

        public Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default)
        {
            ItemListCalls++;
            return failItemList
                ? Task.FromException<IReadOnlyList<ProcurementItem>>(
                    new HttpRequestException("404 simulado", null, HttpStatusCode.NotFound))
                : Task.FromResult<IReadOnlyList<ProcurementItem>>([Item(contract, 1)]);
        }

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<HomologationResult>>(
                new HttpRequestException("404 simulado", null, HttpStatusCode.NotFound));
    }

    private sealed class CompleteCoverageRepository : ICoverageRepository
    {
        public Task EnsureCoverageWindowAsync(
            DateOnly startDate,
            DateOnly endDate,
            IReadOnlyList<long> activeModalityIds,
            string uf = "ALL",
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetCoverageStatusAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string uf,
            CoverageStatus status,
            long? recordsCount = null,
            string? error = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CoverageDay>> GetCoverageDaysAsync(
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoverageDay>>([]);

        public Task<IReadOnlyList<CoverageWorkItem>> GetIncompleteCoverageAsync(
            DateOnly startDate,
            DateOnly endDate,
            int limit,
            bool newestFirst,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoverageWorkItem>>([]);

        public Task<bool> IsCoverageCompleteAsync(
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
