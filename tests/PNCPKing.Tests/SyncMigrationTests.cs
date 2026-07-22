using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class SyncMigrationTests
{
    [Fact]
    public async Task VersionOneMigrationPreservesDeclaredCoverageAndStructuresRecognizedCheckpoints()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy-sync.db");
        try
        {
            await CreateVersionOneDatabaseAsync(path);
            var repository = new SqliteContractRepository(path);
            await repository.InitializeAsync();

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var version = connection.CreateCommand();
                version.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
                Assert.Equal(SqliteContractRepository.CurrentSchemaVersion, Convert.ToInt32(await version.ExecuteScalarAsync()));
            }

            var declaredStart = new DateOnly(2026, 5, 1);
            var declaredEnd = new DateOnly(2026, 5, 2);
            var migratedDeclaration = await repository.GetCoverageDaysAsync(declaredStart, declaredEnd);
            Assert.All(migratedDeclaration, day =>
            {
                Assert.Equal(CoverageStatus.AssumedComplete, day.Status);
                Assert.Equal(1, day.ExpectedModalities); // one-use modality-zero sentinel
            });

            await repository.EnsureCoverageWindowAsync(declaredStart, declaredEnd, [6, 8]);
            var expandedDeclaration = await repository.GetCoverageDaysAsync(declaredStart, declaredEnd);
            Assert.All(expandedDeclaration, day =>
            {
                Assert.Equal(CoverageStatus.AssumedComplete, day.Status);
                Assert.Equal(2, day.ExpectedModalities);
                Assert.Equal(2, day.CompletedModalities);
            });

            // The sentinel is consumed. A modality discovered on a later run is
            // therefore Missing instead of inheriting an old declaration.
            await repository.EnsureCoverageWindowAsync(declaredStart, declaredEnd, [6, 8, 99]);
            var withNewModality = await repository.GetCoverageDaysAsync(declaredStart, declaredEnd);
            Assert.All(withNewModality, day =>
            {
                Assert.Equal(CoverageStatus.Partial, day.Status);
                Assert.Equal(3, day.ExpectedModalities);
                Assert.Equal(2, day.CompletedModalities);
            });

            var completeKey = "Publication:20260601:20260602:m6:ufALL";
            var completeCheckpoint = await repository.GetPartitionCheckpointAsync(completeKey);
            Assert.NotNull(completeCheckpoint);
            Assert.Equal(SyncMode.Publication, completeCheckpoint.Mode);
            Assert.Equal(new DateOnly(2026, 6, 1), completeCheckpoint.StartDate);
            Assert.Equal(new DateOnly(2026, 6, 2), completeCheckpoint.EndDate);
            Assert.Equal(6, completeCheckpoint.ModalityId);
            Assert.Equal("ALL", completeCheckpoint.Uf);
            Assert.Equal(0, completeCheckpoint.NextPage);
            Assert.Equal(SyncPartitionStatus.Complete, completeCheckpoint.Status);

            var partialKey = "Publication:20260603:20260604:m8:ufALL";
            var partialCheckpoint = await repository.GetPartitionCheckpointAsync(partialKey);
            Assert.NotNull(partialCheckpoint);
            Assert.Equal(3, partialCheckpoint.NextPage);
            Assert.Equal(SyncPartitionStatus.Partial, partialCheckpoint.Status);
            Assert.Equal("ALL", partialCheckpoint.Uf);

            var checkpointCoverage = await repository.GetCoverageDaysAsync(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 4));
            Assert.Equal(CoverageStatus.AssumedComplete, checkpointCoverage[0].Status);
            Assert.Equal(CoverageStatus.AssumedComplete, checkpointCoverage[1].Status);
            Assert.Equal(CoverageStatus.Partial, checkpointCoverage[2].Status);
            Assert.Equal(CoverageStatus.Partial, checkpointCoverage[3].Status);

            Assert.Null(await repository.GetPartitionCheckpointAsync("formato-antigo-desconhecido"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static async Task CreateVersionOneDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE schema_info(id INTEGER PRIMARY KEY, version INTEGER NOT NULL);
            INSERT INTO schema_info(id, version) VALUES(1, 1);

            CREATE TABLE dataset(
                id INTEGER PRIMARY KEY,
                start_date TEXT,
                end_date TEXT,
                scope_kind INTEGER NOT NULL DEFAULT 0,
                scope_uf TEXT,
                last_successful_sync TEXT
            );
            INSERT INTO dataset(id, start_date, end_date, scope_kind, last_successful_sync)
            VALUES(1, '2026-05-01', '2026-05-02', 0, '2026-05-03T10:00:00+00:00');

            CREATE TABLE contracts(
                pncp_id TEXT PRIMARY KEY,
                cnpj TEXT NOT NULL,
                purchase_year INTEGER NOT NULL,
                purchase_sequence INTEGER NOT NULL,
                object TEXT NOT NULL DEFAULT '',
                additional_information TEXT NOT NULL DEFAULT '',
                process TEXT NOT NULL DEFAULT '',
                organization TEXT NOT NULL DEFAULT '',
                unit TEXT NOT NULL DEFAULT '',
                municipality TEXT NOT NULL DEFAULT '',
                uf TEXT NOT NULL DEFAULT '',
                modality_id INTEGER NOT NULL,
                modality_name TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT '',
                publication_date TEXT,
                global_updated_at TEXT,
                total_homologated_scaled INTEGER,
                search_text TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE items(
                contract_id TEXT NOT NULL REFERENCES contracts(pncp_id) ON DELETE CASCADE,
                item_number INTEGER NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                unit TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT '',
                has_result INTEGER NOT NULL DEFAULT 0,
                source_updated_at TEXT,
                hydration_status INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                cache_updated_at TEXT,
                PRIMARY KEY(contract_id, item_number)
            );

            CREATE TRIGGER contracts_mark_items_stale
            AFTER UPDATE OF global_updated_at ON contracts
            BEGIN
                UPDATE items SET hydration_status = 4 WHERE contract_id = new.pncp_id AND has_result = 1;
            END;

            CREATE TABLE sync_partitions(
                partition_key TEXT PRIMARY KEY,
                next_page INTEGER NOT NULL DEFAULT 1,
                completed INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            INSERT INTO sync_partitions(partition_key, next_page, completed, updated_at)
            VALUES
                ('Publication:20260601:20260602:m6:ufALL', 0, 1, '2026-06-03T10:00:00+00:00'),
                ('Publication:20260603:20260604:m8:ufALL', 3, 0, '2026-06-04T10:00:00+00:00'),
                ('formato-antigo-desconhecido', 2, 0, '2026-06-04T10:00:00+00:00');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
