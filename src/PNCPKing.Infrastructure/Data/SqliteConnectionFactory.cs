using Microsoft.Data.Sqlite;

namespace PNCPKing.Infrastructure.Data;

public enum SqliteWorkPriority
{
    Visible = 0,
    Normal = 1,
    Background = 2
}

public interface ISqliteWorkCoordinator
{
    ValueTask<IAsyncDisposable> EnterReaderAsync(CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> EnterWriterAsync(
        SqliteWorkPriority priority = SqliteWorkPriority.Normal,
        CancellationToken cancellationToken = default);
}

public interface ISqliteConnectionFactory
{
    string DatabasePath { get; }
    ISqliteWorkCoordinator WorkCoordinator { get; }
    int MigrationCacheKib { get; }
    long MmapBytes { get; }
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly int _cacheKib;

    public SqliteConnectionFactory(
        string databasePath,
        ISqliteWorkCoordinator? workCoordinator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        WorkCoordinator = workCoordinator ?? new SqliteWorkCoordinator();
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var constrained = available > 0 && available <= 8L * 1024 * 1024 * 1024;
        _cacheKib = constrained ? 32 * 1024 : 64 * 1024;
        MigrationCacheKib = constrained ? 128 * 1024 : 256 * 1024;
        MmapBytes = constrained ? 64L * 1024 * 1024 : 128L * 1024 * 1024;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }
    public ISqliteWorkCoordinator WorkCoordinator { get; }
    public int MigrationCacheKib { get; }
    public long MmapBytes { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; " +
            $"PRAGMA cache_size=-{_cacheKib}; PRAGMA temp_store=FILE; PRAGMA mmap_size={MmapBytes};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}

public sealed class SqliteWorkCoordinator : ISqliteWorkCoordinator
{
    private readonly SemaphoreSlim _readers = new(2, 2);
    private readonly object _writerLock = new();
    private readonly Queue<TaskCompletionSource<IAsyncDisposable>>[] _writerQueues =
        [new(), new(), new()];
    private bool _writerActive;

    public async ValueTask<IAsyncDisposable> EnterReaderAsync(CancellationToken cancellationToken = default)
    {
        await _readers.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(() => _readers.Release());
    }

    public ValueTask<IAsyncDisposable> EnterWriterAsync(
        SqliteWorkPriority priority = SqliteWorkPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        lock (_writerLock)
        {
            if (!_writerActive)
            {
                _writerActive = true;
                return ValueTask.FromResult<IAsyncDisposable>(new Lease(ReleaseWriter));
            }

            var waiter = new TaskCompletionSource<IAsyncDisposable>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _writerQueues[(int)priority].Enqueue(waiter);
            return new ValueTask<IAsyncDisposable>(WaitForWriterAsync(waiter, cancellationToken));
        }
    }

    private static async Task<IAsyncDisposable> WaitForWriterAsync(
        TaskCompletionSource<IAsyncDisposable> waiter,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<IAsyncDisposable>)state!).TrySetCanceled(),
            waiter);
        return await waiter.Task.ConfigureAwait(false);
    }

    private void ReleaseWriter()
    {
        lock (_writerLock)
        {
            foreach (var queue in _writerQueues)
            {
                while (queue.TryDequeue(out var waiter))
                {
                    if (waiter.TrySetResult(new Lease(ReleaseWriter)))
                    {
                        return;
                    }
                }
            }

            _writerActive = false;
        }
    }

    private sealed class Lease(Action release) : IAsyncDisposable
    {
        private Action? _release = release;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
