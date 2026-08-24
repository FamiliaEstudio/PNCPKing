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
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private const int DefaultNetworkConcurrency = 4;
    private const int MaximumPersistenceConcurrency = 4;
    private static readonly SemaphoreSlim PersistenceGate = new(
        MaximumPersistenceConcurrency,
        MaximumPersistenceConcurrency);
    public const int DefaultPageSize = ItemSearchDefaults.ContractsPerBatch;
    public const int MaximumBatchCount = 100;
    public const int MaximumFreshItemListsPerAction = ItemSearchDefaults.ContractsPerBatch;

    private readonly IPncpClient _client;
    private readonly IContractRepository _repository;
    private readonly TemporaryItemResultStore _temporaryResults;
    private readonly IPncpRequestTelemetry? _telemetry;
    private readonly PncpRequestScheduler? _requestScheduler;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly List<ItemSearchHit> _hits = [];
    private readonly HashSet<(string ContractId, long ItemNumber)> _hitKeys = [];
    private readonly List<ItemContractCandidate> _candidates = [];
    private readonly HashSet<string> _candidateKeys = new(StringComparer.Ordinal);
    private readonly List<ContractRecord> _processedContracts = [];
    private readonly HashSet<string> _processedContractKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string ContractId, long ItemNumber), PriceAvailability> _priceAvailability = [];
    private CancellationTokenSource? _sessionCancellation;
    private ItemSearchSession? _session;
    private SearchQuery? _contractSearchQuery;
    private SearchExpression? _searchExpression;
    private SearchExpression? _candidateExpression;
    private ItemCandidateCursor? _candidateCursor;
    private ItemCandidateCursor? _processedCandidateCursor;
    private bool _candidateSourceExhausted;
    private int _nextCandidateIndex;
    private int _deliveredHitCount;
    private int _contractsScanned;
    private int _contractsExpanded;
    private int _fullyResolvedContracts;
    private int _cachedItemLists;
    private long _randomPivot;
    private string _anchorKey = string.Empty;
    private string _scopeKey = string.Empty;
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
        IPncpRequestTelemetry? telemetry = null,
        bool persistentSession = false,
        PncpRequestScheduler? requestScheduler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDatabasePath);
        _client = client;
        _repository = repository;
        _temporaryResults = new TemporaryItemResultStore(temporaryDatabasePath, persistentSession);
        _temporaryResults.ClearAbandonedSession();
        _telemetry = telemetry;
        _requestScheduler = requestScheduler;
    }

    public ItemSearchSession? CurrentSession => _session;

    private int CurrentNetworkConcurrency => Math.Clamp(
        _requestScheduler?.GetSnapshot().EffectiveConcurrency ?? DefaultNetworkConcurrency,
        1,
        48);

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
                listFromApi = 1;
                using var requestScope = PncpRequestOptions.BeginScope(
                    priority,
                    PncpRequestCategory.ItemLists);
                var items = await _client.GetItemsAsync(contract, cancellationToken).ConfigureAwait(false);
                await PersistAsync(
                        token => _repository.UpsertItemsAsync(
                            contract.PncpId,
                            items,
                            false,
                            token),
                        CancellationToken.None)
                    .ConfigureAwait(false);
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
        var networkConcurrency = CurrentNetworkConcurrency;
        using var semaphore = new SemaphoreSlim(networkConcurrency, networkConcurrency);
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
                await PersistAsync(
                        token => _repository.ReplaceItemResultsAsync(
                            contract.PncpId,
                            item.ItemNumber,
                            results,
                            token),
                        CancellationToken.None)
                    .ConfigureAwait(false);
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
                await PersistAsync(
                        token => _repository.SetItemHydrationStatusAsync(
                            contract.PncpId,
                            item.ItemNumber,
                            ItemHydrationStatus.Failed,
                            exception.Message,
                            token),
                        CancellationToken.None)
                    .ConfigureAwait(false);
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
            _processedContracts.Clear();
            _processedContractKeys.Clear();
            _priceAvailability.Clear();
            AddCandidates(candidateContracts);
            _contractSearchQuery = null;
            _searchExpression = expression;
            _candidateExpression = expression;
            _anchorKey = CreateAnchorKey(expression, text);
            _scopeKey = string.Empty;
            _candidateCursor = null;
            _processedCandidateCursor = null;
            _candidateSourceExhausted = true;
            _nextCandidateIndex = 0;
            _deliveredHitCount = 0;
            _contractsScanned = 0;
            _contractsExpanded = 0;
            _fullyResolvedContracts = 0;
            _cachedItemLists = 0;
            _randomPivot = Random.Shared.NextInt64(1, long.MaxValue);
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
            await _temporaryResults.ResetAsync(
                    newSession.Id,
                    _anchorKey,
                    _scopeKey,
                    text ?? string.Empty,
                    _randomPivot,
                    newSession.StartedAt,
                    cancellationToken)
                .ConfigureAwait(false);
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
    public Task<ItemSearchSession> StartAsync(
        SearchQuery contractSearch,
        CancellationToken cancellationToken = default) =>
        StartAsync(contractSearch, restart: false, cancellationToken);

    public async Task<ItemSearchSession> StartAsync(
        SearchQuery contractSearch,
        bool restart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contractSearch);
        var expression = SearchText.Parse(contractSearch.Text);
        var anchorKey = CreateAnchorKey(expression, contractSearch.Text);
        var scopeKey = CreateScopeKey(contractSearch, expression);
        var candidateExpression = CreateCandidateExpression(expression);
        _sessionCancellation?.Cancel();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _temporaryResults.ClearAbandonedSession();
            _session = null;
            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            _hits.Clear();
            _hitKeys.Clear();
            _candidates.Clear();
            _candidateKeys.Clear();
            _processedContracts.Clear();
            _processedContractKeys.Clear();
            _priceAvailability.Clear();
            _nextCandidateIndex = 0;
            _deliveredHitCount = 0;
            _contractsScanned = 0;
            _contractsExpanded = 0;
            _fullyResolvedContracts = 0;
            _cachedItemLists = 0;
            _itemListCalls = 0;
            _resultCalls = 0;
            _completedResultCalls = 0;
            _failedResultCalls = 0;
            _telemetryBaseline = _telemetry?.GetSnapshot();
            _elapsed = Stopwatch.StartNew();

            _contractSearchQuery = contractSearch with { Page = 1, PageSize = 200 };
            _searchExpression = expression;
            _candidateExpression = candidateExpression;
            _anchorKey = anchorKey;
            _scopeKey = scopeKey;
            _candidateCursor = null;
            _processedCandidateCursor = null;
            var stored = await _temporaryResults.TryRestoreAsync(anchorKey, cancellationToken)
                .ConfigureAwait(false);
            if (stored is not null &&
                (restart || !string.Equals(stored.ScopeKey, scopeKey, StringComparison.Ordinal)))
            {
                stored = null;
                _randomPivot = Random.Shared.NextInt64(1, long.MaxValue);
                var resetStartedAt = DateTimeOffset.UtcNow;
                var resetSessionId = Guid.NewGuid();
                await _temporaryResults.ResetTraversalAsync(
                        resetSessionId,
                        anchorKey,
                        scopeKey,
                        contractSearch.Text ?? string.Empty,
                        _randomPivot,
                        resetStartedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await StartFreshTraversalAsync(
                        resetSessionId,
                        resetStartedAt,
                        contractSearch,
                        candidateExpression,
                        resetStore: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (stored is not null)
            {
                foreach (var contract in stored.ProcessedContracts)
                {
                    if (_processedContractKeys.Add(contract.PncpId))
                    {
                        _processedContracts.Add(contract);
                    }
                }

                _candidateCursor = stored.Cursor;
                _processedCandidateCursor = stored.Cursor;
                _candidateSourceExhausted = stored.CandidateSetExhausted;
                _deliveredHitCount = _hits.Count;
                _contractsScanned = stored.ContractsScanned;
                _contractsExpanded = stored.ExpandedContracts;
                _fullyResolvedContracts = stored.FullyResolvedContracts;
                _cachedItemLists = stored.CachedItemLists;
                _itemListCalls = stored.ItemListCalls;
                _resultCalls = stored.ItemResultCalls;
                _completedResultCalls = stored.CompletedResultCalls;
                _failedResultCalls = stored.FailedCalls;
                _randomPivot = stored.RandomPivot;
                if (_processedContracts.Count == 0 &&
                    stored.ContractsScanned > 0 &&
                    !await ReconstructMigratedContractsAsync(
                            stored,
                            contractSearch,
                            candidateExpression,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    _randomPivot = Random.Shared.NextInt64(1, long.MaxValue);
                    var resetStartedAt = DateTimeOffset.UtcNow;
                    var resetSessionId = Guid.NewGuid();
                    await _temporaryResults.ResetTraversalAsync(
                            resetSessionId,
                            anchorKey,
                            scopeKey,
                            contractSearch.Text ?? string.Empty,
                            _randomPivot,
                            resetStartedAt,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return await StartFreshTraversalAsync(
                            resetSessionId,
                            resetStartedAt,
                            contractSearch,
                            candidateExpression,
                            resetStore: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!string.Equals(stored.CriteriaText, contractSearch.Text, StringComparison.Ordinal))
                {
                    await RefilterProcessedContractsAsync(contractSearch.Text, cancellationToken)
                        .ConfigureAwait(false);
                    await _temporaryResults.UpdateCriteriaAsync(contractSearch.Text, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    RestoreHits(stored.Hits);
                }

                _currentGeographicStage = stored.CandidateSetExhausted
                    ? "conjunto esgotado"
                    : "pesquisa retomada";
                var restoredSession = new ItemSearchSession(
                    stored.Id,
                    contractSearch.Text ?? string.Empty,
                    stored.StartedAt,
                    stored.ContractsScanned + (stored.CandidateSetExhausted ? 0 : 1),
                    DefaultPageSize,
                    stored.RandomPivot);
                _session = restoredSession;
                return restoredSession;
            }

            _randomPivot = Random.Shared.NextInt64(1, long.MaxValue);
            var startedAt = DateTimeOffset.UtcNow;
            var sessionId = Guid.NewGuid();
            return await StartFreshTraversalAsync(
                    sessionId,
                    startedAt,
                    contractSearch,
                    candidateExpression,
                    resetStore: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<ItemSearchSession> StartFreshTraversalAsync(
        Guid sessionId,
        DateTimeOffset startedAt,
        SearchQuery contractSearch,
        SearchExpression candidateExpression,
        bool resetStore,
        CancellationToken cancellationToken)
    {
        if (resetStore)
        {
            await _temporaryResults.ResetAsync(
                    sessionId,
                    _anchorKey,
                    _scopeKey,
                    contractSearch.Text ?? string.Empty,
                    _randomPivot,
                    startedAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var firstPage = await _repository.SearchItemCandidatesAsync(
                _contractSearchQuery!,
                candidateExpression,
                _randomPivot,
                null,
                200,
                cancellationToken)
            .ConfigureAwait(false);
        AddCandidates(firstPage.Results);
        _candidateCursor = firstPage.NextCursor;
        _candidateSourceExhausted = !firstPage.HasMore;
        _currentGeographicStage = firstPage.Results.Count == 0
            ? "conjunto esgotado"
            : DescribeGeographicStage(firstPage.Results[0].Contract);
        var newSession = new ItemSearchSession(
            sessionId,
            contractSearch.Text ?? string.Empty,
            startedAt,
            firstPage.Results.Count + (firstPage.HasMore ? 1 : 0),
            DefaultPageSize,
            _randomPivot);
        _session = newSession;
        if (!HasMoreContractCandidates)
        {
            await SaveCheckpointAsync(true, cancellationToken).ConfigureAwait(false);
        }

        return newSession;
    }

    private void RestoreHits(IEnumerable<ItemSearchHit> hits)
    {
        foreach (var hit in hits)
        {
            if (_hitKeys.Add((hit.Contract.PncpId, hit.Item.ItemNumber)))
            {
                _hits.Add(hit);
            }
        }

        _deliveredHitCount = _hits.Count;
    }

    private async Task RefilterProcessedContractsAsync(
        string criteriaText,
        CancellationToken cancellationToken)
    {
        var hits = await _repository.SearchItemsAsync(
                _processedContracts,
                criteriaText,
                cancellationToken)
            .ConfigureAwait(false);
        _hits.Clear();
        _hitKeys.Clear();
        foreach (var hit in hits)
        {
            if (_hitKeys.Add((hit.Contract.PncpId, hit.Item.ItemNumber)))
            {
                _hits.Add(hit);
            }
        }

        _deliveredHitCount = _hits.Count;
        await _temporaryResults.ReplaceHitsAsync(_hits, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReconstructMigratedContractsAsync(
        StoredItemSearchSession stored,
        SearchQuery contractSearch,
        SearchExpression candidateExpression,
        CancellationToken cancellationToken)
    {
        var storedExpression = SearchText.Parse(stored.CriteriaText);
        if (!string.Equals(
                storedExpression.CandidateMatchQuery,
                candidateExpression.CandidateMatchQuery,
                StringComparison.Ordinal) ||
            !string.Equals(
                storedExpression.ExplicitContractMatchQuery,
                candidateExpression.ExplicitContractMatchQuery,
                StringComparison.Ordinal))
        {
            return false;
        }

        ItemCandidateCursor? cursor = null;
        while (_processedContracts.Count < stored.ContractsScanned)
        {
            var page = await _repository.SearchItemCandidatesAsync(
                    contractSearch with { Page = 1, PageSize = 200 },
                    candidateExpression,
                    stored.RandomPivot,
                    cursor,
                    Math.Min(200, stored.ContractsScanned - _processedContracts.Count),
                    cancellationToken)
                .ConfigureAwait(false);
            if (page.Results.Count == 0)
            {
                return false;
            }

            foreach (var candidate in page.Results)
            {
                if (_processedContractKeys.Add(candidate.Contract.PncpId))
                {
                    _processedContracts.Add(candidate.Contract);
                    await _temporaryResults.SaveProcessedContractAsync(
                            candidate.Contract,
                            _processedContracts.Count,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            cursor = page.NextCursor;
        }

        return Equals(cursor, stored.Cursor);
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
                    new PriceBatchRequest(
                        1,
                        true,
                        PriceBatchBudgetMode.CandidateContracts),
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

        if (request.ExactContractCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A quantidade exata de contratações deve ser positiva.");
        }

        if (request.RequestedContracts > 500 && !request.LargeRequestConfirmed)
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
        var throttledProgress = new ThrottledProgress<PriceBatchProgress>(progress, ProgressInterval);
        var throttledRows = new CoalescingRowProgress(rowProgress, ProgressInterval);
        try
        {
            var callsAtStart = Volatile.Read(ref _completedResultCalls);
            var failuresAtStart = Volatile.Read(ref _failedResultCalls);
            var contractsAtStart = _contractsScanned;
            var expandedAtStart = _contractsExpanded;
            var resolvedAtStart = _fullyResolvedContracts;
            var previousFailures = await FindFailedItemsAsync(
                    ItemSearchDefaults.ContractsPerBatch,
                    linked.Token)
                .ConfigureAwait(false);
            var previousContractFailures = await _temporaryResults.GetContractFailuresAsync(
                    ItemSearchDefaults.ContractsPerBatch,
                    linked.Token)
                .ConfigureAwait(false);
            int BudgetUsed() => request.BudgetMode == PriceBatchBudgetMode.UnresolvedContracts
                ? _contractsExpanded - expandedAtStart
                : _contractsScanned - contractsAtStart;
            while (BudgetUsed() < request.RequestedContracts)
            {
                var networkConcurrency = CurrentNetworkConcurrency;
                var window = new List<ItemContractCandidate>(networkConcurrency);
                var windowSize = Math.Min(
                    networkConcurrency,
                    request.RequestedContracts - BudgetUsed());
                while (window.Count < windowSize &&
                       await EnsureNextCandidateAvailableAsync(linked.Token).ConfigureAwait(false))
                {
                    window.Add(_candidates[_nextCandidateIndex++]);
                }

                if (window.Count == 0)
                {
                    break;
                }

                var preparedWindow = new PreparedCandidate?[window.Count];
                var pendingPreparations = window
                    .Select((candidate, index) => (
                        Task: PrepareCandidateAsync(
                            candidate,
                            PncpRequestPriority.AdditionalBatches,
                            linked.Token),
                        Index: index))
                    .ToDictionary(value => value.Task, value => value.Index);
                var preparedIndex = 0;
                var completedPreparations = 0;
                var budgetReached = false;
                try
                {
                    while (pendingPreparations.Count > 0)
                    {
                        var completedTask = await Task.WhenAny(pendingPreparations.Keys).ConfigureAwait(false);
                        var completedIndex = pendingPreparations[completedTask];
                        pendingPreparations.Remove(completedTask);
                        preparedWindow[completedIndex] = await completedTask.ConfigureAwait(false);
                        completedPreparations++;
                        throttledProgress.Report(CreateProgress(
                            Volatile.Read(ref _completedResultCalls) - callsAtStart,
                            Volatile.Read(ref _completedResultCalls) - callsAtStart,
                            Volatile.Read(ref _failedResultCalls) - failuresAtStart,
                            false,
                            $"Janela de rede: {completedPreparations:N0}/{window.Count:N0} resposta(s) concluída(s); " +
                            $"{BudgetUsed():N0}/{request.RequestedContracts:N0} contratação(ões) confirmada(s).",
                            requestedContracts: request.RequestedContracts,
                            processedContracts: BudgetUsed()));

                        while (!budgetReached &&
                               preparedIndex < preparedWindow.Length &&
                               preparedWindow[preparedIndex] is { } prepared)
                        {
                            var discovery = await CompletePreparedCandidateAsync(prepared, linked.Token)
                                .ConfigureAwait(false);
                            var matchingHits = discovery.MatchingHits;
                            IReadOnlyList<ItemSearchHit> toHydrate = [];
                            if (matchingHits.Count > 0)
                            {
                                var availableRows = (await BuildRowsAsync(
                                    matchingHits,
                                    minimumUnitPrice,
                                    maximumUnitPrice,
                                    linked.Token)
                                    .ConfigureAwait(false))
                                    .Where(row => row.PriceState != ItemSearchPriceState.Pending)
                                    .ToArray();
                                if (availableRows.Length > 0)
                                {
                                    throttledRows.Report(availableRows);
                                }

                                toHydrate = await FilterItemsNeedingApiAsync(
                                        matchingHits.Where(hit => hit.Item.HasResult),
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
                                        completedRowProgress: throttledRows,
                                        minimumUnitPrice: minimumUnitPrice,
                                        maximumUnitPrice: maximumUnitPrice)
                                    .ConfigureAwait(false);

                                var finalRows = (await BuildRowsAsync(
                                        matchingHits,
                                        minimumUnitPrice,
                                        maximumUnitPrice,
                                        linked.Token)
                                    .ConfigureAwait(false))
                                    .ToArray();
                                throttledRows.Report(finalRows);
                            }

                            if (discovery.FreshItemListUsed || toHydrate.Count > 0)
                            {
                                _contractsExpanded = checked(_contractsExpanded + 1);
                            }
                            else
                            {
                                _fullyResolvedContracts = checked(_fullyResolvedContracts + 1);
                            }

                            _processedCandidateCursor = discovery.Cursor;
                            var nextPreparedIndex = preparedIndex + 1;
                            var windowHasPending = nextPreparedIndex < preparedWindow.Length;
                            await SaveCheckpointAsync(
                                    !windowHasPending && !HasMoreContractCandidates,
                                    linked.Token)
                                .ConfigureAwait(false);
                            preparedIndex = nextPreparedIndex;
                            var examinedContracts = _contractsScanned - contractsAtStart;
                            var expandedContracts = _contractsExpanded - expandedAtStart;
                            var resolvedContracts = _fullyResolvedContracts - resolvedAtStart;
                            var processedContracts = BudgetUsed();
                            throttledProgress.Report(CreateProgress(
                                Volatile.Read(ref _completedResultCalls) - callsAtStart,
                                Volatile.Read(ref _completedResultCalls) - callsAtStart,
                                Volatile.Read(ref _failedResultCalls) - failuresAtStart,
                                false,
                                $"Cobertura nova: {expandedContracts:N0} de {request.RequestedContracts:N0}; " +
                                $"janela {completedPreparations:N0}/{window.Count:N0}; " +
                                $"{examinedContracts:N0} candidata(s) examinada(s), " +
                                $"{resolvedContracts:N0} já resolvida(s) localmente; " +
                                $"{_hits.Count:N0} item(ns) compatível(is) descoberto(s).",
                                requestedContracts: request.RequestedContracts,
                                processedContracts: processedContracts));
                            budgetReached = BudgetUsed() >= request.RequestedContracts;
                        }
                    }
                }
                catch
                {
                    try
                    {
                        await Task.WhenAll(pendingPreparations.Keys).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the original cancellation or failure.
                    }

                    throw;
                }
                finally
                {
                    if (preparedIndex < window.Count)
                    {
                        _nextCandidateIndex -= window.Count - preparedIndex;
                    }
                }
            }

            if (previousContractFailures.Count > 0)
            {
                await RetryPreviousContractFailuresAsync(
                        previousContractFailures,
                        throttledRows,
                        minimumUnitPrice,
                        maximumUnitPrice,
                        linked.Token)
                    .ConfigureAwait(false);
            }

            if (previousFailures.Count > 0)
            {
                await HydrateSelectedAsync(
                        previousFailures,
                        PncpRequestPriority.AdditionalBatches,
                        progress: null,
                        requestedCalls: previousFailures.Count,
                        completedBaseline: callsAtStart,
                        failedBaseline: failuresAtStart,
                        retryFailures: true,
                        cancellationToken: linked.Token,
                        completedRowProgress: throttledRows,
                        minimumUnitPrice: minimumUnitPrice,
                        maximumUnitPrice: maximumUnitPrice)
                    .ConfigureAwait(false);
            }

            var completed = Volatile.Read(ref _completedResultCalls) - callsAtStart;
            var failed = Volatile.Read(ref _failedResultCalls) - failuresAtStart;
            var examined = _contractsScanned - contractsAtStart;
            var expanded = _contractsExpanded - expandedAtStart;
            var resolved = _fullyResolvedContracts - resolvedAtStart;
            var processed = BudgetUsed();
            var exhausted = !HasMoreContractCandidates;
            await SaveCheckpointAsync(exhausted, linked.Token).ConfigureAwait(false);
            _deliveredHitCount = _hits.Count;
            var message = exhausted
                ? $"Conjunto esgotado após {examined:N0} candidata(s) examinada(s) nesta ação."
                : $"Ação concluída: {expanded:N0} contratação(ões) ampliada(s), " +
                  $"{resolved:N0} resolvida(s) pelo cache e {examined:N0} examinada(s).";
            var result = CreateProgress(
                completed,
                completed,
                failed,
                exhausted,
                message,
                requestedContracts: request.RequestedContracts,
                processedContracts: processed);
            throttledRows.Flush();
            throttledProgress.Report(result, force: true);
            return result;
        }
        finally
        {
            throttledRows.Flush();
            throttledProgress.Flush();
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

        var candidate = _candidates[_nextCandidateIndex];
        var contract = candidate.Contract;
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
                await PersistAsync(
                        token => _repository.UpsertItemsAsync(
                            contract.PncpId,
                            items,
                            false,
                            token),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await _temporaryResults.RemoveContractFailureAsync(contract.PncpId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A failure in one contract must not hide already indexed items nor
                // prevent the remaining contracts from being searched.
                Interlocked.Increment(ref _failedResultCalls);
                try
                {
                    await _temporaryResults.SaveContractFailureAsync(
                            contract.PncpId,
                            exception.Message,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original API failure in the progress result.
                }
            }
        }
        else
        {
            _cachedItemLists++;
            await _temporaryResults.RemoveContractFailureAsync(contract.PncpId, cancellationToken)
                .ConfigureAwait(false);
        }

        var matchingHits = await AddMatchingHitsAsync(contract, session.Text, cancellationToken)
            .ConfigureAwait(false);
        if (_processedContractKeys.Add(contract.PncpId))
        {
            _processedContracts.Add(contract);
            await _temporaryResults.SaveProcessedContractAsync(
                    contract,
                    _processedContracts.Count,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new DiscoveryAttempt(
            true,
            needsFreshItemList,
            false,
            candidate.Cursor,
            matchingHits);
    }

    private async Task<PreparedCandidate> PrepareCandidateAsync(
        ItemContractCandidate candidate,
        PncpRequestPriority priority,
        CancellationToken cancellationToken)
    {
        var contract = candidate.Contract;
        var snapshot = await _repository.GetItemSnapshotAsync(contract.PncpId, cancellationToken)
            .ConfigureAwait(false);
        var needsFreshItemList = snapshot is null || !snapshot.IsCurrentFor(contract);
        if (!needsFreshItemList)
        {
            return new PreparedCandidate(candidate, false, null);
        }

        Interlocked.Increment(ref _itemListCalls);
        try
        {
            using var requestScope = PncpRequestOptions.BeginScope(priority, PncpRequestCategory.ItemLists);
            var items = await _client.GetItemsAsync(contract, cancellationToken).ConfigureAwait(false);
            await PersistAsync(
                    token => _repository.UpsertItemsAsync(
                        contract.PncpId,
                        items,
                        false,
                        token),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new PreparedCandidate(candidate, true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new PreparedCandidate(candidate, true, exception);
        }
    }

    private async Task<DiscoveryAttempt> CompletePreparedCandidateAsync(
        PreparedCandidate prepared,
        CancellationToken cancellationToken)
    {
        var session = GetRequiredSession();
        var contract = prepared.Candidate.Contract;
        _contractsScanned++;
        _currentGeographicStage = DescribeGeographicStage(contract);
        if (prepared.Failure is not null)
        {
            Interlocked.Increment(ref _failedResultCalls);
            await _temporaryResults.SaveContractFailureAsync(
                    contract.PncpId,
                    prepared.Failure.Message,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        else
        {
            if (!prepared.FreshItemListUsed)
            {
                _cachedItemLists++;
            }

            await _temporaryResults.RemoveContractFailureAsync(contract.PncpId, cancellationToken)
                .ConfigureAwait(false);
        }

        var matchingHits = await AddMatchingHitsAsync(contract, session.Text, cancellationToken)
            .ConfigureAwait(false);
        if (_processedContractKeys.Add(contract.PncpId))
        {
            _processedContracts.Add(contract);
            await _temporaryResults.SaveProcessedContractAsync(
                    contract,
                    _processedContracts.Count,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new DiscoveryAttempt(
            true,
            prepared.FreshItemListUsed,
            false,
            prepared.Candidate.Cursor,
            matchingHits);
    }

    private async Task RetryPreviousContractFailuresAsync(
        IReadOnlyList<StoredContractFailure> failures,
        IProgress<IReadOnlyList<ItemSearchRow>>? rowProgress,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        CancellationToken cancellationToken)
    {
        var session = GetRequiredSession();
        foreach (var failure in failures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contract = await _repository.GetContractAsync(failure.ContractId, cancellationToken)
                .ConfigureAwait(false);
            if (contract is null)
            {
                await _temporaryResults.RemoveContractFailureAsync(
                        failure.ContractId,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var snapshot = await _repository.GetItemSnapshotAsync(contract.PncpId, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot?.IsCurrentFor(contract) != true)
            {
                Interlocked.Increment(ref _itemListCalls);
                try
                {
                    using var requestScope = PncpRequestOptions.BeginScope(
                        PncpRequestPriority.AdditionalBatches,
                        PncpRequestCategory.ItemLists);
                    var items = await _client.GetItemsAsync(contract, cancellationToken).ConfigureAwait(false);
                    await PersistAsync(
                            token => _repository.UpsertItemsAsync(
                                contract.PncpId,
                                items,
                                false,
                                token),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await _temporaryResults.RemoveContractFailureAsync(
                            contract.PncpId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref _failedResultCalls);
                    await _temporaryResults.SaveContractFailureAsync(
                            contract.PncpId,
                            exception.Message,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }
            }
            else
            {
                _cachedItemLists++;
                await _temporaryResults.RemoveContractFailureAsync(
                        contract.PncpId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var newlyDiscovered = await AddMatchingHitsAsync(
                    contract,
                    session.Text,
                    cancellationToken)
                .ConfigureAwait(false);
            if (newlyDiscovered.Count == 0)
            {
                continue;
            }

            var toHydrate = await FilterItemsNeedingApiAsync(
                    newlyDiscovered.Where(hit => hit.Item.HasResult),
                    retryFailures: true,
                    excludedKeys: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await HydrateSelectedAsync(
                    toHydrate,
                    PncpRequestPriority.AdditionalBatches,
                    progress: null,
                    requestedCalls: toHydrate.Count,
                    completedBaseline: 0,
                    failedBaseline: 0,
                    retryFailures: true,
                    cancellationToken: cancellationToken,
                    completedRowProgress: rowProgress,
                    minimumUnitPrice: minimumUnitPrice,
                    maximumUnitPrice: maximumUnitPrice)
                .ConfigureAwait(false);
            if (rowProgress is not null)
            {
                rowProgress.Report(await BuildRowsAsync(
                        newlyDiscovered,
                        minimumUnitPrice,
                        maximumUnitPrice,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
        }
    }

    private async Task<IReadOnlyList<ItemSearchHit>> AddMatchingHitsAsync(
        ContractRecord contract,
        string searchText,
        CancellationToken cancellationToken)
    {
        var matches = await _repository.SearchItemsAsync(
                contract.PncpId,
                searchText,
                cancellationToken)
            .ConfigureAwait(false);
        var matchingHits = new List<ItemSearchHit>(matches.Count);
        var storedHits = new List<(ItemSearchHit Hit, long DiscoveredOrder)>();
        foreach (var item in matches)
        {
            var hit = new ItemSearchHit(contract, item);
            matchingHits.Add(hit);
            var key = (ContractId: contract.PncpId, ItemNumber: item.ItemNumber);
            if (!_hitKeys.Add(key))
            {
                var existingIndex = _hits.FindIndex(value =>
                    value.Contract.PncpId == key.ContractId &&
                    value.Item.ItemNumber == key.ItemNumber);
                if (existingIndex >= 0)
                {
                    _hits[existingIndex] = hit;
                    storedHits.Add((hit, existingIndex + 1L));
                }

                continue;
            }

            _hits.Add(hit);
            storedHits.Add((hit, _hits.Count));
        }

        await _temporaryResults.SaveHitsAsync(storedHits, cancellationToken).ConfigureAwait(false);
        return matchingHits;
    }

    private bool HasMoreContractCandidates =>
        _nextCandidateIndex < _candidates.Count || !_candidateSourceExhausted;

    private async Task PersistAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await PersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PersistenceGate.Release();
        }
    }

    private Task SaveCheckpointAsync(
        bool candidateSetExhausted,
        CancellationToken cancellationToken) =>
        _temporaryResults.SaveCheckpointAsync(
            _processedCandidateCursor,
            _contractsScanned,
            _contractsExpanded,
            _fullyResolvedContracts,
            _cachedItemLists,
            Volatile.Read(ref _itemListCalls),
            Volatile.Read(ref _resultCalls),
            Volatile.Read(ref _completedResultCalls),
            Volatile.Read(ref _failedResultCalls),
            candidateSetExhausted,
            cancellationToken);

    private async Task<bool> EnsureNextCandidateAvailableAsync(CancellationToken cancellationToken)
    {
        while (_nextCandidateIndex >= _candidates.Count && !_candidateSourceExhausted)
        {
            var query = _contractSearchQuery
                ?? throw new InvalidOperationException("Fonte paginada de contratações ausente.");
            var expression = _candidateExpression
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
            AddCandidates(next.Results);
            _candidateCursor = next.NextCursor;
            _candidateSourceExhausted = !next.HasMore || next.Results.Count == 0;
            if (!_candidateSourceExhausted && Equals(previousCursor, _candidateCursor))
            {
                throw new InvalidOperationException("A continuação de candidatos não avançou o cursor.");
            }
        }

        return _nextCandidateIndex < _candidates.Count;
    }

    private void AddCandidates(IEnumerable<ItemContractCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            var contract = candidate.Contract;
            if (_candidateKeys.Add(contract.PncpId))
            {
                _candidates.Add(candidate);
            }
        }
    }

    private void AddCandidates(IEnumerable<ContractRecord> contracts)
    {
        foreach (var contract in contracts)
        {
            if (_candidateKeys.Add(contract.PncpId))
            {
                _candidates.Add(new ItemContractCandidate(
                    contract,
                    new ItemCandidateCursor(0, 0, 0, _candidates.Count, contract.PncpId)));
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

    private async Task<IReadOnlyList<ItemSearchHit>> FindFailedItemsAsync(
        int maximum,
        CancellationToken cancellationToken)
    {
        var result = new List<ItemSearchHit>(Math.Min(maximum, DefaultPageSize));
        foreach (var hit in _hits.Where(value => value.Item.HasResult))
        {
            var availability = await GetPriceAvailabilityAsync(hit, cancellationToken).ConfigureAwait(false);
            if (availability.Kind != PriceAvailabilityKind.TemporaryFailure)
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
        var networkConcurrency = CurrentNetworkConcurrency;
        using var semaphore = new SemaphoreSlim(networkConcurrency, networkConcurrency);
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
            await PersistAsync(
                    token => _temporaryResults.SaveSuccessAsync(
                        hit.Contract.PncpId,
                        hit.Item.ItemNumber,
                        results,
                        token),
                    CancellationToken.None)
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
                await PersistAsync(
                        token => _temporaryResults.SaveFailureAsync(
                            hit.Contract.PncpId,
                            hit.Item.ItemNumber,
                            exception.Message,
                            token),
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
                continue;
            }

            var temporary = availability.Temporary!;
            if (availability.Kind == PriceAvailabilityKind.TemporaryFailure)
            {
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
            return;
        }

        foreach (var result in results)
        {
            var price = result.HomologatedUnitValue;
            if (price is not > 0)
            {
                continue;
            }

            if (hasRange)
            {
                if (!result.IsActive ||
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
            _priceAvailability.Values.Sum(CountRevealedPrices),
            _contractsExpanded,
            _fullyResolvedContracts,
            -1,
            Volatile.Read(ref _resultCalls),
            Volatile.Read(ref _failedResultCalls));
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

    private static string CreateAnchorKey(SearchExpression expression, string? text) =>
        expression.AnchorTerm.Length > 0
            ? expression.AnchorTerm
            : $"exact:{SearchText.Normalize(text)}";

    private static string CreateScopeKey(SearchQuery query, SearchExpression expression)
    {
        var geo = query.EffectiveGeoFilter;
        return string.Join(
            '\u001F',
            ((int)geo.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            geo.Uf ?? string.Empty,
            query.StartDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            query.EndDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            expression.ExplicitContractMatchQuery);
    }

    private static SearchExpression CreateCandidateExpression(SearchExpression expression)
    {
        if (expression.AnchorTerm.Length == 0)
        {
            return expression;
        }

        return SearchText.Parse(SearchText.ReplaceContractCandidates(
            expression.AnchorTerm,
            expression.ContractCandidates.Select(candidate => candidate.Text)));
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
        bool BudgetBlocked,
        ItemCandidateCursor? Cursor,
        IReadOnlyList<ItemSearchHit> MatchingHits)
    {
        public static DiscoveryAttempt NoCandidate { get; } = new(false, false, false, null, []);
        public static DiscoveryAttempt BudgetLimit { get; } = new(false, false, true, null, []);
    }

    private sealed record PreparedCandidate(
        ItemContractCandidate Candidate,
        bool FreshItemListUsed,
        Exception? Failure);

    private sealed class ThrottledProgress<T>(IProgress<T>? inner, TimeSpan interval)
    {
        private readonly object _gate = new();
        private long _lastReportTimestamp;
        private T? _pending;
        private bool _hasPending;

        public void Report(T value, bool force = false)
        {
            if (inner is null)
            {
                return;
            }

            T? toReport = default;
            var shouldReport = false;
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                if (force ||
                    _lastReportTimestamp == 0 ||
                    Stopwatch.GetElapsedTime(_lastReportTimestamp, now) >= interval)
                {
                    _lastReportTimestamp = now;
                    _pending = default;
                    _hasPending = false;
                    toReport = value;
                    shouldReport = true;
                }
                else
                {
                    _pending = value;
                    _hasPending = true;
                }
            }

            if (shouldReport)
            {
                inner.Report(toReport!);
            }
        }

        public void Flush()
        {
            if (inner is null)
            {
                return;
            }

            T? toReport = default;
            lock (_gate)
            {
                if (!_hasPending)
                {
                    return;
                }

                toReport = _pending;
                _pending = default;
                _hasPending = false;
                _lastReportTimestamp = Stopwatch.GetTimestamp();
            }

            inner.Report(toReport!);
        }
    }

    private sealed class CoalescingRowProgress(
        IProgress<IReadOnlyList<ItemSearchRow>>? inner,
        TimeSpan interval) : IProgress<IReadOnlyList<ItemSearchRow>>
    {
        private readonly object _gate = new();
        private readonly List<ItemSearchRow> _pending = [];
        private long _lastReportTimestamp;

        public void Report(IReadOnlyList<ItemSearchRow> value)
        {
            if (inner is null || value.Count == 0)
            {
                return;
            }

            IReadOnlyList<ItemSearchRow>? toReport = null;
            lock (_gate)
            {
                _pending.AddRange(value);
                var now = Stopwatch.GetTimestamp();
                if (_lastReportTimestamp == 0 ||
                    Stopwatch.GetElapsedTime(_lastReportTimestamp, now) >= interval)
                {
                    toReport = _pending.ToArray();
                    _pending.Clear();
                    _lastReportTimestamp = now;
                }
            }

            if (toReport is not null)
            {
                inner.Report(toReport);
            }
        }

        public void Flush()
        {
            if (inner is null)
            {
                return;
            }

            IReadOnlyList<ItemSearchRow>? toReport = null;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                toReport = _pending.ToArray();
                _pending.Clear();
                _lastReportTimestamp = Stopwatch.GetTimestamp();
            }

            inner.Report(toReport);
        }
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
