using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using PNCPKing.Guard;

namespace PNCPKing.Tests;

public sealed class GuardTests
{
    [Fact]
    public void Partitions_AreCompleteDisjointDeterministicAndWeighted()
    {
        var workers = new[]
        {
            new GuardWorkerDefinition { Id = "a", Name = "Casa", Weight = 1 },
            new GuardWorkerDefinition { Id = "b", Name = "Serviço", Weight = 2 },
            new GuardWorkerDefinition { Id = "c", Name = "Terceiro", Weight = 5 }
        };

        var first = GuardPartitioner.AssignPartitions(workers);
        var second = GuardPartitioner.AssignPartitions(workers);

        Assert.Equal(GuardFormat.PartitionCount, first.Count);
        Assert.Equal(first, second);
        Assert.All(first, worker => Assert.Contains(worker, workers.Select(item => item.Id)));
        Assert.Equal(512, first.Count(worker => worker == "a"));
        Assert.Equal(1024, first.Count(worker => worker == "b"));
        Assert.Equal(2560, first.Count(worker => worker == "c"));
        Assert.All(
            Enumerable.Range(0, 10_000).Select(index => $"contract-{index}"),
            id => Assert.InRange(GuardPartitioner.GetPartition(id), 0, GuardFormat.PartitionCount - 1));
    }

    [Fact]
    public async Task Campaign_ContainsOnlyContractsWithMissingOrStaleItemListsWithoutOverlap()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var missing = Contract("missing", 1, now);
        var complete = Contract("complete", 2, now);
        var incomplete = Contract("incomplete", 3, now);
        await database.Repository.UpsertContractsAsync([missing, complete, incomplete]);
        await database.Repository.UpsertItemsAsync(complete.PncpId, [Item(complete, 1, "Completo")], false);
        await database.Repository.ReplaceItemResultsAsync(
            complete.PncpId,
            1,
            [Result(complete.PncpId, 1, 1, 1)]);
        await database.Repository.UpsertItemsAsync(incomplete.PncpId, [Item(incomplete, 1, "Pendente")], false);

        var root = Path.Combine(database.Directory, "drive");
        var service = new GuardMasterService(new SqliteConnectionFactory(database.Repository.DatabasePath));
        var generated = await service.CreateOrReplaceCampaignAsync(
            root,
            [new GuardWorkerInput("Casa", 1), new GuardWorkerInput("Serviço", 1)]);
        var control = await GuardFileCodec.ReadJsonAsync<GuardControl>(Path.Combine(root, "control.json"));
        var plans = await Task.WhenAll(control.Workers.Select(worker =>
            GuardFileCodec.ReadJsonAsync<GuardWorkerPlan>(Path.Combine(root, worker.PlanRelativePath))));
        var assigned = plans.SelectMany(plan => plan.Contracts.Select(contract => contract.PncpId)).ToArray();

        Assert.Equal(generated.CampaignId, control.CampaignId);
        Assert.Equal(1, generated.ContractCount);
        Assert.Equal(assigned.Length, assigned.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["missing"], assigned.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("complete", assigned);
        Assert.DoesNotContain("incomplete", assigned);
    }

    [Fact]
    public async Task TwoWorkerPackages_MergeIntoMasterWithoutDuplicates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var version = DateTimeOffset.UtcNow.AddHours(-1);
        var contracts = Enumerable.Range(1, 100)
            .Select(index => Contract($"distributed-{index}", index, version.AddSeconds(index)))
            .ToArray();
        await database.Repository.UpsertContractsAsync(contracts);
        var root = Path.Combine(database.Directory, "drive");
        var service = new GuardMasterService(new SqliteConnectionFactory(database.Repository.DatabasePath));
        var campaign = await service.CreateOrReplaceCampaignAsync(
            root,
            [new GuardWorkerInput("Casa", 1), new GuardWorkerInput("Serviço", 1)]);
        var control = await GuardFileCodec.ReadJsonAsync<GuardControl>(Path.Combine(root, "control.json"));
        var nonEmptyWorkers = 0;
        foreach (var controlWorker in control.Workers)
        {
            var plan = await GuardFileCodec.ReadJsonAsync<GuardWorkerPlan>(
                Path.Combine(root, controlWorker.PlanRelativePath));
            if (plan.Contracts.Count == 0)
            {
                continue;
            }

            nonEmptyWorkers++;
            var packageDirectory = Path.Combine(root, "packages", campaign.CampaignId, plan.Worker.Id);
            Directory.CreateDirectory(packageDirectory);
            await GuardFileCodec.WritePackageAsync(
                Path.Combine(packageDirectory, Guid.NewGuid().ToString("N") + GuardFormat.PackageExtension),
                campaign.CampaignId,
                plan.Worker.Id,
                plan.Contracts.Select(contract => Snapshot(
                    contracts.Single(item => item.PncpId == contract.PncpId),
                    DateTimeOffset.UtcNow,
                    "Item " + contract.PncpId,
                    1) with { Results = [] }).ToArray());
        }

        var imported = await service.ImportPackagesAsync(root);

        Assert.Equal(2, nonEmptyWorkers);
        Assert.Equal(nonEmptyWorkers, imported.ImportedPackages);
        Assert.Equal(100, imported.ImportedContracts);
        Assert.Equal((100L, 100L, 0L), await database.Repository.GetCountsAsync());
        var importedItem = Assert.IsType<ProcurementItem>(
            await database.Repository.GetItemAsync(contracts[0].PncpId, 1));
        Assert.Equal(ItemHydrationStatus.NotLoaded, importedItem.HydrationStatus);
    }

    [Fact]
    public async Task LocalCheckpoint_ResumesAndCampaignReplacementClearsOldTemporaryData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new GuardRepository(Path.Combine(directory, "guard.db"));
            await repository.InitializeAsync();
            var contract = Contract("checkpoint", 1, DateTimeOffset.UtcNow);
            var plan = WorkerPlan("campaign-1", contract);
            Assert.True(await repository.ApplyPlanAsync(plan));
            var claimed = Assert.IsType<GuardLocalWork>(await repository.AcquireNextAsync(DateTimeOffset.UtcNow));
            Assert.Equal(contract.PncpId, claimed.Contract.PncpId);

            await repository.ReturnToPendingAsync(contract.PncpId);
            Assert.NotNull(await repository.AcquireNextAsync(DateTimeOffset.UtcNow));
            await repository.SaveSnapshotAsync(Snapshot(contract, DateTimeOffset.UtcNow, "Checkpoint", 1));
            var ready = Assert.Single(await repository.ReadReadySnapshotsAsync(100));
            var localPackage = Path.Combine(directory, "package" + GuardFormat.PackageExtension);
            var manifest = await GuardFileCodec.WritePackageAsync(
                localPackage,
                plan.CampaignId,
                plan.Worker.Id,
                [ready]);
            var hash = await GuardFileCodec.ComputeFileSha256Async(localPackage);
            await repository.RecordPackageAsync(
                manifest,
                hash,
                localPackage,
                [contract.PncpId]);
            Assert.Equal(1, (await repository.GetStatusAsync()).Packaged);

            await repository.AcknowledgeAsync(manifest.PackageId);
            var acknowledged = await repository.GetStatusAsync();
            Assert.Equal(1, acknowledged.Acknowledged);
            Assert.Empty(await repository.ReadReadySnapshotsAsync(100));

            var next = Contract("new-campaign", 2, DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.True(await repository.ApplyPlanAsync(WorkerPlan("campaign-2", next)));
            var replaced = await repository.GetStatusAsync();
            Assert.Equal(1, replaced.Pending);
            Assert.Equal(0, replaced.Acknowledged);
            Assert.Equal(next.PncpId, (await repository.AcquireNextAsync(DateTimeOffset.UtcNow))?.Contract.PncpId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Import_IsAtomicIdempotentPinsSnapshotAndPreservesRichFields()
    {
        await using var database = await TestDatabase.CreateAsync();
        var version = DateTimeOffset.UtcNow.AddHours(-1);
        var contract = Contract("imported", 1, version);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(
            contract.PncpId,
            [
                Item(contract, 1, "Anterior") with
                {
                    Category = "Material",
                    NcmNbsCode = "1234",
                    CatalogCode = "CAT-1"
                },
                Item(contract, 2, "Removido")
            ],
            false);
        await database.Repository.ReplaceItemResultsAsync(
            contract.PncpId,
            1,
            [Result(contract.PncpId, 1, 1, 1)]);
        await database.Repository.SetItemHydrationStatusAsync(
            contract.PncpId,
            1,
            ItemHydrationStatus.Partial,
            "forçar campanha");

        var root = Path.Combine(database.Directory, "drive");
        var service = new GuardMasterService(new SqliteConnectionFactory(database.Repository.DatabasePath));
        var campaign = await service.CreateOrReplaceCampaignAsync(
            root,
            [new GuardWorkerInput("Casa", 1)]);
        var worker = Assert.Single(campaign.Workers);
        var snapshot = Snapshot(
            contract,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "Descrição mínima",
            statusId: 2);
        var packagePath = Path.Combine(
            root,
            "packages",
            campaign.CampaignId,
            worker.WorkerId,
            Guid.NewGuid().ToString("N") + GuardFormat.PackageExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        await File.WriteAllBytesAsync(packagePath + ".partial", [1, 2, 3]);
        var manifest = await GuardFileCodec.WritePackageAsync(
            packagePath,
            campaign.CampaignId,
            worker.WorkerId,
            [snapshot]);

        var imported = await service.ImportPackagesAsync(root);
        var duplicate = await service.ImportPackagesAsync(root);
        await using (var append = new FileStream(packagePath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await append.WriteAsync(new byte[] { 0x01 });
        }
        var conflictingUuid = await service.ImportPackagesAsync(root);
        var storedItem = Assert.IsType<ProcurementItem>(await database.Repository.GetItemAsync(contract.PncpId, 1));
        var cached = Assert.IsType<CachedItemResults>(
            await database.Repository.GetCachedItemResultsAsync(contract.PncpId, 1));

        Assert.Equal(1, imported.ImportedPackages);
        Assert.Equal(1, imported.PackageFiles);
        Assert.Equal(1, imported.ImportedContracts);
        Assert.Equal(1, duplicate.DuplicatePackages);
        Assert.Equal(1, conflictingUuid.RejectedPackages);
        Assert.Contains(conflictingUuid.Errors, error => error.Contains("outro checksum", StringComparison.Ordinal));
        Assert.Equal("Descrição mínima", storedItem.Description);
        Assert.Equal("Material", storedItem.Category);
        Assert.Equal("1234", storedItem.NcmNbsCode);
        Assert.Equal("CAT-1", storedItem.CatalogCode);
        Assert.Equal(ItemHydrationStatus.Complete, storedItem.HydrationStatus);
        Assert.Null(await database.Repository.GetItemAsync(contract.PncpId, 2));
        Assert.Equal(2, Assert.Single(cached.Results).ResultStatusId);
        Assert.True(File.Exists(Path.Combine(
            root,
            "acks",
            campaign.CampaignId,
            manifest.PackageId + ".ack")));

        await using var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_pinned, background_owned,
                   (SELECT COUNT(*) FROM guard_imported_packages WHERE package_id = $package)
              FROM price_cache_contracts WHERE contract_id = $contract;
            """;
        command.Parameters.AddWithValue("$package", manifest.PackageId);
        command.Parameters.AddWithValue("$contract", contract.PncpId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public async Task Import_SkipsDivergentAndOlderSnapshotsAndRejectsCorruption()
    {
        await using var database = await TestDatabase.CreateAsync();
        var version = DateTimeOffset.UtcNow.AddHours(-2);
        var contract = Contract("versions", 1, version);
        await database.Repository.UpsertContractsAsync([contract]);
        var root = Path.Combine(database.Directory, "drive");
        var service = new GuardMasterService(new SqliteConnectionFactory(database.Repository.DatabasePath));
        var campaign = await service.CreateOrReplaceCampaignAsync(root, [new GuardWorkerInput("Casa", 1)]);
        var worker = Assert.Single(campaign.Workers);
        var directory = Path.Combine(root, "packages", campaign.CampaignId, worker.WorkerId);
        Directory.CreateDirectory(directory);

        await GuardFileCodec.WritePackageAsync(
            Path.Combine(directory, "new" + GuardFormat.PackageExtension),
            campaign.CampaignId,
            worker.WorkerId,
            [Snapshot(contract, DateTimeOffset.UtcNow, "Nova", 1)]);
        var first = await service.ImportPackagesAsync(root);
        Assert.Equal(1, first.ImportedContracts);

        await GuardFileCodec.WritePackageAsync(
            Path.Combine(directory, "old" + GuardFormat.PackageExtension),
            campaign.CampaignId,
            worker.WorkerId,
            [Snapshot(contract, DateTimeOffset.UtcNow.AddDays(-1), "Antiga", 1)]);
        await GuardFileCodec.WritePackageAsync(
            Path.Combine(directory, "divergent" + GuardFormat.PackageExtension),
            campaign.CampaignId,
            worker.WorkerId,
            [Snapshot(contract with { GlobalUpdatedAt = version.AddMinutes(1) }, DateTimeOffset.UtcNow.AddMinutes(1), "Divergente", 1)]);
        var corrupt = Path.Combine(directory, "corrupt" + GuardFormat.PackageExtension);
        await File.WriteAllBytesAsync(corrupt, [1, 2, 3, 4]);

        var result = await service.ImportPackagesAsync(root);

        Assert.Equal(1, result.OlderContracts);
        Assert.Equal(1, result.DivergentContracts);
        Assert.Equal(1, result.RejectedPackages);
        Assert.Equal("Nova", (await database.Repository.GetItemAsync(contract.PncpId, 1))?.Description);
    }

    [Fact]
    public async Task Import_RollsBackWholePackageWhenCommitFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = Contract("rollback", 1, DateTimeOffset.UtcNow.AddHours(-1));
        await database.Repository.UpsertContractsAsync([contract]);
        var connections = new SqliteConnectionFactory(database.Repository.DatabasePath);
        var setup = new GuardMasterService(connections);
        var root = Path.Combine(database.Directory, "drive");
        var campaign = await setup.CreateOrReplaceCampaignAsync(root, [new GuardWorkerInput("Casa", 1)]);
        var worker = Assert.Single(campaign.Workers);
        var directory = Path.Combine(root, "packages", campaign.CampaignId, worker.WorkerId);
        Directory.CreateDirectory(directory);
        await GuardFileCodec.WritePackageAsync(
            Path.Combine(directory, "rollback" + GuardFormat.PackageExtension),
            campaign.CampaignId,
            worker.WorkerId,
            [Snapshot(contract, DateTimeOffset.UtcNow, "Não deve persistir", 1)]);
        var failing = new GuardMasterService(connections, () => throw new InvalidOperationException("falha injetada"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ImportPackagesAsync(root));

        Assert.Null(await database.Repository.GetItemAsync(contract.PncpId, 1));
        await using var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM guard_imported_packages;";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task PackageCodec_IgnoresPartialByConventionAndRejectsChangedPayload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var contract = Contract("checksum", 1, DateTimeOffset.UtcNow);
            var path = Path.Combine(directory, "package" + GuardFormat.PackageExtension);
            var manifest = await GuardFileCodec.WritePackageAsync(
                path,
                "campaign",
                "worker",
                [Snapshot(contract, DateTimeOffset.UtcNow, "Item", 1)]);
            var package = await GuardFileCodec.ReadPackageAsync(path);

            Assert.Equal(manifest.PackageId, package.Manifest.PackageId);
            Assert.Single(package.Payload.Contracts);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));

            await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(new byte[] { 0x01 });
            }

            // Bytes após o diretório central podem ser aceitos pelo ZIP; o hash externo ainda muda,
            // e é esse hash que protege a idempotência do UUID no mestre.
            var changed = await GuardFileCodec.ReadPackageAsync(path);
            Assert.NotEqual(package.FileSha256, changed.FileSha256);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static ContractRecord Contract(string id, int sequence, DateTimeOffset version) => new()
    {
        PncpId = id,
        Cnpj = "12345678000199",
        PurchaseYear = 2026,
        PurchaseSequence = sequence,
        Object = "Aquisição",
        PublicationDate = version.AddDays(-1),
        GlobalUpdatedAt = version,
        ModalityId = 6
    };

    private static ProcurementItem Item(ContractRecord contract, long number, string description) => new()
    {
        ContractId = contract.PncpId,
        ItemNumber = number,
        Description = description,
        Unit = "UN",
        RequestedQuantityScaled = DecimalScale.ToScaled(10m),
        HasResult = true,
        HydrationStatus = ItemHydrationStatus.NotLoaded
    };

    private static HomologationResult Result(string contractId, long item, long sequence, int status) => new()
    {
        ContractId = contractId,
        ItemNumber = item,
        ResultSequence = sequence,
        SupplierName = "Fornecedor",
        SupplierTaxId = "11222333000181",
        HomologatedQuantityScaled = DecimalScale.ToScaled(10m),
        HomologatedUnitValueScaled = DecimalScale.ToScaled(20m),
        HomologatedTotalValueScaled = DecimalScale.ToScaled(200m),
        ResultDate = new DateOnly(2026, 8, 1),
        ResultStatusId = status,
        ResultStatusName = status == 1 ? "Ativo" : "Cancelado"
    };

    private static GuardContractSnapshot Snapshot(
        ContractRecord contract,
        DateTimeOffset collectedAt,
        string description,
        int statusId) => new()
    {
        Contract = new GuardPlanContract
        {
            PncpId = contract.PncpId,
            Cnpj = contract.Cnpj,
            PurchaseYear = contract.PurchaseYear,
            PurchaseSequence = contract.PurchaseSequence,
            PublicationDate = contract.PublicationDate,
            GlobalUpdatedAt = contract.GlobalUpdatedAt
        },
        CollectedAt = collectedAt,
        Items =
        [
            new GuardItem
            {
                ItemNumber = 1,
                Description = description,
                AdditionalInformation = "Complemento",
                RequestedQuantityScaled = DecimalScale.ToScaled(10m),
                Unit = "UN",
                HasResult = true
            }
        ],
        Results =
        [
            new GuardResult
            {
                ItemNumber = 1,
                ResultSequence = 1,
                SupplierTaxId = "11222333000181",
                SupplierName = "Fornecedor",
                SupplierType = "PJ",
                SupplierMunicipality = "Ribeirão Preto",
                SupplierUf = "SP",
                HomologatedQuantityScaled = DecimalScale.ToScaled(10m),
                HomologatedUnitValueScaled = DecimalScale.ToScaled(25m),
                HomologatedTotalValueScaled = DecimalScale.ToScaled(250m),
                ResultDate = new DateOnly(2026, 8, 2),
                ResultStatusId = statusId,
                ResultStatusName = statusId == 1 ? "Ativo" : "Cancelado"
            }
        ]
    };

    private static GuardWorkerPlan WorkerPlan(string campaignId, ContractRecord contract) => new()
    {
        CampaignId = campaignId,
        MasterId = "master",
        Worker = new GuardWorkerDefinition { Id = "worker", Name = "Worker", Weight = 1 },
        CreatedAt = DateTimeOffset.UtcNow,
        Contracts =
        [
            new GuardPlanContract
            {
                PncpId = contract.PncpId,
                Cnpj = contract.Cnpj,
                PurchaseYear = contract.PurchaseYear,
                PurchaseSequence = contract.PurchaseSequence,
                PublicationDate = contract.PublicationDate,
                GlobalUpdatedAt = contract.GlobalUpdatedAt
            }
        ]
    };
}
