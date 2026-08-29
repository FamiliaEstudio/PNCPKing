using System.Net;
using System.Text;
using PNCPKing.Core.Models;
using PNCPKing.Guard;

namespace PNCPKing.Tests;

public sealed class GuardClientTests
{
    [Fact]
    public async Task Client_PaginatesItemsAndCallsResultsOnlyWhenFlagged()
    {
        var firstPage = "[" + string.Join(',', Enumerable.Range(1, 500).Select(index =>
            $"{{\"numeroItem\":{index},\"descricao\":\"Item {index}\",\"temResultado\":false}}")) + "]";
        var handler = new RecordingHandler(request =>
        {
            var query = request.RequestUri!.Query;
            if (request.RequestUri.AbsolutePath.EndsWith("/itens", StringComparison.Ordinal) && query.Contains("pagina=1"))
            {
                return Json(firstPage);
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/itens", StringComparison.Ordinal) && query.Contains("pagina=2"))
            {
                return Json("[{\"numeroItem\":501,\"descricao\":\"Com preço\",\"temResultado\":true}]");
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/itens/501/resultados", StringComparison.Ordinal))
            {
                return Json("[{\"sequencialResultado\":1,\"nomeRazaoSocialFornecedor\":\"Fornecedor\",\"situacaoCompraItemResultadoId\":1}]");
            }

            throw new InvalidOperationException("Endpoint inesperado: " + request.RequestUri);
        });
        using var http = new HttpClient(handler);
        var client = new GuardPncpClient(http, new Uri("https://example.test/api/pncp/"));
        var contract = Contract();

        var items = await client.GetItemsAsync(contract, CancellationToken.None);
        var results = new List<GuardResult>();
        foreach (var item in items.Where(item => item.HasResult))
        {
            results.AddRange(await client.GetResultsAsync(contract, item.ItemNumber, CancellationToken.None));
        }

        Assert.Equal(501, items.Count);
        Assert.Single(results);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, uri =>
            uri.Contains("modalidades", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("contratacoes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Requests, uri => uri.Contains("/itens/1/resultados", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Client_TreatsResult404AsCompleteEmptyButItem404AsFailure()
    {
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var client = new GuardPncpClient(http, new Uri("https://example.test/api/pncp/"));

        Assert.Empty(await client.GetResultsAsync(Contract(), 1, CancellationToken.None));
        var exception = await Assert.ThrowsAsync<GuardPncpException>(
            () => client.GetItemsAsync(Contract(), CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task Client_ExposesRetryAfterFor429WithoutIssuingParallelRetry()
    {
        var expected = DateTimeOffset.UtcNow.AddMinutes(7);
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(expected);
            return response;
        });
        using var http = new HttpClient(handler);
        var client = new GuardPncpClient(http, new Uri("https://example.test/api/pncp/"));

        var exception = await Assert.ThrowsAsync<GuardPncpException>(
            () => client.GetItemsAsync(Contract(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(expected, exception.RetryAt);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Client_ConvertsTransportTimeoutIntoDeferredRetry()
    {
        var handler = new RecordingHandler(_ => throw new TaskCanceledException("timeout"));
        using var http = new HttpClient(handler);
        var client = new GuardPncpClient(http, new Uri("https://example.test/api/pncp/"));

        var exception = await Assert.ThrowsAsync<GuardPncpException>(
            () => client.GetItemsAsync(Contract(), CancellationToken.None));

        Assert.Null(exception.StatusCode);
        Assert.NotNull(exception.RetryAt);
        Assert.Single(handler.Requests);
    }

    private static GuardPlanContract Contract() => new()
    {
        PncpId = "contract",
        Cnpj = "12345678000199",
        PurchaseYear = 2026,
        PurchaseSequence = 7,
        GlobalUpdatedAt = DateTimeOffset.UtcNow
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(response(request));
        }
    }
}
