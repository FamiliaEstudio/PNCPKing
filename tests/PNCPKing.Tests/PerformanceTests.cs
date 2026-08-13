using System.Diagnostics;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

public sealed class PerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task RealDatabaseCopy_MeasuresOptimizedCriticalPaths()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_DATABASE_COPY");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("Opt-in: informe PNCPKING_PERFORMANCE_DATABASE_COPY com uma cópia isolada do banco.");
            return;
        }

        Assert.True(File.Exists(path), $"A cópia de desempenho não existe: {path}");
        var connections = new SqliteConnectionFactory(path);
        var contracts = new SqliteContractRepository(connections);
        var stopwatch = Stopwatch.StartNew();
        await contracts.InitializeAsync();
        stopwatch.Stop();
        var migrationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        var quotations = new SqliteQuotationRepository(connections);
        var quotationService = new QuotationService(quotations, new QuotationAnalyzer());
        var projects = await quotations.GetProjectsAsync();
        (QuotationProject? Project, QuotationLine? Line) selectedLine = default;
        foreach (var project in projects)
        {
            var line = (await quotations.GetLinesAsync(project.Id)).FirstOrDefault(candidate =>
                candidate.EffectiveDisplayName.Contains("Café 500g", StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                selectedLine = (project, line);
                break;
            }
        }
        var quotationSamples = new List<double>();
        if (selectedLine.Line is not null)
        {
            for (var sample = 0; sample < 5; sample++)
            {
                stopwatch.Restart();
                Assert.NotNull(await quotationService.GetAnalysisAsync(selectedLine.Project!.Id, selectedLine.Line.Id));
                stopwatch.Stop();
                quotationSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        var catalog = new SqliteCatalogRepository(connections);
        stopwatch.Restart();
        var catalogIndex = await catalog.GetDescriptionIndexProgressAsync();
        while (!catalogIndex.Completed)
        {
            catalogIndex = await catalog.BuildDescriptionIndexBatchAsync(2_000);
        }
        stopwatch.Stop();
        var catalogIndexMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        var catalogSearch = new CatalogSearchService(catalog);
        var catalogSamples = new List<double>();
        for (var sample = 0; sample < 5; sample++)
        {
            stopwatch.Restart();
            var page = await catalogSearch.SearchAsync(new CatalogSearchQuery(
                "café + torrado -cápsula",
                CatalogKind.Catmat));
            stopwatch.Stop();
            Assert.DoesNotContain(page.Results, result =>
                !SearchText.Normalize(result.Entry.Description).Contains("cafe", StringComparison.Ordinal));
            catalogSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var priceCache = new SqlitePriceCacheRepository(connections);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var newestQuery = new SearchQuery(
            string.Empty,
            GeoScope.All,
            today.AddDays(-364),
            today,
            Page: 1,
            PageSize: 50,
            Sort: SearchSort.Newest);
        var nearestQuery = newestQuery with
        {
            GeoFilter = SearchGeoFilter.NearRibeirao,
            Sort = SearchSort.Nearest
        };
        var newestSamples = new List<double>();
        var nearestSamples = new List<double>();
        for (var sample = 0; sample < 5; sample++)
        {
            stopwatch.Restart();
            _ = await contracts.SearchPageAsync(newestQuery);
            stopwatch.Stop();
            newestSamples.Add(stopwatch.Elapsed.TotalMilliseconds);

            stopwatch.Restart();
            _ = await contracts.SearchPageAsync(nearestQuery);
            stopwatch.Stop();
            nearestSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var localSamples = new List<double>();
        for (var sample = 0; sample < 5; sample++)
        {
            stopwatch.Restart();
            _ = await priceCache.SearchLocalAfterAsync(
                new SearchQuery(
                    "cafe",
                    GeoScope.All,
                    today.AddDays(-364),
                    today,
                    Page: 1,
                    PageSize: 50,
                    Sort: SearchSort.Newest),
                SearchText.Parse("cafe"),
                null,
                null,
                null,
                50);
            stopwatch.Stop();
            localSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var checkpoint = new SyncPartitionCheckpoint
        {
            PartitionKey = "performance-empty-commit",
            Mode = SyncMode.Publication,
            StartDate = today,
            EndDate = today,
            ModalityId = 6,
            Uf = "ALL",
            NextPage = 1,
            Status = SyncPartitionStatus.Partial,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var commitSamples = new List<double>();
        var mixedSamples = new List<double>();
        for (var sample = 0; sample < 5; sample++)
        {
            stopwatch.Restart();
            await contracts.CommitSyncPageAsync([], checkpoint);
            stopwatch.Stop();
            commitSamples.Add(stopwatch.Elapsed.TotalMilliseconds);

            stopwatch.Restart();
            var backgroundCommit = contracts.CommitSyncPageAsync(
                [],
                checkpoint with { PartitionKey = $"performance-mixed-{sample}" });
            var visiblePage = contracts.SearchPageAsync(nearestQuery);
            await Task.WhenAll(backgroundCommit, visiblePage);
            stopwatch.Stop();
            mixedSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var walPath = path + "-wal";
        var walBefore = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        stopwatch.Restart();
        await contracts.MaintainWalAsync();
        stopwatch.Stop();
        var checkpointMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        var walAfter = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;

        output.WriteLine(
            "migration_v18_ms={0:N1}; quotation_median_ms={1:N1}; quotation_p95_ms={2:N1}; " +
            "local_price_median_ms={3:N1}; local_price_p95_ms={4:N1}; catalog_index_ms={5:N1}; " +
            "catalog_search_median_ms={6:N1}; catalog_search_p95_ms={7:N1}; " +
            "empty_commit_median_ms={8:N1}; empty_commit_p95_ms={9:N1}; working_set_bytes={10}; " +
            "newest_page_median_ms={11:N1}; nearest_page_median_ms={12:N1}; " +
            "mixed_median_ms={13:N1}; checkpoint_ms={14:N1}; wal_before={15}; wal_after={16}",
            migrationMilliseconds,
            quotationSamples.Count == 0 ? double.NaN : Median(quotationSamples),
            quotationSamples.Count == 0 ? double.NaN : P95(quotationSamples),
            Median(localSamples),
            P95(localSamples),
            catalogIndexMilliseconds,
            Median(catalogSamples),
            P95(catalogSamples),
            Median(commitSamples),
            P95(commitSamples),
            Process.GetCurrentProcess().WorkingSet64,
            Median(newestSamples),
            Median(nearestSamples),
            Median(mixedSamples),
            checkpointMilliseconds,
            walBefore,
            walAfter);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task RealDatabaseCopy_MeasuresMigration15To18()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_DATABASE_COPY");
        var enabled = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_MIGRATE_FROM_V15");
        if (string.IsNullOrWhiteSpace(path) || enabled != "1")
        {
            output.WriteLine("Opt-in destrutivo somente para cópia: informe o banco e PNCPKING_PERFORMANCE_MIGRATE_FROM_V15=1.");
            return;
        }

        Assert.True(File.Exists(path));
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TRIGGER IF EXISTS catalog_description_fts_insert;
                DROP TRIGGER IF EXISTS catalog_description_fts_delete;
                DROP TRIGGER IF EXISTS catalog_description_fts_update;
                DROP TABLE IF EXISTS catalog_description_index_state;
                DROP TABLE IF EXISTS catalog_description_fts;
                DROP TRIGGER IF EXISTS dataset_statistics_contract_insert;
                DROP TRIGGER IF EXISTS dataset_statistics_contract_delete;
                DROP TRIGGER IF EXISTS dataset_statistics_item_insert;
                DROP TRIGGER IF EXISTS dataset_statistics_item_delete;
                DROP TRIGGER IF EXISTS dataset_statistics_result_insert;
                DROP TRIGGER IF EXISTS dataset_statistics_result_delete;
                DROP TABLE IF EXISTS dataset_statistics;
                DROP INDEX IF EXISTS idx_contracts_publication_id;
                DROP INDEX IF EXISTS idx_contracts_uf_publication_id;
                DROP INDEX IF EXISTS idx_contracts_geo_publication_id;
                DROP INDEX IF EXISTS idx_item_results_active_price;
                DROP INDEX IF EXISTS idx_price_cache_contracts_retry;
                CREATE INDEX IF NOT EXISTS idx_contracts_publication
                    ON contracts(publication_date DESC);
                CREATE INDEX IF NOT EXISTS idx_contracts_uf_publication
                    ON contracts(uf, publication_date DESC);
                UPDATE schema_info SET version = 15 WHERE id = 1;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var stopwatch = Stopwatch.StartNew();
        await new SqliteContractRepository(path).InitializeAsync();
        stopwatch.Stop();
        output.WriteLine(
            "migration_v15_to_v18_ms={0:N1}; target_max_ms=359030.0; improvement_vs_512900_ms={1:N1}%",
            stopwatch.Elapsed.TotalMilliseconds,
            (512_900d - stopwatch.Elapsed.TotalMilliseconds) * 100d / 512_900d);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(359_030));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task DeterministicLargeDatabase_MeasuresConsolidationPageAndExactCount()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PNCPKING_RUN_PERFORMANCE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine("Opt-in: defina PNCPKING_RUN_PERFORMANCE_TESTS=1 para executar a base grande.");
            return;
        }

        await using var database = await TestDatabase.CreateAsync();
        var totalContracts = int.TryParse(
            Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_CONTRACTS"),
            out var requestedContracts)
            ? Math.Max(25_000, requestedContracts)
            : 1_470_000;
        var consolidation = Stopwatch.StartNew();
        for (var offset = 0; offset < totalContracts; offset += 2_000)
        {
            var batch = Enumerable.Range(offset, Math.Min(2_000, totalContracts - offset))
                .Select(CreateContract)
                .ToArray();
            await database.Repository.UpsertContractsAsync(batch);
        }

        consolidation.Stop();
        var query = new SearchQuery(
            "material escolar",
            GeoScope.All,
            new DateOnly(2025, 1, 1),
            new DateOnly(2026, 12, 31),
            Page: 1,
            PageSize: 50,
            Sort: SearchSort.Newest);
        var pageSamples = new List<double>();
        var countSamples = new List<double>();
        for (var sample = 0; sample < 5; sample++)
        {
            var stopwatch = Stopwatch.StartNew();
            var page = await database.Repository.SearchPageAsync(query);
            stopwatch.Stop();
            pageSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
            Assert.Equal(50, page.Results.Count);

            stopwatch.Restart();
            var count = await database.Repository.CountSearchAsync(query);
            stopwatch.Stop();
            countSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
            Assert.Equal(totalContracts, count);
        }

        var workingSet = Process.GetCurrentProcess().WorkingSet64;
        output.WriteLine(
            "contracts={0}; consolidation_ms={1:N1}; records_per_second={2:N1}; " +
            "page_median_ms={3:N1}; count_median_ms={4:N1}; working_set_bytes={5}",
            totalContracts,
            consolidation.Elapsed.TotalMilliseconds,
            totalContracts / consolidation.Elapsed.TotalSeconds,
            Median(pageSamples),
            Median(countSamples),
            workingSet);
    }

    private static ContractRecord CreateContract(int number)
    {
        var date = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(number % 700);
        return new ContractRecord
        {
            PncpId = $"performance-{number:D8}",
            Cnpj = $"{number % 100_000_000:D8}000199",
            PurchaseYear = date.Year,
            PurchaseSequence = number + 1,
            Object = $"Aquisição de material escolar lote {number:D8}",
            AdditionalInformation = "Base determinística de desempenho",
            Process = $"P-{number:D8}",
            Organization = "Órgão de desempenho",
            Unit = "Unidade de teste",
            Municipality = number % 2 == 0 ? "Ribeirão Preto" : "São Paulo",
            Uf = "SP",
            ModalityId = 6,
            ModalityName = "Pregão eletrônico",
            Status = "Divulgada",
            PublicationDate = date,
            GlobalUpdatedAt = date,
            TotalHomologatedScaled = number * 10_000L
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered[ordered.Length / 2];
    }

    private static double P95(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * .95) - 1, 0, ordered.Length - 1)];
    }
}
