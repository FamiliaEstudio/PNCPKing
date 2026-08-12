using System.Diagnostics;
using System.Windows.Threading;
using PNCPKing.Core.Interfaces;

namespace PNCPKing.App.Services;

public sealed class DispatcherResponsivenessMonitor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private readonly IPerformanceTelemetry _telemetry;
    private readonly DispatcherTimer _timer;
    private long _lastTick;

    public DispatcherResponsivenessMonitor(
        Dispatcher dispatcher,
        IPerformanceTelemetry telemetry)
    {
        _telemetry = telemetry;
        _lastTick = Stopwatch.GetTimestamp();
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = Interval
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs eventArgs)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastTick, now);
        _lastTick = now;
        var delay = elapsed - Interval;
        if (delay >= TimeSpan.FromMilliseconds(25))
        {
            _telemetry.Record("ui", "dispatcher-delay", delay);
        }
    }
}
