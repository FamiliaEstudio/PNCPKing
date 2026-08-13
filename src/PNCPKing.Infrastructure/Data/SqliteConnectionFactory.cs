using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Infrastructure.Data;

public enum SqliteWorkPriority
{
    Visible = 0,
    Normal = 1,
    Background = 2
}

public interface ISqliteWorkCoordinator
{
    bool IsIdle { get; }
    ValueTask<IAsyncDisposable> EnterReaderAsync(
        SqliteWorkPriority priority = SqliteWorkPriority.Visible,
        CancellationToken cancellationToken = default);
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
    int WorkerThreads { get; }
    string ProfileName { get; }
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly int _cacheKib;

    public SqliteConnectionFactory(
        string databasePath,
        ISqliteWorkCoordinator? workCoordinator = null,
        ISystemResourceProbe? resourceProbe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        WorkCoordinator = workCoordinator ?? new SqliteWorkCoordinator();
        var resources = (resourceProbe ?? new SystemResourceProbe()).GetSnapshot();
        var spacious = resources.Pressure == SystemResourcePressure.Normal &&
                       resources.LogicalProcessors >= 8 &&
                       resources.TotalPhysicalMemoryBytes >= 16L * 1024 * 1024 * 1024;
        if (resources.Pressure != SystemResourcePressure.Normal)
        {
            ProfileName = "Restrito";
            _cacheKib = 16 * 1024;
            MigrationCacheKib = 64 * 1024;
            MmapBytes = 32L * 1024 * 1024;
            WorkerThreads = 1;
        }
        else if (spacious)
        {
            ProfileName = "Amplo";
            _cacheKib = 64 * 1024;
            MigrationCacheKib = 256 * 1024;
            MmapBytes = 128L * 1024 * 1024;
            WorkerThreads = 2;
        }
        else
        {
            ProfileName = "Balanceado";
            _cacheKib = 32 * 1024;
            MigrationCacheKib = 128 * 1024;
            MmapBytes = 64L * 1024 * 1024;
            WorkerThreads = 2;
        }

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
    public int WorkerThreads { get; }
    public string ProfileName { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; " +
            $"PRAGMA cache_size=-{_cacheKib}; PRAGMA temp_store=FILE; PRAGMA mmap_size={MmapBytes}; " +
            $"PRAGMA threads={WorkerThreads};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}

public sealed class SqliteWorkCoordinator : ISqliteWorkCoordinator
{
    private const int MaximumReaders = 2;
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];
    private int _activeReaders;
    private bool _writerActive;
    private long _nextSequence;

    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                PruneCompleted();
                return !_writerActive && _activeReaders == 0 && _waiters.Count == 0;
            }
        }
    }

    public ValueTask<IAsyncDisposable> EnterReaderAsync(
        SqliteWorkPriority priority = SqliteWorkPriority.Visible,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            PruneCompleted();
            if (!_writerActive && _activeReaders < MaximumReaders && !HasQueuedAtOrAbove(priority))
            {
                _activeReaders++;
                return ValueTask.FromResult<IAsyncDisposable>(new Lease(ReleaseReader));
            }

            return QueueWaiter(isWriter: false, priority, cancellationToken);
        }
    }

    public ValueTask<IAsyncDisposable> EnterWriterAsync(
        SqliteWorkPriority priority = SqliteWorkPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            PruneCompleted();
            if (!_writerActive && _activeReaders == 0 && !HasQueuedAtOrAbove(priority))
            {
                _writerActive = true;
                return ValueTask.FromResult<IAsyncDisposable>(new Lease(ReleaseWriter));
            }

            return QueueWaiter(isWriter: true, priority, cancellationToken);
        }
    }

    private ValueTask<IAsyncDisposable> QueueWaiter(
        bool isWriter,
        SqliteWorkPriority priority,
        CancellationToken cancellationToken)
    {
        var waiter = new Waiter(isWriter, priority, _nextSequence++);
        _waiters.Add(waiter);
        return new ValueTask<IAsyncDisposable>(WaitAsync(waiter, cancellationToken));
    }

    private async Task<IAsyncDisposable> WaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static state =>
            {
                var cancellation = (CancellationState)state!;
                cancellation.Owner.CancelWaiter(cancellation.Waiter, cancellation.Token);
            },
            new CancellationState(this, waiter, cancellationToken));
        return await waiter.Completion.Task.ConfigureAwait(false);
    }

    private void CancelWaiter(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (waiter.Completion.TrySetCanceled(cancellationToken))
            {
                _waiters.Remove(waiter);
                Dispatch();
            }
        }
    }

    private bool HasQueuedAtOrAbove(SqliteWorkPriority priority) =>
        _waiters.Any(waiter =>
            !waiter.Completion.Task.IsCompleted && (int)waiter.Priority <= (int)priority);

    private void ReleaseReader()
    {
        lock (_gate)
        {
            _activeReaders--;
            Dispatch();
        }
    }

    private void ReleaseWriter()
    {
        lock (_gate)
        {
            _writerActive = false;
            Dispatch();
        }
    }

    private void Dispatch()
    {
        PruneCompleted();
        if (_writerActive || _waiters.Count == 0)
        {
            return;
        }

        var priority = _waiters.Min(waiter => waiter.Priority);
        var eligible = _waiters
            .Where(waiter => waiter.Priority == priority)
            .OrderBy(waiter => waiter.Sequence)
            .ToArray();
        var first = eligible[0];
        if (first.IsWriter)
        {
            if (_activeReaders != 0)
            {
                return;
            }

            _waiters.Remove(first);
            if (first.Completion.TrySetResult(new Lease(ReleaseWriter)))
            {
                _writerActive = true;
            }
            else
            {
                Dispatch();
            }
            return;
        }

        var firstWriterSequence = eligible
            .Where(waiter => waiter.IsWriter)
            .Select(waiter => waiter.Sequence)
            .DefaultIfEmpty(long.MaxValue)
            .Min();
        foreach (var reader in eligible.Where(waiter =>
                     !waiter.IsWriter && waiter.Sequence < firstWriterSequence))
        {
            if (_activeReaders >= MaximumReaders)
            {
                break;
            }

            _waiters.Remove(reader);
            if (reader.Completion.TrySetResult(new Lease(ReleaseReader)))
            {
                _activeReaders++;
            }
        }
    }

    private void PruneCompleted()
    {
        for (var index = _waiters.Count - 1; index >= 0; index--)
        {
            if (_waiters[index].Completion.Task.IsCompleted)
            {
                _waiters.RemoveAt(index);
            }
        }
    }

    private sealed class Waiter(bool isWriter, SqliteWorkPriority priority, long sequence)
    {
        public bool IsWriter { get; } = isWriter;
        public SqliteWorkPriority Priority { get; } = priority;
        public long Sequence { get; } = sequence;
        public TaskCompletionSource<IAsyncDisposable> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record CancellationState(
        SqliteWorkCoordinator Owner,
        Waiter Waiter,
        CancellationToken Token);

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
