using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
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
    public async Task CatalogDueKinds_ManualModeNeverStartsAutomatically()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCatalogRepository(database.Repository.DatabasePath);
        var service = new CatalogSyncService(new RecordingCatalogClient(), repository);

        Assert.Empty(await service.GetDueKindsAsync(null));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(15)]
    public async Task CatalogDueKinds_UsesConfiguredDayBoundary(int intervalDays)
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCatalogRepository(database.Repository.DatabasePath);
        await PublishCatalogKindAsync(repository, CatalogKind.Catmat, "material");
        await PublishCatalogKindAsync(repository, CatalogKind.Catser, "servico");
        var states = await repository.GetSyncStatesAsync();
        var oldest = states.MinBy(state => state.CompletedAt) ?? throw new InvalidOperationException();
        var interval = TimeSpan.FromDays(intervalDays);

        var beforeBoundary = new CatalogSyncService(
            new RecordingCatalogClient(),
            repository,
            new FixedTimeProvider(oldest.CompletedAt!.Value.Add(interval).AddTicks(-1)));
        Assert.Empty(await beforeBoundary.GetDueKindsAsync(interval));

        var atBoundary = new CatalogSyncService(
            new RecordingCatalogClient(),
            repository,
            new FixedTimeProvider(oldest.CompletedAt.Value.Add(interval)));
        Assert.Contains(oldest.Kind, await atBoundary.GetDueKindsAsync(interval));
    }

    [Fact]
    public async Task CatalogDueKinds_SelectsInterruptedCatserWithoutRepeatingFreshCatmat()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCatalogRepository(database.Repository.DatabasePath);
        await PublishCatalogKindAsync(repository, CatalogKind.Catmat, "material-pronto");
        await PublishCatalogKindAsync(repository, CatalogKind.Catser, "servico-pronto");
        await repository.BeginSyncAsync(CatalogKind.Catser, "servico-interrompido");
        var states = await repository.GetSyncStatesAsync();
        var now = states.Max(state => state.CompletedAt)!.Value.AddHours(1);
        var service = new CatalogSyncService(
            new RecordingCatalogClient(),
            repository,
            new FixedTimeProvider(now));

        Assert.Equal(
            [CatalogKind.Catser],
            await service.GetDueKindsAsync(TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task CatalogSync_SelectsAutomaticKindsButManualUpdateStillForcesBoth()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCatalogRepository(database.Repository.DatabasePath);
        var client = new RecordingCatalogClient();
        var service = new CatalogSyncService(client, repository);

        await service.SynchronizeAsync([CatalogKind.Catser]);
        Assert.Equal([CatalogKind.Catser], client.RequestedKinds);

        client.RequestedKinds.Clear();
        await service.SynchronizeAsync();
        Assert.Equal([CatalogKind.Catmat, CatalogKind.Catser], client.RequestedKinds);
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
        var publishedDuringDownload = await search.SearchAsync(
            new CatalogSearchQuery("999999", CatalogKind.Catmat));
        Assert.Equal("999999", Assert.Single(publishedDuringDownload.Results).Entry.Code);
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

    [Fact]
    public async Task CatalogSearch_UsesFullGrammarAgainstOfficialDescriptionOnly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCatalogRepository(database.Repository.DatabasePath);
        await repository.BeginSyncAsync(CatalogKind.Catmat, "grammar");
        await repository.StagePageAsync(new CatalogPage(
            CatalogKind.Catmat,
            1,
            1,
            5,
            [
                Entry("100001", "CAFÉ TORRADO EM GRÃOS 500 G"),
                Entry("100002", "CAFÉ TORRADO EM CÁPSULA 500 G"),
                Entry("100003", "CHÁ VERDE EM FOLHAS"),
                Entry("100004", "MALTE DE CEVADA", "CAFÉ, CHÁ E CHOCOLATE"),
                Entry("100005", "CAFETEIRA ELÉTRICA")
            ]), "grammar");
        await repository.PublishAsync(CatalogKind.Catmat, "grammar");
        var search = new CatalogSearchService(repository);

        var conjunction = await search.SearchAsync(new CatalogSearchQuery(
            "cafe + torrado -capsula",
            CatalogKind.Catmat));
        var alternatives = await search.SearchAsync(new CatalogSearchQuery(
            "\"café torrado\" OU \"chá verde\" -cápsula",
            CatalogKind.Catmat));
        var code = await search.SearchAsync(new CatalogSearchQuery("100004", CatalogKind.Catmat));

        Assert.Equal("100001", Assert.Single(conjunction.Results).Entry.Code);
        Assert.Equal(["100001", "100003"], alternatives.Results.Select(result => result.Entry.Code).Order().ToArray());
        Assert.Equal("100004", Assert.Single(code.Results).Entry.Code);
        Assert.DoesNotContain(
            (await search.SearchAsync(new CatalogSearchQuery("café", CatalogKind.Catmat))).Results,
            result => result.Entry.Code == "100004");
        Assert.DoesNotContain(conjunction.Results, result => result.Entry.Code == "100005");
    }

    [Fact]
    public async Task CatalogSearch_AppliesSameOperatorsToCatserAndBuildsDescriptionIndexInBatches()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCatalogRepository(database.Repository.DatabasePath);
        await repository.BeginSyncAsync(CatalogKind.Catser, "services");
        await repository.StagePageAsync(new CatalogPage(
            CatalogKind.Catser,
            1,
            1,
            3,
            [
                Entry("200001", "SERVIÇO DE TORREFAÇÃO DE CAFÉ", kind: CatalogKind.Catser),
                Entry("200002", "SERVIÇO DE CAFÉ EM CÁPSULA", kind: CatalogKind.Catser),
                Entry("200003", "SERVIÇO DE MANUTENÇÃO DE CAFETEIRA", kind: CatalogKind.Catser)
            ]), "services");
        await repository.PublishAsync(CatalogKind.Catser, "services");
        CatalogDescriptionIndexProgress progress;
        do
        {
            progress = await repository.BuildDescriptionIndexBatchAsync(2);
        } while (!progress.Completed);

        var page = await new CatalogSearchService(repository).SearchAsync(new CatalogSearchQuery(
            "serviço + café -cápsula",
            CatalogKind.Catser));

        Assert.Equal("200001", Assert.Single(page.Results).Entry.Code);
        Assert.True((await repository.GetDescriptionIndexProgressAsync()).Completed);
    }

    [Fact]
    public async Task HierarchyChildren_ReturnsOnlyOneLevelAtATime()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCatalogRepository(database.Repository.DatabasePath);
        await repository.BeginSyncAsync(CatalogKind.Catmat, "hierarchy");
        await repository.StagePageAsync(new CatalogPage(
            CatalogKind.Catmat,
            1,
            1,
            2,
            [
                Entry("300001", "TUBO A") with
                {
                    Level1Code = "10", Level1Name = "Materiais",
                    Level2Code = "11", Level2Name = "Tubos",
                    Level3Code = "111", Level3Name = "PVC"
                },
                Entry("300002", "CONEXÃO B") with
                {
                    Level1Code = "10", Level1Name = "Materiais",
                    Level2Code = "12", Level2Name = "Conexões",
                    Level3Code = "121", Level3Name = "Metálicas"
                }
            ]), "hierarchy");
        await repository.PublishAsync(CatalogKind.Catmat, "hierarchy");

        var roots = await repository.GetHierarchyChildrenAsync(CatalogKind.Catmat);
        var children = await repository.GetHierarchyChildrenAsync(
            CatalogKind.Catmat,
            Assert.Single(roots).Filter);

        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.Equal(2, child.Level));
        Assert.All(children, child => Assert.True(child.HasChildren));
    }

    [Fact]
    public async Task Migration16To17_CheckpointsExistingCatalogWithoutRebuildingDuringStartup()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCatalogRepository(database.Repository.DatabasePath);
        await repository.BeginSyncAsync(CatalogKind.Catmat, "legacy");
        await repository.StagePageAsync(new CatalogPage(
            CatalogKind.Catmat,
            1,
            1,
            2,
            [Entry("400001", "CAFÉ TORRADO"), Entry("400002", "CHÁ VERDE")]), "legacy");
        await repository.PublishAsync(CatalogKind.Catmat, "legacy");
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TRIGGER catalog_description_fts_insert;
                DROP TRIGGER catalog_description_fts_delete;
                DROP TRIGGER catalog_description_fts_update;
                DROP TABLE catalog_description_index_state;
                DROP TABLE catalog_description_fts;
                UPDATE schema_info SET version = 16 WHERE id = 1;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        await new SqliteContractRepository(database.Repository.DatabasePath).InitializeAsync();
        var pending = await new SqliteCatalogRepository(database.Repository.DatabasePath)
            .GetDescriptionIndexProgressAsync();

        Assert.False(pending.Completed);
        Assert.Equal(0, pending.IndexedRowId);
        Assert.True(pending.TargetRowId >= 2);
    }

    private static async Task PublishCatalogKindAsync(
        ICatalogRepository repository,
        CatalogKind kind,
        string generation)
    {
        await repository.BeginSyncAsync(kind, generation);
        await repository.StagePageAsync(new CatalogPage(
            kind,
            1,
            1,
            1,
            [Entry(kind == CatalogKind.Catmat ? "100001" : "200001", "ITEM OFICIAL", kind: kind)]),
            generation);
        await repository.PublishAsync(kind, generation);
    }

    private static CatalogEntry Entry(
        string code,
        string description,
        string level1Name = "Tubos",
        CatalogKind kind = CatalogKind.Catmat) => new()
    {
        Kind = kind,
        Code = code,
        Description = description,
        Level1Code = "10",
        Level1Name = level1Name,
        Level2Code = "11",
        Level2Name = "Conexões",
        Level3Code = "12",
        Level3Name = "Conexão hidráulica",
        SearchText = SearchText.Normalize($"{description} {level1Name}")
    };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingCatalogClient : IComprasCatalogClient
    {
        public List<CatalogKind> RequestedKinds { get; } = [];

        public Task<CatalogPage> GetPageAsync(
            CatalogKind kind,
            int page,
            int pageSize = 500,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedKinds.Add(kind);
            return Task.FromResult(new CatalogPage(
                kind,
                1,
                1,
                1,
                [Entry(kind == CatalogKind.Catmat ? "100001" : "200001", "ITEM OFICIAL", kind: kind)]));
        }
    }

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
