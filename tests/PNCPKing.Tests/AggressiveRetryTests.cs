using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class AggressiveRetryTests
{
    [Fact]
    public void OneErrorBurstReducesOneTierAndRecoveryTakesEightSuccesses()
    {
        var clock = new Clock();
        var scheduler = new PncpRequestScheduler(48, clock);
        using var aggressive = scheduler.EnableAggressiveBackgroundRequests();
        for (var i = 0; i < 48; i++)
            scheduler.ReportOutcome(PncpRequestCategory.Contracts, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(1));
        Assert.Equal(32, scheduler.GetSnapshot().EffectiveConcurrency);
        Assert.Equal(1, scheduler.GetSnapshot().ConcurrencyReductions);
        Assert.Equal(clock.GetUtcNow().AddMilliseconds(250), scheduler.GetSnapshot().GrowthBlockedUntil);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        for (var i = 0; i < 8; i++)
            scheduler.ReportOutcome(PncpRequestCategory.Contracts, HttpStatusCode.OK, TimeSpan.FromMilliseconds(100));
        Assert.Equal(48, scheduler.GetSnapshot().EffectiveConcurrency);
    }

    [Fact]
    public void SlowErrorsFromThePreviousConcurrencyDoNotKeepReducingTheNewLevel()
    {
        var clock = new Clock();
        var scheduler = new PncpRequestScheduler(48, clock, initialConcurrency: 16);
        using var aggressive = scheduler.EnableAggressiveBackgroundRequests();
        for (var i = 0; i < 16; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            scheduler.ReportOutcome(PncpRequestCategory.Contracts, HttpStatusCode.InternalServerError,
                TimeSpan.FromSeconds(40 + i * 2), concurrencyAtDispatch: 16);
        }
        Assert.Equal(8, scheduler.GetSnapshot().EffectiveConcurrency);
        Assert.Equal(1, scheduler.GetSnapshot().ConcurrencyReductions);
        for (var i = 0; i < 8; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            scheduler.ReportOutcome(PncpRequestCategory.ItemLists, HttpStatusCode.OK,
                TimeSpan.FromMilliseconds(100), concurrencyAtDispatch: 8);
        }
        Assert.Equal(16, scheduler.GetSnapshot().EffectiveConcurrency);
    }

    [Fact]
    public void PersistentFailuresCanStillReduceToOneAndRespectExplicitRetryAfter()
    {
        var clock = new Clock();
        var scheduler = new PncpRequestScheduler(48, clock);
        using var aggressive = scheduler.EnableAggressiveBackgroundRequests();
        for (var i = 0; i < 7; i++)
        {
            scheduler.ReportOutcome(PncpRequestCategory.ItemLists, HttpStatusCode.TooManyRequests,
                TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }
        Assert.Equal(1, scheduler.GetSnapshot().EffectiveConcurrency);
        for (var i = 0; i < 16; i++)
            scheduler.ReportOutcome(PncpRequestCategory.ItemLists, HttpStatusCode.OK, TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, scheduler.GetSnapshot().EffectiveConcurrency);
        clock.Advance(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 8; i++)
            scheduler.ReportOutcome(PncpRequestCategory.ItemLists, HttpStatusCode.OK, TimeSpan.FromMilliseconds(100));
        Assert.Equal(8, scheduler.GetSnapshot().EffectiveConcurrency);
    }

    [Fact]
    public void AggressiveDelayUsesMillisecondsAndDoesNotTruncateServerDeadline()
    {
        using var scope = PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance, retryTransientFailures: true);
        Assert.InRange(PncpClient.DefaultBackoffDelay(1).TotalMilliseconds, 250, 349);
        Assert.InRange(PncpClient.DefaultBackoffDelay(2).TotalMilliseconds, 500, 599);
        Assert.InRange(PncpClient.DefaultBackoffDelay(3).TotalMilliseconds, 1000, 1099);
        Assert.InRange(PncpClient.DefaultBackoffDelay(100).TotalMilliseconds, 2000, 2099);
        using var http = new HttpClient();
        var client = new PncpClient(http);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), client.GetRetryDelay(response, 1));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task NestedItemScopeRecoversWithinThreeAttempts(HttpStatusCode status)
    {
        var handler = new Handler((_, call) => call <= 2 ? new(status) : Json("[]"));
        using var http = new HttpClient(handler);
        var client = Client(http);
        Assert.False(PncpRequestOptions.RetryTransientFailures);
        using (PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance, retryTransientFailures: true))
        using (PncpRequestOptions.BeginScope(PncpRequestPriority.BackgroundPriceCache, PncpRequestCategory.ItemResults))
        {
            await client.GetItemResultsAsync(RepositorySearchTests.Contract("retry", "Objeto", "SP", 1), 1);
            Assert.Equal(3, handler.Calls);
        }
        Assert.False(PncpRequestOptions.RetryTransientFailures);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task PersistentFailuresReturnToCheckpointAfterThreeAttempts(HttpStatusCode status)
    {
        var handler = new Handler((_, _) => new(status));
        using var http = new HttpClient(handler);
        using var scope = PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance, retryTransientFailures: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(http).GetContractsPageAsync(
            new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 16), 6, null, 1, 50,
            SyncMode.Publication, deadline.Token));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("http")]
    public async Task ExhaustedTransportFailuresCanBeDeferredToTheNextCycle(string kind)
    {
        var handler = new Handler((_, _) => throw (kind switch
        {
            "io" => new IOException("Conexão interrompida"),
            "timeout" => new TaskCanceledException("Chamada sem resposta"),
            _ => (Exception)new HttpRequestException("Conexão interrompida")
        }));
        using var http = new HttpClient(handler);
        using var scope = PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance, retryTransientFailures: true);
        var error = await Record.ExceptionAsync(() => Client(http).GetModalitiesAsync());
        Assert.NotNull(error);
        Assert.True(PncpClient.IsTransientFailure(error));
        Assert.Equal(3, handler.Calls);
        Assert.False(PncpClient.IsTransientFailure(new OperationCanceledException()));
        Assert.False(PncpClient.IsTransientFailure(new IOException("Erro de arquivo local")));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task PermanentClientErrorsStillStop(HttpStatusCode status)
    {
        var handler = new Handler((_, _) => new(status));
        using var http = new HttpClient(handler);
        using var scope = PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance, retryTransientFailures: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(http).GetModalitiesAsync());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseDuringRetryPreventsAnotherRequestUntilResumeOrCancellation(bool cancel)
    {
        var gate = new AsyncPauseGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedback = new InlineProgress<string>(_ => { gate.Pause(); entered.TrySetResult(); });
        var handler = new Handler((_, call) => call == 1 ? new(HttpStatusCode.TooManyRequests) : Json("[]"));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        using var scope = PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance,
            retryTransientFailures: true, waitForResume: gate.WaitAsync, retryProgress: feedback);
        var operation = Client(http).GetModalitiesAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Calls);
        Assert.False(operation.IsCompleted);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(1, handler.Calls);
        }
        else
        {
            gate.Resume();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, handler.Calls);
        }
    }

    [Fact]
    public async Task CancellationInterruptsAnExplicitLongServerWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        using var scope = PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance,
            retryTransientFailures: true, retryProgress: new InlineProgress<string>(_ => entered.TrySetResult()));
        var operation = Client(http).GetModalitiesAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OneAuthorizedCycleContinuesThroughContractsItemsAndPricesAfterTransientFailures()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = DataWindow.Start(today);
        var contract = PriceCacheTests.RecentContract("retry-cycle", today, 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.EnsureCoverageWindowAsync(start, today, [6]);
        await database.Repository.SetCoverageStatusAsync(start, today, 6, "ALL", CoverageStatus.Complete);
        await database.Repository.SetDatasetStateAsync(start, today, GeoScope.All, DateTimeOffset.Now);
        var counts = new Dictionary<string, int>();
        var handler = new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var kind = path.EndsWith("modalidades") ? "modalities" : path.Contains("contratacoes") ? "contracts"
                : path.EndsWith("resultados") ? "prices" : "items";
            int count;
            lock (counts) { counts.TryGetValue(kind, out count); counts[kind] = ++count; }
            if (count <= 2) return new(HttpStatusCode.TooManyRequests);
            return Json(kind switch
            {
                "modalities" => """[{"id":6,"nome":"Pregão","statusAtivo":true}]""",
                "contracts" => """{"data":[],"totalRegistros":0,"totalPaginas":0,"numeroPagina":1}""",
                "items" => """[{"numeroItem":1,"descricao":"Teste","unidadeMedida":"UN","temResultado":true}]""",
                _ => "[]"
            });
        });
        var scheduler = new PncpRequestScheduler(48);
        using var http = new HttpClient(new PncpSchedulingHandler(scheduler, new PncpRequestTelemetry()) { InnerHandler = handler });
        var client = Client(http);
        var sync = new SyncService(client, database.Repository);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, start, today);
        await cache.SetNationalPriceIndexAuthorizationAsync(true, start, today);
        using var aggressive = scheduler.EnableAggressiveBackgroundRequests();
        using var scope = PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance,
            retryTransientFailures: true, waitForResume: sync.WaitWhilePausedAsync);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await new AutoSyncCoordinator(client, database.Repository, sync).SynchronizeAsync(cancellationToken: deadline.Token);
        await new PriceCacheService(client, database.Repository, database.Repository, cache)
            .SynchronizeAggressivelyAsync(4, cancellationToken: deadline.Token);
        Assert.Equal(PriceCacheStatus.Complete, (await cache.GetProgressAsync()).Status);
        await new NationalPriceIndexService(client, database.Repository, cache)
            .SynchronizeAggressivelyAsync(4, cancellationToken: deadline.Token);
        Assert.Equal(PriceCacheStatus.Complete, (await cache.GetNationalPriceIndexProgressAsync()).Status);
        Assert.All(new[] { "modalities", "contracts", "items", "prices" }, key => Assert.True(counts[key] >= 3));
        Assert.Equal(1, (await database.Repository.GetCountsAsync()).Items);
        Assert.Equal(0, scheduler.GetSnapshot().ActiveRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task PersistentFailuresLeaveCheckpointsWhileAvailableItemsAndPricesAreSaved(HttpStatusCode contractFailure)
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = DataWindow.Start(today);
        var records = Enumerable.Range(1, 3)
            .Select(i => PriceCacheTests.RecentContract($"partial-{i}", today, i)).ToArray();
        await database.Repository.UpsertContractsAsync(records);
        await database.Repository.EnsureCoverageWindowAsync(start, today, [6]);
        await database.Repository.SetCoverageStatusAsync(start, today, 6, "ALL", CoverageStatus.Complete);
        await database.Repository.SetCoverageStatusAsync(today, today, 6, "ALL", CoverageStatus.Missing);
        var previousSync = DateTimeOffset.UtcNow.AddDays(-1);
        await database.Repository.SetDatasetStateAsync(start, today, GeoScope.All, previousSync);
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var failing = true;
        var handler = new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.AddOrUpdate(path, 1, (_, n) => n + 1);
            if (path.EndsWith("modalidades"))
                return Json("""[{"id":6,"nome":"Pregão","statusAtivo":true}]""");
            if (failing && (path.Contains("contratacoes") ||
                            path.EndsWith("/1/itens") || path.EndsWith("/2/itens/1/resultados")))
                return new(path.Contains("contratacoes") ? contractFailure : HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"message":"For input string: \"\""}""")
                };
            if (path.Contains("contratacoes"))
                return Json("""{"data":[],"totalRegistros":0,"totalPaginas":0,"numeroPagina":1}""");
            return path.EndsWith("resultados")
                ? Json("""[{"sequencialResultado":1,"valorUnitarioHomologado":25,"situacaoCompraItemResultadoId":1}]""")
                : Json("""[{"numeroItem":1,"descricao":"Café","unidadeMedida":"KG","temResultado":true}]""");
        });
        using var http = new HttpClient(handler);
        var client = Client(http);
        var sync = new SyncService(client, database.Repository);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, start, today);
        await cache.SetNationalPriceIndexAuthorizationAsync(true, start, today);
        using var scope = PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance, retryTransientFailures: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var coordinator = new AutoSyncCoordinator(client, database.Repository, sync);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.SynchronizeAsync(cancellationToken: deadline.Token));
        Assert.True(PncpClient.CanDeferContractFailure(error));
        Assert.Equal(contractFailure, error.StatusCode);
        Assert.False(PncpClient.CanDeferContractFailure(new OperationCanceledException()));
        Assert.False(PncpClient.CanDeferContractFailure(new HttpRequestException("Sem autorização", null, HttpStatusCode.Unauthorized)));
        Assert.False(PncpClient.CanDeferContractFailure(new HttpRequestException("Sem acesso", null, HttpStatusCode.Forbidden)));
        Assert.False(await database.Repository.IsCoverageCompleteAsync(start, today));
        Assert.Equal(previousSync, (await database.Repository.GetDatasetStateAsync()).LastSuccessfulSync);
        var checkpoint = await database.Repository.GetPartitionCheckpointAsync($"Publication:{today:yyyyMMdd}:{today:yyyyMMdd}:m6:ufALL");
        Assert.Equal(SyncPartitionStatus.Failed, checkpoint!.Status);
        Assert.Equal(1, checkpoint.NextPage);

        var items = new PriceCacheService(client, database.Repository, database.Repository, cache);
        var prices = new NationalPriceIndexService(client, database.Repository, cache);
        await items.SynchronizeAggressivelyAsync(3, cancellationToken: deadline.Token);
        var itemProgress = await cache.GetProgressAsync();
        Assert.Equal(2, itemProgress.CompletedContracts);
        Assert.Equal(1, itemProgress.FailedContracts);
        Assert.NotEqual(PriceCacheStatus.Complete, itemProgress.Status);
        await prices.SynchronizeAggressivelyAsync(3, cancellationToken: deadline.Token);
        Assert.Equal(1, (await cache.GetNationalPriceIndexProgressAsync()).FailedContracts);
        Assert.Single((await database.Repository.GetCachedItemResultsAsync(records[2].PncpId, 1))!.Results);
        Assert.All(calls.Where(p => p.Key.Contains("contratacoes") || p.Key.EndsWith("/1/itens") ||
                                   p.Key.EndsWith("/2/itens/1/resultados")),
            p => Assert.Equal(p.Key.Contains("contratacoes") && contractFailure == HttpStatusCode.BadRequest ? 1 : 3, p.Value));

        // Make retries eligible in this temporary database without waiting a minute.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE price_cache_contracts SET next_retry_at = NULL, price_index_next_retry_at = NULL;";
            await command.ExecuteNonQueryAsync();
        }
        failing = false;
        await coordinator.SynchronizeAsync(cancellationToken: deadline.Token);
        await items.SynchronizeAggressivelyAsync(3, cancellationToken: deadline.Token);
        await prices.SynchronizeAggressivelyAsync(3, cancellationToken: deadline.Token);
        Assert.True(await database.Repository.IsCoverageCompleteAsync(start, today));
        Assert.Equal(PriceCacheStatus.Complete, (await cache.GetProgressAsync()).Status);
        Assert.Equal(PriceCacheStatus.Complete, (await cache.GetNationalPriceIndexProgressAsync()).Status);
        Assert.Equal(3, (await cache.GetNationalPriceIndexProgressAsync()).PricedItems);
        Assert.All(calls.Where(p => p.Key.Contains("/3/itens")), p => Assert.Equal(1, p.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutageStopsOpeningMoreWorkInsteadOfScanningTheEntireBacklog(bool prices)
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = DataWindow.Start(today);
        var records = Enumerable.Range(1, 10)
            .Select(i => PriceCacheTests.RecentContract($"outage-{i}", today, i)).ToArray();
        await database.Repository.UpsertContractsAsync(records);
        await database.Repository.EnsureCoverageWindowAsync(start, today, [6]);
        await database.Repository.SetCoverageStatusAsync(start, today, 6, "ALL", CoverageStatus.Complete);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, start, today);
        await cache.PrepareWindowAsync(start, today);
        if (prices)
        {
            foreach (var record in records)
            {
                await database.Repository.UpsertItemsAsync(record.PncpId,
                    Enumerable.Range(1, 10).Select(i => PriceCacheTests.Item(record, i)).ToArray(), false);
                await cache.MarkContractCompleteAsync(record.PncpId, record.GlobalUpdatedAt);
            }
            await cache.SetNationalPriceIndexAuthorizationAsync(true, start, today);
        }
        var handler = new Handler((_, _) => new(HttpStatusCode.InternalServerError));
        using var http = new HttpClient(handler);
        var client = Client(http);
        using var scope = PncpRequestOptions.BeginScope(PncpRequestPriority.IndexMaintenance, retryTransientFailures: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (prices)
        {
            await new NationalPriceIndexService(client, database.Repository, cache)
                .SynchronizeAggressivelyAsync(1, cancellationToken: deadline.Token);
            var progress = await cache.GetNationalPriceIndexProgressAsync();
            Assert.Equal(3, progress.FailedContracts);
            Assert.Equal(7, progress.PendingContracts);
            Assert.Equal(0, progress.CompletedItems);
            Assert.Equal(27, handler.Calls);
        }
        else
        {
            await new PriceCacheService(client, database.Repository, database.Repository, cache)
                .SynchronizeAggressivelyAsync(1, cancellationToken: deadline.Token);
            var progress = await cache.GetProgressAsync();
            Assert.Equal(3, progress.FailedContracts);
            Assert.Equal(7, progress.PendingContracts);
            Assert.Equal(9, handler.Calls);
        }
    }

    private static PncpClient Client(HttpClient http) => new(http,
        new Uri("https://example.test/consulta/"), new Uri("https://example.test/pncp/"), _ => TimeSpan.Zero);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(reply(request, Interlocked.Increment(ref _calls)));
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
