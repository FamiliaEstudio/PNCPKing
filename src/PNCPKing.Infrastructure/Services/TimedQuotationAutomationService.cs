using System.Diagnostics;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

/// <summary>
/// Resumable contract-centric automation. A unique contract is opened once and
/// compared locally with every unresolved quotation line.
/// </summary>
public sealed class TimedQuotationAutomationService(
    IContractRepository contracts,
    ItemSearchSessionService itemSearch,
    QuotationService quotations) : ITimedQuotationAutomationService
{
    private const int StrategyVersion = 3;

    public async Task RunAsync(
        QuotationAutomationRun run,
        IProgress<TimedQuotationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Mode != QuotationAutomationMode.TimedRoundRobin)
        {
            throw new ArgumentException("A execução informada não usa o modo temporal.", nameof(run));
        }

        if (run.StrategyVersion < StrategyVersion)
        {
            await quotations.UpgradeContractSearchStrategyAsync(
                run.Id,
                StrategyVersion,
                cancellationToken).ConfigureAwait(false);
            run = run with { StrategyVersion = StrategyVersion };
        }

        var stopwatch = Stopwatch.StartNew();
        var activeAtStart = run.ActiveElapsed;
        var processed = (await quotations.GetProcessedContractsAsync(run.Id, cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(value => value.ContractId, StringComparer.Ordinal);
        var initialAnalyses = (await quotations.GetAnalysesAsync(run.ProjectId, cancellationToken)
                .ConfigureAwait(false))
            .Where(value => value.Line.AutomationRunId == run.Id)
            .ToArray();
        var totals = new MutableTotals(
            run,
            processed.Count,
            initialAnalyses.Where(IsStrictlyResolved).Select(value => value.Line.Id));
        var batchNumber = processed.Count / ItemSearchDefaults.ContractsPerBatch;
        var lastProgressAt = DateTimeOffset.MinValue;

        await quotations.UpdateAutomationRunStateAsync(
            run.Id,
            QuotationAutomationRunState.Running,
            "Pesquisa por contratações relacionadas em andamento.",
            cancellationToken).ConfigureAwait(false);
        foreach (var analysis in initialAnalyses.Where(value => !IsStrictlyResolved(value)))
        {
            await quotations.UpdateAutomationItemStateAsync(
                analysis.Line.Id,
                QuotationAutomationItemState.Pending,
                "Pesquisa por contratações relacionadas em andamento.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var pendingRevalidations = await quotations.GetPendingPromptRevalidationsAsync(
                run.Id,
                cancellationToken).ConfigureAwait(false);
            if (pendingRevalidations.Count > 0)
            {
                if (processed.Count > 0)
                {
                    await ReevaluateCachedContractsAsync(
                        run,
                        pendingRevalidations,
                        processed.Values,
                        totals,
                        progress,
                        activeAtStart + stopwatch.Elapsed,
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (var promptSet in pendingRevalidations)
                {
                    await quotations.MarkPromptRevalidatedAsync(
                        run.Id,
                        promptSet.LineId,
                        promptSet.Version,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            while (activeAtStart + stopwatch.Elapsed < run.TimeBudget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var analyses = await LoadUnresolvedAsync(run, cancellationToken).ConfigureAwait(false);
                if (analyses.Count == 0)
                {
                    break;
                }

                var promptSets = analyses.ToDictionary(
                    value => value.Line.Id,
                    value => value.Line.PromptSet ??
                             new ItemSearchPromptSet
                             {
                                 LineId = value.Line.Id,
                                 RestrictiveText = value.Line.SearchText,
                                 Origin = SearchPromptOrigin.Migrated
                             });
                var globalPrompts = (await quotations.GetContractSearchPromptsAsync(run.Id, cancellationToken)
                        .ConfigureAwait(false))
                    .ToList();
                if (globalPrompts.Count == 0)
                {
                    globalPrompts.AddRange(await EnsureFallbackPromptsAsync(
                        run,
                        promptSets.Values,
                        globalPrompts,
                        cancellationToken).ConfigureAwait(false));
                }

                var batch = await LoadNextUniqueBatchAsync(
                    run,
                    globalPrompts,
                    processed.Keys,
                    cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    globalPrompts = (await quotations.GetContractSearchPromptsAsync(
                            run.Id,
                            cancellationToken)
                        .ConfigureAwait(false)).ToList();
                    var addedFallbacks = await EnsureFallbackPromptsAsync(
                        run,
                        promptSets.Values,
                        globalPrompts,
                        cancellationToken).ConfigureAwait(false);
                    if (addedFallbacks.Count > 0)
                    {
                        continue;
                    }

                    break;
                }

                batchNumber++;
                for (var contractIndex = 0; contractIndex < batch.Count; contractIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (activeAtStart + stopwatch.Elapsed >= run.TimeBudget)
                    {
                        break;
                    }

                    analyses = await LoadUnresolvedAsync(run, cancellationToken).ConfigureAwait(false);
                    if (analyses.Count == 0)
                    {
                        break;
                    }

                    promptSets = analyses.ToDictionary(
                        value => value.Line.Id,
                        value => value.Line.PromptSet!);
                    var candidate = batch[contractIndex];
                    var itemPrompts = BuildItemPrompts(promptSets.Values);
                    if (DateTimeOffset.UtcNow - lastProgressAt >= TimeSpan.FromMilliseconds(250))
                    {
                        Report(
                            progress,
                            totals,
                            activeAtStart + stopwatch.Elapsed,
                            run.TimeBudget,
                            batchNumber,
                            contractIndex + 1,
                            batch.Count,
                            candidate.Contract.PncpId,
                            candidate.Prompt.Text,
                            promptSets.Values,
                            null,
                            $"Abrindo a contratação {contractIndex + 1:N0} de {batch.Count:N0} do lote.");
                        lastProgressAt = DateTimeOffset.UtcNow;
                    }

                    var evaluated = await itemSearch.EvaluateContractAsync(
                            candidate.Contract,
                            itemPrompts,
                            cancellationToken)
                        .ConfigureAwait(false);
                    totals.Add(evaluated);
                    foreach (var analysis in analyses)
                    {
                        if (!evaluated.RowsByLine.TryGetValue(analysis.Line.Id, out var rows) ||
                            rows.Count == 0)
                        {
                            continue;
                        }

                        var updated = await CaptureAndConfirmAsync(
                            run.ProjectId,
                            analysis.Line,
                            rows,
                            cancellationToken).ConfigureAwait(false);
                        if (IsStrictlyResolved(updated))
                        {
                            totals.MarkResolved(analysis.Line.Id);
                        }
                        await quotations.UpdateAutomationItemStateAsync(
                            analysis.Line.Id,
                            IsStrictlyResolved(updated)
                                ? QuotationAutomationItemState.Completed
                                : QuotationAutomationItemState.Pending,
                            IsStrictlyResolved(updated)
                                ? $"Cesta válida completa com {analysis.Line.RequestedBasketSize:N0} preços."
                                : $"{updated.EligibleCount:N0} referência(s) elegível(is); pesquisa continua.",
                            cancellationToken).ConfigureAwait(false);
                    }

                    var activated = new List<ItemSearchPromptSet>();
                    foreach (var analysis in analyses)
                    {
                        var set = promptSets[analysis.Line.Id];
                        var lineMatches = evaluated.RowsByLine.TryGetValue(analysis.Line.Id, out var lineRows)
                            ? lineRows.Count
                            : 0;
                        var level = set.ActiveLevel;
                        var contractsAtLevel = set.ContractsAtActiveLevel + 1;
                        if (contractsAtLevel >= ItemSearchDefaults.ContractsPerBatch &&
                            level < PromptMatchLevel.Broad &&
                            !string.IsNullOrWhiteSpace(set.GetText((PromptMatchLevel)((int)level + 1))))
                        {
                            level = (PromptMatchLevel)((int)level + 1);
                            contractsAtLevel = 0;
                            activated.Add(set with
                            {
                                ActiveLevel = level,
                                ContractsAtActiveLevel = 0
                            });
                        }

                        await quotations.UpdateItemSearchPromptProgressAsync(
                            set.LineId,
                            level,
                            contractsAtLevel,
                            checked(set.MatchedItems + lineMatches),
                            checked(set.RevealedPrices + CountRevealed(lineRows)),
                            cancellationToken).ConfigureAwait(false);
                    }

                    totals.ContractsWithoutResult = evaluated.RevealedPrices == 0
                        ? totals.ContractsWithoutResult + 1
                        : 0;
                    processed[candidate.Contract.PncpId] = new ContractSearchCheckpoint
                    {
                        RunId = run.Id,
                        ContractId = candidate.Contract.PncpId,
                        PromptOrder = candidate.Prompt.DisplayOrder,
                        ProcessedAt = DateTimeOffset.UtcNow,
                        MatchedItems = evaluated.MatchedItems,
                        RevealedPrices = evaluated.RevealedPrices
                    };
                    totals.UniqueContractsProcessed = processed.Count;
                    var snapshot = CreateProgress(
                        totals,
                        activeAtStart + stopwatch.Elapsed,
                        run.TimeBudget,
                        batchNumber,
                        contractIndex + 1,
                        batch.Count,
                        candidate.Contract.PncpId,
                        candidate.Prompt.Text,
                        promptSets.Values,
                        evaluated.RowsByLine.Keys.FirstOrDefault(),
                        $"{processed.Count:N0} contratação(ões) única(s) processada(s).");
                    await quotations.SaveProcessedContractAsync(
                        processed[candidate.Contract.PncpId],
                        snapshot,
                        CancellationToken.None).ConfigureAwait(false);
                    await quotations.UpdateAutomationTimingAsync(
                        run.Id,
                        activeAtStart + stopwatch.Elapsed,
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    if (snapshot.UpdatedLineId is { } updatedLineId && updatedLineId != Guid.Empty ||
                        DateTimeOffset.UtcNow - lastProgressAt >= TimeSpan.FromMilliseconds(250))
                    {
                        progress?.Report(snapshot);
                        lastProgressAt = DateTimeOffset.UtcNow;
                    }

                    if (activated.Count > 0)
                    {
                        await ReevaluateCachedContractsAsync(
                            run,
                            activated,
                            processed.Values,
                            totals,
                            progress,
                            activeAtStart + stopwatch.Elapsed,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            stopwatch.Stop();
            var elapsed = activeAtStart + stopwatch.Elapsed;
            await quotations.UpdateAutomationTimingAsync(
                run.Id,
                elapsed,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var unresolved = await LoadUnresolvedAsync(run, cancellationToken).ConfigureAwait(false);
            if (unresolved.Count == 0)
            {
                await quotations.UpdateAutomationRunStateAsync(
                    run.Id,
                    QuotationAutomationRunState.Completed,
                    "Todos os itens alcançaram cestas completas, elegíveis e com desvio de até 25%.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            foreach (var analysis in unresolved)
            {
                await quotations.UpdateAutomationItemStateAsync(
                    analysis.Line.Id,
                    QuotationAutomationItemState.TimeExpired,
                    elapsed >= run.TimeBudget
                        ? "Prazo encerrado; resultados parciais preservados."
                        : "Contratações relacionadas esgotadas; resultados parciais preservados.",
                    cancellationToken).ConfigureAwait(false);
            }

            await quotations.UpdateAutomationRunStateAsync(
                run.Id,
                QuotationAutomationRunState.TimeExpired,
                elapsed >= run.TimeBudget
                    ? $"Prazo encerrado com {unresolved.Count:N0} item(ns) ainda sem cesta válida."
                    : $"Prompts de contratação esgotados com {unresolved.Count:N0} item(ns) sem cesta válida.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await quotations.UpdateAutomationTimingAsync(
                run.Id,
                activeAtStart + stopwatch.Elapsed,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await quotations.UpdateAutomationRunStateAsync(
                run.Id,
                QuotationAutomationRunState.Pending,
                "Execução pausada; o tempo parado não será contabilizado.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<List<PromptedContract>> LoadNextUniqueBatchAsync(
        QuotationAutomationRun run,
        IReadOnlyList<ContractSearchPrompt> prompts,
        IEnumerable<string> processedIds,
        CancellationToken cancellationToken)
    {
        var processed = processedIds.ToHashSet(StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PromptedContract>(ItemSearchDefaults.ContractsPerBatch);
        var states = prompts
            .GroupBy(value => SearchText.Normalize(value.Text), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(value => value.CandidateSetExhausted)
                .ThenByDescending(value => value.ContractsExamined)
                .ThenBy(value => value.DisplayOrder)
                .First())
            .ToList();
        while (result.Count < ItemSearchDefaults.ContractsPerBatch &&
               states.Any(value => !value.CandidateSetExhausted))
        {
            var advanced = false;
            var round = states
                .Where(value => !value.CandidateSetExhausted)
                .OrderBy(value => value.ContractsExamined)
                .ThenBy(value => value.DisplayOrder)
                .ToArray();
            foreach (var state in round)
            {
                if (result.Count >= ItemSearchDefaults.ContractsPerBatch)
                {
                    break;
                }

                var query = new SearchQuery(
                    state.Text,
                    run.GeoFilter,
                    run.StartDate,
                    run.EndDate,
                    SearchSort.Nearest,
                    1,
                    1);
                var page = await contracts.SearchContractCandidatesAsync(
                    query,
                    state.Text,
                    state.RandomPivot,
                    state.Cursor,
                    1,
                    cancellationToken).ConfigureAwait(false);
                var next = state with
                {
                    Cursor = page.NextCursor,
                    CandidateSetExhausted = !page.HasMore,
                    ContractsExamined = state.ContractsExamined + page.Results.Count
                };
                states[states.FindIndex(value => value.DisplayOrder == state.DisplayOrder)] = next;
                await quotations.SaveContractSearchPromptAsync(next, cancellationToken)
                    .ConfigureAwait(false);
                if (page.Results.Count == 0)
                {
                    continue;
                }

                advanced = true;
                var contract = page.Results[0].Contract;
                if (!processed.Contains(contract.PncpId) && selected.Add(contract.PncpId))
                {
                    result.Add(new PromptedContract(contract, next));
                }
            }

            if (!advanced)
            {
                break;
            }
        }

        return result;
    }

    private async Task<List<ContractSearchPrompt>> EnsureFallbackPromptsAsync(
        QuotationAutomationRun run,
        IEnumerable<ItemSearchPromptSet> promptSets,
        IReadOnlyList<ContractSearchPrompt> existingPrompts,
        CancellationToken cancellationToken)
    {
        var known = existingPrompts
            .Select(value => SearchText.Normalize(value.Text))
            .ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var promptSet in promptSets)
        {
            var source = string.IsNullOrWhiteSpace(promptSet.BroadText)
                ? promptSet.RestrictiveText
                : promptSet.BroadText;
            var text = IdentityPrompt(source);
            if (text.Length == 0 || !known.Add(SearchText.Normalize(text)))
            {
                continue;
            }

            missing.Add(text);
        }

        var nextOrder = Math.Max(
            1000,
            existingPrompts.Select(value => value.DisplayOrder).DefaultIfEmpty(999).Max() + 1);
        var result = new List<ContractSearchPrompt>(missing.Count);
        foreach (var text in missing)
        {
            var prompt = new ContractSearchPrompt
            {
                RunId = run.Id,
                DisplayOrder = nextOrder++,
                Text = text,
                RandomPivot = Random.Shared.NextInt64(1, long.MaxValue),
                IsFallback = true
            };
            await quotations.SaveContractSearchPromptAsync(prompt, cancellationToken)
                .ConfigureAwait(false);
            result.Add(prompt);
        }

        return result;
    }

    private async Task ReevaluateCachedContractsAsync(
        QuotationAutomationRun run,
        IReadOnlyList<ItemSearchPromptSet> activated,
        IEnumerable<ContractSearchCheckpoint> processed,
        MutableTotals totals,
        IProgress<TimedQuotationProgress>? progress,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var prompts = BuildItemPrompts(activated);
        foreach (var checkpoint in processed.OrderBy(value => value.ProcessedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contract = await contracts.GetContractAsync(checkpoint.ContractId, cancellationToken)
                .ConfigureAwait(false);
            if (contract is null)
            {
                continue;
            }

            var evaluated = await itemSearch.EvaluateContractAsync(contract, prompts, cancellationToken)
                .ConfigureAwait(false);
            totals.AddNetworkOnly(evaluated);
            foreach (var set in activated)
            {
                if (!evaluated.RowsByLine.TryGetValue(set.LineId, out var rows) || rows.Count == 0)
                {
                    continue;
                }

                var line = (await quotations.GetAnalysesAsync(run.ProjectId, cancellationToken)
                        .ConfigureAwait(false))
                    .Single(value => value.Line.Id == set.LineId)
                    .Line;
                var updated = await CaptureAndConfirmAsync(
                    run.ProjectId,
                    line,
                    rows,
                    cancellationToken).ConfigureAwait(false);
                if (IsStrictlyResolved(updated))
                {
                    totals.MarkResolved(set.LineId);
                }
            }

            progress?.Report(new TimedQuotationProgress
            {
                ActiveElapsed = elapsed,
                Remaining = run.TimeBudget > elapsed ? run.TimeBudget - elapsed : TimeSpan.Zero,
                UniqueContractsProcessed = totals.UniqueContractsProcessed,
                ItemListsFromCache = totals.ItemListsFromCache,
                ItemListsFromApi = totals.ItemListsFromApi,
                MatchedItems = totals.MatchedItems,
                RevealedPrices = totals.RevealedPrices,
                ItemResultCalls = totals.ItemResultCalls,
                FailedCalls = totals.FailedCalls,
                ContractsWithoutResult = totals.ContractsWithoutResult,
                CurrentContractId = contract.PncpId,
                Message = "Reavaliando listas já abertas com o novo nível, sem repetir a chamada da lista."
            });
        }
    }

    private async Task<QuotationLineAnalysis> CaptureAndConfirmAsync(
        Guid projectId,
        QuotationLine line,
        IReadOnlyList<ItemSearchRow> rows,
        CancellationToken cancellationToken)
    {
        var stages = line.UseEstimatedPrice && line.EstimatedUnitPrice is > 0
            ? new[]
            {
                EstimateResolutionStage.Within25Percent,
                EstimateResolutionStage.Within50Percent,
                EstimateResolutionStage.Unrestricted
            }
            : new[] { EstimateResolutionStage.NotApplicable };
        QuotationLineAnalysis? latest = null;
        foreach (var stage in stages)
        {
            var (minimum, maximum) = GetRange(line.EstimatedUnitPrice, stage);
            latest = await quotations.CaptureSampleAsync(
                projectId,
                line.Id,
                new QuotationLineInput(
                    line.Description,
                    line.RequestedQuantity,
                    line.RequestedUnit,
                    minimum,
                    maximum)
                {
                    Weights = line.Weights,
                    RequestedBasketSize = line.RequestedBasketSize
                },
                rows,
                cancellationToken).ConfigureAwait(false);
            var checkpoint = line.SearchCheckpoint with { EstimateStage = stage };
            await quotations.SaveSearchCheckpointAsync(line.Id, checkpoint, cancellationToken)
                .ConfigureAwait(false);
            if (IsStrictlyResolved(latest))
            {
                break;
            }
        }

        if (latest is null)
        {
            throw new InvalidOperationException("Nenhuma etapa de avaliação foi executada.");
        }
        var recommended = latest.Baskets
            .Where(value => !value.IsManual)
            .FirstOrDefault(value => value.IsRecommended);
        if (recommended is not null)
        {
            await quotations.ConfirmBasketAsync(latest, recommended.Key, cancellationToken)
                .ConfigureAwait(false);
        }

        return latest;
    }

    private async Task<IReadOnlyList<QuotationLineAnalysis>> LoadUnresolvedAsync(
        QuotationAutomationRun run,
        CancellationToken cancellationToken) =>
        (await quotations.GetAnalysesAsync(run.ProjectId, cancellationToken).ConfigureAwait(false))
        .Where(value => value.Line.AutomationRunId == run.Id)
        .Where(value => !IsStrictlyResolved(value))
        .OrderBy(value => value.Line.DisplayOrder)
        .ToArray();

    private static IReadOnlyList<ContractItemPrompt> BuildItemPrompts(
        IEnumerable<ItemSearchPromptSet> promptSets) =>
        promptSets
            .SelectMany(set => set.GetActivePrompts()
                .Select(value => new ContractItemPrompt(set.LineId, value.Level, value.Text)))
            .ToArray();

    private static string IdentityPrompt(string text)
    {
        var expression = SearchText.Parse(text);
        return string.Join(
            " ",
            expression.PositiveText
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(value => value.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .Take(3));
    }

    private static int CountRevealed(IReadOnlyList<ItemSearchRow>? rows) =>
        rows?.Count(value =>
            value.PriceState == ItemSearchPriceState.Homologated &&
            value.Result is { IsActive: true, HomologatedUnitValue: > 0 }) ?? 0;

    private static (decimal? Minimum, decimal? Maximum) GetRange(
        decimal? estimate,
        EstimateResolutionStage stage) =>
        estimate is not > 0 || stage is EstimateResolutionStage.NotApplicable or EstimateResolutionStage.Unrestricted
            ? (null, null)
            : stage == EstimateResolutionStage.Within25Percent
                ? (QuotationMoney.TruncateToCents(estimate.Value * 0.75m),
                    QuotationMoney.TruncateToCents(estimate.Value * 1.25m))
                : (QuotationMoney.TruncateToCents(estimate.Value * 0.50m),
                    QuotationMoney.TruncateToCents(estimate.Value * 1.50m));

    private static bool IsStrictlyResolved(QuotationLineAnalysis analysis) =>
        analysis.Baskets
            .Where(value => !value.IsManual)
            .Any(value =>
                value.References.Count == analysis.Line.RequestedBasketSize &&
                value.References.All(reference => reference.State == QuotationReferenceState.Eligible) &&
                value.MaximumDeviationPercent <= 25m);

    private static void Report(
        IProgress<TimedQuotationProgress>? progress,
        MutableTotals totals,
        TimeSpan elapsed,
        TimeSpan budget,
        int batch,
        int contractInBatch,
        int contractsInBatch,
        string contractId,
        string contractPrompt,
        IEnumerable<ItemSearchPromptSet> sets,
        Guid? updatedLineId,
        string message) =>
        progress?.Report(CreateProgress(
            totals,
            elapsed,
            budget,
            batch,
            contractInBatch,
            contractsInBatch,
            contractId,
            contractPrompt,
            sets,
            updatedLineId,
            message));

    private static TimedQuotationProgress CreateProgress(
        MutableTotals totals,
        TimeSpan elapsed,
        TimeSpan budget,
        int batch,
        int contractInBatch,
        int contractsInBatch,
        string contractId,
        string contractPrompt,
        IEnumerable<ItemSearchPromptSet> sets,
        Guid? updatedLineId,
        string message)
    {
        var values = sets.ToArray();
        return new TimedQuotationProgress
        {
            ActiveElapsed = elapsed,
            Remaining = budget > elapsed ? budget - elapsed : TimeSpan.Zero,
            BatchNumber = batch,
            ContractInBatch = contractInBatch,
            ContractsInBatch = contractsInBatch,
            CurrentContractId = contractId,
            CurrentContractPrompt = contractPrompt,
            UniqueContractsProcessed = totals.UniqueContractsProcessed,
            ItemListsFromCache = totals.ItemListsFromCache,
            ItemListsFromApi = totals.ItemListsFromApi,
            MatchedItems = totals.MatchedItems,
            RevealedPrices = totals.RevealedPrices,
            RestrictiveItems = values.Count(value => value.ActiveLevel == PromptMatchLevel.Restrictive),
            IntermediateItems = values.Count(value => value.ActiveLevel == PromptMatchLevel.Intermediate),
            BroadItems = values.Count(value => value.ActiveLevel == PromptMatchLevel.Broad),
            ResolvedItems = totals.ResolvedItems,
            ItemResultCalls = totals.ItemResultCalls,
            FailedCalls = totals.FailedCalls,
            ContractsWithoutResult = totals.ContractsWithoutResult,
            UpdatedLineId = updatedLineId,
            Message = message
        };
    }

    private sealed record PromptedContract(
        ContractRecord Contract,
        ContractSearchPrompt Prompt);

    private sealed class MutableTotals
    {
        private readonly HashSet<Guid> _resolvedLineIds;

        public MutableTotals(
            QuotationAutomationRun run,
            int processedCount,
            IEnumerable<Guid> resolvedLineIds)
        {
            _resolvedLineIds = resolvedLineIds.ToHashSet();
            UniqueContractsProcessed = Math.Max(processedCount, run.UniqueContractsProcessed);
            ItemListsFromCache = run.ItemListCacheHits;
            ItemListsFromApi = run.ItemListApiCalls;
            MatchedItems = run.MatchedItems;
            RevealedPrices = run.RevealedPrices;
            ItemResultCalls = run.ItemResultApiCalls;
            FailedCalls = run.FailedCalls;
            ContractsWithoutResult = run.ConsecutiveContractsWithoutResult;
            ResolvedItems = _resolvedLineIds.Count;
        }

        public int UniqueContractsProcessed { get; set; }
        public int ItemListsFromCache { get; private set; }
        public int ItemListsFromApi { get; private set; }
        public int MatchedItems { get; private set; }
        public int RevealedPrices { get; private set; }
        public int ItemResultCalls { get; private set; }
        public int FailedCalls { get; private set; }
        public int ContractsWithoutResult { get; set; }
        public int ResolvedItems { get; set; }

        public void MarkResolved(Guid lineId)
        {
            if (_resolvedLineIds.Add(lineId))
            {
                ResolvedItems = _resolvedLineIds.Count;
            }
        }

        public void Add(ContractEvaluationResult value)
        {
            ItemListsFromCache += value.ItemListsFromCache;
            ItemListsFromApi += value.ItemListsFromApi;
            MatchedItems += value.MatchedItems;
            RevealedPrices += value.RevealedPrices;
            ItemResultCalls += value.ItemResultApiCalls;
            FailedCalls += value.FailedCalls;
        }

        public void AddNetworkOnly(ContractEvaluationResult value)
        {
            ItemListsFromCache += value.ItemListsFromCache;
            ItemListsFromApi += value.ItemListsFromApi;
            ItemResultCalls += value.ItemResultApiCalls;
            FailedCalls += value.FailedCalls;
        }
    }
}
