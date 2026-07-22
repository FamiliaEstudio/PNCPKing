using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string directory, SqliteContractRepository repository)
    {
        Directory = directory;
        Repository = repository;
    }

    public string Directory { get; }
    public SqliteContractRepository Repository { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var repository = new SqliteContractRepository(Path.Combine(directory, "test.db"));
        await repository.InitializeAsync();
        return new TestDatabase(directory, repository);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, true);
        }

        return ValueTask.CompletedTask;
    }
}
