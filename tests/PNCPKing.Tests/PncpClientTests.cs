using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;
using PNCPKing.Infrastructure.Services;

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
                  "localidadeFornecedor": {
                    "nomeMunicipio": "Ribeirão Preto",
                    "uf": "SP"
                  },
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
        Assert.Equal("Ribeirão Preto", results[0].SupplierMunicipality);
        Assert.Equal("SP", results[0].SupplierUf);
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
    public async Task Client_ExplainsPncpDatabaseOutageAndKeepsTheHttpFailure()
    {
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""
                {"timestamp":"2026-09-16T01:23:20.830+00:00","status":500,"error":"Internal Server Error","message":"Failed to obtain JDBC Connection; nested exception is java.sql.SQLTransientConnectionException: HikariPool-1 - Connection is not available, request timed out after 30000ms.","path":"/pncp-consulta/v1/contratacoes/publicacao"}
                """)
        });
        var client = CreateClient(handler, _ => TimeSpan.Zero);
        var date = new DateOnly(2026, 9, 15);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetContractsPageAsync(
            date, date, 6, null, 1, 50, SyncMode.Publication));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.Contains("servidor do PNCP não conseguiu acessar o próprio banco", error.Message);
        Assert.Contains("20260915 a 20260915", error.Message);
        Assert.Contains("JDBC Connection", error.Message);
        Assert.Equal(7, handler.Calls);
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

    [Theory]
    [InlineData(SyncMode.Publication, "Período inicial e final maior que 365 dias")]
    [InlineData(SyncMode.GlobalUpdate, "Período inicial e final maior que 365 dias")]
    [InlineData(SyncMode.Publication, "Data Inicial deve ser anterior ou igual à Data Final")]
    public async Task Client_RetriesContradictoryDateRejectionWithoutChangingTheQuery(SyncMode mode, string error)
    {
        var handler = new SequenceHandler(
            _ => DateRejection(error),
            _ => Json("""{"data":[],"totalRegistros":0,"totalPaginas":0,"numeroPagina":2}"""));
        var client = CreateClient(handler, _ => TimeSpan.Zero);
        var date = new DateOnly(2026, 9, 15);

        var result = await client.GetContractsPageAsync(date, date, 6, "SP", 2, 50, mode);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, result.Page);
        var endpoint = mode == SyncMode.Publication ? "publicacao" : "atualizacao";
        Assert.All(handler.RequestUris, uri => Assert.Equal(
            $"https://example.test/consulta/v1/contratacoes/{endpoint}?dataInicial=20260915&dataFinal=20260915&codigoModalidadeContratacao=6&pagina=2&tamanhoPagina=50&uf=SP",
            uri));
    }

    [Fact]
    public async Task Client_PersistentDateRejectionPreservesFailedCoverageAndCanResume()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recovered = false;
        var handler = new SequenceHandler(_ => recovered
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : DateRejection());
        var service = new SyncService(CreateClient(handler, _ => TimeSpan.Zero), database.Repository);
        var date = new DateOnly(2026, 9, 15);
        var options = new SyncExecutionOptions { KnownModalities = [new Modality(6, "Pregão")] };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.SynchronizeAsync(
            date, date, GeoScope.All, SyncMode.Publication, options));

        Assert.Equal(3, handler.Calls);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Contains("intervalo válido", exception.Message);
        Assert.Contains("20260915 a 20260915", exception.Message);
        Assert.Contains("Período inicial e final maior que 365 dias", exception.Message);
        var coverage = (ICoverageRepository)database.Repository;
        Assert.Equal(CoverageStatus.Failed, Assert.Single(await coverage.GetCoverageDaysAsync(date, date)).Status);
        var key = "Publication:20260915:20260915:m6:ufALL";
        var checkpoint = await database.Repository.GetPartitionCheckpointAsync(key);
        Assert.NotNull(checkpoint);
        Assert.Equal(SyncPartitionStatus.Failed, checkpoint.Status);
        Assert.Equal(1, checkpoint.NextPage);
        Assert.NotNull(checkpoint.NextRetryAt);
        Assert.Null((await database.Repository.GetDatasetStateAsync()).LastSuccessfulSync);

        recovered = true;
        await service.SynchronizeAsync(date, date, GeoScope.All, SyncMode.Publication, options);

        Assert.Equal(4, handler.Calls);
        Assert.Equal(CoverageStatus.Complete, Assert.Single(await coverage.GetCoverageDaysAsync(date, date)).Status);
        Assert.Equal(0, (await database.Repository.GetPartitionCheckpointAsync(key))!.NextPage);
        Assert.NotNull((await database.Repository.GetDatasetStateAsync()).LastSuccessfulSync);
    }

    [Theory]
    [InlineData(366, "Período inicial e final maior que 365 dias")]
    [InlineData(0, "Modalidade inválida")]
    public async Task Client_DoesNotRetryOtherValidationErrorsOrOversizedDateRanges(int days, string error)
    {
        var handler = new SequenceHandler(_ => DateRejection(error));
        var client = CreateClient(handler, _ => TimeSpan.Zero);
        var date = new DateOnly(2025, 9, 14);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetContractsPageAsync(
            date, date.AddDays(days), 6, null, 1, 50, SyncMode.Publication));

        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.DoesNotContain("intervalo válido", exception.Message);
    }

    [Fact]
    public async Task Client_DateRejectionRespectsBackgroundAttemptLimit()
    {
        var handler = new SequenceHandler(_ => DateRejection());
        var client = CreateClient(handler, _ => TimeSpan.Zero);
        using var scope = PncpRequestOptions.BeginScope(PncpRequestPriority.BackgroundPriceCache);
        var date = new DateOnly(2026, 9, 15);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetContractsPageAsync(
            date, date, 6, null, 1, 50, SyncMode.Publication));

        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
    }

    [Fact]
    public async Task Client_DateRejectionReleasesResponseBeforeWaitingAndAllowsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var response = DateRejection();
        var content = response.Content;
        var handler = new SequenceHandler(_ => response);
        var client = CreateClient(handler, _ =>
        {
            cancellation.Cancel();
            return TimeSpan.FromSeconds(30);
        });
        var date = new DateOnly(2026, 9, 15);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetContractsPageAsync(
            date, date, 6, null, 1, 50, SyncMode.Publication, cancellation.Token));

        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => content.ReadAsStringAsync());
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
    public async Task Client_SanitizesUnicodeNoncharactersAtTheApiBoundary()
    {
        var handler = new SequenceHandler(_ => Json("""
            {
              "data": [{
                "numeroControlePNCP": "123",
                "anoCompra": 2026,
                "sequencialCompra": 1,
                "objetoCompra": "Aquisição \uFFFE de café",
                "orgaoEntidade": {
                  "cnpj": "12345678000100",
                  "razaoSocial": "Fornecedor \uFFFF"
                }
              }],
              "totalRegistros": 1,
              "totalPaginas": 1,
              "numeroPagina": 1
            }
            """));
        var client = CreateClient(handler);

        var page = await client.GetContractsPageAsync(
            new DateOnly(2026, 5, 7),
            new DateOnly(2026, 5, 7),
            6,
            null,
            10,
            50,
            SyncMode.Publication);

        var contract = Assert.Single(page.Contracts);
        Assert.Equal("Aquisição � de café", contract.Object);
        Assert.Equal("Fornecedor �", contract.Organization);
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

    [Fact]
    public async Task Documents_ListActiveEntriesAndDownloadFromOfficialContractEndpoint()
    {
        var expectedBytes = Encoding.UTF8.GetBytes("%PDF-documento");
        var handler = new SequenceHandler(
            _ => Json("""
                [
                  {
                    "sequencialDocumento": 2,
                    "titulo": "Edital",
                    "tipoDocumentoNome": "Edital",
                    "dataPublicacaoPncp": "2026-07-20T10:30:00Z",
                    "url": "https://pncp.gov.br/arquivo/2",
                    "statusAtivo": true
                  },
                  {
                    "sequencialDocumento": 1,
                    "titulo": "Revogado",
                    "statusAtivo": false
                  }
                ]
                """),
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(expectedBytes)
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                response.Content.Headers.ContentDisposition =
                    new ContentDispositionHeaderValue("attachment") { FileNameStar = "edital.pdf" };
                return response;
            });
        var client = CreateClient(handler);
        var contract = new PncpContractKey(
            "11222333000181-1-000123/2026",
            "11222333000181",
            2026,
            123);

        var document = Assert.Single(await client.ListDocumentsAsync(contract));
        var content = await client.DownloadDocumentAsync(contract, document);

        Assert.Equal(2, document.Sequence);
        Assert.Equal("Edital", document.Title);
        Assert.Equal("https://pncp.gov.br/arquivo/2", document.DownloadUri);
        Assert.Equal("application/pdf", content.ContentType);
        Assert.Equal("edital.pdf", content.FileName);
        Assert.Equal(expectedBytes, content.Bytes);
        Assert.Equal(
            "https://example.test/pncp/v1/orgaos/11222333000181/compras/2026/123/arquivos",
            handler.RequestUris[0]);
        Assert.Equal(
            "https://example.test/pncp/v1/orgaos/11222333000181/compras/2026/123/arquivos/2",
            handler.RequestUris[1]);
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

    private static HttpResponseMessage DateRejection(string message = "Período inicial e final maior que 365 dias") =>
        new(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(
                $$"""{"path":"/pncp-consulta/v1/contratacoes/publicacao","message":"{{message}}","error":"422 UNPROCESSABLE_ENTITY","timestamp":"2026-09-15T10:06:14.209-03:00","status":422}""",
                Encoding.UTF8, "application/json")
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
