using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Api;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationItemSearchService(
    IContractRepository contracts,
    IQuotationItemSearchRepository workspaces,
    ItemSearchSessionService itemSearch) : IQuotationItemSearchService
{
    private const int CandidatePageSize = 200;

    public async Task<QuotationItemSearchState> LoadAsync(
        QuotationItemSearchWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var stored = await workspaces.GetWorkspaceAsync(
                workspace.LineId,
                workspace.Slot,
                cancellationToken)
            .ConfigureAwait(false) ?? workspace;
        var hits = await workspaces.GetWorkspaceHitsAsync(
                workspace.LineId,
                workspace.Slot,
                cancellationToken)
            .ConfigureAwait(false);
        var rows = await BuildRowsAsync(stored, hits, cancellationToken).ConfigureAwait(false);
        return new QuotationItemSearchState(stored, rows);
    }

    public async Task<ItemSearchLocalSummary> GetLocalSummaryAsync(
        QuotationItemSearchWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var expression = SearchText.Parse(workspace.SearchText);
        return await contracts.GetItemSearchLocalSummaryAsync(
                BuildQuery(workspace),
                expression,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SavePreferencesAsync(
        QuotationItemSearchWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Validate(workspace);
        var stored = await workspaces.GetWorkspaceAsync(
                workspace.LineId,
                workspace.Slot,
                cancellationToken)
            .ConfigureAwait(false);
        var contractCandidatesChanged = stored is not null &&
                                        !string.Equals(
                                            ContractCandidateKey(stored),
                                            ContractCandidateKey(workspace),
                                            StringComparison.Ordinal);
        await workspaces.SaveWorkspaceAsync(stored is null
                ? workspace
                : stored with
                {
                    SearchText = workspace.SearchText,
                    GeoFilter = workspace.GeoFilter,
                    StartDate = workspace.StartDate,
                    EndDate = workspace.EndDate,
                    Sort = workspace.Sort,
                    MinimumUnitPrice = workspace.MinimumUnitPrice,
                    MaximumUnitPrice = workspace.MaximumUnitPrice,
                    BatchCount = workspace.BatchCount,
                    Checkpoint = contractCandidatesChanged
                        ? new QuotationItemSearchCheckpoint
                        {
                            RandomPivot = Random.Shared.NextInt64(1, long.MaxValue)
                        }
                        : stored.Checkpoint,
                    StatusMessage = contractCandidatesChanged
                        ? "Crivos de contratações alterados; candidatas reiniciadas sem remover resultados coletados."
                        : stored.StatusMessage,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
            cancellationToken).ConfigureAwait(false);
        if (contractCandidatesChanged)
        {
            var failures = await workspaces.GetWorkspaceFailuresAsync(
                    workspace.LineId,
                    workspace.Slot,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var failure in failures)
            {
                await workspaces.RemoveWorkspaceFailureAsync(
                        workspace.LineId,
                        workspace.Slot,
                        failure.ContractId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task<QuotationItemSearchState> RunAsync(
        QuotationItemSearchWorkspace workspace,
        bool restart,
        IProgress<QuotationItemSearchProgress>? progress = null,
        IProgress<IReadOnlyList<ItemSearchRow>>? rowProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var expression = SearchText.Parse(workspace.SearchText);
        Validate(workspace);
        if (restart)
        {
            workspace = workspace with
            {
                Checkpoint = new QuotationItemSearchCheckpoint
                {
                    RandomPivot = Random.Shared.NextInt64(1, long.MaxValue)
                },
                MatchedItems = 0,
                RevealedPrices = 0,
                ItemListsFromCache = 0,
                ItemListsFromApi = 0,
                ItemResultApiCalls = 0,
                FailedCalls = 0,
                ExpandedContracts = 0,
                FullyResolvedContracts = 0,
                StatusMessage = "Pesquisa reiniciada.",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await workspaces.ResetWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var stored = await workspaces.GetWorkspaceAsync(
                    workspace.LineId,
                    workspace.Slot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stored is not null)
            {
                workspace = stored with
                {
                    MinimumUnitPrice = workspace.MinimumUnitPrice,
                    MaximumUnitPrice = workspace.MaximumUnitPrice,
                    BatchCount = workspace.BatchCount,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            else if (workspace.Checkpoint.RandomPivot == 0)
            {
                workspace = workspace with
                {
                    Checkpoint = workspace.Checkpoint with
                    {
                        RandomPivot = Random.Shared.NextInt64(1, long.MaxValue)
                    }
                };
            }

            await workspaces.SaveWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        }

        var pendingFailures = await workspaces.GetWorkspaceFailuresAsync(
                workspace.LineId,
                workspace.Slot,
                cancellationToken)
            .ConfigureAwait(false);
        if (workspace.Checkpoint.CandidateSetExhausted && pendingFailures.Count == 0)
        {
            progress?.Report(CreateProgress(
                workspace,
                0,
                0,
                "O conjunto de contratações deste prompt já foi esgotado."));
            return await LoadAsync(workspace, cancellationToken).ConfigureAwait(false);
        }

        var requestedContracts = checked(workspace.BatchCount * ItemSearchDefaults.ContractsPerBatch);
        var expandedThisRun = 0;
        var examinedThisRun = 0;
        var query = BuildQuery(workspace);
        while (expandedThisRun < requestedContracts &&
               !workspace.Checkpoint.CandidateSetExhausted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await contracts.SearchItemCandidatesAsync(
                    query,
                    expression,
                    workspace.Checkpoint.RandomPivot,
                    workspace.Checkpoint.Cursor,
                    CandidatePageSize,
                    cancellationToken)
                .ConfigureAwait(false);
            if (page.Results.Count == 0)
            {
                workspace = workspace with
                {
                    Checkpoint = workspace.Checkpoint with { CandidateSetExhausted = true },
                    StatusMessage = "Conjunto de contratações esgotado.",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await workspaces.SaveWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
                break;
            }

            foreach (var candidate in page.Results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expandedThisRun >= requestedContracts)
                {
                    break;
                }

                var level = workspace.Slot switch
                {
                    ItemSearchPromptSlot.Restrictive => PromptMatchLevel.Restrictive,
                    ItemSearchPromptSlot.Intermediate => PromptMatchLevel.Intermediate,
                    _ => PromptMatchLevel.Broad
                };
                var evaluated = await itemSearch.EvaluateContractAsync(
                        candidate.Contract,
                        [new ContractItemPrompt(workspace.LineId, level, workspace.SearchText)],
                        cancellationToken,
                        PncpRequestPriority.VisiblePrices)
                    .ConfigureAwait(false);
                var matchedRows = evaluated.RowsByLine.TryGetValue(workspace.LineId, out var values)
                    ? values.Select(row => row with
                    {
                        MatchedPromptLevel = workspace.Slot == ItemSearchPromptSlot.Custom
                            ? null
                            : level,
                        MatchedSearchText = workspace.SearchText
                    }).ToArray()
                    : [];
                examinedThisRun++;
                var usedNetwork = evaluated.ItemListsFromApi > 0 ||
                                  evaluated.ItemResultApiCalls > 0 ||
                                  evaluated.FailedCalls > 0;
                if (usedNetwork)
                {
                    expandedThisRun++;
                }
                var contractsExamined = checked(workspace.Checkpoint.ContractsExamined + 1);
                var distinctHits = matchedRows
                    .GroupBy(row => (row.Contract.PncpId, row.Item.ItemNumber))
                    .Select(group => new QuotationItemSearchHit
                    {
                        LineId = workspace.LineId,
                        Slot = workspace.Slot,
                        ContractId = group.Key.PncpId,
                        ItemNumber = group.Key.ItemNumber,
                        MatchedPromptLevel = workspace.Slot == ItemSearchPromptSlot.Custom
                            ? null
                            : level,
                        MatchedSearchText = workspace.SearchText,
                        DiscoveredOrder = checked((long)contractsExamined * 1_000_000L + group.Key.ItemNumber)
                    })
                    .ToArray();
                var isLastCandidate = ReferenceEquals(candidate, page.Results[^1]) ||
                                      candidate.Equals(page.Results[^1]);
                var exhausted = !page.HasMore && isLastCandidate;
                workspace = workspace with
                {
                    Checkpoint = workspace.Checkpoint with
                    {
                        Cursor = candidate.Cursor,
                        ContractsExamined = contractsExamined,
                        CandidateSetExhausted = exhausted
                    },
                    MatchedItems = checked(workspace.MatchedItems + evaluated.MatchedItems),
                    RevealedPrices = checked(workspace.RevealedPrices + evaluated.RevealedPrices),
                    ItemListsFromCache = checked(workspace.ItemListsFromCache + evaluated.ItemListsFromCache),
                    ItemListsFromApi = checked(workspace.ItemListsFromApi + evaluated.ItemListsFromApi),
                    ItemResultApiCalls = checked(workspace.ItemResultApiCalls + evaluated.ItemResultApiCalls),
                    FailedCalls = checked(workspace.FailedCalls + evaluated.FailedCalls),
                    ExpandedContracts = checked(workspace.ExpandedContracts + (usedNetwork ? 1 : 0)),
                    FullyResolvedContracts = checked(
                        workspace.FullyResolvedContracts + (usedNetwork ? 0 : 1)),
                    StatusMessage =
                        $"Contrato {candidate.Contract.PncpId}: {evaluated.MatchedItems:N0} item(ns), " +
                        $"{evaluated.RevealedPrices:N0} preço(s).",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await workspaces.SaveProcessedContractAsync(
                        workspace,
                        distinctHits,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (evaluated.FailedCalls > 0)
                {
                    await workspaces.SaveWorkspaceFailureAsync(
                            workspace.LineId,
                            workspace.Slot,
                            candidate.Contract.PncpId,
                            "Uma ou mais consultas do PNCP falharam após as tentativas automáticas.",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await workspaces.RemoveWorkspaceFailureAsync(
                            workspace.LineId,
                            workspace.Slot,
                            candidate.Contract.PncpId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                var visibleRows = ApplyPriceRange(
                    matchedRows,
                    workspace.MinimumUnitPrice,
                    workspace.MaximumUnitPrice);
                if (visibleRows.Count > 0)
                {
                    rowProgress?.Report(visibleRows);
                }

                progress?.Report(CreateProgress(
                    workspace,
                    requestedContracts,
                    expandedThisRun,
                    workspace.StatusMessage,
                    candidate.Contract.PncpId));
            }
        }

        if (pendingFailures.Count > 0)
        {
            workspace = await RetryPreviousFailuresAsync(
                    workspace,
                    pendingFailures.Take(ItemSearchDefaults.ContractsPerBatch).ToArray(),
                    rowProgress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var completedBatches = expandedThisRun == 0
            ? 0
            : (int)Math.Ceiling(expandedThisRun / (double)ItemSearchDefaults.ContractsPerBatch);
        workspace = workspace with
        {
            Checkpoint = workspace.Checkpoint with
            {
                BatchesCompleted = checked(workspace.Checkpoint.BatchesCompleted + completedBatches)
            },
            StatusMessage = workspace.Checkpoint.CandidateSetExhausted
                ? $"Conjunto esgotado após {workspace.Checkpoint.ContractsExamined:N0} contratação(ões)."
                : $"Ação concluída: {expandedThisRun:N0} contratação(ões) ampliada(s) e " +
                  $"{examinedThisRun:N0} candidata(s) examinada(s).",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await workspaces.SaveWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        progress?.Report(CreateProgress(
            workspace,
            requestedContracts,
            expandedThisRun,
            workspace.StatusMessage));
        return await LoadAsync(workspace, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QuotationItemSearchWorkspace> RetryPreviousFailuresAsync(
        QuotationItemSearchWorkspace workspace,
        IReadOnlyList<QuotationItemSearchFailure> failures,
        IProgress<IReadOnlyList<ItemSearchRow>>? rowProgress,
        CancellationToken cancellationToken)
    {
        var existingKeys = (await workspaces.GetWorkspaceHitsAsync(
                workspace.LineId,
                workspace.Slot,
                cancellationToken)
            .ConfigureAwait(false))
            .Select(hit => (hit.ContractId, hit.ItemNumber))
            .ToHashSet();
        var level = workspace.Slot switch
        {
            ItemSearchPromptSlot.Restrictive => PromptMatchLevel.Restrictive,
            ItemSearchPromptSlot.Intermediate => PromptMatchLevel.Intermediate,
            _ => PromptMatchLevel.Broad
        };

        foreach (var failure in failures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contract = await contracts.GetContractAsync(failure.ContractId, cancellationToken)
                .ConfigureAwait(false);
            if (contract is null)
            {
                await workspaces.RemoveWorkspaceFailureAsync(
                        workspace.LineId,
                        workspace.Slot,
                        failure.ContractId,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var evaluated = await itemSearch.EvaluateContractAsync(
                    contract,
                    [new ContractItemPrompt(workspace.LineId, level, workspace.SearchText)],
                    cancellationToken,
                    PncpRequestPriority.VisiblePrices)
                .ConfigureAwait(false);
            var matchedRows = evaluated.RowsByLine.TryGetValue(workspace.LineId, out var values)
                ? values.Select(row => row with
                {
                    MatchedPromptLevel = workspace.Slot == ItemSearchPromptSlot.Custom
                        ? null
                        : level,
                    MatchedSearchText = workspace.SearchText
                }).ToArray()
                : [];
            var distinctHits = matchedRows
                .GroupBy(row => (row.Contract.PncpId, row.Item.ItemNumber))
                .Select(group => new QuotationItemSearchHit
                {
                    LineId = workspace.LineId,
                    Slot = workspace.Slot,
                    ContractId = group.Key.PncpId,
                    ItemNumber = group.Key.ItemNumber,
                    MatchedPromptLevel = workspace.Slot == ItemSearchPromptSlot.Custom
                        ? null
                        : level,
                    MatchedSearchText = workspace.SearchText,
                    DiscoveredOrder = checked(
                        (long)Math.Max(1, workspace.Checkpoint.ContractsExamined) * 1_000_000L +
                        group.Key.ItemNumber)
                })
                .ToArray();
            var newKeys = distinctHits
                .Select(hit => (hit.ContractId, hit.ItemNumber))
                .Where(key => !existingKeys.Contains(key))
                .ToHashSet();
            var revealedDelta = matchedRows.Count(row =>
                newKeys.Contains((row.Contract.PncpId, row.Item.ItemNumber)) &&
                row.Result is { IsActive: true, HomologatedUnitValue: > 0 });
            workspace = workspace with
            {
                MatchedItems = checked(workspace.MatchedItems + newKeys.Count),
                RevealedPrices = checked(workspace.RevealedPrices + revealedDelta),
                ItemListsFromCache = checked(workspace.ItemListsFromCache + evaluated.ItemListsFromCache),
                ItemListsFromApi = checked(workspace.ItemListsFromApi + evaluated.ItemListsFromApi),
                ItemResultApiCalls = checked(workspace.ItemResultApiCalls + evaluated.ItemResultApiCalls),
                FailedCalls = checked(workspace.FailedCalls + evaluated.FailedCalls),
                StatusMessage = evaluated.FailedCalls == 0
                    ? $"Falha anterior resolvida na contratação {contract.PncpId}."
                    : $"A contratação {contract.PncpId} continua pendente após nova tentativa.",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await workspaces.SaveProcessedContractAsync(
                    workspace,
                    distinctHits,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var key in newKeys)
            {
                existingKeys.Add(key);
            }

            if (evaluated.FailedCalls == 0)
            {
                await workspaces.RemoveWorkspaceFailureAsync(
                        workspace.LineId,
                        workspace.Slot,
                        contract.PncpId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await workspaces.SaveWorkspaceFailureAsync(
                        workspace.LineId,
                        workspace.Slot,
                        contract.PncpId,
                        "A consulta continuou falhando após uma retomada posterior.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var visibleRows = ApplyPriceRange(
                matchedRows,
                workspace.MinimumUnitPrice,
                workspace.MaximumUnitPrice);
            if (visibleRows.Count > 0)
            {
                rowProgress?.Report(visibleRows);
            }
        }

        return workspace;
    }

    private async Task<IReadOnlyList<ItemSearchRow>> BuildRowsAsync(
        QuotationItemSearchWorkspace workspace,
        IReadOnlyList<QuotationItemSearchHit> hits,
        CancellationToken cancellationToken)
    {
        var rows = new List<ItemSearchRow>();
        var contractsById = new Dictionary<string, ContractRecord?>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!contractsById.TryGetValue(hit.ContractId, out var contract))
            {
                contract = await contracts.GetContractAsync(hit.ContractId, cancellationToken)
                    .ConfigureAwait(false);
                contractsById[hit.ContractId] = contract;
            }

            if (contract is null)
            {
                continue;
            }

            var cached = await contracts.GetCachedItemResultsAsync(
                    hit.ContractId,
                    hit.ItemNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cached is null)
            {
                continue;
            }

            foreach (var result in cached.Results)
            {
                rows.Add(new ItemSearchRow(
                    contract,
                    cached.Item,
                    result,
                    result.IsActive
                        ? ItemSearchPriceState.Homologated
                        : ItemSearchPriceState.Cancelled,
                    result.IsActive
                        ? "Preço homologado encontrado"
                        : "Resultado cancelado",
                    false,
                    hit.MatchedPromptLevel,
                    hit.MatchedSearchText));
            }
        }

        return ApplyPriceRange(
            rows,
            workspace.MinimumUnitPrice,
            workspace.MaximumUnitPrice);
    }

    private static IReadOnlyList<ItemSearchRow> ApplyPriceRange(
        IEnumerable<ItemSearchRow> rows,
        decimal? minimum,
        decimal? maximum) =>
        rows.Where(row =>
            row.HomologatedUnitValue is > 0 &&
            (row.Result is null ||
             !row.Result.IsActive ||
             (minimum is null || row.HomologatedUnitValue >= minimum) &&
             (maximum is null || row.HomologatedUnitValue <= maximum)))
        .ToArray();

    private static SearchQuery BuildQuery(QuotationItemSearchWorkspace workspace) =>
        new(
            workspace.SearchText,
            workspace.GeoFilter,
            workspace.StartDate,
            workspace.EndDate,
            workspace.Sort,
            1,
            CandidatePageSize);

    private static void Validate(QuotationItemSearchWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.SearchText))
        {
            throw new ArgumentException("Informe uma expressão de pesquisa.");
        }

        if (workspace.StartDate > workspace.EndDate)
        {
            throw new ArgumentException("A data inicial deve ser anterior ou igual à data final.");
        }

        if (workspace.BatchCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workspace),
                "A quantidade de lotes deve estar entre 1 e 100.");
        }

        if (workspace.MinimumUnitPrice < 0 ||
            workspace.MaximumUnitPrice < 0 ||
            workspace.MinimumUnitPrice is not null &&
            workspace.MaximumUnitPrice is not null &&
            workspace.MinimumUnitPrice > workspace.MaximumUnitPrice)
        {
            throw new ArgumentException("A faixa de preços é inválida.");
        }
    }

    private static string ContractCandidateKey(QuotationItemSearchWorkspace workspace) =>
        string.Join(
            '\u001F',
            new[]
            {
                string.Join(
                    '\u001E',
                    SearchText.Parse(workspace.SearchText).ContractCandidates.Select(candidate => candidate.Text)),
                ((int)workspace.GeoFilter.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
                workspace.GeoFilter.Uf ?? string.Empty,
                workspace.StartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                workspace.EndDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ((int)workspace.Sort).ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

    private static QuotationItemSearchProgress CreateProgress(
        QuotationItemSearchWorkspace workspace,
        int requested,
        int processed,
        string message,
        string currentContractId = "") =>
        new()
        {
            RequestedContracts = requested,
            ProcessedContracts = processed,
            CurrentContractId = currentContractId,
            ContractsExamined = workspace.Checkpoint.ContractsExamined,
            BatchesCompleted = workspace.Checkpoint.BatchesCompleted,
            MatchedItems = workspace.MatchedItems,
            RevealedPrices = workspace.RevealedPrices,
            ItemListsFromCache = workspace.ItemListsFromCache,
            ItemListsFromApi = workspace.ItemListsFromApi,
            ItemResultApiCalls = workspace.ItemResultApiCalls,
            FailedCalls = workspace.FailedCalls,
            ExpandedContracts = workspace.ExpandedContracts,
            FullyResolvedContracts = workspace.FullyResolvedContracts,
            CandidateSetExhausted = workspace.Checkpoint.CandidateSetExhausted,
            Message = message
        };
}
