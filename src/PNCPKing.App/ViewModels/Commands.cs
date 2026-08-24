using System.Windows.Input;

namespace PNCPKing.App.ViewModels;

public static class AsyncCommandRuntime
{
    private static Action<Exception>? _exceptionHandler;
    private static Action? _rejectedHandler;

    public static void Configure(Action<Exception> exceptionHandler, Action? rejectedHandler = null)
    {
        _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
        _rejectedHandler = rejectedHandler;
    }

    internal static void ReportRejected() => _rejectedHandler?.Invoke();

    internal static void Handle(Exception exception)
    {
        if (_exceptionHandler is null)
        {
            throw exception;
        }

        _exceptionHandler(exception);
    }

    internal static bool IsCritical(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException or
            AppDomainUnloadedException or BadImageFormatException;
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
    where T : class
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        var value = parameter as T;
        return canExecute?.Invoke(value) ?? true;
    }

    public void Execute(object? parameter) => execute(parameter as T);

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    bool allowConcurrentExecutions = false) : ICommand
{
    private int _runningCount;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _runningCount) != 0;

    public Task? ExecutionTask { get; private set; }

    public bool CanExecute(object? parameter) =>
        (allowConcurrentExecutions || !IsRunning) && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!(canExecute?.Invoke() ?? true) ||
            (!allowConcurrentExecutions && Interlocked.CompareExchange(ref _runningCount, 1, 0) != 0))
        {
            AsyncCommandRuntime.ReportRejected();
            return;
        }

        if (allowConcurrentExecutions)
        {
            Interlocked.Increment(ref _runningCount);
        }

        NotifyCanExecuteChanged();
        ExecutionTask = ExecuteCoreAsync();
        await ExecutionTask.ConfigureAwait(true);
    }

    private async Task ExecuteCoreAsync()
    {
        try
        {
            await execute().ConfigureAwait(true);
        }
        catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
        {
            AsyncCommandRuntime.Handle(exception);
        }
        finally
        {
            if (allowConcurrentExecutions)
            {
                Interlocked.Decrement(ref _runningCount);
            }
            else
            {
                Interlocked.Exchange(ref _runningCount, 0);
            }
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand<T>(Func<T?, Task> execute, Func<T?, bool>? canExecute = null) : ICommand
    where T : class
{
    private int _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public Task? ExecutionTask { get; private set; }

    public bool CanExecute(object? parameter)
    {
        var value = parameter as T;
        return !IsRunning && (canExecute?.Invoke(value) ?? true);
    }

    public async void Execute(object? parameter)
    {
        var value = parameter as T;
        if (!(canExecute?.Invoke(value) ?? true) || Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            AsyncCommandRuntime.ReportRejected();
            return;
        }

        NotifyCanExecuteChanged();
        ExecutionTask = ExecuteCoreAsync(value);
        await ExecutionTask.ConfigureAwait(true);
    }

    private async Task ExecuteCoreAsync(T? value)
    {
        try
        {
            await execute(value).ConfigureAwait(true);
        }
        catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
        {
            AsyncCommandRuntime.Handle(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
