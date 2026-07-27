using System.Diagnostics;
using System.Collections.Concurrent;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Geography;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Api;

namespace PNCPKing.Infrastructure.Services;

/// <summary>
/// Lazily searches item descriptions inside a stable contract result set and keeps
/// automatically downloaded prices in a disposable SQLite database.
/// </summary>
public sealed class ItemSearchSessionService : IAsyncDisposable
{
    public const int DefaultPageSize = ItemSearchDefaults.ContractsPerBatch;
    public const int MaximumBatchCount = 100;
    public const int MaximumFreshItemListsPerAction = ItemSearchDefaults.ContractsPerBatch;

    private readonly IPncpClient _client;
    private readonly IContractRepository _repository;
    private readonly TemporaryItemResultStore _temporaryResults;
    private readonly IPncpRequestTelemetry? _telemetry;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly List<ItemSearchHit> _hits = [];
    private readonly HashSet<(string ContractId, long ItemNumber)> _hitKeys = [];
    private readonly List<ContractRecord> _candidates = [];
    private readonly HashSet<string> _candidateKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string ContractId, long ItemNumber), PriceAvailability> _priceAvailability = [];
    private CancellationTokenSource? _sessionCancellation;
    private ItemSearchSession? _session;
    private SearchQuery? _contractSearchQuery;
    private SearchExpression? _searchExpression;
    private ItemCandidateCursor? _candidateCursor;
    private bool _candidateSourceExhausted;
    private int _nextCandidateIndex;
    private int _deliveredHitCount;
    private int _contractsScanned;
    private int _cachedItemLists;
    private long _randomPivot;
    private string _currentGeographicStage = "50 cidades próximas";
    private int _itemListCalls;
    private int _resultCalls;
    private int _completedResultCalls;
    private int _failedResultCalls;
    private PncpRequestTelemetrySnapshot? _telemetryBaseline;
    private Stopwatch _elapsed = new();

    public ItemSearchSessionService(
        IPncpClient client,
        IContractRepository repository,
        string temporaryDatabasePath,
        IPncpRequestTelemetry? telemetry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDatabasePath);
        _client = client;
        _repository = repository;
        _temporaryResults = new TemporaryItemResultStore(temporaryDatabasePath);
        _temporaryResults.ClearAbandonedSession();
        _telemetry = telemetry;
    }

    public ItemSearchSession? CurrentSession => _session;

    /// <summary>
    /// Opens one contract once, compares its cached item list with every pending
    /// quotation line and reveals only the prices of distinct matching items.
    /// The permanent cache makes later prompt re-evaluation network-free whenever
    /// the list and result snapshots are still current.
    /// </summary>
    public async Task<ContractEvaluationResult> EvaluateContractAsync(
        ContractRecord contract,
        IReadOnlyList<ContractItemPrompt> prompts,
        CancellationToken cancellationToken = default,
        PncpRequestPriority priority = PncpRequestPriority.AdditionalBatches)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(prompts);
        var listFromCache = 0;
        var listFromApi = 0;
        var resultCalls = 0;
        var failedCalls = 0;
        var telemetryBefore = _telemetry?.GetSnapshot();

        var snapshot = await _repository.GetItemSnapshotAsync(contract.PncpId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot?.IsCurrentFor(contract) == true)
        {
            listFromCache = 1;
        }
        else
        {
            try
            {
                using var requestScope = PncpRequestOptions.BeginScope(
                    priority,
                    PncpRequestCategory.ItemLists);
                var items = await _client.GetItemsAsync(contract, cancellationToken).ConfigureAwait(false);
                await _repository.UpsertItemsAsync(
                    contract.PncpId,
                    items,
                    false,
                    cancellationToken).ConfigureAwait(false);
                listFromApi = 1;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failedCalls = 1;
                var actual = GetActualCallDelta(
                    telemetryBefore,
                    listFromApi,
                    resultCalls,
                    failedCalls);
                return new ContractEvaluationResult(
                    contract,
                    new Dictionary<Guid, IReadOnlyList<ItemSearchRow>>(),
                    0,
                    0,
                    listFromCache,
                    actual.ItemListCalls,
                    actual.ItemResultCalls,
                    actual.FailedCalls);
            }
        }

        var matches = new Dictionary<(Guid LineId, long ItemNumber), MatchedContractItem>();
        foreach (var lineGroup in prompts
                     .Where(prompt => !string.IsNullOrWhiteSpace(prompt.Text))
                     .GroupBy(prompt => prompt.LineId))
        {
            foreach (var prompt in lineGroup.OrderBy(value => value.Level))
            {
                var items = await _repository.SearchItemsAsync(
                        contract.PncpId,
                        prompt.Text,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var item in items)
                {
                    var key = (prompt.LineId, item.ItemNumber);
                    if (!matches.ContainsKey(key))
                    {
                        matches[key] = new MatchedContractItem(
                            prompt.LineId,
                            prompt.Level,
                            prompt.Text,
                            item);
                    }
                }
            }
        }

        var resultByItem = new ConcurrentDictionary<long, CachedItemResults>();
        var itemsToHydrate = matches.Values
            .Select(value => value.Item)
            .Where(item => item.HasResult)
            .GroupBy(item => item.ItemNumber)
            .Select(group => group.First())
            .ToArray();
        using var semaphore = new SemaphoreSlim(2, 2);
        var hydrationTasks = itemsToHydrate.Select(async item =>
        {
            var cached = await _repository.GetCachedItemResultsAsync(
                    contract.PncpId,
                    item.ItemNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cached?.IsCurrent == true)
            {
                resultByItem[item.ItemNumber] = cached;
                return;
            }

            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref resultCalls);
                using var requestScope = PncpRequestOptions.BeginScope(
                    priority,
                    PncpRequestCategory.ItemResults);
                var results = await _client.GetItemResultsAsync(
                        contract,
                        item.ItemNumber,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _repository.ReplaceItemResultsAsync(
                    contract.PncpId,
                    item.ItemNumber,
                    results,
                    cancellationToken).ConfigureAwait(false);
                resultByItem[item.ItemNumber] = new CachedItemResults(
                    item with { HydrationStatus = ItemHydrationStatus.Complete },
                    results);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failedCalls);
                await _repository.SetItemHydrationStatusAsync(
                    contract.PncpId,
                    item.ItemNumber,
                    ItemHydrationStatus.Failed,
                    exception.Message,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(hydrationTasks).ConfigureAwait(false);

        var byLine = new Dictionary<Guid, IReadOnlyList<ItemSearchRow>>();
        var revealedPrices = 0;
        foreach (var lineGroup in matches.Values.GroupBy(value => value.LineId))
        {
            var rows = new List<ItemSearchRow>();
            foreach (var match in lineGroup.OrderBy(value => value.Item.ItemNumber))
            {
                if (!match.Item.HasResult ||
                    !resultByItem.TryGetValue(match.Item.ItemNumber, out var cached))
                {
                    continue;
                }

                foreach (var result in cached.Results)
                {
                    var state = result.IsActive
                        ? ItemSearchPriceState.Homologated
                        : ItemSearchPriceState.Cancelled;
                    if (result.IsActive && result.HomologatedUnitValue is > 0)
                    {
                        revealedPrices++;
                    }

                    rows.Add(new ItemSearchRow(
                        contract,
                        match.Item,
                        result,
                        state,
                        result.IsActive ? "Preço homologado encontrado" : "Resultado cancelado",
                        false,
                        match.Level,
                        match.Text));
                }
            }

            byLine[lineGroup.Key] = rows;
        }

        var actualCalls = GetActualCallDelta(
            telemetryBefore,
            listFromApi,
            resultCalls,
            failedCalls);
        return new ContractEvaluationResult(
            contract,
            byLine,
            matches.Count,
            revealedPrices,
            listFromCache,
            actualCalls.ItemListCalls,
            actualCalls.ItemResultCalls,
            actualCalls.FailedCalls);
    }

    private (int ItemListCalls, int ItemResultCalls, int FailedCalls) GetActualCallDelta(
        PncpRequestTelemetrySnapshot? before,
        int logicalListCalls,
        int logicalResultCalls,
        int logicalFailures)
    {
        if (_telemetry is null || before is null)
        {
            return (logicalListCalls, logicalResultCalls, logicalFailures);
        }

        var after = _telemetry.GetSnapshot();
        var listsBefore = before[PncpRequestCategory.ItemLists];
        var listsAfter = after[PncpRequestCategory.ItemLists];
        var resultsBefore = before[PncpRequestCategory.ItemResults];
        var resultsAfter = after[PncpRequestCategory.ItemResults];
        return (
            checked((int)Math.Max(0, listsAfter.Calls - listsBefore.Calls)),
            checked((int)Math.Max(0, resultsAfter.Calls - resultsBefore.Calls)),
            checked((int)Math.Max(
                0,
                listsAfter.Failed - listsBefore.Failed +
                resultsAfter.Failed - resultsBefore.Failed)));
    }

    public async Task<ItemSearchSession> StartAsync(
        string text,
        IReadOnlyList<ContractRecord> candidateContracts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateContracts);
        var expression = SearchText.Parse(text);
        _sessionCancellation?.Cancel();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Starting another search is itself the retention boundary: discard
            // the previous session before any candidate lookup can fail/cancel.
            _temporaryResults.ClearAbandonedSession();
            _session = null;
            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            _hits.Clear();
            _hitKeys.Clear();
            _candidates.Clear();
            _candidateKeys.Clear();
            _priceAvailability.Clear();
            AddCandidates(candidateContracts);
            _contractSearchQuery = null;
            _searchExpression = expression;
            _candidateCursor = null;
            _candidateSourceExhausted = true;
            _nextCandidateIndex = 0;
            _deliveredHitCount = 0;
            _contractsScanned = 0;
            _cachedItemLists = 0;
            _randomPivot = Random.Shared.NextInt64(long.MaxValue);
            _currentGeographicStage = "candidatos fornecidos";
            _itemListCalls = 0;
            _resultCalls = 0;
            _completedResultCalls = 0;
            _failedResultCalls = 0;
            _telemetryBaseline = _telemetry?.GetSnapshot();
            _elapsed = Stopwatch.StartNew();
            var newSession = new ItemSearchSession(
                Guid.NewGuid(),
                text ?? string.Empty,
                DateTimeOffset.UtcNow,
                _candidates.Count,
                DefaultPageSize,
                _randomPivot);
            await _temporaryResults.ResetAsync(newSession.Id, cancellationToken).ConfigureAwait(false);
            _session = newSession;
            return newSession;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Starts a scalable session. Contract candidates are read locally in pages of
    /// 200 only as more matching items are requested.
    /// </summary>
    public async Task<ItemSearchSession> StartAsync(
        SearchQuery contractSearch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contractSearch);
        var expression = SearchText.Parse(contractSearch.Text);
        _sessionCancellation?.Cancel();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Do this before the first repository page so even a failed new
            // search cannot leave prices from the previous text on disk.
            _temporaryResults.ClearAbandonedSession();
            _session = null;
            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            _hits.Clear();
            _hitKeys.Clear();
            _candidates.Clear();
            _candidateKeys.Clear();
            _priceAvailability.Clear();
            _nextCandidateIndex = 0;
            _deliveredHitCount = 0;
            _contractsScanned = 0;
            _cachedItemLists = 0;
            _itemListCalls = 0;
            _resultCalls = 0;
            _completedResultCalls = 0;
            _failedResultCalls = 0;
            _telemetryBaseline = _telemetry?.GetSnapshot();
            _elapsed = Stopwatch.StartNew();

            _contractSearchQuery = contractSearch with { Page = 1, PageSize = 200 };
            _searchExpression = expression;
            _candidateCursor = null;
            _randomPivot = Random.Shared.NextInt64(long.MaxValue);
            var firstPage = await _repository.SearchItemCandidatesAsync(
                    _contractSearchQuery,
                    expression,
                    _randomPivot,
                    null,
                    200,
                    cancellationToken)
                .ConfigureAwait(false);
            AddCandidates(firstPage.Results.Select(candidate => candidate.Contract));
            _candidateCursor = firstPage.NextCursor;
            _candidateSourceExhausted = !firstPage.HasMore;
            _currentGeographicStage = firstPage.Results.Count == 0
                ? "conjunto esgotado"
                : DescribeGeographicStage(firstPage.Results[0].Contract);
            var newSession = new ItemSearchSession(
                Guid.NewGuid(),
                contractSearch.Text ?? string.Empty,
                DateTimeOffset.UtcNow,
                firstPage.Results.Count + (firstPage.HasMore ? 1 : 0),
                DefaultPageSize,
                _randomPivot);
            await _temporaryResults.ResetAsync(newSession.Id, cancellationToken).ConfigureAwait(false);
            _session = newSession;
            return newSession;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ItemSearchPage> LoadPageAsync(
        int page,
        decimal? minimumUnitPrice = null,
        decimal? maximumUnitPrice = null,
        IProgress<PriceBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePriceRange(minimumUnitPrice, maximumUnitPrice);
        var session = GetRequiredSession();
        page = Math.Max(1, page);
        using var linked = CreateLinkedCancellation(cancellationToken);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (session.Text.Length == 0)
            {
                return new ItemSearchPage([], page, session.PageSize, 0, false);
            }

            var firstUndeliveredHit = _deliveredHitCount;
            var discovery = await DiscoverUntilAsync(
                    checked(firstUndeliveredHit + session.PageSize),
                    MaximumFreshItemListsPerAction,
                    linked.Token)
                .ConfigureAwait(false);
            var pageHits = _hits
                .Skip(firstUndeliveredHit)
                .Take(session.PageSize)
                .ToArray();

            var toHydrate = await FilterItemsNeedingApiAsync(
                    pageHits.Where(hit => hit.Item.HasResult),
                    retryFailures: false,
                    excludedKeys: null,
                    linked.Token)
                .ConfigureAwait(false);
            var beforeCompleted = Volatile.Read(ref _completedResultCalls);
            var beforeFailures = Volatile.Read(ref _failedResultCalls);
            await HydrateSelectedAsync(
                    toHydrate,
                    PncpRequestPriority.VisiblePrices,
                    progress,
                    toHydrate.Count,
                    beforeCompleted,
                    beforeFailures,
                    retryFailures: false,
                    cancellationToken: linked.Token)
                .ConfigureAwait(false);
            progress?.Report(CreateProgress(
                toHydrate.Count,
                Volatile.Read(ref _completedResultCalls) - beforeCompleted,
                Volatile.Read(ref _failedResultCalls) - beforeFailures,
                false,
                "Preços do lote atualizados.",
                discovery.FreshItemListsUsed,
                discovery.BudgetExhausted));

            var rows = await BuildRowsAsync(pageHits, minimumUnitPrice, maximumUnitPrice, linked.Token).ConfigureAwait(false);
            _deliveredHitCount += pageHits.Length;
            var hasMore = _hits.Count > _deliveredHitCount || HasMoreContractCandidates;
            return new ItemSearchPage(
                rows,
                page,
                session.PageSize,
                _hits.Count,
                hasMore,
                _contractsScanned,
                discovery.FreshItemListsUsed,
                discovery.BudgetExhausted,
                _currentGeographicStage,
                _cachedItemLists);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<PriceBatchProgress> FireBatchesAsync(
        PriceBatchRequest request,
        IProgress<PriceBatchProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        await RunContinuousAsync(
                request,
                progress: progress,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Executes exactly one resumable batch for the timed quotation automation.
    /// The geographic/random cursor belongs to the quotation line, so switching
    /// between items never restarts or repeats their candidate sequence.
    /// </summary>
    public async Task<TimedPriceBatchResult> RunTimedBatchAsync(
        SearchQuery query,
        ItemSearchCheckpoint checkpoint,
        IProgress<PriceBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(checkpoint);
        var expression = SearchText.Parse(query.Text);
        var pivot = checkpoint.RandomPivot == 0
            ? Random.Shared.NextInt64(1, long.MaxValue)
            : checkpoint.RandomPivot;
        var candidatePage = await _repository.SearchItemCandidatesAsync(
                query with { Page = 1, PageSize = ItemSearchDefaults.ContractsPerBatch },
                expression,
                pivot,
                checkpoint.Cursor,
                ItemSearchDefaults.ContractsPerBatch,
                cancellationToken)
            .ConfigureAwait(false);
        await StartAsync(
                query.Text,
                candidatePage.Results.Select(candidate => candidate.Contract).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        PriceBatchProgress? latestProgress = null;
        var forwardingProgress = new InlineProgress<PriceBatchProgress>(value =>
        {
            latestProgress = value;
            progress?.Report(value);
        });
        PriceBatchProgress batchProgress;
        try
        {
            batchProgress = await RunContinuousAsync(
                    new PriceBatchRequest(1),
                    progress: forwardingProgress,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            var processed = Math.Clamp(
                latestProgress?.ProcessedContracts ?? 0,
                0,
                candidatePage.Results.Count);
            var partialRows = await GetDiscoveredRowsAsync(cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            var partialProgress = latestProgress ?? new PriceBatchProgress(
                0,
                0,
                0,
                0,
                0,
                TimeSpan.Zero,
                false,
                "Lote interrompido; resultados concluídos foram preservados.",
                RequestedContracts: ItemSearchDefaults.ContractsPerBatch,
                ProcessedContracts: processed);
            var partialCheckpoint = checkpoint with
            {
                RandomPivot = pivot,
                Cursor = processed == 0
                    ? checkpoint.Cursor
                    : candidatePage.Results[processed - 1].Cursor,
                ContractsExamined = checked(checkpoint.ContractsExamined + processed),
                CandidateSetExhausted = !candidatePage.HasMore && processed == candidatePage.Results.Count
            };
            throw new TimedPriceBatchInterruptedException(
                new TimedPriceBatchResult(partialRows, partialCheckpoint, partialProgress),
                exception,
                cancellationToken);
        }
        var rows = await GetDiscoveredRowsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var nextCheckpoint = checkpoint with
        {
            RandomPivot = pivot,
            Cursor = candidatePage.NextCursor,
            ContractsExamined = checked(checkpoint.ContractsExamined + batchProgress.ProcessedContracts),
            BatchesCompleted = checked(checkpoint.BatchesCompleted + 1),
            CandidateSetExhausted = !candidatePage.HasMore
        };
        return new TimedPriceBatchResult(rows, nextCheckpoint, batchProgress);
    }

    /// <summary>
    /// Examines the next user-selected batches of fifty candidate contracts. Every
    /// matching item inside each examined contract has its available price revealed.
    /// </summary>
    public async Task<PriceBatchProgress> RunContinuousAsync(
        PriceBatchRequest request,
        decimal? minimumUnitPrice = null,
        decimal? maximumUnitPrice = null,
        IProgress<PriceBatchProgress>? progress = null,
        IProgress<IReadOnlyList<ItemSearchRow>>? rowProgress = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePriceRange(minimumUnitPrice, maximumUnitPrice);
        if (request.BatchCount is < 1 or > MaximumBatchCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"A quantidade de disparos deve estar entre 1 e {MaximumBatchCount}.");
        }

        if (request.BatchCount > 10 && !request.LargeRequestConfirmed)
        {
            throw new InvalidOperationException(
                "Mais de 500 contratações exigem confirmação explícita do usuário.");
        }

        var session = GetRequiredSession();
        if (session.Text.Length == 0)
        {
            return CreateProgress(
                0,
                0,
                0,
                true,
                "Pesquisa vazia não examina contratações.",
                requestedContracts: request.RequestedContracts,
                processedContracts: 0);
        }

        using var linked = CreateLinkedCancellation(cancellationToken);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var callsAtStart = Volatile.Read(ref _completedResultCalls);
            var failuresAtStart = Volatile.Read(ref _failedResultCalls);
            var contractsAtStart = _contractsScanned;
            while (_contractsScanned - contractsAtStart < request.RequestedContracts)
            {
                if (!await EnsureNextCandidateAvailableAsync(linked.Token).ConfigureAwait(false))
                {
                    break;
                }

                var hitCountBefore = _hits.Count;
                await DiscoverNextContractAsync(
                        linked.Token,
                        PncpRequestPriority.AdditionalBatches,
                        allowFreshItemList: true)
                    .ConfigureAwait(false);
                var newlyDiscovered = _hits.Skip(hitCountBefore).ToArray();
                if (newlyDiscovered.Length > 0)
                {
                    if (rowProgress is not null)
                    {
                        var availableRows = (await BuildRowsAsync(
                                newlyDiscovered,
                                minimumUnitPrice,
                                maximumUnitPrice,
                                linked.Token)
                            .ConfigureAwait(false))
                            .Where(row => row.PriceState != ItemSearchPriceState.Pending)
                            .ToArray();
                        if (availableRows.Length > 0)
                        {
                            rowProgress.Report(availableRows);
                        }
                    }

                    var toHydrate = await FilterItemsNeedingApiAsync(
                            newlyDiscovered.Where(hit => hit.Item.HasResult),
                            retryFailures: true,
                            excludedKeys: null,
                            cancellationToken: linked.Token)
                        .ConfigureAwait(false);
                    await HydrateSelectedAsync(
                            toHydrate,
                            PncpRequestPriority.AdditionalBatches,
                            progress: null,
                            requestedCalls: toHydrate.Count,
                            completedBaseline: callsAtStart,
                            failedBaseline: failuresAtStart,
                            retryFailures: true,
                            cancellationToken: linked.Token,
                            completedRowProgress: rowProgress,
                            minimumUnitPrice: minimumUnitPrice,
                            maximumUnitPrice: maximumUnitPrice)
                        .ConfigureAwait(false);

                    if (rowProgress is not null)
                    {
                        var finalRows = (await BuildRowsAsync(
                            newlyDiscovered,
                            minimumUnitPrice,
                            maximumUnitPrice,
                            linked.Token)
                        .ConfigureAwait(false))
                        .ToArray();
                        rowProgress.Report(finalRows);
                    }
                }

                var processedContracts = _contractsScanned - contractsAtStart;
                progress?.Report(CreateProgress(
                    Volatile.Read(ref _completedResultCalls) - callsAtStart,
                    Volatile.Read(ref _completedResultCalls) - callsAtStart,
                    Volatile.Read(ref _failedResultCalls) - failuresAtStart,
                    false,
                    $"Contratações examinadas: {processedContracts:N0} de {request.RequestedContracts:N0}; " +
                    $"{_hits.Count:N0} item(ns) compatível(is) descoberto(s).",
                    requestedContracts: request.RequestedContracts,
                    processedContracts: processedContracts));
            }

            var completed = Volatile.Read(ref _completedResultCalls) - callsAtStart;
            var failed = Volatile.Read(ref _failedResultCalls) - failuresAtStart;
            var processed = _contractsScanned - contractsAtStart;
            var exhausted = !HasMoreContractCandidates;
            _deliveredHitCount = _hits.Count;
            var message = exhausted
                ? $"Conjunto esgotado após {processed:N0} contratação(ões) neste lote."
                : $"Lote concluído: {processed:N0} contratação(ões) examinada(s).";
            var result = CreateProgress(
                completed,
                completed,
                failed,
                exhausted,
                message,
                requestedContracts: request.RequestedContracts,
                processedContracts: processed);
            progress?.Report(result);
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<ItemSearchRow>> GetDiscoveredRowsAsync(
        decimal? minimumUnitPrice = null,
        decimal? maximumUnitPrice = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePriceRange(minimumUnitPrice, maximumUnitPrice);
        _ = GetRequiredSession();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await BuildRowsAsync(_hits, minimumUnitPrice, maximumUnitPrice, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Stop() => _sessionCancellation?.Cancel();

    public async ValueTask DisposeAsync()
    {
        _sessionCancellation?.Cancel();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            _session = null;
            await _temporaryResults.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private async Task<DiscoveryBudgetResult> DiscoverUntilAsync(
        int targetHitCount,
        int maximumFreshItemLists,
        CancellationToken cancellationToken)
    {
        var freshItemListsUsed = 0;
        var budgetExhausted = false;
        while (_hits.Count < targetHitCount &&
               await EnsureNextCandidateAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            var discovery = await DiscoverNextContractAsync(
                    cancellationToken,
                    PncpRequestPriority.VisiblePrices,
                    freshItemListsUsed < maximumFreshItemLists)
                .ConfigureAwait(false);
            if (discovery.BudgetBlocked)
            {
                budgetExhausted = true;
                break;
            }

            if (discovery.FreshItemListUsed)
            {
                freshItemListsUsed++;
            }
        }

        return new DiscoveryBudgetResult(freshItemListsUsed, budgetExhausted);
    }

    private async Task<DiscoveryAttempt> DiscoverNextContractAsync(
        CancellationToken cancellationToken,
        PncpRequestPriority priority,
        bool allowFreshItemList)
    {
        var session = GetRequiredSession();
        if (!await EnsureNextCandidateAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return DiscoveryAttempt.NoCandidate;
        }

        var contract = _candidates[_nextCandidateIndex];
        var snapshot = await _repository.GetItemSnapshotAsync(contract.PncpId, cancellationToken).ConfigureAwait(false);
        var needsFreshItemList = snapshot is null || !snapshot.IsCurrentFor(contract);
        if (needsFreshItemList && !allowFreshItemList)
        {
            return DiscoveryAttempt.BudgetLimit;
        }

        _nextCandidateIndex++;
        _contractsScanned++;
        _currentGeographicStage = DescribeGeographicStage(contract);
        if (needsFreshItemList)
        {
            Interlocked.Increment(ref _itemListCalls);
            try
            {
                using var requestScope = PncpRequestOptions.BeginScope(priority, PncpRequestCategory.ItemLists);
                var items = await _client.GetItemsAsync(contract, cancellationToken).ConfigureAwait(false);
                await _repository.UpsertItemsAsync(contract.PncpId, items, false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A failure in one contract must not hide already indexed items nor
                // prevent the remaining contracts from being searched.
            }
        }
        else
        {
            _cachedItemLists++;
        }

        var matches = await _repository.SearchItemsAsync(contract.PncpId, session.Text, cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in matches)
        {
            if (_hitKeys.Add((contract.PncpId, item.ItemNumber)))
            {
                _hits.Add(new ItemSearchHit(contract, item));
            }
        }

        return new DiscoveryAttempt(true, needsFreshItemList, false);
    }

    private bool HasMoreContractCandidates =>
        _nextCandidateIndex < _candidates.Count || !_candidateSourceExhausted;

    private async Task<bool> EnsureNextCandidateAvailableAsync(CancellationToken cancellationToken)
    {
        while (_nextCandidateIndex >= _candidates.Count && !_candidateSourceExhausted)
        {
            var query = _contractSearchQuery
                ?? throw new InvalidOperationException("Fonte paginada de contratações ausente.");
            var expression = _searchExpression
                ?? throw new InvalidOperationException("Expressão da pesquisa ausente.");
            var previousCursor = _candidateCursor;
            var next = await _repository.SearchItemCandidatesAsync(
                    query,
                    expression,
                    _randomPivot,
                    previousCursor,
                    200,
                    cancellationToken)
                .ConfigureAwait(false);
            AddCandidates(next.Results.Select(candidate => candidate.Contract));
            _candidateCursor = next.NextCursor;
            _candidateSourceExhausted = !next.HasMore || next.Results.Count == 0;
            if (!_candidateSourceExhausted && Equals(previousCursor, _candidateCursor))
            {
                throw new InvalidOperationException("A continuação de candidatos não avançou o cursor.");
            }
        }

        return _nextCandidateIndex < _candidates.Count;
    }

    private void AddCandidates(IEnumerable<ContractRecord> contracts)
    {
        foreach (var contract in contracts)
        {
            if (_candidateKeys.Add(contract.PncpId))
            {
                _candidates.Add(contract);
            }
        }
    }

    private static string DescribeGeographicStage(ContractRecord contract)
    {
        if (BrazilMunicipalityCatalog.IsFirstFifty(
                contract.MunicipalityIbgeCode,
                contract.Municipality,
                contract.Uf))
        {
            return $"50 cidades — {contract.Municipality}/{contract.Uf}";
        }

        var state = contract.Uf?.Trim().ToUpperInvariant();
        return state is { Length: 2 }
            ? $"UF {state}"
            : "localização não reconhecida";
    }

    private async Task<IReadOnlyList<ItemSearchHit>> FindItemsNeedingApiAsync(
        int maximum,
        ISet<(string ContractId, long ItemNumber)> excludedKeys,
        CancellationToken cancellationToken)
    {
        // New items take precedence over retries so a permanently failing item
        // cannot starve the rest of a 5,000-item batch. Failures are used only to
        // fill capacity left after every untouched discovered item.
        var untouched = (await FilterItemsNeedingApiAsync(
                _hits.Where(hit => hit.Item.HasResult),
                retryFailures: false,
                excludedKeys: excludedKeys,
                cancellationToken: cancellationToken,
                maximum: maximum)
            .ConfigureAwait(false)).ToList();
        if (untouched.Count == maximum)
        {
            return untouched;
        }

        var retryExclusions = new HashSet<(string ContractId, long ItemNumber)>(excludedKeys);
        foreach (var hit in untouched)
        {
            retryExclusions.Add((hit.Contract.PncpId, hit.Item.ItemNumber));
        }

        var retries = await FilterItemsNeedingApiAsync(
                _hits.Where(hit => hit.Item.HasResult),
                retryFailures: true,
                excludedKeys: retryExclusions,
                cancellationToken: cancellationToken,
                maximum: maximum - untouched.Count)
            .ConfigureAwait(false);
        untouched.AddRange(retries);
        return untouched;
    }

    private async Task<IReadOnlyList<ItemSearchHit>> FilterItemsNeedingApiAsync(
        IEnumerable<ItemSearchHit> hits,
        bool retryFailures,
        ISet<(string ContractId, long ItemNumber)>? excludedKeys,
        CancellationToken cancellationToken,
        int maximum = int.MaxValue)
    {
        var result = new List<ItemSearchHit>(Math.Min(maximum, DefaultPageSize));
        foreach (var hit in hits)
        {
            var key = (hit.Contract.PncpId, hit.Item.ItemNumber);
            if (excludedKeys?.Contains(key) == true)
            {
                continue;
            }

            var availability = await GetPriceAvailabilityAsync(hit, cancellationToken).ConfigureAwait(false);
            if (availability.Kind is PriceAvailabilityKind.Permanent or PriceAvailabilityKind.TemporarySuccess)
            {
                continue;
            }

            if (availability.Kind == PriceAvailabilityKind.TemporaryFailure && !retryFailures)
            {
                continue;
            }

            result.Add(hit);
            if (result.Count == maximum)
            {
                break;
            }
        }

        return result;
    }

    private async Task HydrateSelectedAsync(
        IReadOnlyList<ItemSearchHit> hits,
        PncpRequestPriority priority,
        IProgress<PriceBatchProgress>? progress,
        int requestedCalls,
        int completedBaseline,
        int failedBaseline,
        bool retryFailures,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<ItemSearchRow>>? completedRowProgress = null,
        decimal? minimumUnitPrice = null,
        decimal? maximumUnitPrice = null)
    {
        using var semaphore = new SemaphoreSlim(2, 2);
        var progressGate = new object();
        var lastReportedCompleted = -1;
        var tasks = hits.Select(async hit =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var attempt = await HydrateSingleAsync(
                        hit,
                        priority,
                        retryFailures,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!attempt.CalledApi)
                {
                    return;
                }

                if (completedRowProgress is not null)
                {
                    var completedRows = await BuildRowsAsync(
                            [hit],
                            minimumUnitPrice,
                            maximumUnitPrice,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (completedRows.Count > 0)
                    {
                        completedRowProgress.Report(completedRows);
                    }
                }

                var completed = Volatile.Read(ref _completedResultCalls) - completedBaseline;
                var failed = Volatile.Read(ref _failedResultCalls) - failedBaseline;
                lock (progressGate)
                {
                    if (completed > lastReportedCompleted)
                    {
                        lastReportedCompleted = completed;
                        progress?.Report(CreateProgress(
                            requestedCalls,
                            completed,
                            failed,
                            false,
                            $"Preços consultados: {completed} de {requestedCalls} itens"));
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<(bool CalledApi, bool Failed)> HydrateSingleAsync(
        ItemSearchHit hit,
        PncpRequestPriority priority,
        bool retryFailures,
        CancellationToken cancellationToken)
    {
        var availability = await GetPriceAvailabilityAsync(hit, cancellationToken).ConfigureAwait(false);
        if (availability.Kind is PriceAvailabilityKind.Permanent or PriceAvailabilityKind.TemporarySuccess)
        {
            return (false, false);
        }

        if (availability.Kind == PriceAvailabilityKind.TemporaryFailure && !retryFailures)
        {
            return (false, true);
        }

        Interlocked.Increment(ref _resultCalls);
        try
        {
            using var requestScope = PncpRequestOptions.BeginScope(priority, PncpRequestCategory.ItemResults);
            var results = await _client.GetItemResultsAsync(
                    hit.Contract,
                    hit.Item.ItemNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            await _temporaryResults.SaveSuccessAsync(
                    hit.Contract.PncpId,
                    hit.Item.ItemNumber,
                    results,
                    cancellationToken)
                .ConfigureAwait(false);
            _priceAvailability[(hit.Contract.PncpId, hit.Item.ItemNumber)] =
                PriceAvailability.FromTemporary(new TemporaryItemResultEntry(true, null, results));
            Interlocked.Increment(ref _completedResultCalls);
            return (true, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _failedResultCalls);
            try
            {
                await _temporaryResults.SaveFailureAsync(
                        hit.Contract.PncpId,
                        hit.Item.ItemNumber,
                        exception.Message,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original API failure in the progress result.
            }

            _priceAvailability[(hit.Contract.PncpId, hit.Item.ItemNumber)] =
                PriceAvailability.FromTemporary(new TemporaryItemResultEntry(false, exception.Message, []));
            Interlocked.Increment(ref _completedResultCalls);
            return (true, true);
        }
    }

    private async Task<IReadOnlyList<ItemSearchRow>> BuildRowsAsync(
        IReadOnlyList<ItemSearchHit> hits,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        CancellationToken cancellationToken)
    {
        var hasRange = minimumUnitPrice is not null || maximumUnitPrice is not null;
        var rows = new List<ItemSearchRow>();
        foreach (var hit in hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hit.Item.HasResult)
            {
                if (!hasRange)
                {
                    rows.Add(CreateStatusRow(hit, ItemSearchPriceState.NoHomologatedResult, "Item sem resultado homologado", false));
                }

                continue;
            }

            var availability = await GetPriceAvailabilityAsync(hit, cancellationToken).ConfigureAwait(false);
            if (availability.Kind == PriceAvailabilityKind.Permanent)
            {
                AddResultRows(
                    rows,
                    hit,
                    availability.Permanent!.Results,
                    false,
                    hasRange,
                    minimumUnitPrice,
                    maximumUnitPrice);
                continue;
            }

            if (availability.Kind == PriceAvailabilityKind.NeedsApi)
            {
                if (!hasRange)
                {
                    rows.Add(CreateStatusRow(hit, ItemSearchPriceState.Pending, "Consulta pendente — requer conexão", false));
                }

                continue;
            }

            var temporary = availability.Temporary!;
            if (availability.Kind == PriceAvailabilityKind.TemporaryFailure)
            {
                if (!hasRange)
                {
                    rows.Add(CreateStatusRow(
                        hit,
                        ItemSearchPriceState.Failed,
                        string.IsNullOrWhiteSpace(temporary.Error)
                            ? "Falha ao consultar — tentar novamente"
                            : $"Falha ao consultar — tentar novamente. Detalhe: {temporary.Error}",
                        true));
                }

                continue;
            }

            AddResultRows(rows, hit, temporary.Results, true, hasRange, minimumUnitPrice, maximumUnitPrice);
        }

        return rows;
    }

    private async Task<PriceAvailability> GetPriceAvailabilityAsync(
        ItemSearchHit hit,
        CancellationToken cancellationToken)
    {
        var key = (hit.Contract.PncpId, hit.Item.ItemNumber);
        if (_priceAvailability.TryGetValue(key, out var known))
        {
            return known;
        }

        var permanent = await _repository.GetCachedItemResultsAsync(
                hit.Contract.PncpId,
                hit.Item.ItemNumber,
                cancellationToken)
            .ConfigureAwait(false);
        PriceAvailability discovered;
        if (permanent?.IsCurrent == true)
        {
            discovered = PriceAvailability.FromPermanent(permanent);
        }
        else
        {
            var temporary = await _temporaryResults.GetAsync(
                    hit.Contract.PncpId,
                    hit.Item.ItemNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            discovered = temporary switch
            {
                { Succeeded: true } => PriceAvailability.FromTemporary(temporary),
                { Succeeded: false } => PriceAvailability.FromTemporary(temporary),
                _ => PriceAvailability.NeedsApi
            };
        }

        return _priceAvailability.GetOrAdd(key, discovered);
    }

    private static void AddResultRows(
        ICollection<ItemSearchRow> rows,
        ItemSearchHit hit,
        IReadOnlyList<HomologationResult> results,
        bool isTemporary,
        bool hasRange,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice)
    {
        if (results.Count == 0)
        {
            if (!hasRange)
            {
                rows.Add(CreateStatusRow(hit, ItemSearchPriceState.NoHomologatedResult, "Item sem resultado homologado", isTemporary));
            }

            return;
        }

        foreach (var result in results)
        {
            if (hasRange)
            {
                var price = result.HomologatedUnitValue;
                if (!result.IsActive || price is null ||
                    minimumUnitPrice is not null && price < minimumUnitPrice ||
                    maximumUnitPrice is not null && price > maximumUnitPrice)
                {
                    continue;
                }
            }

            rows.Add(new ItemSearchRow(
                hit.Contract,
                hit.Item,
                result,
                result.IsActive ? ItemSearchPriceState.Homologated : ItemSearchPriceState.Cancelled,
                result.IsActive ? "Preço homologado encontrado" : "Resultado cancelado",
                isTemporary));
        }
    }

    private static ItemSearchRow CreateStatusRow(
        ItemSearchHit hit,
        ItemSearchPriceState state,
        string status,
        bool isTemporary) =>
        new(hit.Contract, hit.Item, null, state, status, isTemporary);

    private PriceBatchProgress CreateProgress(
        int requested,
        int completed,
        int failed,
        bool exhausted,
        string message,
        int freshItemListsUsed = 0,
        bool itemListBudgetExhausted = false,
        int requestedContracts = 0,
        int processedContracts = 0)
    {
        var network = CurrentNetworkMetrics;
        return new PriceBatchProgress(
            requested,
            completed,
            failed,
            Volatile.Read(ref _itemListCalls),
            network.TotalBytesReceived,
            _elapsed.Elapsed,
            exhausted,
            message,
            network,
            _contractsScanned,
            freshItemListsUsed,
            itemListBudgetExhausted,
            _currentGeographicStage,
            _cachedItemLists,
            requestedContracts,
            processedContracts,
            _hits.Count,
            _priceAvailability.Values.Sum(CountRevealedPrices));
    }

    private static int CountRevealedPrices(PriceAvailability availability)
    {
        var results = availability.Kind switch
        {
            PriceAvailabilityKind.Permanent => availability.Permanent?.Results,
            PriceAvailabilityKind.TemporarySuccess => availability.Temporary?.Results,
            _ => null
        };
        return results?.Count(result =>
            result.IsActive &&
            result.HomologatedUnitValue is > 0) ?? 0;
    }

    public ItemSearchNetworkMetrics CurrentNetworkMetrics
    {
        get
        {
            var baseline = _telemetryBaseline;
            if (_telemetry is null || baseline is null)
            {
                return new ItemSearchNetworkMetrics(
                    Volatile.Read(ref _itemListCalls),
                    Volatile.Read(ref _resultCalls),
                    0,
                    0,
                    TimeSpan.Zero,
                    TimeSpan.Zero);
            }

            var current = _telemetry.GetSnapshot();
            var lists = Difference(
                current[PncpRequestCategory.ItemLists],
                baseline[PncpRequestCategory.ItemLists]);
            var results = Difference(
                current[PncpRequestCategory.ItemResults],
                baseline[PncpRequestCategory.ItemResults]);
            return new ItemSearchNetworkMetrics(
                lists.Calls,
                results.Calls,
                lists.Bytes,
                results.Bytes,
                lists.Duration,
                results.Duration);
        }
    }

    private static (long Calls, long Bytes, TimeSpan Duration) Difference(
        PncpRequestCategorySnapshot current,
        PncpRequestCategorySnapshot baseline) =>
        (
            Math.Max(0, current.Calls - baseline.Calls),
            Math.Max(0, current.BytesReceived - baseline.BytesReceived),
            current.TotalDuration >= baseline.TotalDuration
                ? current.TotalDuration - baseline.TotalDuration
                : TimeSpan.Zero);

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken)
    {
        var session = GetRequiredSession();
        _ = session;
        return CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionCancellation!.Token);
    }

    private ItemSearchSession GetRequiredSession() =>
        _session ?? throw new InvalidOperationException("Inicie uma pesquisa antes de consultar itens.");

    private static void ValidatePriceRange(decimal? minimum, decimal? maximum)
    {
        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            throw new ArgumentException("O preço mínimo deve ser menor ou igual ao preço máximo.");
        }
    }

    private enum PriceAvailabilityKind
    {
        NeedsApi,
        Permanent,
        TemporarySuccess,
        TemporaryFailure
    }

    private sealed record DiscoveryBudgetResult(
        int FreshItemListsUsed,
        bool BudgetExhausted);

    private sealed record DiscoveryAttempt(
        bool Processed,
        bool FreshItemListUsed,
        bool BudgetBlocked)
    {
        public static DiscoveryAttempt NoCandidate { get; } = new(false, false, false);
        public static DiscoveryAttempt BudgetLimit { get; } = new(false, false, true);
    }

    private sealed record MatchedContractItem(
        Guid LineId,
        PromptMatchLevel Level,
        string Text,
        ProcurementItem Item);

    private sealed record PriceAvailability(
        PriceAvailabilityKind Kind,
        CachedItemResults? Permanent,
        TemporaryItemResultEntry? Temporary)
    {
        public static PriceAvailability NeedsApi { get; } =
            new(PriceAvailabilityKind.NeedsApi, null, null);

        public static PriceAvailability FromPermanent(CachedItemResults value) =>
            new(PriceAvailabilityKind.Permanent, value, null);

        public static PriceAvailability FromTemporary(TemporaryItemResultEntry value) =>
            new(
                value.Succeeded
                    ? PriceAvailabilityKind.TemporarySuccess
                    : PriceAvailabilityKind.TemporaryFailure,
                null,
                value);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}

public sealed class TimedPriceBatchInterruptedException : OperationCanceledException
{
    public TimedPriceBatchInterruptedException(
        TimedPriceBatchResult partialResult,
        Exception innerException,
        CancellationToken cancellationToken)
        : base("O lote temporal foi interrompido.", innerException, cancellationToken)
    {
        PartialResult = partialResult;
    }

    public TimedPriceBatchResult PartialResult { get; }
}
