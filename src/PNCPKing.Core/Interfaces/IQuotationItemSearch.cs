using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IQuotationItemSearchRepository
{
    Task<QuotationItemSearchWorkspace?> GetWorkspaceAsync(
        Guid lineId,
        ItemSearchPromptSlot slot,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuotationItemSearchHit>> GetWorkspaceHitsAsync(
        Guid lineId,
        ItemSearchPromptSlot slot,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuotationItemSearchFailure>> GetWorkspaceFailuresAsync(
        Guid lineId,
        ItemSearchPromptSlot slot,
        CancellationToken cancellationToken = default);
    Task SaveWorkspaceAsync(
        QuotationItemSearchWorkspace workspace,
        CancellationToken cancellationToken = default);
    Task SaveProcessedContractAsync(
        QuotationItemSearchWorkspace workspace,
        IReadOnlyList<QuotationItemSearchHit> hits,
        CancellationToken cancellationToken = default);
    Task ResetWorkspaceAsync(
        QuotationItemSearchWorkspace workspace,
        CancellationToken cancellationToken = default);
    Task SaveWorkspaceFailureAsync(
        Guid lineId,
        ItemSearchPromptSlot slot,
        string contractId,
        string error,
        CancellationToken cancellationToken = default);
    Task RemoveWorkspaceFailureAsync(
        Guid lineId,
        ItemSearchPromptSlot slot,
        string contractId,
        CancellationToken cancellationToken = default);
}

public interface IQuotationItemSearchService
{
    Task<QuotationItemSearchState> LoadAsync(
        QuotationItemSearchWorkspace workspace,
        CancellationToken cancellationToken = default);
    Task<ItemSearchLocalSummary> GetLocalSummaryAsync(
        QuotationItemSearchWorkspace workspace,
        CancellationToken cancellationToken = default);
    Task SavePreferencesAsync(
        QuotationItemSearchWorkspace workspace,
        CancellationToken cancellationToken = default);
    Task<QuotationItemSearchState> RunAsync(
        QuotationItemSearchWorkspace workspace,
        bool restart,
        IProgress<QuotationItemSearchProgress>? progress = null,
        IProgress<IReadOnlyList<ItemSearchRow>>? rowProgress = null,
        CancellationToken cancellationToken = default);
}
