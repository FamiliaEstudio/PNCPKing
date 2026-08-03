using System.Net;
using System.Text;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class CatalogTests
{
    [Fact]
    public async Task OfficialClient_UsesActivePagedEndpointAndMapsHierarchy()
    {
        var handler = new JsonHandler("""
            {
              "resultado": [{
                "codigoItem": 123456,
                "descricaoItem": "CONEXÃO PARA TUBO, DIÂMETRO: 25,4 MM",
                "statusItem": true,
                "codigoGrupo": 10,
                "nomeGrupo": "Tubos",
                "codigoClasse": 11,
                "nomeClasse": "Conexões",
                "codigoPdm": 12,
                "nomePdm": "Conexão hidráulica",
                "dataHoraAtualizacao": "2026-07-01T12:00:00Z"
              }],
              "totalRegistros": 1,
              "totalPaginas": 1
            }
            """);
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://dadosabertos.compras.gov.br/")
        };

        var page = await new ComprasCatalogClient(http).GetPageAsync(CatalogKind.Catmat, 1, 500);

        Assert.Contains("modulo-material/4_consultarItemMaterial", handler.LastRequest!.AbsoluteUri);
        Assert.Contains("tamanhoPagina=500", handler.LastRequest.Query);
        Assert.Contains("statusItem=1", handler.LastRequest.Query);
        var entry = Assert.Single(page.Entries);
        Assert.Equal("123456", entry.Code);
        Assert.Equal("Tubos › Conexões › Conexão hidráulica", entry.Hierarchy);
        Assert.True(entry.Active);
    }

    [Fact]
    public async Task CatalogSnapshot_IsAtomicDeactivatesMissingAndSearchesEquivalentMeasures()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCatalogRepository(database.Repository.DatabasePath);
        await repository.BeginSyncAsync(CatalogKind.Catmat, "primeira");
        await repository.StagePageAsync(new CatalogPage(
            CatalogKind.Catmat,
            1,
            1,
            2,
            [
                Entry("123456", "CONEXÃO PARA TUBO, DIÂMETRO: 25,4 MM"),
                Entry("999999", "CONEXÃO PARA TUBO, DIÂMETRO: 50 MM")
            ]), "primeira");
        await repository.PublishAsync(CatalogKind.Catmat, "primeira");

        var search = new CatalogSearchService(repository);
        var equivalent = await search.SearchAsync(new CatalogSearchQuery("conexão 1\"", CatalogKind.Catmat));
        var best = Assert.Single(equivalent.Results, result => result.Entry.Code == "123456");
        Assert.Contains(best.Signals, signal =>
            signal.State == CatalogMatchState.Match &&
            signal.Explanation.Contains("conversão", StringComparison.OrdinalIgnoreCase));

        await repository.BeginSyncAsync(CatalogKind.Catmat, "incompleta");
        await repository.StagePageAsync(new CatalogPage(
            CatalogKind.Catmat,
            1,
            1,
            2,
            [Entry("123456", "CONEXÃO PARA TUBO, DIÂMETRO: 25,4 MM")]), "incompleta");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.PublishAsync(CatalogKind.Catmat, "incompleta"));
        Assert.True((await repository.GetEntryAsync(CatalogKind.Catmat, "999999"))!.Active);

        await repository.BeginSyncAsync(CatalogKind.Catmat, "segunda");
        await repository.StagePageAsync(new CatalogPage(
            CatalogKind.Catmat,
            1,
            1,
            1,
            [Entry("123456", "CONEXÃO PARA TUBO, DIÂMETRO: 25,4 MM")]), "segunda");
        await repository.PublishAsync(CatalogKind.Catmat, "segunda");
        Assert.False((await repository.GetEntryAsync(CatalogKind.Catmat, "999999"))!.Active);
        var inactiveSearch = await search.SearchAsync(new CatalogSearchQuery("999999", CatalogKind.Catmat));
        Assert.DoesNotContain(inactiveSearch.Results, result => result.Entry.Code == "999999");
    }

    [Fact]
    public async Task EquivalenceDictionary_RejectsAmbiguousAliasAndRestoresDefaults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCatalogRepository(database.Repository.DatabasePath);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveEquivalenceRuleAsync(
            new CatalogEquivalenceRule
            {
                Id = Guid.NewGuid(),
                Kind = CatalogRuleKind.Alias,
                Canonical = "OUTRO GRUPO",
                Alias = "UN",
                Factor = 1m
            }));

        var defaultRule = (await repository.GetEquivalenceRulesAsync()).First(rule => rule.Alias == "POL");
        await repository.DeleteEquivalenceRuleAsync(defaultRule.Id);
        Assert.DoesNotContain(await repository.GetEquivalenceRulesAsync(), rule => rule.Alias == "POL");
        await repository.ResetDefaultEquivalenceRulesAsync();
        Assert.Contains(await repository.GetEquivalenceRulesAsync(), rule => rule.Alias == "POL");
    }

    private static CatalogEntry Entry(string code, string description) => new()
    {
        Kind = CatalogKind.Catmat,
        Code = code,
        Description = description,
        Level1Code = "10",
        Level1Name = "Tubos",
        Level2Code = "11",
        Level2Name = "Conexões",
        Level3Code = "12",
        Level3Name = "Conexão hidráulica",
        SearchText = description
    };

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public Uri? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
