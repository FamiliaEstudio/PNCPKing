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
}
