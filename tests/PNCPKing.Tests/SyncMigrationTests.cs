using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class SyncMigrationTests
{
    [Fact]
    public async Task VersionSevenToEightPreservesLinesReferencesChoicesAndAutomation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "version-seven.db");
        try
        {
            var contracts = new SqliteContractRepository(path);
            await contracts.InitializeAsync();
            var quotation = new SqliteQuotationRepository(path);
            var project = await quotation.CreateProjectAsync("Preservar versão 7");
            var lineId = Guid.NewGuid();
            var reference = new QuotationReference
            {
                Id = "contrato|1|1",
                LineId = lineId,
                ContractId = "contrato",
                ItemNumber = 1,
                ResultSequence = 1,
                SupplierName = "Fornecedor",
                SupplierTaxId = "11222333000181",
                UnitPrice = 25m,
                ItemDescription = "Café",
                ItemUnit = "pacote",
                State = QuotationReferenceState.Eligible
            };
            await quotation.SaveSampleAsync(
                project.Id,
                lineId,
                new QuotationLineInput("Café", 10m, "pacote", null, null),
                [reference]);
            await quotation.ConfirmBasketAsync(lineId, reference.Id);
            var run = await quotation.CreateAutomationRunAsync(
                project.Id,
                Path.Combine(directory, "saida.xlsx"),
                SearchGeoFilter.All,
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 7, 1),
                [new QuotationImportItem(1, "acucar", "Açúcar", 5m, "kg", null, null, 4)],
                AdequacyWeights.Default);

            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var downgrade = connection.CreateCommand();
                downgrade.CommandText = """
                    PRAGMA foreign_keys=OFF;
                    DROP TABLE quotation_manual_basket_references;
                    DROP TABLE quotation_manual_baskets;
                    ALTER TABLE quotation_lines DROP COLUMN requested_basket_size;
                    UPDATE schema_info SET version = 7 WHERE id = 1;
                    PRAGMA foreign_keys=ON;
                    """;
                await downgrade.ExecuteNonQueryAsync();
            }

            SqliteConnection.ClearAllPools();
            await new SqliteContractRepository(path).InitializeAsync();
            var restoredRepository = new SqliteQuotationRepository(path);
            var lines = await restoredRepository.GetLinesAsync(project.Id);
            var restoredManual = lines.Single(line => line.Id == lineId);
            var restoredAutomation = lines.Single(line => line.AutomationRunId == run.Id);

            Assert.Equal(3, restoredManual.RequestedBasketSize);
            Assert.True(restoredManual.SelectionConfirmed);
            Assert.Equal(reference.Id, restoredManual.SelectedBasketKey);
            Assert.Single(await restoredRepository.GetReferencesAsync(lineId));
            Assert.Equal(3, restoredAutomation.RequestedBasketSize);
            Assert.Equal(4, restoredAutomation.RequestedBatchCount);
            Assert.Equal(run.Id, restoredAutomation.AutomationRunId);
            Assert.NotNull(await restoredRepository.GetLatestAutomationRunAsync(project.Id));
            Assert.Empty(await restoredRepository.GetManualBasketsAsync(lineId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task VersionSixMigrationAddsAutomationAndSweetCodesWithoutLosingQuotationLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "version-six.db");
        try
        {
            var current = new SqliteContractRepository(path);
            await current.InitializeAsync();
            var quotation = new SqliteQuotationRepository(path);
            var project = await quotation.CreateProjectAsync("Preservar");
            await quotation.SaveSampleAsync(
                project.Id,
                null,
                new QuotationLineInput("Café", 10m, "pacote", 30m, 50m),
                []);

            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                string referenceSql;
                await using (var readSql = connection.CreateCommand())
                {
                    readSql.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'quotation_references';";
                    referenceSql = Convert.ToString(await readSql.ExecuteScalarAsync())!;
                }

                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    PRAGMA foreign_keys=OFF;
                    DROP TABLE quotation_references;
                    DROP TABLE quotation_automation_runs;
                    DROP TABLE sweet_codes;
                    DROP TABLE sweet_code_settings;
                    ALTER TABLE quotation_lines RENAME TO quotation_lines_v7;
                    CREATE TABLE quotation_lines(
                        id TEXT PRIMARY KEY,
                        project_id TEXT NOT NULL REFERENCES quotation_projects(id) ON DELETE CASCADE,
                        description TEXT NOT NULL,
                        requested_quantity_scaled INTEGER NOT NULL,
                        requested_unit TEXT NOT NULL,
                        minimum_unit_price_scaled INTEGER,
                        maximum_unit_price_scaled INTEGER,
                        sample_version INTEGER NOT NULL DEFAULT 1,
                        sampled_at TEXT NOT NULL,
                        selected_basket_key TEXT,
                        selection_confirmed INTEGER NOT NULL DEFAULT 0,
                        description_weight INTEGER NOT NULL DEFAULT 50,
                        unit_weight INTEGER NOT NULL DEFAULT 20,
                        quantity_weight INTEGER NOT NULL DEFAULT 10,
                        proximity_weight INTEGER NOT NULL DEFAULT 15,
                        recency_weight INTEGER NOT NULL DEFAULT 5
                    );
                    INSERT INTO quotation_lines(
                        id, project_id, description, requested_quantity_scaled, requested_unit,
                        minimum_unit_price_scaled, maximum_unit_price_scaled, sample_version,
                        sampled_at, selected_basket_key, selection_confirmed, description_weight,
                        unit_weight, quantity_weight, proximity_weight, recency_weight)
                    SELECT id, project_id, description, requested_quantity_scaled, requested_unit,
                           minimum_unit_price_scaled, maximum_unit_price_scaled, sample_version,
                           sampled_at, selected_basket_key, selection_confirmed, description_weight,
                           unit_weight, quantity_weight, proximity_weight, recency_weight
                      FROM quotation_lines_v7;
                    DROP TABLE quotation_lines_v7;
                    {referenceSql};
                    CREATE INDEX idx_quotation_references_line_state
                        ON quotation_references(line_id, state, unit_price_scaled);
                    CREATE INDEX idx_quotation_lines_project ON quotation_lines(project_id, sampled_at);
                    UPDATE schema_info SET version = 6 WHERE id = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            SqliteConnection.ClearAllPools();
            var migrated = new SqliteContractRepository(path);
            await migrated.InitializeAsync();

            var lines = await new SqliteQuotationRepository(path).GetLinesAsync(project.Id);
            var line = Assert.Single(lines);
            Assert.Equal("Café", line.Description);
            Assert.Equal("Café", line.SearchText);
            Assert.Equal(QuotationAutomationItemState.Manual, line.AutomationState);
            Assert.True((await new SqliteSweetCodeRepository(path).LoadAsync()).Enabled);
            await using var verify = new SqliteConnection($"Data Source={path}");
            await verify.OpenAsync();
            await using var version = verify.CreateCommand();
            version.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
            Assert.Equal(8, Convert.ToInt32(await version.ExecuteScalarAsync()));
            Assert.Equal(3, line.RequestedBasketSize);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

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
