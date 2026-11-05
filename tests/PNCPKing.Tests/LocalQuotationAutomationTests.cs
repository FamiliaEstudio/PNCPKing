using System.Reflection;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class LocalQuotationAutomationTests
{
    [Fact]
    public async Task ExcelAutomation_CollectsEveryCriterionDeduplicatesAndPersistsProvenanceAndBaskets()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(75);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer());
        var project = await service.CreateProjectAsync("Alternativas");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var run = await service.CreateAutomationRunAsync(project.Id, "unused.xlsx", "Teste",
            SearchGeoFilter.All, today.AddDays(-30), today,
            [new(1, "café -solúvel", "Café", 10, "pacote", null, null, 2,
                IntermediateSearchText: "açúcar", BroadSearchText: "café + açúcar", PromptOrigin: SearchPromptOrigin.Excel)],
            AdequacyWeights.Default);
        var prices = DispatchProxy.Create<IPriceCacheRepository, CountingPrices>();
        var proxy = (CountingPrices)prices;
        proxy.Inner = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var progress = new List<LocalQuotationAutomationProgress>();
        await service.CollectLocalAutomationAsync(run, prices, new InlineProgress(progress.Add));
        var line = Assert.Single(await repository.GetLinesAsync(project.Id));
        var references = await repository.GetReferencesAsync(line.Id);
        Assert.Equal(150, references.Count);
        Assert.Equal(150, references.Select(reference => reference.Id).Distinct().Count());
        Assert.All(references.Where(reference => reference.ItemDescription == "Café torrado"),
            reference => Assert.Equal(PromptMatchLevel.Restrictive, reference.MatchedPromptLevel));
        Assert.Contains(references, reference => reference.MatchedPromptLevel == PromptMatchLevel.Intermediate);
        Assert.Equal(["café -solúvel", "açúcar", "café"], proxy.Queries.Distinct());
        Assert.Equal(1, proxy.MaximumConcurrentReads);
        Assert.Equal(QuotationAutomationItemState.Completed, line.AutomationState);
        Assert.True(line.SelectionConfirmed);
        Assert.Equal(100d, progress[^1].Percentage);
        var analysis = (await service.GetAnalysisAsync(project.Id, line.Id))!;
        Assert.NotNull(analysis.SelectedBasket);
        Assert.All(analysis.SelectedBasket.References,
            reference => Assert.Equal(PromptMatchLevel.Restrictive, reference.MatchedPromptLevel));
        var calls = proxy.Queries.Count;
        await service.CollectLocalAutomationAsync(run, prices);
        Assert.Equal(calls, proxy.Queries.Count); // completed items are not searched again
    }

    [Fact]
    public async Task ExcelAutomation_CancelPreservesStreamedRowsAndResumeCompletesWithoutDuplicates()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(75);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer());
        var project = await service.CreateProjectAsync("Retomada");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var run = await service.CreateAutomationRunAsync(project.Id, "unused.xlsx", "Teste",
            SearchGeoFilter.All, today.AddDays(-30), today,
            [new(1, "café + açúcar", "Café", 10, "pacote", null, null, 2, PromptOrigin: SearchPromptOrigin.Excel)],
            AdequacyWeights.Default);
        var prices = DispatchProxy.Create<IPriceCacheRepository, CountingPrices>();
        var proxy = (CountingPrices)prices;
        proxy.Inner = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        proxy.AfterRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CollectLocalAutomationAsync(run, prices, cancellationToken: cancellation.Token));
        var line = Assert.Single(await repository.GetLinesAsync(project.Id));
        Assert.Equal(QuotationAutomationItemState.Pending, line.AutomationState);
        Assert.Equal(50, (await repository.GetReferencesAsync(line.Id)).Count);
        proxy.AfterRead = null;
        await service.CollectLocalAutomationAsync(run, prices);
        Assert.Equal(150, (await repository.GetReferencesAsync(line.Id)).Count);
        Assert.Equal(QuotationAutomationItemState.Completed,
            Assert.Single(await repository.GetLinesAsync(project.Id)).AutomationState);
    }

    [Fact]
    public async Task ExcelAutomation_Processes423ItemsSequentiallyAndReportsEachCompletion()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(5);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer());
        var project = await service.CreateProjectAsync("423 itens");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var items = Enumerable.Range(1, 423).Select(index => new QuotationImportItem(index,
            "café + açúcar", "Café", 10, "pacote", null, null, 1,
            PromptOrigin: SearchPromptOrigin.Excel)).ToArray();
        var run = await service.CreateAutomationRunAsync(project.Id, "unused.xlsx", "Teste",
            SearchGeoFilter.All, today.AddDays(-30), today, items, AdequacyWeights.Default);
        var prices = DispatchProxy.Create<IPriceCacheRepository, CountingPrices>();
        var proxy = (CountingPrices)prices;
        proxy.Inner = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var completed = 0;
        await service.CollectLocalAutomationAsync(run, prices, new InlineProgress(value =>
        {
            if (value.UpdatedAnalysis is not { } analysis) return;
            completed++;
            Assert.Equal(423, value.TotalItems);
            Assert.Equal(10, analysis.References.Count);
            Assert.Equal(completed * 100d / 423, value.Percentage);
        }));
        Assert.Equal(423, completed);
        Assert.Equal(846, proxy.Queries.Count);
        Assert.Equal(1, proxy.MaximumConcurrentReads);
        Assert.All(await repository.GetLinesAsync(project.Id),
            line => Assert.Equal(QuotationAutomationItemState.Completed, line.AutomationState));
    }

    public class CountingPrices : DispatchProxy
    {
        public IPriceCacheRepository Inner = null!;
        public List<string> Queries = [];
        public Action? AfterRead;
        public int MaximumConcurrentReads;
        private int _concurrent;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IPriceCacheRepository.SearchLocalAfterAsync), method!.Name);
            Assert.Equal(50, args![5]);
            Assert.Equal(PriceCacheLocalReadOrder.Discovery, args[6]);
            Queries.Add(((SearchQuery)args[0]!).Text);
            return ReadAsync(method, args);
        }
        private async Task<PriceCacheLocalPage> ReadAsync(MethodInfo method, object?[] args)
        {
            MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, ++_concurrent);
            try
            {
                var result = await ((Task<PriceCacheLocalPage>)method.Invoke(Inner, args)!).ConfigureAwait(false);
                AfterRead?.Invoke();
                return result;
            }
            finally { _concurrent--; }
        }
    }
    private sealed class InlineProgress(Action<LocalQuotationAutomationProgress> report) : IProgress<LocalQuotationAutomationProgress>
    {
        public void Report(LocalQuotationAutomationProgress value) => report(value);
    }
}
