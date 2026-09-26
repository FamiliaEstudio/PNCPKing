using System.Diagnostics;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    private MainSearchLocalSequence? _localSearchSequence;
    private int _activeCompoundPart;
    private int _compoundReadGeneration;

    public bool IsCompoundSearch => _localSearchSequence is not null;
    public string MainSearchApiHint => IsCompoundSearch
        ? "Pesquisas com '+' consultam somente o banco local."
        : "Consulta explicitamente os próximos lotes no PNCP.";

    private bool HasCurrentQuotationSample => IsCompoundSearch
        ? !_isLocalPricePageLoading && !_isResultPageLoading && _currentItemResultKeys.Count > 0
        : _itemSearchService.CurrentSession is not null;

    private Task<IReadOnlyList<ItemSearchRow>> GetCurrentQuotationSampleAsync(decimal? minimum, decimal? maximum)
    {
        if (!IsCompoundSearch)
            return _itemSearchService.GetDiscoveredRowsAsync(minimumUnitPrice: minimum, maximumUnitPrice: maximum);
        IReadOnlyList<ItemSearchRow> rows = ItemSearchRows
            .Where(row => _currentItemResultKeys.Contains(RowKey(row)))
            .Select(row => row.Source)
            .Where(row => row.Result is { IsActive: true, HomologatedUnitValue: > 0 } &&
                (minimum is null || row.HomologatedUnitValue >= minimum) &&
                (maximum is null || row.HomologatedUnitValue <= maximum)).ToArray();
        return Task.FromResult(rows);
    }

    private string CompoundCriteriaLabel => _localSearchSequence is { } sequence
        ? $"Trecho {_activeCompoundPart + 1}/{sequence.Expressions.Count}: {sequence.Expressions[_activeCompoundPart].OriginalText}"
        : string.Empty;

    private bool MatchesCurrentMainCriteria(ItemSearchRow row) =>
        _activeItemSearchExpression is null ||
        _activeItemSearchExpression.MatchesItem(row.Item.Description, row.Item.Unit) ||
        (_localSearchSequence?.Expressions.Any(expression =>
            expression != _activeItemSearchExpression && expression.MatchesItem(row.Item.Description, row.Item.Unit)) ?? false);

    private string MainCriteriaDescription()
    {
        var expressions = SearchText.ParseMainCriteria(QueryText);
        var description = string.Join(" + ", expressions.Select(expression => expression.PositiveText)
            .Where(text => text.Length > 0));
        return description.Length > 0 ? description : QueryText.Trim();
    }

    private void SelectCompoundPart(MainSearchLocalSequence sequence, int part)
    {
        if (_activeCompoundPart == part && _activeSearchQuery == sequence.Queries[part]) return;
        _activeCompoundPart = part;
        _activeSearchQuery = sequence.Queries[part];
        _activeItemSearchExpression = sequence.Expressions[part];
        InvalidateContractResults();
    }

    private string BuildCompoundSummary(int added) =>
        $"{CompoundCriteriaLabel}. Mais {added:N0} preço(s) local(is); {_localPriceRowsLoaded:N0} exibido(s). " +
        (_localSearchSequence?.HasMore == true
            ? "Há trechos pendentes; Carregar mais resultados alterna para o próximo."
            : "Todos os trechos foram concluídos.");

    private async Task<int> LoadCompoundPricePageAsync(CancellationToken cancellationToken,
        decimal? minimum = null, decimal? maximum = null)
    {
        var sequence = _localSearchSequence!;
        if (minimum is null && maximum is null) (minimum, maximum) = ParsePriceRange();
        var generation = Volatile.Read(ref _contractSearchGeneration);
        var dataVersion = _localPriceDataVersion;
        var readGeneration = ++_compoundReadGeneration;
        bool IsCurrent() => generation == Volatile.Read(ref _contractSearchGeneration) &&
            dataVersion == _localPriceDataVersion && ReferenceEquals(sequence, _localSearchSequence) &&
            readGeneration == _compoundReadGeneration;
        var added = 0;
        _localPendingApplyKeys.Clear();
        _localApplyStarted = Stopwatch.GetTimestamp();
        _localApplyGeneration = generation;
        _localAppliedRows = 0;
        var progress = new Progress<(int Part, PriceCacheLocalProgress Page)>(value =>
        {
            if (!IsCurrent()) return;
            SelectCompoundPart(sequence, value.Part);
            var count = AppendUniqueRows(value.Page.Rows, localPage: true);
            added += count;
            _localPriceRowsLoaded += count;
            _hasMoreLocalPriceRows = sequence.HasMore;
            HasMoreItemCandidates = sequence.HasMore;
            ItemSearchSummary = $"{CompoundCriteriaLabel}. {added:N0}/50 preços locais novos nesta página…";
            StatusText = ItemSearchSummary;
        });
        _isLocalPricePageLoading = true;
        NotifyCommands();
        try
        {
            var page = await Task.Run(() => sequence.LoadNextAsync(minimum, maximum,
                (part, value) => ((IProgress<(int, PriceCacheLocalProgress)>)progress).Report((part, value)),
                cancellationToken), cancellationToken).ConfigureAwait(true);
            if (!IsCurrent()) return 0;
            SelectCompoundPart(sequence, sequence.CurrentPart);
            var count = AppendUniqueRows(page?.Rows ?? [], localPage: true);
            added += count;
            _localPriceRowsLoaded += count;
            await _itemResultBuffer.FlushAsync().ConfigureAwait(true);
            if (!IsCurrent()) return 0;
            _hasMoreLocalPriceRows = sequence.HasMore;
            HasMoreItemCandidates = sequence.HasMore;
            CurrentItemPage = sequence.ActionsStarted;
            ItemSearchSummary = BuildCompoundSummary(added);
            StatusText = ItemSearchSummary;
            await EnsureContractResultsAsync().ConfigureAwait(true);
            return added;
        }
        finally
        {
            if (generation == Volatile.Read(ref _contractSearchGeneration))
            {
                _isLocalPricePageLoading = false;
                NotifyCommands();
            }
        }
    }

    private async Task ReloadCompoundPricesAtomicallyAsync(CancellationToken cancellationToken)
    {
        var previous = _localSearchSequence!;
        var sequence = previous.Restart();
        var actions = Math.Max(1, previous.ActionsStarted);
        var generation = Volatile.Read(ref _contractSearchGeneration);
        var dataVersion = _localPriceDataVersion;
        var (minimum, maximum) = ParsePriceRange();
        _localPricesNeedRefresh = true;
        var rows = await Task.Run(async () =>
        {
            var collected = new List<ItemSearchRow>();
            for (var action = 0; action < actions && sequence.HasMore; action++)
            {
                var page = await sequence.LoadNextAsync(minimum, maximum,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                collected.AddRange(page?.Rows ?? []);
            }
            return collected;
        }, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _contractSearchGeneration) || dataVersion != _localPriceDataVersion ||
            !ReferenceEquals(previous, _localSearchSequence)) return;
        _localSearchSequence = sequence;
        SelectCompoundPart(sequence, sequence.CurrentPart);
        ReplaceVisiblePriceRows(rows);
        _localPricesNeedRefresh = false;
        _hasMoreLocalPriceRows = sequence.HasMore;
        _localPriceRowsLoaded = _currentItemResultKeys.Count;
        CurrentItemPage = sequence.ActionsStarted;
        HasMoreItemCandidates = sequence.HasMore;
        ItemSearchSummary = BuildCompoundSummary(0);
        NotifyCommands();
        await EnsureContractResultsAsync().ConfigureAwait(true);
    }
}
