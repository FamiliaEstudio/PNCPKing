using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

/// <summary>
/// Small, in-memory continuation for the main search. Every part uses the existing
/// discovery reader; no network service or additional database is involved.
/// Callers serialize actions and discard this instance when filters/data change.
/// </summary>
public sealed class MainSearchLocalSequence
{
    private readonly IPriceCacheRepository _repository;
    private readonly PriceCacheLocalCursor?[] _cursors;
    private readonly bool[] _exhausted;
    private int _nextPart;

    public MainSearchLocalSequence(IPriceCacheRepository repository, SearchQuery filters,
        IReadOnlyList<SearchExpression> expressions)
    {
        if (expressions.Count == 0) throw new ArgumentException("Informe ao menos um critério.", nameof(expressions));
        _repository = repository;
        Expressions = expressions;
        Queries = expressions.Select(expression => filters with { Text = expression.OriginalText }).ToArray();
        _cursors = new PriceCacheLocalCursor?[expressions.Count];
        _exhausted = new bool[expressions.Count];
    }

    public IReadOnlyList<SearchExpression> Expressions { get; }
    public IReadOnlyList<SearchQuery> Queries { get; }
    public bool HasMore => _exhausted.Any(exhausted => !exhausted);
    public int CurrentPart { get; private set; }
    public int ActionsStarted { get; private set; }

    public MainSearchLocalSequence Restart() => new(_repository, Queries[0], Expressions);

    public async Task<PriceCacheLocalPage?> LoadNextAsync(decimal? minimum, decimal? maximum,
        Action<int, PriceCacheLocalProgress>? report = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasMore) return null;
        ActionsStarted++;
        PriceCacheLocalPage? last = null;
        for (var visited = 0; visited < Expressions.Count; visited++)
        {
            var part = _nextPart;
            if (_exhausted[part])
            {
                _nextPart = (part + 1) % Expressions.Count;
                continue;
            }
            CurrentPart = part;
            var progress = new InlineProgress(value =>
            {
                // Advance on streamed delivery, including cancellation before page confirmation.
                _cursors[part] = value.Cursor;
                if (value.Completed) _exhausted[part] = !value.HasMore;
                report?.Invoke(part, value);
            });
            last = await _repository.SearchLocalAfterAsync(Queries[part], Expressions[part],
                minimum, maximum, _cursors[part], ItemSearchDefaults.ContractsPerBatch,
                PriceCacheLocalReadOrder.Discovery, progress, cancellationToken).ConfigureAwait(false);
            _cursors[part] = last.Cursor;
            _exhausted[part] = !last.HasMore;
            _nextPart = (part + 1) % Expressions.Count;
            // Even an overlapping page is one bounded action. Deduplication belongs to the grid.
            if (last.Rows is { Count: > 0 }) break;
        }
        return last;
    }

    private sealed class InlineProgress(Action<PriceCacheLocalProgress> report) : IProgress<PriceCacheLocalProgress>
    {
        public void Report(PriceCacheLocalProgress value) => report(value);
    }
}
