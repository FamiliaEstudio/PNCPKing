using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Api;

/// <summary>
/// Priority assigned to a PNCP HTTP request. Lower numeric values are served
/// more frequently, while the weighted schedule still gives every queue a turn.
/// </summary>
public enum PncpRequestPriority
{
    UserSelectedItem = 0,
    VisiblePrices = 1,
    AdditionalBatches = 2,
    IndexMaintenance = 3,
    BackgroundPriceCache = 4
}

public sealed record PncpSchedulerSnapshot(
    int MaximumConcurrency,
    int EffectiveConcurrency,
    int ActiveRequests,
    int QueuedUserSelectedItems,
    int QueuedVisiblePrices,
    int QueuedAdditionalBatches,
    int QueuedIndexMaintenance,
    int QueuedBackgroundPriceCache,
    int ActiveBackgroundPriceCache,
    int BackgroundSuppressions,
    int ConsecutiveSuccesses,
    int ConcurrencyReductions,
    DateTimeOffset? GrowthBlockedUntil,
    TimeSpan? RollingP50 = null,
    TimeSpan? RollingP95 = null,
    double RollingThroughput = 0,
    string? LastReductionReason = null,
    DateTimeOffset? LastConcurrencyChangeAt = null)
{
    public int InitialConcurrency { get; init; }
    public bool AggressiveBackgroundEnabled { get; init; }

    public int CurrentTier => EffectiveConcurrency;

    public int TotalQueued =>
        QueuedUserSelectedItems +
        QueuedVisiblePrices +
        QueuedAdditionalBatches +
        QueuedIndexMaintenance +
        QueuedBackgroundPriceCache;
}

/// <summary>
/// Process-wide request gate with weighted priority and bounded starvation.
/// A single instance must be shared by every PNCP HTTP client in the process.
/// </summary>
public sealed class PncpRequestScheduler
{
    private const int OutcomeWindowSize = 32;
    private static readonly int[] ConcurrencyTiers = [1, 8, 16, 24, 32, 48];

    // Five user-selected requests, two visible-price requests, one additional
    // batch, one maintenance request and one background opportunity per cycle.
    // The background queue has an additional strict-idle gate below.
    private static readonly PncpRequestPriority[] Schedule =
    [
        PncpRequestPriority.UserSelectedItem,
        PncpRequestPriority.UserSelectedItem,
        PncpRequestPriority.UserSelectedItem,
        PncpRequestPriority.UserSelectedItem,
        PncpRequestPriority.UserSelectedItem,
        PncpRequestPriority.VisiblePrices,
        PncpRequestPriority.VisiblePrices,
        PncpRequestPriority.AdditionalBatches,
        PncpRequestPriority.IndexMaintenance,
        PncpRequestPriority.BackgroundPriceCache
    ];

    private readonly object _gate = new();
    private readonly LinkedList<Waiter>[] _queues =
        Enumerable.Range(0, 5).Select(_ => new LinkedList<Waiter>()).ToArray();
    private readonly int[] _activeByPriority = new int[5];
    private readonly int _maximumConcurrency;
    private readonly int _initialConcurrency;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<SuccessfulOutcome> _successfulOutcomes = new();
    private int _effectiveConcurrency;
    private int _activeRequests;
    private int _schedulePosition;
    private int _backgroundSuppressions;
    private int _aggressiveBackgroundModes;
    private int _consecutiveSuccesses;
    private int _concurrencyReductions;
    private int _successesSinceLatencyEvaluation;
    private int _consecutiveSlowWindows;
    private int _pressureEventsAtNormalFloor;
    private DateTimeOffset? _growthBlockedUntil;
    private string? _lastReductionReason;
    private DateTimeOffset? _lastConcurrencyChangeAt;

    public PncpRequestScheduler(
        int maximumConcurrency = 2,
        TimeProvider? timeProvider = null,
        int? initialConcurrency = null)
    {
        if (maximumConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrency),
                "A concorrência máxima deve ser pelo menos 1.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumConcurrency = maximumConcurrency;
        _effectiveConcurrency = Math.Clamp(
            initialConcurrency ?? maximumConcurrency,
            1,
            maximumConcurrency);
        _initialConcurrency = _effectiveConcurrency;
        _lastConcurrencyChangeAt = _timeProvider.GetUtcNow();
    }

    internal static (int MaximumConcurrency, int InitialConcurrency) GetRecommendedConcurrency(
        SystemResourcePressure pressure) => pressure switch
        {
            SystemResourcePressure.Critical => (8, 1),
            SystemResourcePressure.Constrained => (16, 8),
            _ => (48, 16)
        };

    public Task<IDisposable> AcquireAsync(
        PncpRequestPriority priority,
        CancellationToken cancellationToken = default)
    {
        ValidatePriority(priority);
        cancellationToken.ThrowIfCancellationRequested();

        var waiter = new Waiter(this, priority, cancellationToken);
        if (cancellationToken.CanBeCanceled)
        {
            waiter.CancellationRegistration = cancellationToken.UnsafeRegister(
                static state => ((Waiter)state!).Owner.Cancel((Waiter)state),
                waiter);
        }

        lock (_gate)
        {
            if (waiter.CancellationRequested)
            {
                waiter.Completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                waiter.Node = _queues[(int)priority].AddLast(waiter);
                DispatchLocked();
            }
        }

        return waiter.Completion.Task;
    }

    /// <summary>
    /// Prevents a new background request from starting while a foreground
    /// operation is between HTTP calls. A background call already in flight is
    /// deliberately allowed to finish.
    /// </summary>
    public IDisposable SuppressBackgroundRequests()
    {
        lock (_gate)
        {
            _backgroundSuppressions++;
        }

        return new SuppressionLease(this);
    }

    /// <summary>
    /// Allows the rebuildable item index to occupy every otherwise idle
    /// scheduler slot. Foreground suppression and higher-priority queues still
    /// take precedence, and adaptive concurrency remains authoritative.
    /// </summary>
    public IDisposable EnableAggressiveBackgroundRequests()
    {
        lock (_gate)
        {
            _aggressiveBackgroundModes++;
            DispatchLocked();
        }

        return new AggressiveBackgroundLease(this);
    }

    public PncpSchedulerSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var (p50, p95, throughput) = CalculateRollingMetricsLocked();
            return new PncpSchedulerSnapshot(
                _maximumConcurrency,
                _effectiveConcurrency,
                _activeRequests,
                _queues[0].Count,
                _queues[1].Count,
                _queues[2].Count,
                _queues[3].Count,
                _queues[4].Count,
                _activeByPriority[(int)PncpRequestPriority.BackgroundPriceCache],
                _backgroundSuppressions,
                _consecutiveSuccesses,
                _concurrencyReductions,
                _growthBlockedUntil,
                p50,
                p95,
                throughput,
                _lastReductionReason,
                _lastConcurrencyChangeAt)
            {
                InitialConcurrency = _initialConcurrency,
                AggressiveBackgroundEnabled = _aggressiveBackgroundModes > 0
            };
        }
    }

    internal void ReportOutcome(
        PncpRequestCategory category,
        System.Net.HttpStatusCode? statusCode,
        TimeSpan duration,
        TimeSpan? retryAfter = null,
        bool transportFailure = false)
    {
        if (category is not (PncpRequestCategory.ItemLists or PncpRequestCategory.ItemResults))
        {
            return;
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (transportFailure ||
                statusCode == System.Net.HttpStatusCode.RequestTimeout ||
                statusCode is >= System.Net.HttpStatusCode.InternalServerError)
            {
                ApplyPressureLocked(
                    transportFailure
                        ? "falha de transporte"
                        : statusCode == System.Net.HttpStatusCode.RequestTimeout
                            ? "timeout"
                            : $"HTTP {(int)statusCode.GetValueOrDefault()}",
                    now,
                    now.AddMinutes(1));
                return;
            }

            if (statusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var cooldown = retryAfter.GetValueOrDefault() > TimeSpan.FromMinutes(2)
                    ? retryAfter.GetValueOrDefault()
                    : TimeSpan.FromMinutes(2);
                SetConcurrencyLocked(1, "HTTP 429", now, countAsReduction: true);
                BlockGrowthUntilLocked(now.Add(cooldown));
                ResetSuccessWindowLocked();
                return;
            }

            if (statusCode is < System.Net.HttpStatusCode.OK or >= System.Net.HttpStatusCode.MultipleChoices)
            {
                return;
            }

            RecordSuccessLocked(duration, now);
        }
    }

    private void RecordSuccessLocked(TimeSpan duration, DateTimeOffset now)
    {
        _consecutiveSuccesses++;
        _successesSinceLatencyEvaluation++;
        _successfulOutcomes.Enqueue(new SuccessfulOutcome(duration, now));
        while (_successfulOutcomes.Count > OutcomeWindowSize)
        {
            _successfulOutcomes.Dequeue();
        }

        if (_successesSinceLatencyEvaluation >= OutcomeWindowSize &&
            _successfulOutcomes.Count == OutcomeWindowSize)
        {
            _successesSinceLatencyEvaluation = 0;
            var (_, p95, _) = CalculateRollingMetricsLocked();
            _consecutiveSlowWindows = p95 is { } value && value > TimeSpan.FromSeconds(30)
                ? _consecutiveSlowWindows + 1
                : 0;
            if (_consecutiveSlowWindows >= 2)
            {
                ApplyPressureLocked(
                    $"latência p95 de {p95!.Value.TotalSeconds:N0}s",
                    now,
                    now.AddMinutes(1));
                return;
            }
        }

        if (_growthBlockedUntil is { } blockedUntil && blockedUntil > now)
        {
            _consecutiveSuccesses = 0;
            return;
        }

        if (_effectiveConcurrency >= _maximumConcurrency ||
            _consecutiveSuccesses < OutcomeWindowSize ||
            _successfulOutcomes.Count < OutcomeWindowSize)
        {
            return;
        }

        var (_, rollingP95, _) = CalculateRollingMetricsLocked();
        if (rollingP95 is null || rollingP95 > TimeSpan.FromSeconds(30))
        {
            return;
        }

        var next = NextHigherTier(_effectiveConcurrency, _maximumConcurrency);
        SetConcurrencyLocked(next, reason: null, now, countAsReduction: false);
        _pressureEventsAtNormalFloor = 0;
        ResetSuccessWindowLocked();
        DispatchLocked();
    }

    private void ApplyPressureLocked(
        string reason,
        DateTimeOffset now,
        DateTimeOffset blockedUntil)
    {
        var next = _effectiveConcurrency;
        if (_effectiveConcurrency > 32)
        {
            next = Math.Min(32, _maximumConcurrency);
        }
        else if (_effectiveConcurrency > 24)
        {
            next = Math.Min(24, _maximumConcurrency);
        }
        else if (_effectiveConcurrency > 16)
        {
            next = Math.Min(16, _maximumConcurrency);
        }
        else if (_effectiveConcurrency == 16)
        {
            _pressureEventsAtNormalFloor++;
            if (_pressureEventsAtNormalFloor >= 2)
            {
                next = Math.Min(8, _maximumConcurrency);
                _pressureEventsAtNormalFloor = 0;
            }
        }
        else if (_effectiveConcurrency > 8)
        {
            next = Math.Min(8, _maximumConcurrency);
        }

        SetConcurrencyLocked(next, reason, now, countAsReduction: true);
        BlockGrowthUntilLocked(blockedUntil);
        ResetSuccessWindowLocked();
    }

    private void SetConcurrencyLocked(
        int next,
        string? reason,
        DateTimeOffset now,
        bool countAsReduction)
    {
        next = Math.Clamp(next, 1, _maximumConcurrency);
        if (next == _effectiveConcurrency)
        {
            return;
        }

        var reduced = next < _effectiveConcurrency;
        _effectiveConcurrency = next;
        _lastConcurrencyChangeAt = now;
        if (reduced && countAsReduction)
        {
            _concurrencyReductions++;
            _lastReductionReason = reason;
        }
    }

    private void BlockGrowthUntilLocked(DateTimeOffset blockedUntil)
    {
        _growthBlockedUntil = _growthBlockedUntil is { } current && current > blockedUntil
            ? current
            : blockedUntil;
    }

    private void ResetSuccessWindowLocked()
    {
        _consecutiveSuccesses = 0;
        _successesSinceLatencyEvaluation = 0;
        _consecutiveSlowWindows = 0;
        _successfulOutcomes.Clear();
    }

    private (TimeSpan? P50, TimeSpan? P95, double Throughput) CalculateRollingMetricsLocked()
    {
        if (_successfulOutcomes.Count == 0)
        {
            return (null, null, 0);
        }

        var outcomes = _successfulOutcomes.ToArray();
        var durations = outcomes
            .Select(outcome => outcome.Duration)
            .OrderBy(value => value)
            .ToArray();
        var earliestStart = outcomes.Min(outcome => outcome.CompletedAt - outcome.Duration);
        var latestCompletion = outcomes.Max(outcome => outcome.CompletedAt);
        var elapsedSeconds = Math.Max(
            0.001,
            (latestCompletion - earliestStart).TotalSeconds);
        return (
            Percentile(durations, 0.50),
            Percentile(durations, 0.95),
            outcomes.Length / elapsedSeconds);
    }

    private static TimeSpan Percentile(TimeSpan[] values, double percentile)
    {
        var index = Math.Clamp(
            (int)Math.Ceiling(values.Length * percentile) - 1,
            0,
            values.Length - 1);
        return values[index];
    }

    private static int NextHigherTier(int current, int maximum)
    {
        foreach (var tier in ConcurrencyTiers)
        {
            if (tier > current && tier <= maximum)
            {
                return tier;
            }
        }

        return maximum > current ? maximum : current;
    }

    private void Cancel(Waiter waiter)
    {
        lock (_gate)
        {
            waiter.CancellationRequested = true;
            if (waiter.Node?.List is not null)
            {
                waiter.Node.List.Remove(waiter.Node);
                waiter.Node = null;
                waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            }
        }
    }

    private void DispatchLocked()
    {
        while (_activeRequests < _effectiveConcurrency && TryTakeNextLocked() is { } waiter)
        {
            _activeRequests++;
            _activeByPriority[(int)waiter.Priority]++;
            waiter.CancellationRegistration.Unregister();
            waiter.Completion.TrySetResult(new Lease(this, waiter.Priority));
        }
    }

    private Waiter? TryTakeNextLocked()
    {
        for (var offset = 0; offset < Schedule.Length; offset++)
        {
            var scheduleIndex = (_schedulePosition + offset) % Schedule.Length;
            var priority = Schedule[scheduleIndex];
            var queue = _queues[(int)priority];
            if (queue.First is null)
            {
                continue;
            }

            if (priority == PncpRequestPriority.BackgroundPriceCache &&
                (_backgroundSuppressions > 0 ||
                 HasForegroundWorkLocked() ||
                 (_aggressiveBackgroundModes == 0 && _activeByPriority[(int)priority] > 0)))
            {
                continue;
            }

            if (priority == PncpRequestPriority.IndexMaintenance &&
                _activeByPriority[(int)priority] >= 2)
            {
                continue;
            }

            var waiter = queue.First.Value;
            queue.RemoveFirst();
            waiter.Node = null;
            _schedulePosition = (scheduleIndex + 1) % Schedule.Length;
            return waiter;
        }

        return null;
    }

    private bool HasForegroundWorkLocked()
    {
        for (var index = 0; index < (int)PncpRequestPriority.BackgroundPriceCache; index++)
        {
            if (_activeByPriority[index] > 0 || _queues[index].Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void Release(PncpRequestPriority priority)
    {
        lock (_gate)
        {
            if (_activeRequests == 0 || _activeByPriority[(int)priority] == 0)
            {
                throw new InvalidOperationException("O agendador recebeu uma liberação sem aquisição correspondente.");
            }

            _activeRequests--;
            _activeByPriority[(int)priority]--;
            DispatchLocked();
        }
    }

    private void ReleaseBackgroundSuppression()
    {
        lock (_gate)
        {
            if (_backgroundSuppressions == 0)
            {
                throw new InvalidOperationException("A supressão do cache de fundo já foi liberada.");
            }

            _backgroundSuppressions--;
            DispatchLocked();
        }
    }

    private void ReleaseAggressiveBackgroundMode()
    {
        lock (_gate)
        {
            if (_aggressiveBackgroundModes == 0)
            {
                throw new InvalidOperationException("O modo agressivo de fundo já foi liberado.");
            }

            _aggressiveBackgroundModes--;
            DispatchLocked();
        }
    }

    private static void ValidatePriority(PncpRequestPriority priority)
    {
        if ((int)priority is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }
    }

    private sealed class Waiter(
        PncpRequestScheduler owner,
        PncpRequestPriority priority,
        CancellationToken cancellationToken)
    {
        public PncpRequestScheduler Owner { get; } = owner;
        public PncpRequestPriority Priority { get; } = priority;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<IDisposable> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public bool CancellationRequested { get; set; }
    }

    private sealed class Lease(
        PncpRequestScheduler owner,
        PncpRequestPriority priority) : IDisposable
    {
        private PncpRequestScheduler? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(priority);
    }

    private sealed class SuppressionLease(PncpRequestScheduler owner) : IDisposable
    {
        private PncpRequestScheduler? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseBackgroundSuppression();
    }

    private sealed class AggressiveBackgroundLease(PncpRequestScheduler owner) : IDisposable
    {
        private PncpRequestScheduler? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseAggressiveBackgroundMode();
    }

    private sealed record SuccessfulOutcome(TimeSpan Duration, DateTimeOffset CompletedAt);
}
