using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;
using System.Collections.Concurrent;

namespace PNCPKing.Tests;

public sealed class ItemSearchSessionTests
{
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
        Assert.False(permanent.IsCurrent);
        Assert.Empty(permanent.Results);

        // A new search drops the disposable price database, so the same visible
        // page is fetched again while the permanent item-description index is reused.
        await service.StartAsync(new SearchQuery("cafe", GeoScope.All));
        await service.LoadPageAsync(1);
        Assert.Equal(110, client.ResultCalls);
        Assert.Equal(1, client.ItemListCalls);
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
    public async Task ManualBatch_DoesNotRepeatAContractWhosePriceFailed()
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
        Assert.Equal(2, attempts);
        Assert.Equal(2, client.ResultCalls);
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
    public async Task ItemsWithoutResult_AreShownButDoNotConsumePriceBatchCalls()
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

        Assert.Equal(60, rows.Count);
        Assert.Equal(50, rows.Count(row => row.PriceState == ItemSearchPriceState.NoHomologatedResult));
        Assert.Equal(10, rows.Count(row => row.PriceState == ItemSearchPriceState.Homologated));
        Assert.Equal(10, batch.CompletedItemCalls);
        Assert.Equal(10, client.ResultCalls);
        Assert.True(batch.CandidateSetExhausted);
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
            ["last"] = [Item("last", 1, "Café encontrado", false)]
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
            Path.Combine(database.Directory, "continuous.db"));
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
        Func<ContractRecord, long, IReadOnlyList<HomologationResult>>? resultFactory = null) : IPncpClient
    {
        public int ItemListCalls { get; private set; }
        public int ResultCalls { get; private set; }

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
            ItemListCalls++;
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
            ResultCalls++;
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
