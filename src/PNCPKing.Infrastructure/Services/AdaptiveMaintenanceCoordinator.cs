using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed record MaintenanceDecision(
    SystemResourceSnapshot Resources,
    bool CanRun,
    TimeSpan SliceDuration,
    TimeSpan RetryDelay,
    string Description);

public sealed class AdaptiveMaintenanceCoordinator
{
    public static readonly TimeSpan VisibleIdleDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ConstrainedVisibleIdleDelay = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _activityGate = new();
    private readonly ISystemResourceProbe _resourceProbe;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _lastVisibleActivity;
    private CancellationTokenSource? _activeSlice;

    public AdaptiveMaintenanceCoordinator(
        ISystemResourceProbe? resourceProbe = null,
        TimeProvider? timeProvider = null)
    {
        _resourceProbe = resourceProbe ?? new SystemResourceProbe();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastVisibleActivity = _timeProvider.GetUtcNow() - ConstrainedVisibleIdleDelay;
    }

    public void NotifyVisibleActivity()
    {
        CancellationTokenSource? active;
        lock (_activityGate)
        {
            _lastVisibleActivity = _timeProvider.GetUtcNow();
            active = _activeSlice;
        }

        RequestCancellation(active);
    }

    public void CancelActiveSlice()
    {
        CancellationTokenSource? active;
        lock (_activityGate)
        {
            active = _activeSlice;
        }

        RequestCancellation(active);
    }

    public MaintenanceDecision GetDecision()
    {
        var resources = _resourceProbe.GetSnapshot();
        if (resources.Pressure == SystemResourcePressure.Critical)
        {
            return new MaintenanceDecision(
                resources,
                false,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(2),
                "manutenção aguardando: RAM física em nível crítico");
        }

        var visibleIdleDelay = resources.Pressure == SystemResourcePressure.Constrained
            ? ConstrainedVisibleIdleDelay
            : VisibleIdleDelay;
        TimeSpan remainingIdle;
        lock (_activityGate)
        {
            remainingIdle = visibleIdleDelay - (_timeProvider.GetUtcNow() - _lastVisibleActivity);
        }

        if (remainingIdle > TimeSpan.Zero)
        {
            return new MaintenanceDecision(
                resources,
                false,
                TimeSpan.Zero,
                remainingIdle,
                $"aguardando {visibleIdleDelay.TotalSeconds:N0} segundos sem interação do usuário");
        }

        return resources.Pressure switch
        {
            SystemResourcePressure.Constrained => new MaintenanceDecision(
                resources,
                true,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(60),
                "modo restrito: uma fatia de até 10 segundos"),
            _ => new MaintenanceDecision(
                resources,
                true,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(30),
                "modo normal: uma fatia de até 60 segundos")
        };
    }

    public IAsyncDisposable? TryEnter()
    {
        return _gate.Wait(0) ? new Lease(_gate) : null;
    }

    public MaintenanceSlice BeginSlice(CancellationToken cancellationToken = default)
    {
        var source = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        lock (_activityGate)
        {
            _activeSlice?.Cancel();
            _activeSlice?.Dispose();
            _activeSlice = source;
            var resources = _resourceProbe.GetSnapshot();
            var visibleIdleDelay = resources.Pressure == SystemResourcePressure.Constrained
                ? ConstrainedVisibleIdleDelay
                : VisibleIdleDelay;
            if (_timeProvider.GetUtcNow() - _lastVisibleActivity < visibleIdleDelay)
            {
                source.Cancel();
            }
        }

        return new MaintenanceSlice(this, source);
    }

    private void EndSlice(CancellationTokenSource source)
    {
        lock (_activityGate)
        {
            if (ReferenceEquals(_activeSlice, source))
            {
                _activeSlice = null;
            }
        }

        source.Dispose();
    }

    private static void RequestCancellation(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(
            static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A fatia terminou antes de o pedido assíncrono chegar.
                }
            },
            source,
            preferLocal: false);
    }

    public sealed class MaintenanceSlice : IAsyncDisposable
    {
        private AdaptiveMaintenanceCoordinator? _owner;
        private CancellationTokenSource? _source;

        internal MaintenanceSlice(
            AdaptiveMaintenanceCoordinator owner,
            CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
        }

        public CancellationToken Token => _source?.Token ?? new CancellationToken(canceled: true);

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var source = Interlocked.Exchange(ref _source, null);
            if (owner is not null && source is not null)
            {
                owner.EndSlice(source);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
