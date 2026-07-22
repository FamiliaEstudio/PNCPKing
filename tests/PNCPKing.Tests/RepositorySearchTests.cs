using PNCPKing.Core.Models;

namespace PNCPKing.Tests;

public sealed class RepositorySearchTests
{
    [Fact]
    public async Task Search_IsAccentInsensitiveAndSupportsPrefixAndGeoScopes()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            Contract("sp", "Aquisição de café para escolas", "SP", 1),
            Contract("mg", "Medicamentos para saúde pública", "MG", 2),
            Contract("ba", "Aquisição de veículos", "BA", 3)
        ]);

        var cafe = await database.Repository.SearchAsync(new SearchQuery("cafe", GeoScope.All));
        var prefix = await database.Repository.SearchAsync(new SearchQuery("medic", GeoScope.All));
        var southeast = await database.Repository.SearchAsync(new SearchQuery(string.Empty, GeoScope.Southeast));
        var saoPaulo = await database.Repository.SearchAsync(new SearchQuery(string.Empty, GeoScope.State("SP")));

        Assert.Single(cafe.Results);
        Assert.Equal("sp", cafe.Results[0].PncpId);
        Assert.Single(prefix.Results);
        Assert.Equal("mg", prefix.Results[0].PncpId);
        Assert.Equal(2, southeast.Total);
        Assert.Single(saoPaulo.Results);
    }

    [Fact]
    public async Task UpdatingContractGlobalDate_MarksCachedItemsAsStale()
    {
        await using var database = await TestDatabase.CreateAsync();
        var original = Contract("sp", "Café", "SP", 1) with
        {
            GlobalUpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        await database.Repository.UpsertContractsAsync([original]);
        await database.Repository.UpsertItemsAsync("sp", [new ProcurementItem
        {
            ContractId = "sp",
            ItemNumber = 1,
            Description = "Café",
            Unit = "kg",
            HasResult = true,
            HydrationStatus = ItemHydrationStatus.Complete
        }], false);
        await database.Repository.ReplaceItemResultsAsync("sp", 1, [new HomologationResult
        {
            ContractId = "sp",
            ItemNumber = 1,
            ResultSequence = 1,
            SupplierName = "Fornecedor",
            HomologatedUnitValueScaled = DecimalScale.ToScaled(25.1234m),
            ResultStatusId = 1,
            ResultStatusName = "Informado"
        }]);

        await database.Repository.UpsertContractsAsync([original with
        {
            GlobalUpdatedAt = original.GlobalUpdatedAt.Value.AddDays(1)
        }]);

        var pending = await database.Repository.GetPendingItemsAsync("sp", false);
        var rows = await database.Repository.GetItemDisplayRowsAsync("sp");
        Assert.Single(pending);
        Assert.Equal(ItemHydrationStatus.Stale, pending[0].HydrationStatus);
        Assert.Single(rows);
        Assert.Equal(25.1234m, rows[0].HomologatedUnitValue);
        Assert.StartsWith("Preço desatualizado", rows[0].DisplayStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_PaginatesPriceBatchesOfTwentyWithoutOverlap()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = Enumerable.Range(1, 25)
            .Select(number => Contract($"contract-{number}", $"Material escolar {number}", "SP", number))
            .ToArray();
        await database.Repository.UpsertContractsAsync(contracts);

        var first = await database.Repository.SearchAsync(new SearchQuery(
            "material",
            GeoScope.All,
            Page: 1,
            PageSize: 20));
        var second = await database.Repository.SearchAsync(new SearchQuery(
            "material",
            GeoScope.All,
            Page: 2,
            PageSize: 20));

        Assert.Equal(25, first.Total);
        Assert.Equal(20, first.Results.Count);
        Assert.Equal(5, second.Results.Count);
        Assert.Empty(first.Results.Select(item => item.PncpId).Intersect(second.Results.Select(item => item.PncpId)));
    }

    internal static ContractRecord Contract(string id, string objectText, string uf, int sequence) => new()
    {
        PncpId = id,
        Cnpj = "ABC12345000199",
        PurchaseYear = 2026,
        PurchaseSequence = sequence,
        Object = objectText,
        Organization = "Órgão de Teste",
        Unit = "Unidade",
        Municipality = uf == "SP" ? "São Paulo" : "Cidade",
        Uf = uf,
        ModalityId = 6,
        ModalityName = "Pregão - Eletrônico",
        Status = "Divulgada",
        PublicationDate = new DateTimeOffset(2026, 6, sequence, 12, 0, 0, TimeSpan.Zero),
        GlobalUpdatedAt = new DateTimeOffset(2026, 6, sequence, 12, 0, 0, TimeSpan.Zero),
        TotalHomologatedScaled = DecimalScale.ToScaled(1000m)
    };
}
