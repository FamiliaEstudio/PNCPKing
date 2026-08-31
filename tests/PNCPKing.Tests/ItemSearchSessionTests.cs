using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Api;
using PNCPKing.Infrastructure.Services;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Data.Sqlite;

namespace PNCPKing.Tests;

public sealed class ItemSearchSessionTests
{
    [Theory]
    [InlineData(1, 50)]
    [InlineData(3, 150)]
    [InlineData(100, 5_000)]
    [InlineData(200, 10_000)]
    public void RequestedContracts_FollowsTheUserSelectedBatchCount(
        int batchCount,
        int expectedContracts)
    {
        var request = new PriceBatchRequest(
            batchCount,
            true,
            PriceBatchBudgetMode.UnresolvedContracts);

        Assert.Equal(expectedContracts, request.RequestedContracts);
    }

    [Fact]
    public void ConsecutiveSelections_KeepTheirAdditiveCoverage()
    {
        var first = new PriceBatchRequest(30, true, PriceBatchBudgetMode.UnresolvedContracts);
        var second = new PriceBatchRequest(60, true, PriceBatchBudgetMode.UnresolvedContracts);

        Assert.Equal(1_500, first.RequestedContracts);
        Assert.Equal(3_000, second.RequestedContracts);
        Assert.Equal(4_500, first.RequestedContracts + second.RequestedContracts);
    }

    [Fact]
    public async Task GeneralSearch_AcceptsTwoHundredBatchesAndRejectsTwoHundredAndOne()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var service = new ItemSearchSessionService(
            new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>()),
            database.Repository,
            Path.Combine(database.Directory, "batch-limit.db"));
        await service.StartAsync("cafe", []);

        var accepted = await service.RunContinuousAsync(new PriceBatchRequest(200, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.RunContinuousAsync(new PriceBatchRequest(201, true)));

        Assert.True(accepted.CandidateSetExhausted);
        Assert.Equal(10_000, accepted.RequestedContracts);
    }

    [Fact]
    public async Task ExhaustiveRequest_TraversesTheCandidateSetInOneOperation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(520, "exhaustive");
        await database.Repository.UpsertContractsAsync(contracts);
        var client = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>(),
            itemFactory: contract => [Item(contract.PncpId, 1, "Café em grãos", false)]);
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "exhaustive.db"));
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var result = await service.RunContinuousAsync(new PriceBatchRequest(
            1,
            LargeRequestConfirmed: true,
            BudgetMode: PriceBatchBudgetMode.CandidateContracts,
            ExactContractCount: 50)
        {
            ExhaustCandidateSet = true
        });

        Assert.True(result.CandidateSetExhausted);
        Assert.Equal(520, result.ContractsScanned);
        Assert.Equal(520, result.ProcessedContracts);
        Assert.Equal(520, client.ItemListCalls);
    }

    [Fact]
    public async Task ExhaustiveRequest_RequiresExplicitConfirmation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("exhaustive-confirm", "Café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await using var service = new ItemSearchSessionService(
            new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>()),
            database.Repository,
            Path.Combine(database.Directory, "exhaustive-confirm.db"));
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunContinuousAsync(new PriceBatchRequest(1)
            {
                ExhaustCandidateSet = true
            }));

        Assert.Contains("confirmação", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Session_ReturnsOnlyMatchingItemsAndHydratesFiftyAtATime()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("contract", "Registro de preços de café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = Enumerable.Range(1, 150)
                .Select(number => Item(
                    contract.PncpId,
                    number,
                    number <= 60 ? $"Café torrado tipo {number}" : $"Açúcar tipo {number}",
                    true))
                .ToArray()
        });
        var temporaryPath = Path.Combine(database.Directory, "search-session.db");
        await using var service = new ItemSearchSessionService(client, database.Repository, temporaryPath);
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var first = await service.LoadPageAsync(1);
        Assert.Equal(50, first.Rows.Count);
        Assert.All(first.Rows, row => Assert.Contains("Café", row.Item.Description, StringComparison.Ordinal));
        Assert.Equal(50, client.ResultCalls);
        Assert.All(first.Rows, row => Assert.True(row.IsTemporary));

        var second = await service.LoadPageAsync(2);
        Assert.Equal(10, second.Rows.Count);
        Assert.Equal(60, client.ResultCalls);
        Assert.False(second.HasMoreCandidates);

        var permanent = await database.Repository.GetCachedItemResultsAsync(contract.PncpId, 1);
        Assert.NotNull(permanent);
        Assert.True(permanent.IsCurrent);
        Assert.Single(permanent.Results);

        // A new search drops the disposable paging database, but the homologated
        // results are reused from the permanent cache.
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));
        await service.LoadPageAsync(1);
        Assert.Equal(60, client.ResultCalls);
        Assert.Equal(1, client.ItemListCalls);
    }

    [Fact]
    public async Task IndexedItem_IsFoundInsideGenericContractAndPermanentPriceIsReused()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract(
            "generic-food",
            "Aquisição de gêneros não perecíveis",
            "SP",
            1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [
            Item(contract.PncpId, 1, "Café torrado em grãos", true),
            Item(contract.PncpId, 2, "Café sem resultado homologado", false),
            Item(contract.PncpId, 3, "Açúcar refinado", true)
        ], false);
        var client = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>());
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "generic-item.db"),
            persistentSession: true);

        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));
        var first = await service.LoadPageAsync(1);

        Assert.Single(first.Rows);
        Assert.Equal("generic-food", first.Rows[0].Contract.PncpId);
        Assert.Equal(1, first.Rows[0].Item.ItemNumber);
        Assert.Equal(0, client.ItemListCalls);
        Assert.Equal(1, client.ResultCalls);

        await service.StartAsync(new SearchQuery("cafe", GeoScope.All), restart: true);
        var repeated = await service.LoadPageAsync(1);

        Assert.Single(repeated.Rows);
        Assert.False(repeated.Rows[0].IsTemporary);
        Assert.Equal(0, client.ItemListCalls);
        Assert.Equal(1, client.ResultCalls);
    }

    [Fact]
    public async Task EmptyHomologationResponse_IsPersistedAndNotRequestedAgain()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("empty-price", "Não perecíveis", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(
            contract.PncpId,
            [Item(contract.PncpId, 1, "Café torrado", true)],
            false);
        var client = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>(),
            resultFactory: (_, _) => []);
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "empty-price.db"));

        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));
        Assert.Empty((await service.LoadPageAsync(1)).Rows);
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));
        Assert.Empty((await service.LoadPageAsync(1)).Rows);

        var cached = await database.Repository.GetCachedItemResultsAsync(contract.PncpId, 1);
        Assert.NotNull(cached);
        Assert.True(cached.IsCurrent);
        Assert.Empty(cached.Results);
        Assert.Equal(1, client.ResultCalls);
    }

    [Fact]
    public async Task TemporaryResultPaging_ReturnsAtMostFiftyRowsWithAStableCursor()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = CandidateContracts(1, "result-page")[0];
        await database.Repository.UpsertContractsAsync([contract]);
        var client = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>
            {
                [contract.PncpId] = [Item(contract.PncpId, 1, "Café em grãos", true)]
            },
            resultFactory: (_, itemNumber) => Enumerable.Range(1, 75)
                .Select(sequence => Result(contract.PncpId, itemNumber, sequence, sequence, 1))
                .ToArray());
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "result-page.db"),
            persistentSession: true);
        await service.StartAsync("cafe", [contract]);
        var completed = await service.RunContinuousAsync(new PriceBatchRequest(
            1,
            true,
            PriceBatchBudgetMode.CandidateContracts,
            1));

        var first = await service.LoadDiscoveredResultPageAsync(null, pageSize: 500);
        var second = await service.LoadDiscoveredResultPageAsync(first.NextCursor, pageSize: 500);

        Assert.Equal(75, completed.AvailableSessionRows);
        Assert.Equal(50, first.Rows.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(25, second.Rows.Count);
        Assert.False(second.HasMore);
        Assert.Equal(75, first.Rows.Concat(second.Rows)
            .Select(row => row.Result!.ResultSequence)
            .Distinct()
            .Count());
        Assert.Equal(1, client.ItemListCalls);
        Assert.Equal(1, client.ResultCalls);
    }

    [Fact]
    public async Task InclusiveUnitPriceRange_ContainsLimitsAndExcludesCancelledResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("contract", "Café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = [Item(contract.PncpId, 1, "Café em grãos", true)]
        }, (_, _) => [
            Result(contract.PncpId, 1, 1, 10m, 1),
            Result(contract.PncpId, 1, 2, 20m, 1),
            Result(contract.PncpId, 1, 3, 15m, 2)
        ]);
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "prices.db"));
        await service.StartAsync("cafe", [contract]);
        await service.LoadPageAsync(1);

        var all = await service.GetDiscoveredRowsAsync();
        var ranged = await service.GetDiscoveredRowsAsync(10m, 20m);

        Assert.Equal(3, all.Count);
        Assert.Contains(all, row => row.PriceState == ItemSearchPriceState.Cancelled);
        Assert.Equal(2, ranged.Count);
        Assert.Equal([10m, 20m], ranged.Select(row => row.HomologatedUnitValue!.Value).Order().ToArray());
        Assert.All(ranged, row => Assert.Equal(ItemSearchPriceState.Homologated, row.PriceState));
    }

    [Fact]
    public async Task ManualBatch_ConsultsNextFiftyAndLargeRequestRequiresConfirmation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(120, "batch");
        await database.Repository.UpsertContractsAsync(contracts);
        var client = new SessionPncpClient(contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]));
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "batches.db"));
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var firstBatch = await service.FireBatchesAsync(new PriceBatchRequest(1));
        Assert.Equal(50, firstBatch.ProcessedContracts);
        Assert.Equal(50, firstBatch.CompletedItemCalls);
        Assert.Equal(50, client.ItemListCalls);
        Assert.Equal(50, client.ResultCalls);
        Assert.False(firstBatch.CandidateSetExhausted);

        var secondBatch = await service.FireBatchesAsync(new PriceBatchRequest(1));
        Assert.Equal(50, secondBatch.ProcessedContracts);
        Assert.Equal(100, client.ItemListCalls);
        Assert.Equal(100, client.ResultCalls);
        Assert.False(secondBatch.CandidateSetExhausted);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.FireBatchesAsync(new PriceBatchRequest(11)));

        service.Stop();
        var retained = await service.GetDiscoveredRowsAsync();
        Assert.Equal(100, retained.Count);
        Assert.All(retained, row => Assert.Equal(ItemSearchPriceState.Homologated, row.PriceState));
    }

    [Fact]
    public async Task ManualBatch_RetriesPriceFailuresAfterUnseenCandidatesAreExhausted()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(2, "failure");
        await database.Repository.UpsertContractsAsync(contracts);
        var attempts = 0;
        var client = new SessionPncpClient(contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]), (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new HttpRequestException("falha transitória");
        });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "retry.db"));
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var first = await service.FireBatchesAsync(new PriceBatchRequest(1));
        var second = await service.FireBatchesAsync(new PriceBatchRequest(1));

        Assert.Equal(2, first.ProcessedContracts);
        Assert.Equal(2, first.FailedItemCalls);
        Assert.True(first.CandidateSetExhausted);
        Assert.Equal(0, second.ProcessedContracts);
        Assert.Equal(2, second.FailedItemCalls);
        Assert.Equal(4, attempts);
        Assert.Equal(4, client.ResultCalls);
    }

    [Fact]
    public async Task ManualBatch_DoesNotRepeatPreviouslyProcessedSuccessfulContracts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(60, "continuation");
        await database.Repository.UpsertContractsAsync(contracts);
        var client = new SessionPncpClient(contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]));
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "single-retry.db"));
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var first = await service.FireBatchesAsync(new PriceBatchRequest(1));
        var second = await service.FireBatchesAsync(new PriceBatchRequest(1));
        var exhausted = await service.FireBatchesAsync(new PriceBatchRequest(1));

        Assert.Equal(50, first.ProcessedContracts);
        Assert.Equal(10, second.ProcessedContracts);
        Assert.Equal(0, exhausted.ProcessedContracts);
        Assert.True(second.CandidateSetExhausted);
        Assert.True(exhausted.CandidateSetExhausted);
        Assert.Equal(60, client.ResultCalls);
    }

    [Fact]
    public async Task ManualBatch_ConsultsAllMatchingItemsInsideEachContract()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("contract", "Café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var attempts = new ConcurrentDictionary<long, int>();
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = Enumerable.Range(1, 60)
                .Select(number => Item(contract.PncpId, number, $"Café {number}", true))
                .ToArray()
        }, (_, itemNumber) =>
        {
            attempts.AddOrUpdate(itemNumber, 1, static (_, current) => current + 1);
            if (itemNumber == 1)
            {
                throw new HttpRequestException("falha persistente");
            }

            return [Result(contract.PncpId, itemNumber, 1, itemNumber, 1)];
        });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "no-starvation.db"));
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var result = await service.FireBatchesAsync(new PriceBatchRequest(1));

        Assert.Equal(1, result.ProcessedContracts);
        Assert.Equal(60, result.CompletedItemCalls);
        Assert.Equal(1, result.FailedItemCalls);
        Assert.Equal(1, attempts[1]);
        Assert.All(Enumerable.Range(2, 59), itemNumber => Assert.Equal(1, attempts[itemNumber]));
    }

    [Fact]
    public async Task BatchProgress_IsCumulativeAcrossFiftyContractChunks()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(120, "progress");
        await database.Repository.UpsertContractsAsync(contracts);
        var client = new SessionPncpClient(contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]));
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "progress.db"));
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));
        var reports = new List<PriceBatchProgress>();
        var progress = new InlineProgress<PriceBatchProgress>(reports.Add);

        var completed = await service.FireBatchesAsync(new PriceBatchRequest(2), progress);

        Assert.Equal(100, completed.ProcessedContracts);
        Assert.Equal(100, completed.CompletedItemCalls);
        Assert.Equal(100, reports[^1].ProcessedContracts);
        Assert.True(
            reports.Select(report => report.ProcessedContracts)
                .SequenceEqual(reports.Select(report => report.ProcessedContracts).Order()),
            "O progresso não pode regredir entre blocos de 50 contratações.");
    }

    [Fact]
    public async Task EmptySearch_DoesNotDownloadItemListsOrPrices()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("contract", "Café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = [Item(contract.PncpId, 1, "Café", true)]
        });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "empty.db"));
        await service.StartAsync(string.Empty, [contract]);

        var page = await service.LoadPageAsync(1);
        var batch = await service.FireBatchesAsync(new PriceBatchRequest(1));

        Assert.Empty(page.Rows);
        Assert.False(page.HasMoreCandidates);
        Assert.True(batch.CandidateSetExhausted);
        Assert.Equal(0, client.ItemListCalls);
        Assert.Equal(0, client.ResultCalls);
    }

    [Fact]
    public async Task ItemsWithoutResult_AreHiddenAndDoNotConsumePriceBatchCalls()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("contract", "Café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = Enumerable.Range(1, 60)
                .Select(number => Item(
                    contract.PncpId,
                    number,
                    $"Café {number}",
                    hasResult: number > 50))
                .ToArray()
        });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "without-result.db"));
        await service.StartAsync("cafe", [contract]);

        var batch = await service.FireBatchesAsync(new PriceBatchRequest(1));
        var rows = await service.GetDiscoveredRowsAsync();

        Assert.Equal(10, rows.Count);
        Assert.All(rows, row => Assert.Equal(ItemSearchPriceState.Homologated, row.PriceState));
        Assert.Equal(10, batch.CompletedItemCalls);
        Assert.Equal(10, client.ResultCalls);
        Assert.True(batch.CandidateSetExhausted);
    }

    [Fact]
    public async Task SearchRows_HideMissingZeroEmptyAndFailedPricesButKeepLowPositiveAndCancelledValues()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("contract", "Café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = Enumerable.Range(1, 8)
                .Select(number => Item(
                    contract.PncpId,
                    number,
                    $"Café {number}",
                    hasResult: number != 1))
                .ToArray()
        }, (_, itemNumber) => itemNumber switch
        {
            2 => [],
            3 => [Result(contract.PncpId, itemNumber, 1, 1m, 1) with
            {
                HomologatedUnitValueScaled = null
            }],
            4 => [Result(contract.PncpId, itemNumber, 1, 0m, 1)],
            5 => [Result(contract.PncpId, itemNumber, 1, 0.01m, 1)],
            6 => [Result(contract.PncpId, itemNumber, 1, 0.10m, 1)],
            7 => [Result(contract.PncpId, itemNumber, 1, 0.20m, 2)],
            8 => throw new HttpRequestException("falha simulada"),
            _ => throw new InvalidOperationException()
        });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "useful-prices.db"));
        await service.StartAsync("cafe", [contract]);
        var reportedRows = new List<ItemSearchRow>();

        var batch = await service.RunContinuousAsync(
            new PriceBatchRequest(1),
            rowProgress: new InlineProgress<IReadOnlyList<ItemSearchRow>>(
                rows => reportedRows.AddRange(rows)));
        var rows = await service.GetDiscoveredRowsAsync();

        Assert.Equal(8, batch.MatchedItems);
        Assert.Equal(2, batch.RevealedPrices);
        Assert.Equal(1, batch.TotalFailedCalls);
        Assert.Equal(7, client.ResultCalls);
        Assert.Equal([0.01m, 0.10m, 0.20m], rows.Select(row => row.HomologatedUnitValue!.Value).ToArray());
        Assert.Equal(
            rows.Select(row => row.HomologatedUnitValue).Order().ToArray(),
            reportedRows.Select(row => row.HomologatedUnitValue).Distinct().Order().ToArray());
        Assert.All(rows, row => Assert.True(row.HomologatedUnitValue > 0));
        Assert.All(reportedRows, row => Assert.True(row.HomologatedUnitValue > 0));
        Assert.Single(rows, row => row.PriceState == ItemSearchPriceState.Cancelled);
    }

    [Fact]
    public async Task TemporaryDatabase_IsDeletedAtStartupAndDisposal()
    {
        await using var database = await TestDatabase.CreateAsync();
        var temporaryPath = Path.Combine(database.Directory, "abandoned.db");
        await File.WriteAllTextAsync(temporaryPath, "resíduo de encerramento inesperado");
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>());

        var service = new ItemSearchSessionService(client, database.Repository, temporaryPath);
        Assert.False(File.Exists(temporaryPath));
        await service.StartAsync(string.Empty, []);
        Assert.True(File.Exists(temporaryPath));

        await service.DisposeAsync();
        Assert.False(File.Exists(temporaryPath));
        Assert.False(File.Exists(temporaryPath + "-wal"));
        Assert.False(File.Exists(temporaryPath + "-shm"));
    }

    [Fact]
    public async Task PermanentCurrentPrice_IsReusedWithoutAnApiCall()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("contract", "Café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [
            Item(contract.PncpId, 1, "Café", true)
        ], false);
        await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [
            Result(contract.PncpId, 1, 1, 12m, 1)
        ]);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>());
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "reuse.db"));
        await service.StartAsync("cafe", [contract]);

        var page = await service.LoadPageAsync(1);

        Assert.Single(page.Rows);
        Assert.False(page.Rows[0].IsTemporary);
        Assert.Equal(12m, page.Rows[0].HomologatedUnitValue);
        Assert.Equal(0, client.ItemListCalls);
        Assert.Equal(0, client.ResultCalls);
    }

    [Fact]
    public async Task StalePermanentPrice_IsNotShownAsCurrentAndIsRefreshedTemporarily()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("contract", "Café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [
            Item(contract.PncpId, 1, "Café", true)
        ], false);
        await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [
            Result(contract.PncpId, 1, 1, 12m, 1)
        ]);
        var updatedContract = contract with
        {
            GlobalUpdatedAt = contract.GlobalUpdatedAt!.Value.AddHours(1)
        };
        await database.Repository.UpsertContractsAsync([updatedContract]);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = [Item(contract.PncpId, 1, "Café", true)]
        }, (_, _) => [Result(contract.PncpId, 1, 1, 25m, 1)]);
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "stale.db"));
        await service.StartAsync("cafe", [updatedContract]);

        var page = await service.LoadPageAsync(1);

        Assert.Single(page.Rows);
        Assert.True(page.Rows[0].IsTemporary);
        Assert.Equal(25m, page.Rows[0].HomologatedUnitValue);
        Assert.Equal(1, client.ItemListCalls);
        Assert.Equal(1, client.ResultCalls);
    }

    [Fact]
    public async Task RestrictedRevalidation_CallsOnlyKnownStaleMatchingContract()
    {
        await using var database = await TestDatabase.CreateAsync();
        var current = RepositorySearchTests.Contract("current-price", "Café", "SP", 1);
        var stale = RepositorySearchTests.Contract("stale-price", "Café", "SP", 2);
        var neverLoaded = RepositorySearchTests.Contract("never-price", "Café", "SP", 3);
        await database.Repository.UpsertContractsAsync([current, stale, neverLoaded]);
        await database.Repository.UpsertItemsAsync(current.PncpId, [
            Item(current.PncpId, 1, "Coffee Break", true) with
            {
                HydrationStatus = ItemHydrationStatus.Complete
            }
        ], false);
        await database.Repository.ReplaceItemResultsAsync(current.PncpId, 1, [
            Result(current.PncpId, 1, 1, 12m, 1)
        ]);
        await database.Repository.UpsertItemsAsync(stale.PncpId, [
            Item(stale.PncpId, 1, "Coffee Break", true) with
            {
                HydrationStatus = ItemHydrationStatus.Complete
            }
        ], false);
        await database.Repository.ReplaceItemResultsAsync(stale.PncpId, 1, [
            Result(stale.PncpId, 1, 1, 15m, 1)
        ]);
        await database.Repository.UpsertItemsAsync(neverLoaded.PncpId, [
            Item(neverLoaded.PncpId, 1, "Coffee Break", true)
        ], false);
        var updatedStale = stale with
        {
            GlobalUpdatedAt = stale.GlobalUpdatedAt!.Value.AddHours(1)
        };
        await database.Repository.UpsertContractsAsync([updatedStale]);

        var client = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>
            {
                [stale.PncpId] = [Item(stale.PncpId, 1, "Coffee Break premium", true)]
            },
            (_, _) => [Result(stale.PncpId, 1, 2, 25m, 1)]);
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "restricted-revalidation.db"),
            requestScheduler: new PncpRequestScheduler(maximumConcurrency: 16));
        var query = new SearchQuery("Coffee Break", GeoScope.All);

        var result = await service.RevalidateStaleMatchesAsync(
            query,
            SearchText.Parse(query.Text));

        Assert.Equal(1, result.TotalContracts);
        Assert.Equal(1, result.ProcessedContracts);
        Assert.Equal(1, client.ItemListCalls);
        Assert.Equal(1, client.ResultCalls);
        Assert.Equal(12m, (await database.Repository.GetCachedItemResultsAsync(current.PncpId, 1))!
            .Results.Single().HomologatedUnitValue);
        Assert.Equal(25m, (await database.Repository.GetCachedItemResultsAsync(stale.PncpId, 1))!
            .Results.Single().HomologatedUnitValue);
        Assert.Equal(
            ItemHydrationStatus.NotLoaded,
            (await database.Repository.GetItemAsync(neverLoaded.PncpId, 1))!.HydrationStatus);
    }

    [Fact]
    public async Task RestrictedRevalidation_RemovedItemMakesNoResultCall()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("removed-stale", "Café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [
            Item(contract.PncpId, 1, "Coffee Break", true) with
            {
                HydrationStatus = ItemHydrationStatus.Complete
            }
        ], false);
        await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [
            Result(contract.PncpId, 1, 1, 15m, 1)
        ]);
        await database.Repository.UpsertContractsAsync([
            contract with { GlobalUpdatedAt = contract.GlobalUpdatedAt!.Value.AddHours(1) }
        ]);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = []
        });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "removed-revalidation.db"));

        var result = await service.RevalidateStaleMatchesAsync(
            new SearchQuery("Coffee Break", GeoScope.All),
            SearchText.Parse("Coffee Break"));

        Assert.Equal(1, result.ProcessedContracts);
        Assert.Equal(1, client.ItemListCalls);
        Assert.Equal(0, client.ResultCalls);
        Assert.Null(await database.Repository.GetItemAsync(contract.PncpId, 1));
    }

    [Fact]
    public async Task RepositoryCandidateSource_ContinuesByCursorAndCapsFreshListsPerAction()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = Enumerable.Range(1, 200)
            .Select(number => RepositorySearchTests.Contract($"empty-{number}", "Café", "SP", 1) with
            {
                PublicationDate = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero)
            })
            .Append(RepositorySearchTests.Contract("last", "Café", "SP", 1) with
            {
                PublicationDate = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)
            })
            .ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            ["last"] = [Item("last", 1, "Café encontrado", true)]
        });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "paged.db"));
        var session = await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        ItemSearchPage? page = null;
        for (var action = 1; action <= 5; action++)
        {
            var before = client.ItemListCalls;
            page = await service.LoadPageAsync(action);
            Assert.InRange(client.ItemListCalls - before, 0, ItemSearchSessionService.MaximumFreshItemListsPerAction);
            if (page.Rows.Count > 0)
            {
                break;
            }

            Assert.True(page.ItemListBudgetExhausted);
        }

        Assert.Equal(201, session.CandidateContractCount);
        Assert.NotNull(page);
        Assert.Single(page.Rows);
        Assert.Equal("last", page.Rows[0].Contract.PncpId);
        Assert.InRange(client.ItemListCalls, 1, 201);
    }

    [Fact]
    public async Task ContinuousRun_StreamsAllRowsForSelectedDisparosWithoutPaging()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("continuous", "Café", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = Enumerable.Range(1, 120)
                .Select(number => Item(contract.PncpId, number, $"Café {number}", true))
                .ToArray()
        });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "continuous.db"),
            requestScheduler: new PncpRequestScheduler(
                maximumConcurrency: 16,
                initialConcurrency: 8));
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));
        var streamed = new List<ItemSearchRow>();
        var progressValues = new List<int>();

        var result = await service.RunContinuousAsync(
            new PriceBatchRequest(2),
            progress: new InlineProgress<PriceBatchProgress>(value => progressValues.Add(value.CompletedItemCalls)),
            rowProgress: new InlineProgress<IReadOnlyList<ItemSearchRow>>(rows => streamed.AddRange(rows)));

        Assert.Equal(1, result.ProcessedContracts);
        Assert.Equal(120, result.CompletedItemCalls);
        Assert.Equal(120, client.ResultCalls);
        Assert.Equal(
            120,
            streamed
                .Where(row => row.PriceState == ItemSearchPriceState.Homologated)
                .Select(row => (row.Contract.PncpId, row.Item.ItemNumber, row.Result!.ResultSequence))
                .Distinct()
                .Count());
        Assert.True(progressValues.SequenceEqual(progressValues.Order()));
        Assert.True(result.CandidateSetExhausted);
    }

    [Fact]
    public async Task ContinuousRun_LeavesTheFiftyFirstContractForTheNextBatch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = Enumerable.Range(1, 75)
            .Select(number => RepositorySearchTests.Contract($"empty-continuous-{number}", "Café", "SP", 1))
            .Append(RepositorySearchTests.Contract("continuous-last", "Café", "SP", 1))
            .ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            ["continuous-last"] = [Item("continuous-last", 1, "Café encontrado", true)]
        });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "continuous-lists.db"));
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var first = await service.RunContinuousAsync(new PriceBatchRequest(1));
        var second = await service.RunContinuousAsync(new PriceBatchRequest(1));

        Assert.Equal(50, first.ProcessedContracts);
        Assert.False(first.CandidateSetExhausted);
        Assert.Equal(26, second.ProcessedContracts);
        Assert.True(second.CandidateSetExhausted);
        Assert.Equal(76, client.ItemListCalls);
        Assert.Equal(1, client.ResultCalls);
        Assert.Equal(1, first.CompletedItemCalls + second.CompletedItemCalls);
    }

    [Fact]
    public async Task UnresolvedBudget_SkipsFullyCachedContractsWithoutConsumingTheQuota()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(120, "unresolved");
        await database.Repository.UpsertContractsAsync(contracts);
        foreach (var contract in contracts.Take(20))
        {
            await database.Repository.UpsertItemsAsync(contract.PncpId, [
                Item(contract.PncpId, 1, "Café em grãos", true)
            ], false);
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [
                Result(contract.PncpId, 1, 1, 15m, 1)
            ]);
        }

        var client = new SessionPncpClient(contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]));
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "unresolved-budget.db"));
        await service.StartAsync("cafe", contracts);

        var result = await service.RunContinuousAsync(new PriceBatchRequest(
            2,
            true,
            PriceBatchBudgetMode.UnresolvedContracts));

        Assert.Equal(120, result.ContractsScanned);
        Assert.Equal(100, result.ExpandedContracts);
        Assert.Equal(20, result.FullyResolvedContracts);
        Assert.Equal(100, result.ProcessedContracts);
        Assert.Equal(100, client.ItemListCalls);
        Assert.Equal(100, client.ResultCalls);
        Assert.True(result.CandidateSetExhausted);
    }

    [Fact]
    public async Task FullyCachedCandidates_AreDrainedInSlicesOfOneThousandWithoutApiCalls()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(4_650, "cached-slices");
        await database.Repository.UpsertContractsAsync(contracts);
        await SeedResolvedItemListsAsync(database.Repository.DatabasePath, contracts);
        var client = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>());
        var progressUpdates = new List<PriceBatchProgress>();
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "cached-slices.db"),
            requestScheduler: new PncpRequestScheduler(maximumConcurrency: 48));
        await service.StartAsync("cafe", contracts);

        var result = await service.RunContinuousAsync(
            new PriceBatchRequest(200, true, PriceBatchBudgetMode.UnresolvedContracts),
            progress: new InlineProgress<PriceBatchProgress>(progressUpdates.Add));

        Assert.True(result.CandidateSetExhausted);
        Assert.Equal(4_650, result.ContractsScanned);
        Assert.Equal(0, result.ProcessedContracts);
        Assert.Equal(0, result.ExpandedContracts);
        Assert.Equal(4_650, result.FullyResolvedContracts);
        Assert.Equal(0, client.ItemListCalls);
        Assert.Equal(0, client.ResultCalls);
        Assert.Equal(5, result.CompletedCacheSlices);
        Assert.Equal(650, result.CachedContractsProcessedInSlice);
        Assert.Equal(4, progressUpdates.Count(update =>
            update.CachedContractsProcessedInSlice == ItemSearchSessionService.CachedContractsPerSlice));
        Assert.Contains(progressUpdates, update =>
            update.CompletedCacheSlices == 5 &&
            update.CachedContractsProcessedInSlice == 650);
    }

    [Fact]
    public async Task PersistentSession_ResumesAfterDisposalWithoutRepeatingCompletedCandidates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(60, "persistent");
        await database.Repository.UpsertContractsAsync(contracts);
        var items = contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]);
        var path = Path.Combine(database.Directory, "persistent-search.db");
        var firstClient = new SessionPncpClient(items);
        await using (var first = new ItemSearchSessionService(
                         firstClient,
                         database.Repository,
                         path,
                         persistentSession: true))
        {
            await first.StartAsync(new SearchQuery("cafe", GeoScope.All));
            var initial = await first.RunContinuousAsync(new PriceBatchRequest(
                1,
                true,
                PriceBatchBudgetMode.UnresolvedContracts));
            Assert.Equal(50, initial.ExpandedContracts);
            Assert.False(initial.CandidateSetExhausted);
        }

        Assert.True(File.Exists(path));
        var secondClient = new SessionPncpClient(items);
        await using (var second = new ItemSearchSessionService(
                         secondClient,
                         database.Repository,
                         path,
                         persistentSession: true))
        {
            var restored = await second.StartAsync(new SearchQuery("cafe", GeoScope.All));
            var completed = await second.RunContinuousAsync(new PriceBatchRequest(
                1,
                true,
                PriceBatchBudgetMode.UnresolvedContracts));
            var rows = await second.GetDiscoveredRowsAsync();

            Assert.NotEqual(0, restored.RandomPivot);
            Assert.Equal(60, completed.ExpandedContracts);
            Assert.Equal(10, completed.ProcessedContracts);
            Assert.True(completed.CandidateSetExhausted);
            Assert.Equal(60, rows.Count(row => row.PriceState == ItemSearchPriceState.Homologated));
        }

        Assert.Equal(50, firstClient.ItemListCalls);
        Assert.Equal(50, firstClient.ResultCalls);
        Assert.Equal(10, secondClient.ItemListCalls);
        Assert.Equal(10, secondClient.ResultCalls);
    }

    [Fact]
    public async Task PersistentSession_RestoresMetadataBeforeMaterializingResultHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(75, "lazy-restore");
        await database.Repository.UpsertContractsAsync(contracts);
        var items = contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]);
        var path = Path.Combine(database.Directory, "lazy-restore.db");
        await using (var first = new ItemSearchSessionService(
                         new SessionPncpClient(items),
                         database.Repository,
                         path,
                         persistentSession: true,
                         requestScheduler: new PncpRequestScheduler(maximumConcurrency: 48)))
        {
            await first.StartAsync(new SearchQuery("cafe", GeoScope.All));
            await first.RunContinuousAsync(new PriceBatchRequest(
                2,
                true,
                PriceBatchBudgetMode.CandidateContracts,
                75));
        }

        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var corruptLastHistoryRow = connection.CreateCommand();
            corruptLastHistoryRow.CommandText = """
                UPDATE search_hits
                   SET contract_json = '{'
                 WHERE discovered_order = (SELECT MAX(discovered_order) FROM search_hits);
                """;
            Assert.Equal(1, await corruptLastHistoryRow.ExecuteNonQueryAsync());
        }

        await using var resumed = new ItemSearchSessionService(
            new SessionPncpClient(items),
            database.Repository,
            path,
            persistentSession: true);
        var restored = await resumed.StartAsync(new SearchQuery("cafe", GeoScope.All));
        var firstPage = await resumed.LoadDiscoveredResultPageAsync(null);

        Assert.Equal(75, restored.CandidateContractCount);
        Assert.Equal(50, firstPage.Rows.Count);
        Assert.True(firstPage.HasMore);
    }

    [Fact]
    public async Task PersistentSession_RetriesListFailuresOnlyAfterUnseenCandidates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = CandidateContracts(1, "list-failure")[0];
        await database.Repository.UpsertContractsAsync([contract]);
        var path = Path.Combine(database.Directory, "persistent-list-failure.db");
        var failingClient = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>(),
            itemFactory: _ => throw new HttpRequestException("falha definitiva simulada"));

        await using (var first = new ItemSearchSessionService(
                         failingClient,
                         database.Repository,
                         path,
                         persistentSession: true))
        {
            await first.StartAsync(new SearchQuery("cafe", GeoScope.All));
            var failed = await first.RunContinuousAsync(new PriceBatchRequest(
                1,
                true,
                PriceBatchBudgetMode.UnresolvedContracts));

            Assert.True(failed.CandidateSetExhausted);
            Assert.Equal(1, failed.ExpandedContracts);
            Assert.Equal(1, failed.TotalFailedCalls);
        }

        var recoveredClient = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = [Item(contract.PncpId, 1, "Café em grãos", true)]
        });
        await using (var second = new ItemSearchSessionService(
                         recoveredClient,
                         database.Repository,
                         path,
                         persistentSession: true))
        {
            await second.StartAsync(new SearchQuery("cafe", GeoScope.All));
            var resumed = await second.RunContinuousAsync(new PriceBatchRequest(
                1,
                true,
                PriceBatchBudgetMode.UnresolvedContracts));
            var rows = await second.GetDiscoveredRowsAsync();

            Assert.Equal(0, resumed.ProcessedContracts);
            Assert.Single(rows, row => row.PriceState == ItemSearchPriceState.Homologated);
        }

        Assert.Equal(1, failingClient.ItemListCalls);
        Assert.Equal(1, recoveredClient.ItemListCalls);
        Assert.Equal(1, recoveredClient.ResultCalls);
    }

    [Fact]
    public async Task PersistentSession_DifferentCriteriaAndExplicitRestartCreateNewRotations()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = CandidateContracts(1, "criteria")[0];
        await database.Repository.UpsertContractsAsync([contract]);
        var client = new SessionPncpClient(new Dictionary<string, IReadOnlyList<ProcurementItem>>
        {
            [contract.PncpId] = [Item(contract.PncpId, 1, "Café em grãos", true)]
        });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "criteria-session.db"),
            persistentSession: true);
        var original = await service.StartAsync(new SearchQuery("cafe", GeoScope.All));
        await service.RunContinuousAsync(new PriceBatchRequest(
            1,
            true,
            PriceBatchBudgetMode.UnresolvedContracts));

        var changed = await service.StartAsync(new SearchQuery("acucar", GeoScope.All));
        Assert.NotEqual(original.Id, changed.Id);
        Assert.Empty(await service.GetDiscoveredRowsAsync());

        var restarted = await service.StartAsync(
            new SearchQuery("acucar", GeoScope.All),
            restart: true);
        Assert.NotEqual(changed.Id, restarted.Id);
    }

    [Fact]
    public async Task AnchoredSession_RefiltersAndRevertsWithoutNetworkCalls()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = Enumerable.Range(1, 10)
            .Select(number => RepositorySearchTests.Contract(
                $"television-{number:D2}",
                $"Aquisição de televisão {number}",
                "SP",
                number))
            .ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        var client = new SessionPncpClient(contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(
                    contract.PncpId,
                    1,
                    contract.PurchaseSequence % 2 == 0
                        ? "Televisão smart 50 polegadas"
                        : "Televisão básica 32 polegadas",
                    true)
            ]));
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "anchored-session.db"),
            persistentSession: true);

        var original = await service.StartAsync(new SearchQuery("Televisão", GeoScope.All));
        await service.RunContinuousAsync(new PriceBatchRequest(
            1,
            true,
            PriceBatchBudgetMode.CandidateContracts,
            10));
        Assert.Equal(10, (await service.GetDiscoveredRowsAsync()).Count);
        var listCalls = client.ItemListCalls;
        var resultCalls = client.ResultCalls;

        var narrowed = await service.StartAsync(new SearchQuery("televisao smart", GeoScope.All));
        var narrowedRows = await service.GetDiscoveredRowsAsync();
        Assert.Equal(original.Id, narrowed.Id);
        Assert.Equal(5, narrowedRows.Count);
        Assert.All(narrowedRows, row => Assert.Contains("smart", row.Item.Description));
        Assert.Equal(listCalls, client.ItemListCalls);
        Assert.Equal(resultCalls, client.ResultCalls);

        var reverted = await service.StartAsync(new SearchQuery("TELEVISÃO", GeoScope.All));
        Assert.Equal(original.Id, reverted.Id);
        Assert.Equal(10, (await service.GetDiscoveredRowsAsync()).Count);
        Assert.Equal(listCalls, client.ItemListCalls);
        Assert.Equal(resultCalls, client.ResultCalls);
    }

    [Fact]
    public async Task AnchoredSession_AppliesAllExclusionsWithContractPriorityWithoutNetworkCalls()
    {
        await using var database = await TestDatabase.CreateAsync();
        var descriptions = new[]
        {
            "Televisão smart 50 polegadas",
            "Televisão para serviço de monitoramento",
            "Televisão com instalação e controle remoto",
            "Suporte para televisão de circuito fechado",
            "Televisão com suporte articulado"
        };
        var contracts = descriptions
            .Select((_, index) => RepositorySearchTests.Contract(
                $"television-exclusion-{index:D2}",
                $"Aquisicao de eletroeletronico e televisao {index}",
                "SP",
                index + 1))
            .ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        var client = new SessionPncpClient(contracts
            .Select((contract, index) => (contract, index))
            .ToDictionary(
                value => value.contract.PncpId,
                value => (IReadOnlyList<ProcurementItem>)[
                    Item(value.contract.PncpId, 1, descriptions[value.index], true)
                ]));
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "anchored-exclusions.db"),
            persistentSession: true);

        var original = await service.StartAsync(new SearchQuery(
            "Televisão C:(Eletroeletrônico)",
            GeoScope.All));
        await service.RunContinuousAsync(new PriceBatchRequest(
            1,
            true,
            PriceBatchBudgetMode.CandidateContracts,
            descriptions.Length));
        Assert.Equal(descriptions.Length, (await service.GetDiscoveredRowsAsync()).Count);
        var listCalls = client.ItemListCalls;
        var resultCalls = client.ResultCalls;

        var filtered = await service.StartAsync(new SearchQuery(
            "Televisão -serviço -monitoramento -instalação -controle " +
            "-suporte -suporte -circuito C:(Eletroeletrônico)",
            GeoScope.All));
        var rows = await service.GetDiscoveredRowsAsync();

        Assert.Equal(original.Id, filtered.Id);
        Assert.Equal("Televisão smart 50 polegadas", Assert.Single(rows).Item.Description);
        Assert.Equal(listCalls, client.ItemListCalls);
        Assert.Equal(resultCalls, client.ResultCalls);
    }

    [Fact]
    public async Task InterruptedBatch_ResumesTheExactRemainingFortyContracts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(60, "exact-resume");
        await database.Repository.UpsertContractsAsync(contracts);
        var items = contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]);
        foreach (var contract in contracts)
        {
            await database.Repository.UpsertItemsAsync(contract.PncpId, items[contract.PncpId], false);
        }

        var path = Path.Combine(database.Directory, "exact-resume.db");
        var allRemainingResultsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedContractId = string.Empty;
        var firstClient = new SessionPncpClient(
            items,
            resultAsyncFactory: async (contract, itemNumber, call, cancellationToken) =>
            {
                if (call == 50)
                {
                    allRemainingResultsStarted.TrySetResult();
                }

                if (string.Equals(contract.PncpId, blockedContractId, StringComparison.Ordinal))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return [Result(contract.PncpId, itemNumber, 1, itemNumber, 1)];
            });
        await using (var first = new ItemSearchSessionService(
                         firstClient,
                         database.Repository,
                         path,
                         persistentSession: true,
                         requestScheduler: new PncpRequestScheduler(maximumConcurrency: 48)))
        {
            var query = new SearchQuery("cafe", GeoScope.All);
            var session = await first.StartAsync(query);
            var ordered = await database.Repository.SearchItemCandidatesAsync(
                query,
                SearchText.Parse(query.Text),
                session.RandomPivot,
                null,
                50);
            blockedContractId = ordered.Results[10].Contract.PncpId;

            var initial = await first.RunContinuousAsync(new PriceBatchRequest(
                1,
                true,
                PriceBatchBudgetMode.CandidateContracts,
                10));
            Assert.Equal(10, initial.ProcessedContracts);

            using var cancellation = new CancellationTokenSource();
            var running = first.RunContinuousAsync(
                new PriceBatchRequest(
                    1,
                    true,
                    PriceBatchBudgetMode.CandidateContracts,
                    40),
                cancellationToken: cancellation.Token);
            await allRemainingResultsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }

        var secondClient = new SessionPncpClient(items);
        await using (var second = new ItemSearchSessionService(
                         secondClient,
                         database.Repository,
                         path,
                         persistentSession: true,
                         requestScheduler: new PncpRequestScheduler(maximumConcurrency: 48)))
        {
            await second.StartAsync(new SearchQuery("café em", GeoScope.All));
            var completed = await second.RunContinuousAsync(new PriceBatchRequest(
                1,
                true,
                PriceBatchBudgetMode.CandidateContracts,
                40));

            Assert.Equal(40, completed.ProcessedContracts);
            Assert.Equal(50, completed.ContractsScanned);
        }

        Assert.Equal(1, secondClient.ResultCalls);
        Assert.Equal(0, firstClient.ItemListCalls + secondClient.ItemListCalls);
    }

    [Fact]
    public async Task ContinuousBatch_HydratesResultsAcrossContractsWithinTheAdaptiveLimit()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(8, "parallel-results");
        await database.Repository.UpsertContractsAsync(contracts);
        var items = contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]);
        foreach (var contract in contracts)
        {
            await database.Repository.UpsertItemsAsync(contract.PncpId, items[contract.PncpId], false);
        }

        var firstWaveReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        static void UpdateMaximum(ref int maximum, int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref maximum);
                if (observed >= current ||
                    Interlocked.CompareExchange(ref maximum, current, observed) == observed)
                {
                    return;
                }
            }
        }

        var client = new SessionPncpClient(
            items,
            resultAsyncFactory: async (contract, itemNumber, call, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                try
                {
                    if (current == 4)
                    {
                        firstWaveReady.TrySetResult();
                    }

                    await firstWaveReady.Task.WaitAsync(cancellationToken);
                    return [Result(contract.PncpId, itemNumber, 1, itemNumber, 1)];
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 4);
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "parallel-results.db"),
            requestScheduler: scheduler);
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var result = await service.RunContinuousAsync(new PriceBatchRequest(
            1,
            true,
            PriceBatchBudgetMode.CandidateContracts,
            8));

        Assert.Equal(8, result.ProcessedContracts);
        Assert.Equal(8, client.ResultCalls);
        Assert.Equal(0, client.ItemListCalls);
        Assert.Equal(4, maximumActive);
    }

    [Fact]
    public async Task TemporaryResultPaging_RemainsAvailableDuringAndAfterStoppingAContinuousBatch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(2, "paging-while-running");
        await database.Repository.UpsertContractsAsync(contracts);
        var items = contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]);
        foreach (var contract in contracts)
        {
            await database.Repository.UpsertItemsAsync(contract.PncpId, items[contract.PncpId], false);
        }

        var secondWaveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompleteSecondWave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new SessionPncpClient(
            items,
            resultAsyncFactory: async (contract, itemNumber, call, cancellationToken) =>
            {
                if (call >= 2)
                {
                    secondWaveStarted.TrySetResult();
                    await neverCompleteSecondWave.Task.WaitAsync(cancellationToken);
                }

                var resultCount = call == 1 ? 75 : 1;
                return Enumerable.Range(1, resultCount)
                    .Select(sequence => Result(
                        contract.PncpId,
                        itemNumber,
                        sequence,
                        sequence,
                        1))
                    .ToArray();
            });
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "paging-while-running.db"),
            persistentSession: true,
            requestScheduler: new PncpRequestScheduler(maximumConcurrency: 1));
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var running = service.RunContinuousAsync(new PriceBatchRequest(
            1,
            true,
            PriceBatchBudgetMode.CandidateContracts,
            2));
        await secondWaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstPage = await service.LoadDiscoveredResultPageAsync(null)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(50, firstPage.Rows.Count);
        Assert.True(firstPage.HasMore);
        Assert.False(running.IsCompleted);

        service.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var secondPage = await service.LoadDiscoveredResultPageAsync(firstPage.NextCursor);

        Assert.Equal(25, secondPage.Rows.Count);
        Assert.False(secondPage.HasMore);
        Assert.Equal(75, firstPage.Rows.Concat(secondPage.Rows).Count());
    }

    [Fact]
    public async Task ContinuousBatch_UsesAWindowOfFortyEightThenTwo()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(50, "window-48");
        await database.Repository.UpsertContractsAsync(contracts);
        var firstWindowReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWindowReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var client = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>(),
            itemAsyncFactory: async (contract, call, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                while (true)
                {
                    var maximum = Volatile.Read(ref maximumActive);
                    if (maximum >= current ||
                        Interlocked.CompareExchange(ref maximumActive, current, maximum) == maximum)
                    {
                        break;
                    }
                }

                try
                {
                    if (call <= 48)
                    {
                        if (call == 48)
                        {
                            firstWindowReady.TrySetResult();
                        }

                        await firstWindowReady.Task.WaitAsync(cancellationToken);
                    }
                    else
                    {
                        if (call == 50)
                        {
                            secondWindowReady.TrySetResult();
                        }

                        await secondWindowReady.Task.WaitAsync(cancellationToken);
                    }

                    return [Item(contract.PncpId, 1, "Café em grãos", false)];
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 48);
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "window-48.db"),
            requestScheduler: scheduler);
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var result = await service.RunContinuousAsync(new PriceBatchRequest(
            1,
            true,
            PriceBatchBudgetMode.CandidateContracts,
            50));

        Assert.Equal(50, result.ProcessedContracts);
        Assert.Equal(50, client.ItemListCalls);
        Assert.Equal(48, maximumActive);
        Assert.True(firstWindowReady.Task.IsCompletedSuccessfully);
        Assert.True(secondWindowReady.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ContinuousBatch_UsesReducedConcurrencyForTheNextWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(80, "adaptive-window");
        await database.Repository.UpsertContractsAsync(contracts);
        var firstWindowReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWindow = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWindowReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondWindow = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWindowActive = 0;
        var firstWindowMaximum = 0;
        var secondWindowActive = 0;
        var secondWindowMaximum = 0;

        static void UpdateMaximum(ref int maximum, int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref maximum);
                if (observed >= current ||
                    Interlocked.CompareExchange(ref maximum, current, observed) == observed)
                {
                    return;
                }
            }
        }

        var client = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>(),
            itemAsyncFactory: async (contract, call, cancellationToken) =>
            {
                if (call <= 48)
                {
                    var current = Interlocked.Increment(ref firstWindowActive);
                    UpdateMaximum(ref firstWindowMaximum, current);
                    try
                    {
                        if (call == 48)
                        {
                            firstWindowReady.TrySetResult();
                        }

                        await releaseFirstWindow.Task.WaitAsync(cancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref firstWindowActive);
                    }
                }
                else
                {
                    var current = Interlocked.Increment(ref secondWindowActive);
                    UpdateMaximum(ref secondWindowMaximum, current);
                    try
                    {
                        if (call == 80)
                        {
                            secondWindowReady.TrySetResult();
                        }

                        await releaseSecondWindow.Task.WaitAsync(cancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref secondWindowActive);
                    }
                }

                return [Item(contract.PncpId, 1, "Café em grãos", false)];
            });
        var scheduler = new PncpRequestScheduler(maximumConcurrency: 48);
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "adaptive-window.db"),
            requestScheduler: scheduler);
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));

        var running = service.RunContinuousAsync(new PriceBatchRequest(
            2,
            true,
            PriceBatchBudgetMode.CandidateContracts,
            80));
        await firstWindowReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.ReportOutcome(
            PncpRequestCategory.ItemLists,
            HttpStatusCode.InternalServerError,
            TimeSpan.FromSeconds(1));
        Assert.Equal(32, scheduler.GetSnapshot().EffectiveConcurrency);
        releaseFirstWindow.TrySetResult();
        await secondWindowReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseSecondWindow.TrySetResult();

        var result = await running;
        Assert.Equal(80, result.ProcessedContracts);
        Assert.Equal(80, result.ContractsScanned);
        Assert.Equal(80, client.ItemListCalls);
        Assert.Equal(48, firstWindowMaximum);
        Assert.Equal(32, secondWindowMaximum);
    }

    [Fact]
    public async Task OutOfOrderCompletedListsRemainCachedWithoutAdvancingTheCheckpoint()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(40, "out-of-order");
        await database.Repository.UpsertContractsAsync(contracts);
        var path = Path.Combine(database.Directory, "out-of-order.db");
        var allCallsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedContractId = string.Empty;
        var firstClient = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>(),
            itemAsyncFactory: async (contract, call, cancellationToken) =>
            {
                if (call == 32)
                {
                    allCallsStarted.TrySetResult();
                }

                if (string.Equals(contract.PncpId, blockedContractId, StringComparison.Ordinal))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return [Item(contract.PncpId, 1, "Café em grãos", false)];
            });
        var query = new SearchQuery("cafe", GeoScope.All);
        await using (var first = new ItemSearchSessionService(
                         firstClient,
                         database.Repository,
                         path,
                         persistentSession: true,
                         requestScheduler: new PncpRequestScheduler(maximumConcurrency: 32)))
        {
            var session = await first.StartAsync(query);
            var ordered = await database.Repository.SearchItemCandidatesAsync(
                query,
                SearchText.Parse(query.Text),
                session.RandomPivot,
                null,
                32);
            blockedContractId = ordered.Results[0].Contract.PncpId;
            using var cancellation = new CancellationTokenSource();
            var running = first.RunContinuousAsync(
                new PriceBatchRequest(1, true, PriceBatchBudgetMode.CandidateContracts, 32),
                cancellationToken: cancellation.Token);
            await allCallsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }

        var secondClient = new SessionPncpClient(
            new Dictionary<string, IReadOnlyList<ProcurementItem>>(),
            itemFactory: contract => [Item(contract.PncpId, 1, "Café em grãos", false)]);
        await using (var second = new ItemSearchSessionService(
                         secondClient,
                         database.Repository,
                         path,
                         persistentSession: true,
                         requestScheduler: new PncpRequestScheduler(maximumConcurrency: 32)))
        {
            await second.StartAsync(query);
            var resumed = await second.RunContinuousAsync(new PriceBatchRequest(
                1,
                true,
                PriceBatchBudgetMode.CandidateContracts,
                32));

            Assert.Equal(32, resumed.ProcessedContracts);
            Assert.Equal(32, resumed.ContractsScanned);
        }

        Assert.Equal(1, secondClient.ItemListCalls);
    }

    [Fact]
    public async Task ScopeChangeStartsANewRotationButPreservesTemporaryPrices()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = CandidateContracts(1, "scope");
        await database.Repository.UpsertContractsAsync(contracts);
        var client = new SessionPncpClient(contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[
                Item(contract.PncpId, 1, "Café em grãos", true)
            ]));
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "scope-session.db"),
            persistentSession: true);
        var original = await service.StartAsync(new SearchQuery("cafe", GeoScope.All));
        await service.RunContinuousAsync(new PriceBatchRequest(
            1,
            true,
            PriceBatchBudgetMode.CandidateContracts,
            1));
        var resultCalls = client.ResultCalls;

        var changedScope = await service.StartAsync(new SearchQuery(
            "cafe",
            SearchGeoFilter.State("SP")));
        Assert.NotEqual(original.Id, changedScope.Id);
        await service.RunContinuousAsync(new PriceBatchRequest(
            1,
            true,
            PriceBatchBudgetMode.CandidateContracts,
            1));
        Assert.Equal(resultCalls, client.ResultCalls);

        var changedSort = await service.StartAsync(new SearchQuery(
            "cafe",
            SearchGeoFilter.State("SP"),
            Sort: SearchSort.Newest));
        Assert.Equal(changedScope.Id, changedSort.Id);
    }

    [Fact]
    public async Task VersionTwoSession_IsMigratedAndKeepsItsCursorForSimpleAnchor()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = Enumerable.Range(1, 20)
            .Select(number => RepositorySearchTests.Contract(
                $"migration-{number:D2}",
                $"Aquisição de televisão {number}",
                "SP",
                number))
            .ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        const long pivot = 123456;
        var query = new SearchQuery("Televisão", GeoScope.All);
        var candidates = await database.Repository.SearchItemCandidatesAsync(
            query,
            SearchText.Parse(query.Text),
            pivot,
            null,
            20);
        var cursor = candidates.Results[9].Cursor;
        var sessionId = Guid.NewGuid();
        var path = Path.Combine(database.Directory, "version-two.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE session_info(
                    id TEXT PRIMARY KEY, search_key TEXT NOT NULL, started_at TEXT NOT NULL,
                    random_pivot INTEGER NOT NULL, cursor_geo_layer INTEGER,
                    cursor_group_rank INTEGER, cursor_rotation_band INTEGER,
                    cursor_random_key INTEGER, cursor_pncp_id TEXT,
                    contracts_scanned INTEGER NOT NULL DEFAULT 0,
                    expanded_contracts INTEGER NOT NULL DEFAULT 0,
                    fully_resolved_contracts INTEGER NOT NULL DEFAULT 0,
                    cached_item_lists INTEGER NOT NULL DEFAULT 0,
                    item_list_calls INTEGER NOT NULL DEFAULT 0,
                    item_result_calls INTEGER NOT NULL DEFAULT 0,
                    completed_result_calls INTEGER NOT NULL DEFAULT 0,
                    failed_calls INTEGER NOT NULL DEFAULT 0,
                    candidate_set_exhausted INTEGER NOT NULL DEFAULT 0,
                    updated_at TEXT NOT NULL);
                CREATE TABLE search_hits(
                    contract_id TEXT NOT NULL, item_number INTEGER NOT NULL,
                    discovered_order INTEGER NOT NULL, contract_json TEXT NOT NULL,
                    item_json TEXT NOT NULL, PRIMARY KEY(contract_id, item_number));
                CREATE TABLE queried_items(
                    contract_id TEXT NOT NULL, item_number INTEGER NOT NULL,
                    succeeded INTEGER NOT NULL, error TEXT, queried_at TEXT NOT NULL,
                    PRIMARY KEY(contract_id, item_number));
                CREATE TABLE contract_failures(
                    contract_id TEXT PRIMARY KEY, attempts INTEGER NOT NULL DEFAULT 1,
                    error TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL);
                PRAGMA user_version=2;
                INSERT INTO session_info(
                    id, search_key, started_at, random_pivot,
                    cursor_geo_layer, cursor_group_rank, cursor_rotation_band,
                    cursor_random_key, cursor_pncp_id, contracts_scanned, updated_at)
                VALUES($id, $searchKey, $startedAt, $pivot,
                       $layer, $group, $band, $random, $contractId, 10, $updatedAt);
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
            command.Parameters.AddWithValue("$searchKey", "Televisão\u001F0\u001F\u001F\u001F\u001F0");
            command.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$pivot", pivot);
            command.Parameters.AddWithValue("$layer", cursor.GeographicLayer);
            command.Parameters.AddWithValue("$group", cursor.GroupRank);
            command.Parameters.AddWithValue("$band", cursor.RotationBand);
            command.Parameters.AddWithValue("$random", cursor.RandomOrderKey);
            command.Parameters.AddWithValue("$contractId", cursor.PncpId);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var client = new SessionPncpClient(contracts.ToDictionary(
            contract => contract.PncpId,
            contract => (IReadOnlyList<ProcurementItem>)[]));
        await using var service = new ItemSearchSessionService(
            client,
            database.Repository,
            path,
            persistentSession: true);
        var restored = await service.StartAsync(query);
        var next = await service.RunContinuousAsync(new PriceBatchRequest(
            1,
            true,
            PriceBatchBudgetMode.CandidateContracts,
            1));

        Assert.Equal(sessionId, restored.Id);
        Assert.Equal(11, next.ContractsScanned);
        Assert.Equal(1, next.ProcessedContracts);
        await using var verification = new SqliteConnection(connectionString);
        await verification.OpenAsync();
        await using var version = verification.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(3L, (long)(await version.ExecuteScalarAsync())!);
    }

    private static async Task SeedResolvedItemListsAsync(
        string databasePath,
        IReadOnlyList<ContractRecord> contracts)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insertItem = connection.CreateCommand();
        insertItem.Transaction = (SqliteTransaction)transaction;
        insertItem.CommandText = """
            INSERT INTO items(
                contract_id, item_number, description, unit, status, has_result,
                hydration_status, cache_updated_at, search_text)
            VALUES($contractId, 1, 'Café em grãos', 'kg', 'Ativo', 0, 2, $now, 'cafe em graos');
            """;
        insertItem.Parameters.Add("$contractId", SqliteType.Text);
        insertItem.Parameters.Add("$now", SqliteType.Text);

        await using var insertSnapshot = connection.CreateCommand();
        insertSnapshot.Transaction = (SqliteTransaction)transaction;
        insertSnapshot.CommandText = """
            INSERT INTO contract_item_snapshots(
                contract_id, fetched_at, item_count, source_global_updated_at)
            VALUES($contractId, $now, 1, $sourceUpdatedAt);
            """;
        insertSnapshot.Parameters.Add("$contractId", SqliteType.Text);
        insertSnapshot.Parameters.Add("$now", SqliteType.Text);
        insertSnapshot.Parameters.Add("$sourceUpdatedAt", SqliteType.Text);

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var contract in contracts)
        {
            insertItem.Parameters["$contractId"].Value = contract.PncpId;
            insertItem.Parameters["$now"].Value = now;
            await insertItem.ExecuteNonQueryAsync();

            insertSnapshot.Parameters["$contractId"].Value = contract.PncpId;
            insertSnapshot.Parameters["$now"].Value = now;
            insertSnapshot.Parameters["$sourceUpdatedAt"].Value =
                contract.GlobalUpdatedAt?.ToString("O") ?? (object)DBNull.Value;
            await insertSnapshot.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static ContractRecord[] CandidateContracts(int count, string prefix)
    {
        var published = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(1, count)
            .Select(number => RepositorySearchTests.Contract(
                $"{prefix}-{number:D4}",
                $"Aquisição de café {number}",
                "SP",
                (number - 1) % 28 + 1) with
            {
                PurchaseSequence = number,
                PublicationDate = published.AddMinutes(number),
                GlobalUpdatedAt = published.AddMinutes(number)
            })
            .ToArray();
    }

    private static ProcurementItem Item(string contractId, long number, string description, bool hasResult) => new()
    {
        ContractId = contractId,
        ItemNumber = number,
        Description = description,
        Unit = "kg",
        Status = "Ativo",
        HasResult = hasResult,
        HydrationStatus = hasResult ? ItemHydrationStatus.NotLoaded : ItemHydrationStatus.Complete
    };

    private static HomologationResult Result(
        string contractId,
        long itemNumber,
        long sequence,
        decimal unitPrice,
        int status) => new()
    {
        ContractId = contractId,
        ItemNumber = itemNumber,
        ResultSequence = sequence,
        SupplierName = $"Fornecedor {sequence}",
        HomologatedQuantityScaled = DecimalScale.ToScaled(2m),
        HomologatedUnitValueScaled = DecimalScale.ToScaled(unitPrice),
        HomologatedTotalValueScaled = DecimalScale.ToScaled(unitPrice * 2m),
        ResultDate = new DateOnly(2026, 7, 1),
        ResultStatusId = status,
        ResultStatusName = status == 1 ? "Informado" : "Cancelado"
    };

    private sealed class SessionPncpClient(
        IReadOnlyDictionary<string, IReadOnlyList<ProcurementItem>> items,
        Func<ContractRecord, long, IReadOnlyList<HomologationResult>>? resultFactory = null,
        Func<ContractRecord, IReadOnlyList<ProcurementItem>>? itemFactory = null,
        Func<ContractRecord, long, int, CancellationToken, Task<IReadOnlyList<HomologationResult>>>?
            resultAsyncFactory = null,
        Func<ContractRecord, int, CancellationToken, Task<IReadOnlyList<ProcurementItem>>>?
            itemAsyncFactory = null) : IPncpClient
    {
        private int _itemListCalls;
        private int _resultCalls;

        public int ItemListCalls => Volatile.Read(ref _itemListCalls);
        public int ResultCalls => Volatile.Read(ref _resultCalls);

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Modality>>([]);

        public Task<ContractPage> GetContractsPageAsync(
            DateOnly startDate,
            DateOnly endDate,
            long modalityId,
            string? uf,
            int page,
            int pageSize,
            SyncMode mode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractPage([], 0, 0, page, 0, TimeSpan.Zero));

        public Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.TryGetValue(contract.PncpId, out var value) ? value.Count : 0);

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _itemListCalls);
            if (itemAsyncFactory is not null)
            {
                return itemAsyncFactory(contract, call, cancellationToken);
            }

            if (itemFactory is not null)
            {
                return Task.FromResult(itemFactory(contract));
            }

            return Task.FromResult(
                items.TryGetValue(contract.PncpId, out var value)
                    ? value
                    : (IReadOnlyList<ProcurementItem>)[]);
        }

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _resultCalls);
            if (resultAsyncFactory is not null)
            {
                return resultAsyncFactory(contract, itemNumber, call, cancellationToken);
            }

            var values = resultFactory?.Invoke(contract, itemNumber) ?? [
                Result(contract.PncpId, itemNumber, 1, itemNumber, 1)
            ];
            return Task.FromResult(values);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
