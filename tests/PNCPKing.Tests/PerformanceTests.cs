using System.Diagnostics;
using PNCPKing.Core.Models;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

public sealed class PerformanceTests(ITestOutputHelper output)
{
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
        const int totalContracts = 25_000;
        var consolidation = Stopwatch.StartNew();
        for (var offset = 0; offset < totalContracts; offset += 500)
        {
            var batch = Enumerable.Range(offset, Math.Min(500, totalContracts - offset))
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
}
