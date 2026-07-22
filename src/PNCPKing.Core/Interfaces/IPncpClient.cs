using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IPncpClient
{
    Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default);

    Task<ContractPage> GetContractsPageAsync(
        DateOnly startDate,
        DateOnly endDate,
        long modalityId,
        string? uf,
        int page,
        int pageSize,
        SyncMode mode,
        CancellationToken cancellationToken = default);

    Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
        ContractRecord contract,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
        ContractRecord contract,
        long itemNumber,
        CancellationToken cancellationToken = default);
}
