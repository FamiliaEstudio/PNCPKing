using System.Net;
using System.Text;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;

namespace PNCPKing.Tests;

public sealed class PncpRequestSchedulingTests
{
    [Fact]
    public async Task Scheduler_ServesHigherPrioritiesFirstWhenAllQueuesAreWaiting()
    {
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 1);
        using var blocker = await scheduler.AcquireAsync(PncpRequestPriority.IndexMaintenance);
        var maintenance = scheduler.AcquireAsync(PncpRequestPriority.IndexMaintenance);
        var additionalBatch = scheduler.AcquireAsync(PncpRequestPriority.AdditionalBatches);
        var visiblePrices = scheduler.AcquireAsync(PncpRequestPriority.VisiblePrices);
        var selectedItem = scheduler.AcquireAsync(PncpRequestPriority.UserSelectedItem);

        blocker.Dispose();

        Assert.Same(selectedItem, await Task.WhenAny(selectedItem, visiblePrices, additionalBatch, maintenance));
        (await selectedItem).Dispose();
        Assert.Same(visiblePrices, await Task.WhenAny(visiblePrices, additionalBatch, maintenance));
        (await visiblePrices).Dispose();
        Assert.Same(additionalBatch, await Task.WhenAny(additionalBatch, maintenance));
        (await additionalBatch).Dispose();
        (await maintenance).Dispose();
    }

    [Fact]
    public async Task Scheduler_UsesPrioritiesAndStillServesMaintenance()
    {
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 1);
        using var blocker = await scheduler.AcquireAsync(PncpRequestPriority.UserSelectedItem);

        var highPriority = Enumerable.Range(0, 10)
            .Select(_ => scheduler.AcquireAsync(PncpRequestPriority.UserSelectedItem))
            .ToList();
        var maintenance = scheduler.AcquireAsync(PncpRequestPriority.IndexMaintenance);

        blocker.Dispose();

        var remainingHighPriority = highPriority.ToList();
        var grantsBeforeMaintenance = 0;
        while (true)
        {
            var granted = await Task.WhenAny(remainingHighPriority.Append(maintenance));
            if (ReferenceEquals(granted, maintenance))
            {
                break;
            }

            remainingHighPriority.Remove(granted);
            (await granted).Dispose();
            grantsBeforeMaintenance++;
            Assert.True(grantsBeforeMaintenance <= 5, "A fila de manutenção sofreu inanição.");
        }

        (await maintenance).Dispose();
        foreach (var pending in remainingHighPriority)
        {
            (await pending).Dispose();
        }

        Assert.Equal(0, scheduler.GetSnapshot().ActiveRequests);
        Assert.Equal(0, scheduler.GetSnapshot().TotalQueued);
    }

    [Fact]
    public async Task BackgroundCache_WaitsForAllForegroundWorkAndUsesOnlyOneSlot()
    {
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 2);
        using var index = await scheduler.AcquireAsync(PncpRequestPriority.IndexMaintenance);
        var background1 = scheduler.AcquireAsync(PncpRequestPriority.BackgroundPriceCache);
        var background2 = scheduler.AcquireAsync(PncpRequestPriority.BackgroundPriceCache);

        await Task.Delay(20);
        Assert.False(background1.IsCompleted);
        Assert.Equal(2, scheduler.GetSnapshot().QueuedBackgroundPriceCache);

        index.Dispose();
        using var first = await background1;
        await Task.Delay(20);
        Assert.False(background2.IsCompleted);
        Assert.Equal(1, scheduler.GetSnapshot().ActiveBackgroundPriceCache);

        var user = scheduler.AcquireAsync(PncpRequestPriority.UserSelectedItem);
        using var userLease = await user;
        first.Dispose();
        await Task.Delay(20);
        Assert.False(background2.IsCompleted);

        userLease.Dispose();
        (await background2).Dispose();
        Assert.Equal(0, scheduler.GetSnapshot().TotalQueued);
    }

    [Fact]
    public async Task ForegroundOperation_SuppressesBackgroundBetweenHttpCalls()
    {
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 2);
        var suppression = scheduler.SuppressBackgroundRequests();
        var background = scheduler.AcquireAsync(PncpRequestPriority.BackgroundPriceCache);

        await Task.Delay(20);
        Assert.False(background.IsCompleted);
        Assert.Equal(1, scheduler.GetSnapshot().BackgroundSuppressions);

        suppression.Dispose();
        (await background).Dispose();
        Assert.Equal(0, scheduler.GetSnapshot().BackgroundSuppressions);
    }

    [Fact]
    public async Task Handler_NeverExceedsTheSharedConcurrencyLimit()
    {
        var inner = new BlockingHandler(expectedInitialCalls: 2);
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 2);
        var telemetry = new PncpRequestTelemetry();
        using var client = new HttpClient(new PncpSchedulingHandler(scheduler, telemetry)
        {
            InnerHandler = inner
        });

        var requests = Enumerable.Range(0, 8)
            .Select(index => client.GetAsync($"https://example.test/contratacoes/publicacao?p={index}"))
            .ToArray();

        await inner.WaitForInitialCallsAsync();
        Assert.Equal(2, scheduler.GetSnapshot().ActiveRequests);
        Assert.Equal(6, scheduler.GetSnapshot().TotalQueued);

        inner.Release();
        var responses = await Task.WhenAll(requests);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        Assert.Equal(2, inner.MaximumConcurrentCalls);
        Assert.Equal(0, scheduler.GetSnapshot().ActiveRequests);
        Assert.Equal(8, telemetry.GetSnapshot()[PncpRequestCategory.Contracts].Calls);
    }

    [Fact]
    public async Task AmbientScope_CanPrioritizeAnExistingPncpClientCallWithoutChangingItsInterface()
    {
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 1);
        using var blocker = await scheduler.AcquireAsync(PncpRequestPriority.UserSelectedItem);
        using var client = new HttpClient(new PncpSchedulingHandler(
            scheduler,
            new PncpRequestTelemetry())
        {
            InnerHandler = new PayloadHandler()
        });

        Task<byte[]> request;
        using (PncpRequestOptions.BeginScope(PncpRequestPriority.AdditionalBatches))
        {
            request = client.GetByteArrayAsync(
                "https://example.test/api/pncp/v1/orgaos/1/compras/2026/1/itens/2/resultados");
        }

        Assert.Equal(1, scheduler.GetSnapshot().QueuedAdditionalBatches);
        blocker.Dispose();
        Assert.Equal(5, (await request).Length);
    }

    [Fact]
    public async Task Telemetry_ClassifiesCallsAndCountsActualPayloadBytes()
    {
        var telemetry = new PncpRequestTelemetry();
        using var client = new HttpClient(new PncpSchedulingHandler(
            new PncpRequestScheduler(),
            telemetry)
        {
            InnerHandler = new PayloadHandler()
        });

        await client.GetByteArrayAsync("https://example.test/api/consulta/v1/contratacoes/publicacao");
        await client.GetByteArrayAsync("https://example.test/api/pncp/v1/orgaos/1/compras/2026/1/itens");
        await client.GetByteArrayAsync("https://example.test/api/pncp/v1/orgaos/1/compras/2026/1/itens/2/resultados");
        await client.GetByteArrayAsync("https://example.test/api/pncp/v1/modalidades");

        var snapshot = telemetry.GetSnapshot();
        Assert.Equal(4, snapshot.TotalCalls);
        Assert.Equal(3 + 4 + 5 + 6, snapshot.TotalBytesReceived);
        AssertCategory(snapshot, PncpRequestCategory.Contracts, expectedBytes: 3);
        AssertCategory(snapshot, PncpRequestCategory.ItemLists, expectedBytes: 4);
        AssertCategory(snapshot, PncpRequestCategory.ItemResults, expectedBytes: 5);
        AssertCategory(snapshot, PncpRequestCategory.Other, expectedBytes: 6);
    }

    [Fact]
    public async Task PncpRetries_AreMeasuredAsSeparateHttpCalls()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(string.Empty)
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            }
        ]);
        var telemetry = new PncpRequestTelemetry();
        using var httpClient = new HttpClient(new PncpSchedulingHandler(
            new PncpRequestScheduler(),
            telemetry)
        {
            InnerHandler = new QueueHandler(responses)
        });
        var client = new PncpClient(
            httpClient,
            new Uri("https://example.test/consulta/"),
            new Uri("https://example.test/pncp/"),
            _ => TimeSpan.Zero);

        await client.GetItemResultsAsync(RepositorySearchTests.Contract("id", "Objeto", "SP", 1), 1);

        var results = telemetry.GetSnapshot()[PncpRequestCategory.ItemResults];
        Assert.Equal(2, results.Calls);
        Assert.Equal(1, results.Succeeded);
        Assert.Equal(1, results.Failed);
        Assert.Equal(2, results.BytesReceived);
    }

    [Fact]
    public async Task CancelingAQueuedRequest_DoesNotLeakCapacity()
    {
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 1);
        using var blocker = await scheduler.AcquireAsync(PncpRequestPriority.UserSelectedItem);
        using var cancellation = new CancellationTokenSource();
        var canceled = scheduler.AcquireAsync(
            PncpRequestPriority.VisiblePrices,
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);
        Assert.Equal(0, scheduler.GetSnapshot().TotalQueued);

        blocker.Dispose();
        using var next = await scheduler.AcquireAsync(PncpRequestPriority.IndexMaintenance);
        Assert.Equal(1, scheduler.GetSnapshot().ActiveRequests);
    }

    [Fact]
    public void AdaptiveConcurrency_StartsAtThirtyTwoAndBacksOffThroughTheDefinedTiers()
    {
        var clock = new ManualTimeProvider();
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 32, timeProvider: clock);
        Assert.Equal(32, scheduler.GetSnapshot().EffectiveConcurrency);
        scheduler.ReportOutcome(
            PncpRequestCategory.ItemLists,
            HttpStatusCode.NotFound,
            TimeSpan.FromSeconds(1));
        Assert.Equal(32, scheduler.GetSnapshot().EffectiveConcurrency);

        scheduler.ReportOutcome(
            PncpRequestCategory.ItemLists,
            HttpStatusCode.InternalServerError,
            TimeSpan.FromSeconds(1));
        Assert.Equal(24, scheduler.GetSnapshot().EffectiveConcurrency);
        scheduler.ReportOutcome(
            PncpRequestCategory.ItemResults,
            HttpStatusCode.RequestTimeout,
            TimeSpan.FromSeconds(1));
        Assert.Equal(16, scheduler.GetSnapshot().EffectiveConcurrency);
        scheduler.ReportOutcome(
            PncpRequestCategory.ItemLists,
            statusCode: null,
            TimeSpan.FromSeconds(1),
            transportFailure: true);
        Assert.Equal(16, scheduler.GetSnapshot().EffectiveConcurrency);
        scheduler.ReportOutcome(
            PncpRequestCategory.ItemLists,
            HttpStatusCode.BadGateway,
            TimeSpan.FromSeconds(1));
        Assert.Equal(8, scheduler.GetSnapshot().EffectiveConcurrency);
        scheduler.ReportOutcome(
            PncpRequestCategory.ItemResults,
            HttpStatusCode.TooManyRequests,
            TimeSpan.FromSeconds(1),
            retryAfter: TimeSpan.FromSeconds(10));

        var reduced = scheduler.GetSnapshot();
        Assert.Equal(1, reduced.EffectiveConcurrency);
        Assert.Equal(4, reduced.ConcurrencyReductions);
        Assert.Equal("HTTP 429", reduced.LastReductionReason);
        Assert.NotNull(reduced.GrowthBlockedUntil);
        Assert.True(reduced.GrowthBlockedUntil >= clock.GetUtcNow().AddMinutes(2));
    }

    [Fact]
    public void AdaptiveConcurrency_RecoversOneTierAfterThirtyTwoFastSuccesses()
    {
        var clock = new ManualTimeProvider();
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 32, timeProvider: clock);
        scheduler.ReportOutcome(
            PncpRequestCategory.ItemResults,
            HttpStatusCode.TooManyRequests,
            TimeSpan.FromSeconds(1));
        Assert.Equal(1, scheduler.GetSnapshot().EffectiveConcurrency);
        clock.Advance(TimeSpan.FromMinutes(2));

        foreach (var expected in new[] { 8, 16, 24, 32 })
        {
            for (var index = 0; index < 32; index++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(50));
                scheduler.ReportOutcome(
                    PncpRequestCategory.ItemLists,
                    HttpStatusCode.OK,
                    TimeSpan.FromSeconds(1));
            }

            Assert.Equal(expected, scheduler.GetSnapshot().EffectiveConcurrency);
        }
    }

    [Fact]
    public void AdaptiveConcurrency_ReducesAfterTwoSlowWindows()
    {
        var clock = new ManualTimeProvider();
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 32, timeProvider: clock);
        for (var index = 0; index < 32; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            scheduler.ReportOutcome(
                PncpRequestCategory.ItemLists,
                HttpStatusCode.OK,
                TimeSpan.FromSeconds(31));
        }

        Assert.Equal(32, scheduler.GetSnapshot().EffectiveConcurrency);
        for (var index = 0; index < 32; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            scheduler.ReportOutcome(
                PncpRequestCategory.ItemResults,
                HttpStatusCode.OK,
                TimeSpan.FromSeconds(31));
        }

        var snapshot = scheduler.GetSnapshot();
        Assert.Equal(24, snapshot.EffectiveConcurrency);
        Assert.Contains("latência p95", snapshot.LastReductionReason);
    }

    [Fact]
    public async Task ItemRequestTimeout_IsReportedAsPressure()
    {
        var inner = new BlockingHandler(expectedInitialCalls: 1);
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 32);
        using var client = new HttpClient(new PncpSchedulingHandler(
            scheduler,
            new PncpRequestTelemetry(),
            itemRequestTimeout: TimeSpan.FromMilliseconds(50))
        {
            InnerHandler = inner
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetByteArrayAsync(
                "https://example.test/api/pncp/v1/orgaos/1/compras/2026/1/itens"));

        var snapshot = scheduler.GetSnapshot();
        Assert.Equal(24, snapshot.EffectiveConcurrency);
        Assert.Equal("timeout", snapshot.LastReductionReason);
    }

    [Fact]
    public async Task MaintenanceRemainsLimitedToTwoWhenAdaptiveCeilingIsThirtyTwo()
    {
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 32);

        using var first = await scheduler.AcquireAsync(PncpRequestPriority.IndexMaintenance);
        using var second = await scheduler.AcquireAsync(PncpRequestPriority.IndexMaintenance);
        var third = scheduler.AcquireAsync(PncpRequestPriority.IndexMaintenance);
        await Task.Delay(20);
        Assert.False(third.IsCompleted);

        first.Dispose();
        (await third).Dispose();
    }

    private static void AssertCategory(
        PncpRequestTelemetrySnapshot snapshot,
        PncpRequestCategory category,
        long expectedBytes)
    {
        var value = snapshot[category];
        Assert.Equal(1, value.Calls);
        Assert.Equal(1, value.Succeeded);
        Assert.Equal(0, value.Failed);
        Assert.Equal(expectedBytes, value.BytesReceived);
        Assert.True(value.TotalDuration >= TimeSpan.Zero);
        Assert.True(value.TotalQueueDuration >= TimeSpan.Zero);
    }

    private sealed class BlockingHandler(int expectedInitialCalls) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _initialCalls =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls;
        private int _calls;
        private int _maximumConcurrentCalls;

        public int MaximumConcurrentCalls => Volatile.Read(ref _maximumConcurrentCalls);

        public Task WaitForInitialCallsAsync() => _initialCalls.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeCalls);
            SetMaximum(active);
            if (Interlocked.Increment(ref _calls) >= expectedInitialCalls)
            {
                _initialCalls.TrySetResult();
            }

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1])
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        private void SetMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentCalls);
                if (current >= value ||
                    Interlocked.CompareExchange(ref _maximumConcurrentCalls, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class PayloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var length = path.Contains("contratacoes", StringComparison.OrdinalIgnoreCase) ? 3 :
                path.EndsWith("/itens", StringComparison.OrdinalIgnoreCase) ? 4 :
                path.EndsWith("/resultados", StringComparison.OrdinalIgnoreCase) ? 5 : 6;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Enumerable.Repeat((byte)'x', length).ToArray())
            });
        }
    }

    private sealed class QueueHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responses.Dequeue());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
