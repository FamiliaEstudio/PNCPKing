using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationItemSearchTests
{
    [Fact]
    public async Task Migration18To19_AddsCoverageCountersAndFailureQueue()
    {
        await using var database = await TestDatabase.CreateAsync();
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection(
                         $"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TABLE quotation_item_search_failures;
                ALTER TABLE quotation_item_search_workspaces DROP COLUMN expanded_contracts;
                ALTER TABLE quotation_item_search_workspaces DROP COLUMN fully_resolved_contracts;
                UPDATE schema_info SET version = 18 WHERE id = 1;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var initialization = await new SqliteContractRepository(
                database.Repository.DatabasePath)
            .InitializeAsync();

        Assert.Equal(18, initialization.PreviousVersion);
        Assert.Equal(26, initialization.CurrentVersion);
        Assert.Equal([19, 20, 21, 22, 23, 24, 25, 26], initialization.AppliedMigrations);
    }

    [Fact]
    public async Task WorkspaceRepository_PersistsSlotsFiltersCursorHitsAndResetIndependently()
    {
        await using var database = await TestDatabase.CreateAsync();
        var quotation = new SqliteQuotationRepository(
            Path.Combine(database.Directory, "test.db"));
        var project = await quotation.CreateProjectAsync("Pesquisa detalhada");
        var lineId = Guid.NewGuid();
        await quotation.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Agulha", 10, "unidade", null, null),
            []);
        var restrictive = Workspace(lineId, ItemSearchPromptSlot.Restrictive, "agulha") with
        {
            GeoFilter = SearchGeoFilter.State("SP"),
            Sort = SearchSort.Newest,
            MinimumUnitPrice = 2.5m,
            MaximumUnitPrice = 30m,
            BatchCount = 7,
            Checkpoint = new QuotationItemSearchCheckpoint
            {
                RandomPivot = 123,
                Cursor = new ItemCandidateCursor(1, 2, 0, 456, "contrato-9"),
                ContractsExamined = 9,
                BatchesCompleted = 1
            },
            MatchedItems = 4,
            RevealedPrices = 3,
            StatusMessage = "Checkpoint salvo"
        };
        var hit = new QuotationItemSearchHit
        {
            LineId = lineId,
            Slot = ItemSearchPromptSlot.Restrictive,
            ContractId = "contrato-9",
            ItemNumber = 2,
            MatchedPromptLevel = PromptMatchLevel.Restrictive,
            MatchedSearchText = "agulha",
            DiscoveredOrder = 9_000_002
        };

        await quotation.SaveProcessedContractAsync(restrictive, [hit]);
        await quotation.SaveWorkspaceFailureAsync(
            lineId,
            ItemSearchPromptSlot.Restrictive,
            "contrato-falho",
            "falha transitória");
        await quotation.SaveWorkspaceAsync(
            Workspace(lineId, ItemSearchPromptSlot.Custom, "agulha bordado"));

        var restored = Assert.IsType<QuotationItemSearchWorkspace>(
            await quotation.GetWorkspaceAsync(lineId, ItemSearchPromptSlot.Restrictive));
        Assert.Equal(SearchGeoFilterKind.State, restored.GeoFilter.Kind);
        Assert.Equal("SP", restored.GeoFilter.Uf);
        Assert.Equal(SearchSort.Newest, restored.Sort);
        Assert.Equal(2.5m, restored.MinimumUnitPrice);
        Assert.Equal(30m, restored.MaximumUnitPrice);
        Assert.Equal(7, restored.BatchCount);
        Assert.Equal(9, restored.Checkpoint.ContractsExamined);
        Assert.Equal("contrato-9", restored.Checkpoint.Cursor!.PncpId);
        Assert.Single(await quotation.GetWorkspaceHitsAsync(
            lineId,
            ItemSearchPromptSlot.Restrictive));
        Assert.Empty(await quotation.GetWorkspaceHitsAsync(
            lineId,
            ItemSearchPromptSlot.Custom));
        Assert.Single(await quotation.GetWorkspaceFailuresAsync(
            lineId,
            ItemSearchPromptSlot.Restrictive));

        await quotation.ResetWorkspaceAsync(restrictive with
        {
            Checkpoint = new QuotationItemSearchCheckpoint { RandomPivot = 999 },
            MatchedItems = 0,
            RevealedPrices = 0,
            StatusMessage = "Reiniciada"
        });

        Assert.Empty(await quotation.GetWorkspaceHitsAsync(
            lineId,
            ItemSearchPromptSlot.Restrictive));
        Assert.Empty(await quotation.GetWorkspaceFailuresAsync(
            lineId,
            ItemSearchPromptSlot.Restrictive));
        Assert.Equal(
            999,
            (await quotation.GetWorkspaceAsync(lineId, ItemSearchPromptSlot.Restrictive))!
            .Checkpoint.RandomPivot);
        Assert.NotNull(await quotation.GetWorkspaceAsync(lineId, ItemSearchPromptSlot.Custom));
    }

    [Fact]
    public async Task ChangingOnlyContractCandidates_RestartsCursorAndPreservesCollectedHits()
    {
        await using var database = await TestDatabase.CreateAsync();
        var quotation = new SqliteQuotationRepository(Path.Combine(database.Directory, "test.db"));
        var project = await quotation.CreateProjectAsync("Crivos");
        var lineId = Guid.NewGuid();
        await quotation.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Fita crepe", 10, "rolo", null, null),
            []);
        var stored = Workspace(
            lineId,
            ItemSearchPromptSlot.Restrictive,
            "fita crepe C:(material escolar)") with
        {
            Checkpoint = new QuotationItemSearchCheckpoint
            {
                RandomPivot = 123,
                Cursor = new ItemCandidateCursor(-1, 2, 0, 456, "contrato-9"),
                ContractsExamined = 9,
                BatchesCompleted = 1,
                CandidateSetExhausted = true
            },
            MatchedItems = 4,
            RevealedPrices = 3
        };
        await quotation.SaveProcessedContractAsync(stored, [
            new QuotationItemSearchHit
            {
                LineId = lineId,
                Slot = ItemSearchPromptSlot.Restrictive,
                ContractId = "contrato-9",
                ItemNumber = 2,
                MatchedSearchText = stored.SearchText,
                DiscoveredOrder = 9_000_002
            }
        ]);
        await using var itemSearch = new ItemSearchSessionService(
            new CountingClient(),
            database.Repository,
            Path.Combine(database.Directory, "candidate-reset.db"));
        var service = new QuotationItemSearchService(database.Repository, quotation, itemSearch);

        await service.SavePreferencesAsync(stored with
        {
            SearchText = "fita crepe C:(materiais de expediente)"
        });

        var updated = Assert.IsType<QuotationItemSearchWorkspace>(
            await quotation.GetWorkspaceAsync(lineId, ItemSearchPromptSlot.Restrictive));
        Assert.Null(updated.Checkpoint.Cursor);
        Assert.False(updated.Checkpoint.CandidateSetExhausted);
        Assert.Equal(0, updated.Checkpoint.ContractsExamined);
        Assert.NotEqual(123, updated.Checkpoint.RandomPivot);
        Assert.Equal(4, updated.MatchedItems);
        Assert.Equal(3, updated.RevealedPrices);
        Assert.Single(await quotation.GetWorkspaceHitsAsync(lineId, ItemSearchPromptSlot.Restrictive));
    }

    [Fact]
    public async Task IndependentSearch_CheckpointsEveryContractAndResumesWithoutRepeatingAfterCancellation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = Enumerable.Range(1, 60)
            .Select(number => Contract(number))
            .ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        var quotation = new SqliteQuotationRepository(
            Path.Combine(database.Directory, "test.db"));
        var project = await quotation.CreateProjectAsync("Retomada");
        var lineId = Guid.NewGuid();
        await quotation.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Agulha", 10, "unidade", null, null),
            []);
        var client = new CountingClient();
        var seed = Workspace(lineId, ItemSearchPromptSlot.Restrictive, "agulha") with
        {
            BatchCount = 1
        };

        using (var cancellation = new CancellationTokenSource())
        {
            await using var itemSearch = new ItemSearchSessionService(
                client,
                database.Repository,
                Path.Combine(database.Directory, "first-session.db"));
            var service = new QuotationItemSearchService(
                database.Repository,
                quotation,
                itemSearch);
            var progress = new InlineProgress<QuotationItemSearchProgress>(value =>
            {
                if (value.ProcessedContracts == 7)
                {
                    cancellation.Cancel();
                }
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.RunAsync(
                    seed,
                    restart: true,
                    progress,
                    cancellationToken: cancellation.Token));
        }

        var interrupted = (await quotation.GetWorkspaceAsync(
            lineId,
            ItemSearchPromptSlot.Restrictive))!;
        Assert.Equal(7, interrupted.Checkpoint.ContractsExamined);
        Assert.Equal(7, client.ItemListCalls);
        Assert.Equal(7, client.ResultCalls);

        await using (var itemSearch = new ItemSearchSessionService(
                         client,
                         database.Repository,
                         Path.Combine(database.Directory, "second-session.db")))
        {
            var service = new QuotationItemSearchService(
                database.Repository,
                quotation,
                itemSearch);
            var continued = await service.RunAsync(seed, restart: false);
            Assert.Equal(57, continued.Workspace.Checkpoint.ContractsExamined);
            Assert.False(continued.Workspace.Checkpoint.CandidateSetExhausted);

            var completed = await service.RunAsync(seed, restart: false);
            Assert.Equal(60, completed.Workspace.Checkpoint.ContractsExamined);
            Assert.True(completed.Workspace.Checkpoint.CandidateSetExhausted);
            Assert.Equal(60, completed.Rows.Count);
            Assert.All(completed.Rows, row =>
            {
                Assert.NotNull(row.Contract.PublicationDate);
                Assert.Equal("SP", row.Contract.Uf);
                Assert.Equal("Agulha para bordado", row.Item.Description);
                Assert.Equal("unidade", row.Item.Unit);
            });
        }

        Assert.Equal(60, client.ItemListCalls);
        Assert.Equal(60, client.ResultCalls);
        Assert.Equal(
            60,
            (await quotation.GetWorkspaceHitsAsync(
                lineId,
                ItemSearchPromptSlot.Restrictive)).Count);
    }

    [Fact]
    public async Task IndependentSearch_RetriesFailuresAfterUnseenCandidatesAndDoesNotBlockCoverage()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([Contract(1), Contract(2)]);
        var quotation = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await quotation.CreateProjectAsync("Falhas retomáveis");
        var lineId = Guid.NewGuid();
        await quotation.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Agulha", 10, "unidade", null, null),
            []);
        var client = new CountingClient(failFirstItemList: true);
        await using var itemSearch = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "quotation-failures.db"));
        var service = new QuotationItemSearchService(database.Repository, quotation, itemSearch);
        var seed = Workspace(lineId, ItemSearchPromptSlot.Restrictive, "agulha") with
        {
            BatchCount = 1
        };

        var first = await service.RunAsync(seed, restart: true);

        Assert.True(first.Workspace.Checkpoint.CandidateSetExhausted);
        Assert.Equal(2, first.Workspace.ExpandedContracts);
        Assert.Single(first.Rows);
        Assert.Single(await quotation.GetWorkspaceFailuresAsync(
            lineId,
            ItemSearchPromptSlot.Restrictive));

        var resumed = await service.RunAsync(seed, restart: false);

        Assert.Equal(2, resumed.Rows.Count);
        Assert.Empty(await quotation.GetWorkspaceFailuresAsync(
            lineId,
            ItemSearchPromptSlot.Restrictive));
        Assert.Equal(3, client.ItemListCalls);
        Assert.Equal(2, client.ResultCalls);
    }

    [Fact]
    public async Task IndependentSearch_CachedContractsDoNotConsumeTheUnresolvedQuota()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = Enumerable.Range(1, 60).Select(Contract).ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        var quotation = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await quotation.CreateProjectAsync("Orçamento por não resolvidas");
        var lineId = Guid.NewGuid();
        await quotation.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Agulha", 10, "unidade", null, null),
            []);
        var seed = Workspace(lineId, ItemSearchPromptSlot.Restrictive, "agulha") with
        {
            BatchCount = 1,
            Checkpoint = new QuotationItemSearchCheckpoint { RandomPivot = 1 }
        };
        var candidateOrder = await database.Repository.SearchItemCandidatesAsync(
            new SearchQuery(
                seed.SearchText,
                seed.GeoFilter,
                seed.StartDate,
                seed.EndDate,
                seed.Sort,
                1,
                100),
            PNCPKing.Core.Search.SearchText.Parse(seed.SearchText),
            seed.Checkpoint.RandomPivot,
            null,
            100);
        foreach (var candidate in candidateOrder.Results.Take(10))
        {
            await database.Repository.UpsertItemsAsync(candidate.Contract.PncpId, [
                SearchItem(candidate.Contract.PncpId)
            ], false);
            await database.Repository.ReplaceItemResultsAsync(candidate.Contract.PncpId, 1, [
                SearchResult(candidate.Contract)
            ]);
        }

        var client = new CountingClient();
        await using var itemSearch = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "quotation-unresolved.db"));
        var service = new QuotationItemSearchService(database.Repository, quotation, itemSearch);

        var completed = await service.RunAsync(seed, restart: false);

        Assert.Equal(60, completed.Workspace.Checkpoint.ContractsExamined);
        Assert.Equal(50, completed.Workspace.ExpandedContracts);
        Assert.Equal(10, completed.Workspace.FullyResolvedContracts);
        Assert.Equal(50, client.ItemListCalls);
        Assert.Equal(50, client.ResultCalls);
        Assert.True(completed.Workspace.Checkpoint.CandidateSetExhausted);
    }

    [Fact]
    public async Task IndependentSearch_ShowsOnlyPositivePricesAndPreservesRawCachedResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contracts = Enumerable.Range(1, 3).Select(Contract).ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        var quotation = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await quotation.CreateProjectAsync("Preços úteis");
        var lineId = Guid.NewGuid();
        await quotation.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Agulha", 10, "unidade", null, null),
            []);
        var client = new CountingClient(resultFactory: contract => contract.PurchaseSequence switch
        {
            1 => [SearchResult(contract) with { HomologatedUnitValueScaled = null }],
            2 => [SearchResult(contract) with { HomologatedUnitValueScaled = DecimalScale.ToScaled(0m) }],
            3 =>
            [
                SearchResult(contract) with
                {
                    HomologatedUnitValueScaled = DecimalScale.ToScaled(0.01m)
                },
                SearchResult(contract) with
                {
                    ResultSequence = 2,
                    HomologatedUnitValueScaled = DecimalScale.ToScaled(0.10m),
                    ResultStatusId = 2,
                    ResultStatusName = "Cancelado"
                }
            ],
            _ => throw new InvalidOperationException()
        });
        await using var itemSearch = new ItemSearchSessionService(
            client,
            database.Repository,
            Path.Combine(database.Directory, "quotation-useful-prices.db"));
        var service = new QuotationItemSearchService(database.Repository, quotation, itemSearch);

        var completed = await service.RunAsync(
            Workspace(lineId, ItemSearchPromptSlot.Restrictive, "agulha") with { BatchCount = 1 },
            restart: true);

        Assert.Equal(3, completed.Workspace.MatchedItems);
        Assert.Equal(1, completed.Workspace.RevealedPrices);
        Assert.Equal([0.01m, 0.10m], completed.Rows
            .Select(row => row.HomologatedUnitValue!.Value)
            .Order()
            .ToArray());
        Assert.Single(completed.Rows, row => row.PriceState == ItemSearchPriceState.Cancelled);
        var missingPrice = Assert.IsType<CachedItemResults>(
            await database.Repository.GetCachedItemResultsAsync(contracts[0].PncpId, 1));
        var zeroPrice = Assert.IsType<CachedItemResults>(
            await database.Repository.GetCachedItemResultsAsync(contracts[1].PncpId, 1));
        Assert.Null(Assert.Single(missingPrice.Results).HomologatedUnitValue);
        Assert.Equal(0m, Assert.Single(zeroPrice.Results).HomologatedUnitValue);
    }

    private static QuotationItemSearchWorkspace Workspace(
        Guid lineId,
        ItemSearchPromptSlot slot,
        string text) =>
        new()
        {
            LineId = lineId,
            Slot = slot,
            SearchText = text,
            StartDate = new DateOnly(2025, 7, 26),
            EndDate = new DateOnly(2026, 7, 25)
        };

    private static ContractRecord Contract(int number) =>
        new()
        {
            PncpId = $"agulha-{number:000}",
            Cnpj = "12345678000199",
            PurchaseYear = 2026,
            PurchaseSequence = number,
            Object = $"Aquisição de agulha para bordado {number}",
            Organization = "Órgão",
            Unit = "Unidade",
            Municipality = "São Paulo",
            Uf = "SP",
            ModalityId = 6,
            ModalityName = "Pregão eletrônico",
            Status = "Divulgada",
            PublicationDate = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)
                .AddMinutes(number),
            GlobalUpdatedAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)
                .AddMinutes(number)
        };

    private static ProcurementItem SearchItem(string contractId) => new()
    {
        ContractId = contractId,
        ItemNumber = 1,
        Description = "Agulha para bordado",
        Unit = "unidade",
        RequestedQuantityScaled = DecimalScale.ToScaled(10),
        Status = "Ativo",
        HasResult = true,
        HydrationStatus = ItemHydrationStatus.NotLoaded
    };

    private static HomologationResult SearchResult(ContractRecord contract) => new()
    {
        ContractId = contract.PncpId,
        ItemNumber = 1,
        ResultSequence = 1,
        SupplierName = $"Fornecedor {contract.PurchaseSequence}",
        HomologatedUnitValueScaled = DecimalScale.ToScaled(contract.PurchaseSequence),
        ResultStatusId = 1,
        ResultStatusName = "Informado"
    };

    private sealed class CountingClient(
        bool failFirstItemList = false,
        Func<ContractRecord, IReadOnlyList<HomologationResult>>? resultFactory = null) : IPncpClient
    {
        public int ItemListCalls { get; private set; }
        public int ResultCalls { get; private set; }

        public Task<IReadOnlyList<Modality>> GetModalitiesAsync(
            CancellationToken cancellationToken = default) =>
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

        public Task<int> GetItemCountAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
            ContractRecord contract,
            CancellationToken cancellationToken = default)
        {
            ItemListCalls++;
            if (failFirstItemList && ItemListCalls == 1)
            {
                throw new HttpRequestException("falha definitiva simulada");
            }

            return Task.FromResult<IReadOnlyList<ProcurementItem>>(
            [
                new ProcurementItem
                {
                    ContractId = contract.PncpId,
                    ItemNumber = 1,
                    Description = "Agulha para bordado",
                    Unit = "unidade",
                    RequestedQuantityScaled = DecimalScale.ToScaled(10),
                    Status = "Ativo",
                    HasResult = true,
                    HydrationStatus = ItemHydrationStatus.NotLoaded
                }
            ]);
        }

        public Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
            ContractRecord contract,
            long itemNumber,
            CancellationToken cancellationToken = default)
        {
            ResultCalls++;
            return Task.FromResult(
                resultFactory?.Invoke(contract) ??
                (IReadOnlyList<HomologationResult>)
                [
                    new HomologationResult
                    {
                        ContractId = contract.PncpId,
                        ItemNumber = itemNumber,
                        ResultSequence = 1,
                        SupplierName = $"Fornecedor {contract.PurchaseSequence}",
                        SupplierTaxId = "11222333000181",
                        HomologatedQuantityScaled = DecimalScale.ToScaled(10),
                        HomologatedUnitValueScaled = DecimalScale.ToScaled(contract.PurchaseSequence),
                        HomologatedTotalValueScaled = DecimalScale.ToScaled(
                            contract.PurchaseSequence * 10m),
                        ResultDate = new DateOnly(2026, 7, 2),
                        ResultStatusId = 1,
                        ResultStatusName = "Informado"
                    }
                ]);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
