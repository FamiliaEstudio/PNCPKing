using System.Collections.Concurrent;

namespace PNCPKing.Infrastructure.Data;

/// <summary>
/// Coordinates factory-created connections and database file replacement within this process.
/// Direct SQLite connections and other processes must be excluded separately by the caller.
/// </summary>
public static class SqliteDatabaseTransferGate
{
    private static readonly ConcurrentDictionary<string, DatabaseState> States =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static DatabaseState GetState(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return States.GetOrAdd(Path.GetFullPath(databasePath), static _ => new DatabaseState());
    }

    internal static async ValueTask<IDisposable> EnterConnectionAsync(
        string databasePath, CancellationToken cancellationToken = default)
    {
        var state = GetState(databasePath);
        await state.Admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (state.Sync)
            {
                if (state.ActiveConnections++ == 0)
                    state.Drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return new Lease(() =>
            {
                lock (state.Sync)
                {
                    if (--state.ActiveConnections == 0)
                        state.Drained.TrySetResult();
                }
            });
        }
        finally
        {
            state.Admission.Release();
        }
    }

    /// <summary>
    /// Waits for a connection-free interval, then blocks new factory connections. Admission
    /// remains open while draining because existing operations may open nested connections.
    /// The caller must clear SQLite pools after acquisition, before replacing the file. Do
    /// not open a factory connection for this path while holding the lease: use a private,
    /// unpooled connection. Acquisition times out after thirty seconds if the database is busy.
    /// </summary>
    public static async ValueTask<IAsyncDisposable> EnterExclusiveAsync(
        string databasePath, CancellationToken cancellationToken = default)
    {
        var state = GetState(databasePath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                Task drained;
                lock (state.Sync)
                    drained = state.ActiveConnections == 0 ? Task.CompletedTask : state.Drained.Task;
                // Do not hold Admission here: a connection already in use may need to open
                // another connection before it can finish and dispose the first one.
                await drained.WaitAsync(timeout.Token).ConfigureAwait(false);
                await state.Admission.WaitAsync(timeout.Token).ConfigureAwait(false);
                var acquired = false;
                try
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    lock (state.Sync)
                    {
                        // A connection may have opened between observing zero and taking
                        // Admission. In that case reopen admission and wait for the next zero.
                        if (state.ActiveConnections == 0)
                        {
                            acquired = true;
                            return new Lease(() => state.Admission.Release());
                        }
                    }
                }
                finally
                {
                    if (!acquired) state.Admission.Release();
                }
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException(
                "O banco continua em uso após 30 segundos. Aguarde as operações em andamento e tente importar novamente.",
                exception);
        }
    }

    private sealed class DatabaseState
    {
        internal readonly SemaphoreSlim Admission = new(1, 1);
        internal readonly object Sync = new();
        internal int ActiveConnections;
        internal TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(Action release) : IDisposable, IAsyncDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}