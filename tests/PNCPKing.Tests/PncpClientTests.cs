using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;

namespace PNCPKing.Tests;

public sealed class PncpClientTests
{
    [Fact]
    public async Task Client_Retries429AndMapsHomologatedValues()
    {
        var handler = new SequenceHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            },
            _ => Json("""
                [{
                  "sequencialResultado": 7,
                  "niFornecedor": "123",
                  "nomeRazaoSocialFornecedor": "Fornecedor",
                  "quantidadeHomologada": 3.5,
                  "valorUnitarioHomologado": 10.1234,
                  "valorTotalHomologado": 35.4319,
                  "dataResultado": "2026-06-01",
                  "situacaoCompraItemResultadoId": "1",
                  "situacaoCompraItemResultadoNome": "Informado"
                }]
                """));
        var client = new PncpClient(
            new HttpClient(handler),
            new Uri("https://example.test/consulta/"),
            new Uri("https://example.test/pncp/"));
        var contract = RepositorySearchTests.Contract("id", "Objeto", "SP", 1);

        var results = await client.GetItemResultsAsync(contract, 99);

        Assert.Equal(2, handler.Calls);
        Assert.Single(results);
        Assert.Equal(10.1234m, results[0].HomologatedUnitValue);
        Assert.True(results[0].IsActive);
        Assert.DoesNotContain(
            handler.RequestUris,
            uri => uri.Contains("arquivos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Client_NeverUsesEstimatedValuesAsHomologatedValues()
    {
        var handler = new SequenceHandler(_ => Json("""
            [{
              "sequencialResultado": 1,
              "valorUnitarioEstimado": 999.99,
              "valorTotalEstimado": 1999.98,
              "quantidadeEstimada": 2,
              "situacaoCompraItemResultadoId": 1
            }]
            """));
        var client = CreateClient(handler);

        var result = Assert.Single(await client.GetItemResultsAsync(
            RepositorySearchTests.Contract("id", "Objeto", "SP", 1),
            1));

        Assert.Null(result.HomologatedQuantity);
        Assert.Null(result.HomologatedUnitValue);
        Assert.Null(result.HomologatedTotalValue);
    }

    [Fact]
    public async Task Client_RetriesTimeoutButDoesNotRetryClientErrors()
    {
        var timeoutHandler = new SequenceHandler(
            _ => throw new TaskCanceledException("Timeout simulado"),
            _ => Json("[]"));
        var timeoutClient = CreateClient(timeoutHandler, _ => TimeSpan.Zero);

        await timeoutClient.GetItemResultsAsync(
            RepositorySearchTests.Contract("id", "Objeto", "SP", 1),
            1);

        Assert.Equal(2, timeoutHandler.Calls);

        var badRequestHandler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var badRequestClient = CreateClient(badRequestHandler, _ => TimeSpan.Zero);
        await Assert.ThrowsAsync<HttpRequestException>(() => badRequestClient.GetItemResultsAsync(
            RepositorySearchTests.Contract("id", "Objeto", "SP", 1),
            1));
        Assert.Equal(1, badRequestHandler.Calls);
    }

    [Fact]
    public async Task Client_RetriesGatewayTimeoutAndPreservesStatusAfterExhaustion()
    {
        var recoveringHandler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout),
            _ => Json("""
                {"data":[],"totalRegistros":0,"totalPaginas":0,"numeroPagina":1}
                """));
        var recoveringClient = CreateClient(recoveringHandler, _ => TimeSpan.Zero);

        await recoveringClient.GetContractsPageAsync(
            new DateOnly(2026, 7, 20),
            new DateOnly(2026, 7, 20),
            6,
            null,
            1,
            50,
            SyncMode.Publication);

        Assert.Equal(2, recoveringHandler.Calls);

        var persistentHandler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        var persistentClient = CreateClient(persistentHandler, _ => TimeSpan.Zero);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => persistentClient.GetContractsPageAsync(
            new DateOnly(2026, 7, 20),
            new DateOnly(2026, 7, 20),
            6,
            null,
            1,
            50,
            SyncMode.Publication));

        Assert.Equal(HttpStatusCode.GatewayTimeout, exception.StatusCode);
        Assert.Equal(7, persistentHandler.Calls);
    }

    [Fact]
    public async Task Client_RejectsAnInvertedDateRangeBeforeCallingPncp()
    {
        var handler = new SequenceHandler(_ => Json("{}"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetContractsPageAsync(
            new DateOnly(2026, 7, 20),
            new DateOnly(2026, 7, 19),
            6,
            null,
            1,
            50,
            SyncMode.Publication));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Client_MapsNumericIbgeCodeWithoutLosingIt()
    {
        var handler = new SequenceHandler(_ => Json("""
            {
              "data": [{
                "numeroControlePNCP": "123",
                "anoCompra": 2026,
                "sequencialCompra": 1,
                "orgaoEntidade": { "cnpj": "12345678000100" },
                "unidadeOrgao": {
                  "municipioNome": "Ribeirão Preto",
                  "ufSigla": "SP",
                  "codigoIbge": 3543402
                }
              }],
              "totalRegistros": 1,
              "totalPaginas": 1,
              "numeroPagina": 1
            }
            """));
        var client = CreateClient(handler);

        var page = await client.GetContractsPageAsync(
            new DateOnly(2026, 7, 20),
            new DateOnly(2026, 7, 20),
            6,
            null,
            1,
            50,
            SyncMode.Publication);

        Assert.Equal("3543402", Assert.Single(page.Contracts).MunicipalityIbgeCode);
    }

    [Fact]
    public async Task ItemPagination_LoadsDistinctFullPagesWithoutDuplicatingItems()
    {
        var handler = new SequenceHandler(
            _ => Json(ItemsJson(1, 500)),
            _ => Json(ItemsJson(501, 2)));
        var client = CreateClient(handler);

        var items = await client.GetItemsAsync(
            RepositorySearchTests.Contract("id", "Objeto", "SP", 1));

        Assert.Equal(2, handler.Calls);
        Assert.Equal(502, items.Count);
        Assert.Equal(Enumerable.Range(1, 502).Select(value => (long)value), items.Select(item => item.ItemNumber));
    }

    [Fact]
    public async Task ItemPagination_StopsWhenPncpRepeatsTheSameFullPage()
    {
        var repeatedPage = ItemsJson(1, 500);
        var handler = new SequenceHandler(
            _ => Json(repeatedPage),
            _ => Json(repeatedPage),
            _ => throw new InvalidOperationException("A terceira página não deveria ser solicitada."));
        var client = CreateClient(handler);

        var items = await client.GetItemsAsync(
            RepositorySearchTests.Contract("id", "Objeto", "SP", 1));

        Assert.Equal(2, handler.Calls);
        Assert.Equal(500, items.Count);
        Assert.Equal(500, items.Select(item => item.ItemNumber).Distinct().Count());
    }

    private static PncpClient CreateClient(
        HttpMessageHandler handler,
        Func<int, TimeSpan>? backoff = null) => new(
        new HttpClient(handler),
        new Uri("https://example.test/consulta/"),
        new Uri("https://example.test/pncp/"),
        backoff);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string ItemsJson(int first, int count) =>
        "[" + string.Join(
            ',',
            Enumerable.Range(first, count).Select(number =>
                $$"""{"numeroItem":{{number}},"descricao":"Item {{number}}","unidadeMedida":"UN","temResultado":true}""")) + "]";

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;
        public int Calls { get; private set; }
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, responses.Length - 1);
            return Task.FromResult(responses[index](request));
        }
    }
}
