using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class DatabaseInitializationCancellationTests
{
    [Fact]
    public async Task WaitingForWriter_ReportsPhaseBeforeWaitingAndCanBeCancelled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var connections = new SqliteConnectionFactory(database.Repository.DatabasePath);
        var repository = new SqliteContractRepository(connections);
        await using var writer = await connections.WorkCoordinator.EnterWriterAsync();
        using var cancellation = new CancellationTokenSource();
        var phases = new List<DatabaseInitializationProgress>();
        var operation = repository.InitializeAsync(cancellation.Token,
            new InlineProgress(phases.Add));

        Assert.False(operation.IsCompleted);
        Assert.Equal("Aguardando acesso ao banco", Assert.Single(phases).Phase);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        await writer.DisposeAsync();
        var reopened = await repository.InitializeAsync();
        Assert.Empty(reopened.AppliedMigrations);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(34)]
    public async Task NativeSqlCancellation_RollsBackMigrationAndPreservesQuotationBeforeRetry(int targetVersion)
    {
        await using var database = await TestDatabase.CreateAsync();
        var quotations = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await quotations.CreateProjectAsync("Cotação preservada na migração");
        var resetSchema = targetVersion == 33 ? """
            ALTER TABLE quotation_lines DROP COLUMN catmat_code_override;
            ALTER TABLE quotation_lines DROP COLUMN minimum_order_quantity_scaled;
            """ : """
            ALTER TABLE official_update_chunks DROP COLUMN last_contract_id;
            ALTER TABLE official_update_chunks DROP COLUMN processed_contracts;
            """;
        var table = targetVersion == 33 ? "quotation_lines" : "official_update_chunks";
        var column = targetVersion == 33 ? "catmat_code_override" : "last_contract_id";
        await ExecuteAsync(database.Repository.DatabasePath, resetSchema + $$"""
            UPDATE schema_info SET version={{targetVersion - 1}} WHERE id=1;
            CREATE TRIGGER cancel_migration BEFORE UPDATE OF version ON schema_info
            WHEN NEW.version={{targetVersion}} BEGIN
                SELECT (WITH RECURSIVE n(x) AS (
                    VALUES(cancel_now()) UNION ALL SELECT x+1 FROM n WHERE x<100000000
                ) SELECT SUM(x) FROM n);
            END;
            """);
        using var cancellation = new CancellationTokenSource();
        var connections = new CancellingConnections(
            new SqliteConnectionFactory(database.Repository.DatabasePath), cancellation);
        var repository = new SqliteContractRepository(connections);
        var phases = new List<DatabaseInitializationProgress>();

        var operation = Task.Run(() => repository.InitializeAsync(
            cancellation.Token, new InlineProgress(phases.Add)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Contains(phases, phase => phase.Phase == $"Migrando esquema v{targetVersion - 1} → v{targetVersion}");
        await using (var connection = await new SqliteConnectionFactory(database.Repository.DatabasePath).OpenAsync())
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT version FROM schema_info WHERE id=1;";
            Assert.Equal((long)targetVersion - 1, Convert.ToInt64(await check.ExecuteScalarAsync()));
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}';";
            Assert.Equal(0L, Convert.ToInt64(await check.ExecuteScalarAsync()));
            check.CommandText = "DROP TRIGGER cancel_migration;";
            await check.ExecuteNonQueryAsync();
        }
        Assert.Equal(project.Id, Assert.Single(await quotations.GetProjectsAsync()).Id);
        var recovered = await database.Repository.InitializeAsync();
        Assert.Equal(SqliteContractRepository.CurrentSchemaVersion, recovered.CurrentVersion);
        Assert.Equal(project.Id, Assert.Single(await quotations.GetProjectsAsync()).Id);
        Assert.Empty((await database.Repository.InitializeAsync()).AppliedMigrations);
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = await new SqliteConnectionFactory(path).OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class InlineProgress(Action<DatabaseInitializationProgress> report)
        : IProgress<DatabaseInitializationProgress>
    {
        public void Report(DatabaseInitializationProgress value) => report(value);
    }

    private sealed class CancellingConnections(
        SqliteConnectionFactory source, CancellationTokenSource cancellation) : ISqliteConnectionFactory
    {
        public string DatabasePath => source.DatabasePath;
        public ISqliteWorkCoordinator WorkCoordinator => source.WorkCoordinator;
        public int MigrationCacheKib => source.MigrationCacheKib;
        public long MmapBytes => source.MmapBytes;
        public int WorkerThreads => source.WorkerThreads;
        public string ProfileName => source.ProfileName;

        public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = await source.OpenAsync(cancellationToken);
            connection.CreateFunction("cancel_now", () => { cancellation.Cancel(); return 1; });
            return connection;
        }
    }
}
