using System.Windows.Threading;

namespace PNCPKing.App.ViewModels;

internal sealed class UiBatchBuffer<T> : IDisposable
{
    private const int MaximumBatchSize = 10;
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(150);
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Queue<T> _pending = new();
    private readonly Action<IReadOnlyList<T>> _apply;
    private bool _drainScheduled;
    private bool _disposed;

    public UiBatchBuffer(Action<IReadOnlyList<T>> apply)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = MaximumDelay
        };
        _timer.Tick += OnTimerTick;
    }

    public void Enqueue(IEnumerable<T> values)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var value in values)
        {
            _pending.Enqueue(value);
        }

        if (_pending.Count == 0)
        {
            return;
        }

        if (_pending.Count >= MaximumBatchSize)
        {
            ScheduleDrain();
        }
        else if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public async Task FlushAsync()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer.Stop();
        while (_pending.Count > 0)
        {
            DrainOnce();
            if (_pending.Count > 0)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
    }

    public void Clear()
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
        _pending.Clear();
    }

    public void Dispose()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Dispose);
            return;
        }

        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _pending.Clear();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        DrainOnce();
    }

    private void ScheduleDrain()
    {
        if (_drainScheduled)
        {
            return;
        }

        _drainScheduled = true;
        _timer.Stop();
        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _drainScheduled = false;
                if (!_disposed)
                {
                    DrainOnce();
                }
            }));
    }

    private void DrainOnce()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var count = Math.Min(MaximumBatchSize, _pending.Count);
        var batch = new T[count];
        for (var index = 0; index < count; index++)
        {
            batch[index] = _pending.Dequeue();
        }

        _apply(batch);
        if (_pending.Count >= MaximumBatchSize)
        {
            ScheduleDrain();
        }
        else if (_pending.Count > 0)
        {
            _timer.Start();
        }
    }
}
