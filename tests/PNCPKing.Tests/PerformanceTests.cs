using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    public async Task RealDatabaseCopy_FtsCandidatesAlternateFiveRoundsAndMeetTargets()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_DATABASE_COPY");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine(
                "Opt-in: informe PNCPKING_PERFORMANCE_DATABASE_COPY com uma cópia isolada do banco.");
            return;
        }

        Assert.True(File.Exists(path), $"A cópia de desempenho não existe: {path}");
        var commonTerm = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_COMMON_TERM") ?? "a";
        var selectiveTerm = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_SELECTIVE_TERM") ?? "agua";
        var connections = new SqliteConnectionFactory(path);
        await new SqliteContractRepository(connections).InitializeAsync();
        var repository = new SqlitePriceCacheRepository(connections);

        var common = await BenchmarkFtsTermAsync(connections, repository, commonTerm);
        var selective = await BenchmarkFtsTermAsync(connections, repository, selectiveTerm);
        var commonLegacyMedian = Median(common.LegacyMilliseconds);
        var commonOptimizedMedian = Median(common.OptimizedMilliseconds);
        var selectiveLegacyMedian = Median(selective.LegacyMilliseconds);
        var selectiveOptimizedMedian = Median(selective.OptimizedMilliseconds);
        output.WriteLine(
            "common={0}; legacy_median_ms={1:N1}; optimized_median_ms={2:N1}; gain={3:N2}x; " +
            "selective={4}; legacy_median_ms={5:N1}; optimized_median_ms={6:N1}; ratio={7:N3}",
            commonTerm,
            commonLegacyMedian,
            commonOptimizedMedian,
            commonLegacyMedian / commonOptimizedMedian,
            selectiveTerm,
            selectiveLegacyMedian,
            selectiveOptimizedMedian,
            selectiveOptimizedMedian / selectiveLegacyMedian);

        Assert.True(
            commonLegacyMedian / commonOptimizedMedian >= 2d,
            $"Ganho do termo comum abaixo de 2x: {commonLegacyMedian / commonOptimizedMedian:N2}x.");
        Assert.True(
            selectiveOptimizedMedian <= selectiveLegacyMedian * 1.10d,
            "A regressão mediana do termo seletivo ultrapassou 10%. " +
            $"Antiga={selectiveLegacyMedian:N1} ms; otimizada={selectiveOptimizedMedian:N1} ms.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task RealDatabaseCopy_ExplainsFtsCandidateOrdering()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_DATABASE_COPY");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine(
                "Opt-in: informe PNCPKING_PERFORMANCE_DATABASE_COPY com uma cópia isolada do banco.");
            return;
        }

        Assert.True(File.Exists(path), $"A cópia de desempenho não existe: {path}");
        var term = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_COMMON_TERM") ?? "a";
        var expression = SearchText.Parse(term);
        Assert.False(expression.IsEmpty);
        var connections = new SqliteConnectionFactory(path);
        await using var connection = await connections.OpenAsync();

        var bm25Plan = await ExplainAsync(
            connection,
            """
            SELECT items_fts.rowid, bm25(items_fts) AS primary_rank
              FROM items_fts
             WHERE items_fts MATCH $itemMatch
             ORDER BY primary_rank
             LIMIT 5001;
            """,
            expression.ItemMatchQuery);
        var rankPlan = await ExplainAsync(
            connection,
            """
            SELECT items_fts.rowid, rank AS primary_rank
              FROM items_fts
             WHERE items_fts MATCH $itemMatch
             ORDER BY rank
             LIMIT 5001;
            """,
            expression.ItemMatchQuery);

        output.WriteLine("bm25_plan={0}", string.Join(" | ", bm25Plan));
        output.WriteLine("rank_plan={0}", string.Join(" | ", rankPlan));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task RealDatabaseCopy_MeasuresRankedFtsCandidatesFiveRounds()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_DATABASE_COPY");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine(
                "Opt-in: informe PNCPKING_PERFORMANCE_DATABASE_COPY com uma cópia isolada do banco.");
            return;
        }

        Assert.True(File.Exists(path), $"A cópia de desempenho não existe: {path}");
        var term = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_COMMON_TERM") ?? "a";
        var expression = SearchText.Parse(term);
        Assert.False(expression.IsEmpty);
        var connections = new SqliteConnectionFactory(path);
        var repository = new SqlitePriceCacheRepository(connections);
        var query = new SearchQuery(term, GeoScope.All, Sort: SearchSort.Relevance);
        var samples = new List<double>(5);
        IReadOnlyList<FtsBenchmarkKey>? expected = null;

        for (var round = 0; round < 5; round++)
        {
            var measured = await MeasureAsync(() =>
                ReadOptimizedFtsPageAsync(repository, query, expression));
            expected ??= measured.Rows;
            Assert.Equal(expected, measured.Rows);
            samples.Add(measured.Duration.TotalMilliseconds);
        }

        var verified = Assert.IsAssignableFrom<IReadOnlyList<FtsBenchmarkKey>>(expected);
        Assert.NotEmpty(verified);
        output.WriteLine(
            "term={0}; ranked_median_ms={1:N1}; ranked_p95_ms={2:N1}; rows={3}",
            term,
            Median(samples),
            P95(samples),
            verified.Count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task RealDatabaseCopy_BenchmarksRepresentativeFtsMatrix()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_DATABASE_COPY");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine(
                "Opt-in: informe PNCPKING_PERFORMANCE_DATABASE_COPY com uma cópia isolada do banco.");
            return;
        }

        Assert.True(File.Exists(path), $"A cópia de desempenho não existe: {path}");
        var rounds = ReadPositiveEnvironmentInteger("PNCPKING_PERFORMANCE_ROUNDS", 3);
        var timeoutMinutes = ReadPositiveEnvironmentInteger("PNCPKING_PERFORMANCE_TIMEOUT_MINUTES", 20);
        var cardinalityCeiling = ReadPositiveEnvironmentInteger(
            "PNCPKING_PERFORMANCE_CARDINALITY_CEILING",
            1_000_000);
        var reportPath = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            reportPath = Path.Combine(
                Path.GetDirectoryName(path)!,
                "fts-representative-benchmark.json");
        }

        var connections = new SqliteConnectionFactory(path);
        await new SqliteContractRepository(connections).InitializeAsync();
        var repository = new SqlitePriceCacheRepository(connections);
        var newestDate = await ReadNewestPublicationDateAsync(connections);
        var scenarios = BuildRepresentativeFtsScenarios(newestDate);
        var report = new RepresentativeBenchmarkReport(
            DateTimeOffset.UtcNow,
            new FileInfo(path).Length,
            rounds,
            timeoutMinutes,
            cardinalityCeiling,
            []);

        foreach (var scenario in scenarios)
        {
            var expression = SearchText.Parse(scenario.Query.Text);
            var cardinality = await ReadFtsCardinalityAsync(
                connections,
                expression.ItemMatchQuery,
                cardinalityCeiling);
            var result = new RepresentativeScenarioResult(
                scenario.Name,
                scenario.Query.Text,
                cardinality.Count,
                cardinality.Capped,
                [],
                null);
            report.Scenarios.Add(result);

            if (cardinality.Count == 0)
            {
                result.SkipReason = "Sem correspondências FTS nesta base.";
                await WriteRepresentativeReportAsync(reportPath, report);
                continue;
            }

            if (cardinality.Capped)
            {
                result.SkipReason =
                    $"Cardinalidade superior ao teto de {cardinalityCeiling:N0}; cenário extremo pulado.";
                await WriteRepresentativeReportAsync(reportPath, report);
                continue;
            }

            IReadOnlyList<FtsBenchmarkKey>? expected = null;
            for (var round = 0; round < rounds; round++)
            {
                var timedOut = false;
                var engines = round % 2 == 0
                    ? new[] { "legacy", "optimized" }
                    : new[] { "optimized", "legacy" };
                foreach (var engine in engines)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
                    try
                    {
                        var measured = await MeasureAsync(() => engine == "legacy"
                            ? ReadLegacyFtsPageAsync(
                                connections,
                                expression,
                                scenario.Query,
                                scenario.MinimumUnitPrice,
                                scenario.MaximumUnitPrice,
                                scenario.PageSize,
                                timeout.Token)
                            : ReadOptimizedFtsPageAsync(
                                repository,
                                scenario.Query,
                                expression,
                                scenario.MinimumUnitPrice,
                                scenario.MaximumUnitPrice,
                                scenario.PageSize,
                                timeout.Token));
                        expected ??= measured.Rows;
                        Assert.Equal(expected, measured.Rows);
                        result.Measurements.Add(new RepresentativeMeasurement(
                            round + 1,
                            engine,
                            measured.Duration.TotalMilliseconds,
                            measured.Rows.Count,
                            TimedOut: false));
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        result.Measurements.Add(new RepresentativeMeasurement(
                            round + 1,
                            engine,
                            TimeSpan.FromMinutes(timeoutMinutes).TotalMilliseconds,
                            Rows: 0,
                            TimedOut: true));
                        result.SkipReason =
                            $"{engine} excedeu o limite de {timeoutMinutes} minuto(s); repetições interrompidas.";
                        timedOut = true;
                    }

                    await WriteRepresentativeReportAsync(reportPath, report);
                }

                if (timedOut)
                {
                    break;
                }
            }

            await WriteRepresentativeReportAsync(reportPath, report);
            var legacy = result.Measurements
                .Where(value => value.Engine == "legacy" && !value.TimedOut)
                .Select(value => value.ElapsedMilliseconds)
                .ToArray();
            var optimized = result.Measurements
                .Where(value => value.Engine == "optimized" && !value.TimedOut)
                .Select(value => value.ElapsedMilliseconds)
                .ToArray();
            output.WriteLine(
                "scenario={0}; fts={1:N0}; legacy_median_ms={2}; optimized_median_ms={3}; report={4}",
                scenario.Name,
                cardinality.Count,
                legacy.Length == 0 ? "n/a" : Median(legacy).ToString("N1", CultureInfo.InvariantCulture),
                optimized.Length == 0 ? "n/a" : Median(optimized).ToString("N1", CultureInfo.InvariantCulture),
                reportPath);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task RealDatabaseCopy_ComparesRankCandidateWindowLimits()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_DATABASE_COPY");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine(
                "Opt-in: informe PNCPKING_PERFORMANCE_DATABASE_COPY com uma cópia isolada do banco.");
            return;
        }

        Assert.True(File.Exists(path), $"A cópia de desempenho não existe: {path}");
        var rounds = ReadPositiveEnvironmentInteger("PNCPKING_PERFORMANCE_ROUNDS", 3);
        var timeoutMinutes = ReadPositiveEnvironmentInteger("PNCPKING_PERFORMANCE_TIMEOUT_MINUTES", 5);
        var cardinalityCeiling = ReadPositiveEnvironmentInteger(
            "PNCPKING_PERFORMANCE_CARDINALITY_CEILING",
            1_000_000);
        var limits = ReadCandidateLimits();
        var reportPath = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            reportPath = Path.Combine(Path.GetDirectoryName(path)!, "fts-rank-window-limits.json");
        }

        var connections = new SqliteConnectionFactory(path);
        await new SqliteContractRepository(connections).InitializeAsync();
        var newestDate = await ReadNewestPublicationDateAsync(connections);
        var scenarios = BuildRepresentativeFtsScenarios(newestDate)
            .Append(new RepresentativeFtsScenario(
                "uma-exclusao-pagina-200",
                new SearchQuery("cafe -capsula", GeoScope.All, Sort: SearchSort.Relevance),
                PageSize: 200))
            .ToArray();
        var report = new CandidateLimitBenchmarkReport(
            DateTimeOffset.UtcNow,
            new FileInfo(path).Length,
            rounds,
            timeoutMinutes,
            limits,
            []);

        foreach (var scenario in scenarios)
        {
            var expression = SearchText.Parse(scenario.Query.Text);
            var cardinality = await ReadFtsCardinalityAsync(
                connections,
                expression.ItemMatchQuery,
                cardinalityCeiling);
            var result = new CandidateLimitScenarioResult(
                scenario.Name,
                scenario.Query.Text,
                scenario.PageSize,
                cardinality.Count,
                cardinality.Capped,
                [],
                null);
            report.Scenarios.Add(result);

            if (cardinality.Count == 0 || cardinality.Capped)
            {
                result.SkipReason = cardinality.Count == 0
                    ? "Sem correspondências FTS nesta base."
                    : $"Cardinalidade superior ao teto de {cardinalityCeiling:N0}.";
                await WriteCandidateLimitReportAsync(reportPath, report);
                continue;
            }

            var warmupRepository = new SqlitePriceCacheRepository(
                connections,
                performance: null,
                initialFtsCandidateLimit: 5_000,
                highCardinalityFtsThreshold: 0);
            var expected = await ReadOptimizedFtsPageAsync(
                warmupRepository,
                scenario.Query,
                expression,
                scenario.MinimumUnitPrice,
                scenario.MaximumUnitPrice,
                scenario.PageSize,
                CancellationToken.None);

            for (var round = 0; round < rounds; round++)
            {
                foreach (var limit in OrderCandidateLimits(limits, round))
                {
                    var repository = new SqlitePriceCacheRepository(
                        connections,
                        performance: null,
                        initialFtsCandidateLimit: limit,
                        highCardinalityFtsThreshold: 0);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
                    try
                    {
                        var measured = await MeasureAsync(() => ReadOptimizedFtsPageAsync(
                            repository,
                            scenario.Query,
                            expression,
                            scenario.MinimumUnitPrice,
                            scenario.MaximumUnitPrice,
                            scenario.PageSize,
                            timeout.Token));
                        Assert.Equal(expected, measured.Rows);
                        result.Measurements.Add(new CandidateLimitMeasurement(
                            round + 1,
                            limit,
                            measured.Duration.TotalMilliseconds,
                            measured.Rows.Count,
                            TimedOut: false));
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        result.Measurements.Add(new CandidateLimitMeasurement(
                            round + 1,
                            limit,
                            TimeSpan.FromMinutes(timeoutMinutes).TotalMilliseconds,
                            Rows: 0,
                            TimedOut: true));
                    }

                    await WriteCandidateLimitReportAsync(reportPath, report);
                }
            }

            output.WriteLine(
                "scenario={0}; fts={1:N0}; page={2}; {3}",
                scenario.Name,
                cardinality.Count,
                scenario.PageSize,
                string.Join(
                    "; ",
                    limits.Select(limit =>
                    {
                        var samples = result.Measurements
                            .Where(value => value.CandidateLimit == limit && !value.TimedOut)
                            .Select(value => value.ElapsedMilliseconds)
                            .ToArray();
                        return samples.Length == 0
                            ? $"limit_{limit}=timeout"
                            : $"limit_{limit}_median_ms={Median(samples):N1}";
                    })));
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task RealDatabaseCopy_ComparesStructuralHybridPolicies()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_DATABASE_COPY");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine(
                "Opt-in: informe PNCPKING_PERFORMANCE_DATABASE_COPY com uma cópia isolada do banco.");
            return;
        }

        Assert.True(File.Exists(path), $"A cópia de desempenho não existe: {path}");
        var rounds = ReadPositiveEnvironmentInteger("PNCPKING_PERFORMANCE_ROUNDS", 3);
        var timeoutMinutes = ReadPositiveEnvironmentInteger("PNCPKING_PERFORMANCE_TIMEOUT_MINUTES", 2);
        var cardinalityCeiling = ReadPositiveEnvironmentInteger(
            "PNCPKING_PERFORMANCE_CARDINALITY_CEILING",
            1_000_000);
        var reportPath = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            reportPath = Path.Combine(Path.GetDirectoryName(path)!, "fts-structural-hybrid.json");
        }

        var connections = new SqliteConnectionFactory(path);
        await new SqliteContractRepository(connections).InitializeAsync();
        var newestDate = await ReadNewestPublicationDateAsync(connections);
        var scenarios = BuildStructuralHybridScenarios(newestDate);
        var report = new StructuralHybridBenchmarkReport(
            DateTimeOffset.UtcNow,
            new FileInfo(path).Length,
            rounds,
            timeoutMinutes,
            []);

        foreach (var scenario in scenarios)
        {
            var expression = SearchText.Parse(scenario.Query.Text);
            var cardinality = await ReadFtsCardinalityAsync(
                connections,
                expression.ItemMatchQuery,
                cardinalityCeiling);
            var rankLimit = SelectBenchmarkCandidateLimit(scenario, expression);
            var conservative = SelectStructuralHybridEngine(scenario, expression, aggressive: false);
            var aggressive = SelectStructuralHybridEngine(scenario, expression, aggressive: true);
            var result = new StructuralHybridScenarioResult(
                scenario.Name,
                scenario.Query.Text,
                cardinality.Count,
                cardinality.Capped,
                rankLimit,
                conservative,
                aggressive,
                [],
                null);
            report.Scenarios.Add(result);

            if (cardinality.Count == 0 || cardinality.Capped)
            {
                result.SkipReason = cardinality.Count == 0
                    ? "Sem correspondências FTS nesta base."
                    : $"Cardinalidade superior ao teto de {cardinalityCeiling:N0}.";
                await WriteStructuralHybridReportAsync(reportPath, report);
                continue;
            }

            var rankRepository = new SqlitePriceCacheRepository(
                connections,
                performance: null,
                initialFtsCandidateLimit: rankLimit,
                highCardinalityFtsThreshold: 0);
            var expected = await ReadOptimizedFtsPageAsync(
                rankRepository,
                scenario.Query,
                expression,
                scenario.MinimumUnitPrice,
                scenario.MaximumUnitPrice,
                scenario.PageSize,
                CancellationToken.None);

            for (var round = 0; round < rounds; round++)
            {
                var engines = round % 2 == 0
                    ? new[] { "direct", "rank" }
                    : new[] { "rank", "direct" };
                foreach (var engine in engines)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
                    try
                    {
                        var measured = await MeasureAsync(() => engine == "direct"
                            ? ReadLegacyFtsPageAsync(
                                connections,
                                expression,
                                scenario.Query,
                                scenario.MinimumUnitPrice,
                                scenario.MaximumUnitPrice,
                                scenario.PageSize,
                                timeout.Token)
                            : ReadOptimizedFtsPageAsync(
                                rankRepository,
                                scenario.Query,
                                expression,
                                scenario.MinimumUnitPrice,
                                scenario.MaximumUnitPrice,
                                scenario.PageSize,
                                timeout.Token));
                        Assert.Equal(expected, measured.Rows);
                        result.Measurements.Add(new StructuralHybridMeasurement(
                            round + 1,
                            engine,
                            measured.Duration.TotalMilliseconds,
                            measured.Rows.Count,
                            TimedOut: false));
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        result.Measurements.Add(new StructuralHybridMeasurement(
                            round + 1,
                            engine,
                            TimeSpan.FromMinutes(timeoutMinutes).TotalMilliseconds,
                            Rows: 0,
                            TimedOut: true));
                    }

                    await WriteStructuralHybridReportAsync(reportPath, report);
                }
            }

            var direct = result.Measurements
                .Where(value => value.Engine == "direct" && !value.TimedOut)
                .Select(value => value.ElapsedMilliseconds)
                .ToArray();
            var rank = result.Measurements
                .Where(value => value.Engine == "rank" && !value.TimedOut)
                .Select(value => value.ElapsedMilliseconds)
                .ToArray();
            output.WriteLine(
                "scenario={0}; fts={1:N0}; direct_ms={2}; rank_ms={3}; limit={4}; conservative={5}; aggressive={6}",
                scenario.Name,
                cardinality.Count,
                direct.Length == 0 ? "timeout" : Median(direct).ToString("N1", CultureInfo.InvariantCulture),
                rank.Length == 0 ? "timeout" : Median(rank).ToString("N1", CultureInfo.InvariantCulture),
                rankLimit,
                conservative,
                aggressive);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task RealDatabaseCopy_ComparesBoundedProbeHybrid()
    {
        var path = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_DATABASE_COPY");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine(
                "Opt-in: informe PNCPKING_PERFORMANCE_DATABASE_COPY com uma cópia isolada do banco.");
            return;
        }

        Assert.True(File.Exists(path), $"A cópia de desempenho não existe: {path}");
        var rounds = ReadPositiveEnvironmentInteger("PNCPKING_PERFORMANCE_ROUNDS", 3);
        var timeoutMinutes = ReadPositiveEnvironmentInteger("PNCPKING_PERFORMANCE_TIMEOUT_MINUTES", 2);
        var cardinalityCeiling = ReadPositiveEnvironmentInteger(
            "PNCPKING_PERFORMANCE_CARDINALITY_CEILING",
            1_000_000);
        var probeLimits = ReadProbeLimits();
        var reportPath = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            reportPath = Path.Combine(Path.GetDirectoryName(path)!, "fts-bounded-probe-hybrid.json");
        }

        var connections = new SqliteConnectionFactory(path);
        await new SqliteContractRepository(connections).InitializeAsync();
        var newestDate = await ReadNewestPublicationDateAsync(connections);
        var scenarios = BuildBoundedProbeScenarios(newestDate);
        var report = new BoundedProbeBenchmarkReport(
            DateTimeOffset.UtcNow,
            new FileInfo(path).Length,
            rounds,
            timeoutMinutes,
            probeLimits,
            []);

        foreach (var scenario in scenarios)
        {
            var expression = SearchText.Parse(scenario.Query.Text);
            var cardinality = await ReadFtsCardinalityAsync(
                connections,
                expression.ItemMatchQuery,
                cardinalityCeiling);
            var rankLimit = SelectBenchmarkCandidateLimit(scenario, expression);
            var result = new BoundedProbeScenarioResult(
                scenario.Name,
                scenario.Query.Text,
                cardinality.Count,
                cardinality.Capped,
                rankLimit,
                [],
                null);
            report.Scenarios.Add(result);

            if (cardinality.Count == 0 || cardinality.Capped)
            {
                result.SkipReason = cardinality.Count == 0
                    ? "Sem correspondências FTS nesta base."
                    : $"Cardinalidade superior ao teto de {cardinalityCeiling:N0}.";
                await WriteBoundedProbeReportAsync(reportPath, report);
                continue;
            }

            var rankRepository = new SqlitePriceCacheRepository(
                connections,
                performance: null,
                initialFtsCandidateLimit: rankLimit,
                highCardinalityFtsThreshold: 0);
            var expected = await ReadOptimizedFtsPageAsync(
                rankRepository,
                scenario.Query,
                expression,
                scenario.MinimumUnitPrice,
                scenario.MaximumUnitPrice,
                scenario.PageSize,
                CancellationToken.None);

            for (var round = 0; round < rounds; round++)
            {
                var baselineEngines = round % 2 == 0
                    ? new[] { "direct", "rank" }
                    : new[] { "rank", "direct" };
                foreach (var engine in baselineEngines)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
                    var measured = await MeasureAsync(() => engine == "direct"
                        ? ReadLegacyFtsPageAsync(
                            connections,
                            expression,
                            scenario.Query,
                            scenario.MinimumUnitPrice,
                            scenario.MaximumUnitPrice,
                            scenario.PageSize,
                            timeout.Token)
                        : ReadOptimizedFtsPageAsync(
                            rankRepository,
                            scenario.Query,
                            expression,
                            scenario.MinimumUnitPrice,
                            scenario.MaximumUnitPrice,
                            scenario.PageSize,
                            timeout.Token));
                    Assert.Equal(expected, measured.Rows);
                    result.Measurements.Add(new BoundedProbeMeasurement(
                        round + 1,
                        engine,
                        ProbeLimit: null,
                        ProbeMilliseconds: 0,
                        QueryMilliseconds: measured.Duration.TotalMilliseconds,
                        TotalMilliseconds: measured.Duration.TotalMilliseconds,
                        ProbeRows: 0,
                        ProbeExhausted: false,
                        measured.Rows.Count));
                }

                foreach (var probeLimit in OrderProbeLimits(probeLimits, round))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
                    var total = Stopwatch.StartNew();
                    var probe = await ReadBoundedFtsProbeAsync(
                        connections,
                        expression.ItemMatchQuery,
                        probeLimit,
                        timeout.Token);
                    var selectedEngine = probe.Exhausted ? "direct" : "rank";
                    var query = await MeasureAsync(() => selectedEngine == "direct"
                        ? ReadLegacyFtsPageAsync(
                            connections,
                            expression,
                            scenario.Query,
                            scenario.MinimumUnitPrice,
                            scenario.MaximumUnitPrice,
                            scenario.PageSize,
                            timeout.Token)
                        : ReadOptimizedFtsPageAsync(
                            rankRepository,
                            scenario.Query,
                            expression,
                            scenario.MinimumUnitPrice,
                            scenario.MaximumUnitPrice,
                            scenario.PageSize,
                            timeout.Token));
                    total.Stop();
                    Assert.Equal(expected, query.Rows);
                    result.Measurements.Add(new BoundedProbeMeasurement(
                        round + 1,
                        selectedEngine,
                        probeLimit,
                        probe.Duration.TotalMilliseconds,
                        query.Duration.TotalMilliseconds,
                        total.Elapsed.TotalMilliseconds,
                        probe.Rows,
                        probe.Exhausted,
                        query.Rows.Count));
                    await WriteBoundedProbeReportAsync(reportPath, report);
                }
            }

            var direct = Median(result.Measurements
                .Where(value => value.ProbeLimit is null && value.Engine == "direct")
                .Select(value => value.TotalMilliseconds));
            var rank = Median(result.Measurements
                .Where(value => value.ProbeLimit is null && value.Engine == "rank")
                .Select(value => value.TotalMilliseconds));
            output.WriteLine(
                "scenario={0}; fts={1:N0}; direct_ms={2:N1}; rank_ms={3:N1}; {4}",
                scenario.Name,
                cardinality.Count,
                direct,
                rank,
                string.Join(
                    "; ",
                    probeLimits.Select(limit =>
                    {
                        var samples = result.Measurements
                            .Where(value => value.ProbeLimit == limit)
                            .Select(value => value.TotalMilliseconds)
                            .ToArray();
                        var engine = result.Measurements.First(value => value.ProbeLimit == limit).Engine;
                        return $"probe_{limit}_{engine}_ms={Median(samples):N1}";
                    })));
        }
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

    private static async Task<FtsBenchmarkResult> BenchmarkFtsTermAsync(
        SqliteConnectionFactory connections,
        SqlitePriceCacheRepository repository,
        string text)
    {
        var expression = SearchText.Parse(text);
        Assert.False(expression.IsEmpty);
        var query = new SearchQuery(text, GeoScope.All, Sort: SearchSort.Relevance);
        var legacyMilliseconds = new List<double>(5);
        var optimizedMilliseconds = new List<double>(5);
        IReadOnlyList<FtsBenchmarkKey>? expected = null;

        for (var round = 0; round < 5; round++)
        {
            (TimeSpan Duration, IReadOnlyList<FtsBenchmarkKey> Rows) legacy;
            (TimeSpan Duration, IReadOnlyList<FtsBenchmarkKey> Rows) optimized;
            if (round % 2 == 0)
            {
                legacy = await MeasureAsync(() => ReadLegacyFtsPageAsync(connections, expression));
                optimized = await MeasureAsync(() => ReadOptimizedFtsPageAsync(repository, query, expression));
            }
            else
            {
                optimized = await MeasureAsync(() => ReadOptimizedFtsPageAsync(repository, query, expression));
                legacy = await MeasureAsync(() => ReadLegacyFtsPageAsync(connections, expression));
            }

            expected ??= legacy.Rows;
            Assert.Equal(expected, legacy.Rows);
            Assert.Equal(expected, optimized.Rows);
            legacyMilliseconds.Add(legacy.Duration.TotalMilliseconds);
            optimizedMilliseconds.Add(optimized.Duration.TotalMilliseconds);
        }

        return new FtsBenchmarkResult(legacyMilliseconds, optimizedMilliseconds);
    }

    private static async Task<IReadOnlyList<FtsBenchmarkKey>> ReadOptimizedFtsPageAsync(
        SqlitePriceCacheRepository repository,
        SearchQuery query,
        SearchExpression expression)
        => await ReadOptimizedFtsPageAsync(
            repository,
            query,
            expression,
            null,
            null,
            200,
            CancellationToken.None);

    private static async Task<IReadOnlyList<FtsBenchmarkKey>> ReadOptimizedFtsPageAsync(
        SqlitePriceCacheRepository repository,
        SearchQuery query,
        SearchExpression expression,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var page = await repository.SearchLocalAfterAsync(
            query,
            expression,
            minimumUnitPrice,
            maximumUnitPrice,
            null,
            pageSize,
            cancellationToken);
        return (page.Rows ?? [])
            .Select(row => new FtsBenchmarkKey(
                row.Contract.PncpId,
                row.Item.ItemNumber,
                row.Result?.ResultSequence ?? 0))
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> ExplainAsync(
        SqliteConnection connection,
        string sql,
        string itemMatch)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("$itemMatch", itemMatch);
        var steps = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            steps.Add(reader.GetString(3));
        }

        return steps;
    }

    private static async Task<IReadOnlyList<FtsBenchmarkKey>> ReadLegacyFtsPageAsync(
        SqliteConnectionFactory connections,
        SearchExpression expression)
        => await ReadLegacyFtsPageAsync(
            connections,
            expression,
            new SearchQuery(expression.OriginalText, GeoScope.All, Sort: SearchSort.Relevance),
            null,
            null,
            200,
            CancellationToken.None);

    private static async Task<IReadOnlyList<FtsBenchmarkKey>> ReadLegacyFtsPageAsync(
        SqliteConnectionFactory connections,
        SearchExpression expression,
        SearchQuery query,
        decimal? minimumUnitPrice,
        decimal? maximumUnitPrice,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var conditions = new List<string>
        {
            "items_fts MATCH $itemMatch",
            "i.hydration_status = $complete",
            "COALESCE(s.source_global_updated_at, '') = COALESCE(c.global_updated_at, '')",
            "r.result_status_id = 1",
            "r.unit_value_scaled > 0"
        };
        switch (query.EffectiveGeoFilter.Kind)
        {
            case SearchGeoFilterKind.Southeast:
                conditions.Add("c.uf IN ('ES','MG','RJ','SP')");
                break;
            case SearchGeoFilterKind.State:
                conditions.Add("c.uf = $uf");
                break;
            case SearchGeoFilterKind.NearRibeirao:
                conditions.Add("c.geo_layer = 0");
                break;
        }
        if (query.StartDate is not null)
        {
            conditions.Add("c.publication_date >= $startDate");
        }
        if (query.EndDate is not null)
        {
            conditions.Add("c.publication_date < $endDateExclusive");
        }
        if (minimumUnitPrice is not null)
        {
            conditions.Add("r.unit_value_scaled >= $minimum");
        }
        if (maximumUnitPrice is not null)
        {
            conditions.Add("r.unit_value_scaled <= $maximum");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.pncp_id, c.cnpj, c.purchase_year, c.purchase_sequence, c.object,
                   c.additional_information, c.process, c.organization, c.unit, c.municipality,
                   c.municipality_ibge_code, c.uf, c.modality_id, c.modality_name, c.status,
                   c.publication_date, c.global_updated_at, c.total_homologated_scaled,
                   c.distance_from_ribeirao_km,
                   i.contract_id, i.item_number, i.description, i.unit, i.requested_quantity_scaled,
                   i.additional_information, i.item_category, i.ncm_nbs_code, i.ncm_nbs_description,
                   i.catalog_code, i.catalog_name, i.catalog_category, i.status, i.has_result,
                   i.source_updated_at, i.hydration_status, i.last_error,
                   0 AS sort_priority, bm25(items_fts) AS primary_rank, 0.0 AS secondary_rank,
                   r.result_sequence, r.supplier_tax_id, r.supplier_name, r.supplier_type,
                   r.supplier_municipality, r.supplier_uf, r.quantity_scaled,
                   r.unit_value_scaled, r.total_value_scaled, r.result_date,
                   r.result_status_id, r.result_status_name
              FROM items_fts
              CROSS JOIN items i ON i.rowid = items_fts.rowid
              CROSS JOIN contracts c ON c.pncp_id = i.contract_id
              CROSS JOIN contract_item_snapshots s ON s.contract_id = i.contract_id
              CROSS JOIN item_results r
                ON r.contract_id = i.contract_id AND r.item_number = i.item_number
             WHERE {string.Join(" AND ", conditions)}
             ORDER BY bm25(items_fts), c.publication_date DESC,
                      c.pncp_id, i.item_number, r.result_sequence
             LIMIT $scanLimit;
            """;
        command.Parameters.AddWithValue("$itemMatch", expression.ItemMatchQuery);
        command.Parameters.AddWithValue("$complete", (int)ItemHydrationStatus.Complete);
        command.Parameters.AddWithValue("$scanLimit", Math.Min(10_000, Math.Max(pageSize * 4, pageSize + 1)));
        if (query.EffectiveGeoFilter.Kind == SearchGeoFilterKind.State)
        {
            command.Parameters.AddWithValue("$uf", query.EffectiveGeoFilter.Uf!);
        }
        if (query.StartDate is not null)
        {
            command.Parameters.AddWithValue("$startDate", query.StartDate.Value.ToString("yyyy-MM-dd"));
        }
        if (query.EndDate is not null)
        {
            command.Parameters.AddWithValue(
                "$endDateExclusive",
                query.EndDate.Value.AddDays(1).ToString("yyyy-MM-dd"));
        }
        if (minimumUnitPrice is not null)
        {
            command.Parameters.AddWithValue("$minimum", DecimalScale.ToScaled(minimumUnitPrice.Value)!.Value);
        }
        if (maximumUnitPrice is not null)
        {
            command.Parameters.AddWithValue("$maximum", DecimalScale.ToScaled(maximumUnitPrice.Value)!.Value);
        }

        var rows = new List<FtsBenchmarkKey>(pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                _ = reader.GetValue(ordinal);
            }
            if (!expression.MatchesItem(reader.GetString(21), reader.GetString(22)))
            {
                continue;
            }
            rows.Add(new FtsBenchmarkKey(reader.GetString(0), reader.GetInt64(20), reader.GetInt64(39)));
            if (rows.Count >= pageSize)
            {
                break;
            }
        }
        return rows.ToArray();
    }

    private static IReadOnlyList<RepresentativeFtsScenario> BuildRepresentativeFtsScenarios(
        DateOnly newestDate) =>
    [
        new("produto-simples", new SearchQuery("arame", GeoScope.All, Sort: SearchSort.Relevance)),
        new("dois-termos-and", new SearchQuery("arame galvanizado", GeoScope.All, Sort: SearchSort.Relevance)),
        new("frase", new SearchQuery("\"papel a4\"", GeoScope.All, Sort: SearchSort.Relevance)),
        new("uma-exclusao", new SearchQuery("cafe -capsula", GeoScope.All, Sort: SearchSort.Relevance)),
        new(
            "varias-exclusoes",
            new SearchQuery(
                "televisao -servico -instalacao -manutencao -locacao -suporte",
                GeoScope.All,
                Sort: SearchSort.Relevance)),
        new(
            "ou-com-exclusoes",
            new SearchQuery(
                "cafe OU cha -capsula -maquina -soluvel",
                GeoScope.All,
                Sort: SearchSort.Relevance)),
        new(
            "estado-data-preco",
            new SearchQuery(
                "material escolar -servico -locacao",
                SearchGeoFilter.State("SP"),
                newestDate.AddDays(-364),
                newestDate,
                Sort: SearchSort.Relevance),
            MinimumUnitPrice: 1m,
            MaximumUnitPrice: 5_000m),
        new(
            "numero-unidade-pos-filtro",
            new SearchQuery(
                "cafe %500 g -capsula",
                SearchGeoFilter.Southeast,
                newestDate.AddDays(-364),
                newestDate,
                Sort: SearchSort.Relevance),
            MinimumUnitPrice: .01m,
            MaximumUnitPrice: 1_000m,
            PageSize: 25)
    ];

    private static IReadOnlyList<RepresentativeFtsScenario> BuildStructuralHybridScenarios(
        DateOnly newestDate) => BuildRepresentativeFtsScenarios(newestDate)
        .Concat(
        [
            new(
                "muitas-exclusoes-cafe",
                new SearchQuery(
                    "cafe -capsula -maquina -soluvel -bebida -po -grao",
                    GeoScope.All,
                    Sort: SearchSort.Relevance)),
            new(
                "muitas-exclusoes-arame",
                new SearchQuery(
                    "arame -galvanizado -farpado -cobre -plastificado -aco",
                    GeoScope.All,
                    Sort: SearchSort.Relevance)),
            new(
                "and-com-muitas-exclusoes",
                new SearchQuery(
                    "arame galvanizado -farpado -plastificado -cobre -soldado",
                    GeoScope.All,
                    Sort: SearchSort.Relevance)),
            new(
                "frase-com-muitas-exclusoes",
                new SearchQuery(
                    "\"papel a4\" -reciclado -adesivo -fotografico -bobina",
                    GeoScope.All,
                    Sort: SearchSort.Relevance)),
            new(
                "and-com-filtros",
                new SearchQuery(
                    "arame galvanizado",
                    SearchGeoFilter.State("SP"),
                    newestDate.AddDays(-364),
                    newestDate,
                    Sort: SearchSort.Relevance),
                MinimumUnitPrice: 1m,
                MaximumUnitPrice: 5_000m),
            new(
                "frase-com-filtros",
                new SearchQuery(
                    "\"papel a4\"",
                    SearchGeoFilter.State("SP"),
                    newestDate.AddDays(-364),
                    newestDate,
                    Sort: SearchSort.Relevance),
                MinimumUnitPrice: 1m,
                MaximumUnitPrice: 5_000m),
            new(
                "termo-simples-com-filtros",
                new SearchQuery(
                    "arame",
                    SearchGeoFilter.State("SP"),
                    newestDate.AddDays(-364),
                    newestDate,
                    Sort: SearchSort.Relevance),
                MinimumUnitPrice: 1m,
                MaximumUnitPrice: 5_000m),
            new(
                "ou-com-filtros",
                new SearchQuery(
                    "cafe OU cha -capsula -maquina -soluvel",
                    SearchGeoFilter.State("SP"),
                    newestDate.AddDays(-364),
                    newestDate,
                    Sort: SearchSort.Relevance),
                MinimumUnitPrice: 1m,
                MaximumUnitPrice: 5_000m)
        ])
        .ToArray();

    private static IReadOnlyList<RepresentativeFtsScenario> BuildBoundedProbeScenarios(
        DateOnly newestDate) => BuildStructuralHybridScenarios(newestDate)
        .Concat(
        [
            new(
                "raro-desfibrilador",
                new SearchQuery("desfibrilador externo automatico", GeoScope.All, Sort: SearchSort.Relevance)),
            new(
                "raro-espectrofotometro",
                new SearchQuery("espectrofotometro", GeoScope.All, Sort: SearchSort.Relevance)),
            new(
                "raro-microscopio",
                new SearchQuery("microscopio binocular", GeoScope.All, Sort: SearchSort.Relevance)),
            new(
                "raro-cimento-odontologico",
                new SearchQuery("\"cimento odontologico\"", GeoScope.All, Sort: SearchSort.Relevance)),
            new(
                "raro-autoclave",
                new SearchQuery("autoclave horizontal", GeoScope.All, Sort: SearchSort.Relevance)),
            new(
                "raro-retroescavadeira",
                new SearchQuery(
                    "retroescavadeira -locacao -servico",
                    GeoScope.All,
                    Sort: SearchSort.Relevance))
        ])
        .ToArray();

    private static long SelectBenchmarkCandidateLimit(
        RepresentativeFtsScenario scenario,
        SearchExpression expression)
    {
        var hasDescriptionPostFilter = expression.AcceptedUnits.Count > 0 ||
            expression.PositiveGroups.Any(group => (group.ApproximateNumbers?.Count ?? 0) > 0);
        if (scenario.PageSize > 50 || hasDescriptionPostFilter)
        {
            return 5_000;
        }
        return HasDetailFilters(scenario) ? 1_000 : 250;
    }

    private static string SelectStructuralHybridEngine(
        RepresentativeFtsScenario scenario,
        SearchExpression expression,
        bool aggressive)
    {
        var hasDescriptionPostFilter = expression.AcceptedUnits.Count > 0 ||
            expression.PositiveGroups.Any(group => (group.ApproximateNumbers?.Count ?? 0) > 0);
        if (expression.PositiveGroups.Count != 1 || hasDescriptionPostFilter || scenario.PageSize > 50)
        {
            return "rank";
        }

        var group = expression.PositiveGroups[0];
        var hasConjunctionOrPhrase = group.Terms.Count > 1 ||
            group.Terms.Any(term => term.IsPhrase && term.Words.Count > 1);
        if (HasDetailFilters(scenario) && hasConjunctionOrPhrase)
        {
            return "direct";
        }
        if (aggressive && expression.Exclusions.Count >= 4)
        {
            return "direct";
        }
        return "rank";
    }

    private static bool HasDetailFilters(RepresentativeFtsScenario scenario) =>
        scenario.Query.EffectiveGeoFilter.Kind != SearchGeoFilterKind.All ||
        scenario.Query.StartDate is not null ||
        scenario.Query.EndDate is not null ||
        scenario.MinimumUnitPrice is not null ||
        scenario.MaximumUnitPrice is not null;

    private static async Task<DateOnly> ReadNewestPublicationDateAsync(SqliteConnectionFactory connections)
    {
        await using var connection = await connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(substr(publication_date, 1, 10)) FROM contracts;";
        var value = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateOnly.FromDateTime(DateTime.Today);
    }

    private static async Task<(int Count, bool Capped)> ReadFtsCardinalityAsync(
        SqliteConnectionFactory connections,
        string itemMatch,
        int ceiling)
    {
        await using var connection = await connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
              FROM (
                  SELECT rowid
                    FROM items_fts
                   WHERE items_fts MATCH $itemMatch
                   LIMIT $limit
              );
            """;
        command.Parameters.AddWithValue("$itemMatch", itemMatch);
        command.Parameters.AddWithValue("$limit", ceiling + 1L);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        return (Math.Min(count, ceiling), count > ceiling);
    }

    private static int ReadPositiveEnvironmentInteger(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value
            : fallback;

    private static IReadOnlyList<long> ReadCandidateLimits()
    {
        var configured = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_CANDIDATE_LIMITS");
        var values = string.IsNullOrWhiteSpace(configured)
            ? [250L, 500L, 1_000L, 2_000L, 5_000L, 10_000L]
            : configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => long.Parse(value, CultureInfo.InvariantCulture))
                .ToArray();
        Assert.NotEmpty(values);
        Assert.All(values, value => Assert.InRange(value, 1, 2_000_000_000));
        return values.Distinct().Order().ToArray();
    }

    private static IReadOnlyList<int> ReadProbeLimits()
    {
        var configured = Environment.GetEnvironmentVariable("PNCPKING_PERFORMANCE_PROBE_LIMITS");
        var values = string.IsNullOrWhiteSpace(configured)
            ? [500, 1_000, 2_000, 5_000]
            : configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                .ToArray();
        Assert.NotEmpty(values);
        Assert.All(values, value => Assert.InRange(value, 1, 100_000));
        return values.Distinct().Order().ToArray();
    }

    private static IEnumerable<long> OrderCandidateLimits(IReadOnlyList<long> limits, int round) =>
        (round % 3) switch
        {
            0 => limits,
            1 => limits.Reverse(),
            _ => limits.Skip(limits.Count / 2).Concat(limits.Take(limits.Count / 2))
        };

    private static IEnumerable<int> OrderProbeLimits(IReadOnlyList<int> limits, int round) =>
        (round % 3) switch
        {
            0 => limits,
            1 => limits.Reverse(),
            _ => limits.Skip(limits.Count / 2).Concat(limits.Take(limits.Count / 2))
        };

    private static async Task<(int Rows, bool Exhausted, TimeSpan Duration)> ReadBoundedFtsProbeAsync(
        SqliteConnectionFactory connections,
        string itemMatch,
        int limit,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rowid
              FROM items_fts
             WHERE items_fts MATCH $itemMatch
             LIMIT $window;
            """;
        command.Parameters.AddWithValue("$itemMatch", itemMatch);
        command.Parameters.AddWithValue("$window", limit + 1L);
        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows++;
        }
        stopwatch.Stop();
        return (rows, rows <= limit, stopwatch.Elapsed);
    }

    private static async Task WriteRepresentativeReportAsync(
        string path,
        RepresentativeBenchmarkReport report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporaryPath = path + ".partial";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task WriteCandidateLimitReportAsync(
        string path,
        CandidateLimitBenchmarkReport report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporaryPath = path + ".partial";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task WriteStructuralHybridReportAsync(
        string path,
        StructuralHybridBenchmarkReport report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporaryPath = path + ".partial";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task WriteBoundedProbeReportAsync(
        string path,
        BoundedProbeBenchmarkReport report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporaryPath = path + ".partial";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task<(TimeSpan Duration, IReadOnlyList<FtsBenchmarkKey> Rows)> MeasureAsync(
        Func<Task<IReadOnlyList<FtsBenchmarkKey>>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = await action();
        stopwatch.Stop();
        return (stopwatch.Elapsed, rows);
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

    private sealed record FtsBenchmarkKey(string ContractId, long ItemNumber, long ResultSequence);

    private sealed record FtsBenchmarkResult(
        IReadOnlyList<double> LegacyMilliseconds,
        IReadOnlyList<double> OptimizedMilliseconds);

    private sealed record RepresentativeFtsScenario(
        string Name,
        SearchQuery Query,
        decimal? MinimumUnitPrice = null,
        decimal? MaximumUnitPrice = null,
        int PageSize = 50);

    private sealed record RepresentativeBenchmarkReport(
        DateTimeOffset StartedAt,
        long DatabaseBytes,
        int Rounds,
        int TimeoutMinutes,
        int CardinalityCeiling,
        List<RepresentativeScenarioResult> Scenarios);

    private sealed record RepresentativeScenarioResult(
        string Name,
        string Query,
        int FtsCardinality,
        bool CardinalityCapped,
        List<RepresentativeMeasurement> Measurements,
        string? InitialSkipReason)
    {
        public string? SkipReason { get; set; } = InitialSkipReason;
    }

    private sealed record RepresentativeMeasurement(
        int Round,
        string Engine,
        double ElapsedMilliseconds,
        int Rows,
        bool TimedOut);

    private sealed record CandidateLimitBenchmarkReport(
        DateTimeOffset StartedAt,
        long DatabaseBytes,
        int Rounds,
        int TimeoutMinutes,
        IReadOnlyList<long> CandidateLimits,
        List<CandidateLimitScenarioResult> Scenarios);

    private sealed record CandidateLimitScenarioResult(
        string Name,
        string Query,
        int PageSize,
        int FtsCardinality,
        bool CardinalityCapped,
        List<CandidateLimitMeasurement> Measurements,
        string? InitialSkipReason)
    {
        public string? SkipReason { get; set; } = InitialSkipReason;
    }

    private sealed record CandidateLimitMeasurement(
        int Round,
        long CandidateLimit,
        double ElapsedMilliseconds,
        int Rows,
        bool TimedOut);

    private sealed record StructuralHybridBenchmarkReport(
        DateTimeOffset StartedAt,
        long DatabaseBytes,
        int Rounds,
        int TimeoutMinutes,
        List<StructuralHybridScenarioResult> Scenarios);

    private sealed record StructuralHybridScenarioResult(
        string Name,
        string Query,
        int FtsCardinality,
        bool CardinalityCapped,
        long RankCandidateLimit,
        string ConservativeEngine,
        string AggressiveEngine,
        List<StructuralHybridMeasurement> Measurements,
        string? InitialSkipReason)
    {
        public string? SkipReason { get; set; } = InitialSkipReason;
    }

    private sealed record StructuralHybridMeasurement(
        int Round,
        string Engine,
        double ElapsedMilliseconds,
        int Rows,
        bool TimedOut);

    private sealed record BoundedProbeBenchmarkReport(
        DateTimeOffset StartedAt,
        long DatabaseBytes,
        int Rounds,
        int TimeoutMinutes,
        IReadOnlyList<int> ProbeLimits,
        List<BoundedProbeScenarioResult> Scenarios);

    private sealed record BoundedProbeScenarioResult(
        string Name,
        string Query,
        int FtsCardinality,
        bool CardinalityCapped,
        long RankCandidateLimit,
        List<BoundedProbeMeasurement> Measurements,
        string? InitialSkipReason)
    {
        public string? SkipReason { get; set; } = InitialSkipReason;
    }

    private sealed record BoundedProbeMeasurement(
        int Round,
        string Engine,
        int? ProbeLimit,
        double ProbeMilliseconds,
        double QueryMilliseconds,
        double TotalMilliseconds,
        int ProbeRows,
        bool ProbeExhausted,
        int Rows);
}
