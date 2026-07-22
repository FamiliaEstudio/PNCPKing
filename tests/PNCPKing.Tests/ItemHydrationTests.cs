using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class ItemHydrationTests
{
    [Fact]
    public async Task Hydration_PersistsEveryResultAndKeepsPerItemFailuresVisible()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("contract", "Material hospitalar", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var client = new FakePncpClient(contract);
        var service = new ItemHydrationService(client, database.Repository);

        await service.HydrateAsync(contract, false);

        var rows = await database.Repository.GetItemDisplayRowsAsync(contract.PncpId);
        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, row => row.ItemNumber == 1 && row.DisplayStatus == "Preço homologado encontrado" && row.HomologatedUnitValue == 12.3456m);
        Assert.Contains(rows, row => row.ItemNumber == 1 && row.DisplayStatus == "Resultado cancelado");
        Assert.Contains(rows, row => row.ItemNumber == 2 && row.DisplayStatus == "Item sem resultado homologado");
        Assert.Contains(rows, row => row.ItemNumber == 3 && row.DisplayStatus.StartsWith("Falha ao consultar", StringComparison.Ordinal));
        Assert.Equal(2, client.ResultCalls);
    }

    private sealed class FakePncpClient(ContractRecord contract) : IPncpClient
    {
        public int ResultCalls { get; private set; }

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([new Modality(6, "Pregão")]);

        public Task<ContractPage> GetContractsPageAsync(DateOnly startDate, DateOnly endDate, long modalityId, string? uf, int page, int pageSize, SyncMode mode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractPage([contract], 1, 1, 1, 1000, TimeSpan.FromMilliseconds(10)));

        public Task<int> GetItemCountAsync(ContractRecord value, CancellationToken cancellationToken = default) =>
            Task.FromResult(3);

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(ContractRecord value, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcurementItem>>([
                Item(1, true), Item(2, false), Item(3, true)
            ]);

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(ContractRecord value, long itemNumber, CancellationToken cancellationToken = default)
        {
            ResultCalls++;
            if (itemNumber == 3)
            {
                throw new HttpRequestException("Falha simulada");
            }

            return Task.FromResult<IReadOnlyList<HomologationResult>>([
                Result(1, 1, "Fornecedor vencedor", 12.3456m),
                Result(2, 2, "Fornecedor cancelado", 99m)
            ]);
        }

        private ProcurementItem Item(long number, bool hasResult) => new()
        {
            ContractId = contract.PncpId,
            ItemNumber = number,
            Description = $"Item {number}",
            Unit = "un",
            HasResult = hasResult,
            Status = hasResult ? "Homologado" : "Em andamento",
            HydrationStatus = hasResult ? ItemHydrationStatus.NotLoaded : ItemHydrationStatus.Complete
        };

        private HomologationResult Result(long sequence, int status, string supplier, decimal unitValue) => new()
        {
            ContractId = contract.PncpId,
            ItemNumber = 1,
            ResultSequence = sequence,
            SupplierName = supplier,
            HomologatedQuantityScaled = DecimalScale.ToScaled(2m),
            HomologatedUnitValueScaled = DecimalScale.ToScaled(unitValue),
            HomologatedTotalValueScaled = DecimalScale.ToScaled(unitValue * 2m),
            ResultDate = new DateOnly(2026, 6, 1),
            ResultStatusId = status,
            ResultStatusName = status == 1 ? "Informado" : "Cancelado"
        };
    }
}
