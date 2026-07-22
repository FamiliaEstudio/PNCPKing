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
    IndexMaintenance = 3
}

public sealed record PncpSchedulerSnapshot(
    int MaximumConcurrency,
    int ActiveRequests,
    int QueuedUserSelectedItems,
    int QueuedVisiblePrices,
    int QueuedAdditionalBatches,
    int QueuedIndexMaintenance)
{
    public int TotalQueued =>
        QueuedUserSelectedItems +
        QueuedVisiblePrices +
        QueuedAdditionalBatches +
        QueuedIndexMaintenance;
}

/// <summary>
/// Process-wide request gate with weighted priority and bounded starvation.
/// A single instance must be shared by every PNCP HTTP client in the process.
/// </summary>
public sealed class PncpRequestScheduler
{
    // Five user-selected requests, two visible-price requests, one additional
    // batch and one maintenance request per fully occupied scheduling cycle.
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
        PncpRequestPriority.IndexMaintenance
    ];

    private readonly object _gate = new();
    private readonly LinkedList<Waiter>[] _queues =
        Enumerable.Range(0, 4).Select(_ => new LinkedList<Waiter>()).ToArray();
    private readonly int _maximumConcurrency;
    private int _activeRequests;
    private int _schedulePosition;

    public PncpRequestScheduler(int maximumConcurrency = 2)
    {
        if (maximumConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrency),
                "A concorrência máxima deve ser pelo menos 1.");
        }

        _maximumConcurrency = maximumConcurrency;
    }

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

    public PncpSchedulerSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new PncpSchedulerSnapshot(
                _maximumConcurrency,
                _activeRequests,
                _queues[0].Count,
                _queues[1].Count,
                _queues[2].Count,
                _queues[3].Count);
        }
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
        while (_activeRequests < _maximumConcurrency && TryTakeNextLocked() is { } waiter)
        {
            _activeRequests++;
            waiter.CancellationRegistration.Unregister();
            waiter.Completion.TrySetResult(new Lease(this));
        }
    }

    private Waiter? TryTakeNextLocked()
    {
        for (var offset = 0; offset < Schedule.Length; offset++)
        {
            var scheduleIndex = (_schedulePosition + offset) % Schedule.Length;
            var queue = _queues[(int)Schedule[scheduleIndex]];
            if (queue.First is null)
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

    private void Release()
    {
        lock (_gate)
        {
            if (_activeRequests == 0)
            {
                throw new InvalidOperationException("O agendador recebeu uma liberação sem aquisição correspondente.");
            }

            _activeRequests--;
            DispatchLocked();
        }
    }

    private static void ValidatePriority(PncpRequestPriority priority)
    {
        if ((int)priority is < 0 or > 3)
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

    private sealed class Lease(PncpRequestScheduler owner) : IDisposable
    {
        private PncpRequestScheduler? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
