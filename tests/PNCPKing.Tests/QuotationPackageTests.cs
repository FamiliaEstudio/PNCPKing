using System.IO.Compression;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using SkiaSharp;

namespace PNCPKing.Tests;

public sealed class QuotationPackageTests
{
    [Fact]
    public async Task Package_RoundTripsPricesBasketsSearchesDraftsAndPrints()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var sourceRepository = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var sourceQuotations = new QuotationService(
            sourceRepository,
            new QuotationAnalyzer(new DateOnly(2026, 7, 27)));
        var project = await sourceQuotations.CreateProjectAsync("Cotação portátil");
        var lineId = Guid.NewGuid();
        await sourceRepository.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Café torrado", 10m, "pacote", null, null),
            [
                Reference(lineId, "pncp-1", 90m),
                Reference(lineId, "pncp-2", 100m),
                Reference(lineId, "pncp-3", 110m)
            ]);
        var initial = Assert.Single(await sourceQuotations.GetAnalysesAsync(project.Id));
        var manual = await sourceRepository.SaveManualBasketAsync(
            lineId,
            null,
            "Pesquisa local",
            initial.References.Select(reference => reference.Id).ToArray());

        var evidenceStore = new InternetEvidenceStore(source.Directory);
        var internet = new InternetPriceService(
            sourceRepository,
            sourceQuotations,
            evidenceStore);
        var pricePrint = await evidenceStore.SavePngAsync(
            CreatePng(SKColors.CornflowerBlue),
            640,
            360);
        var taxPrint = await evidenceStore.SavePngAsync(
            CreatePng(SKColors.Goldenrod),
            640,
            360);
        var now = DateTimeOffset.UtcNow;
        var completed = await internet.CompleteDraftAsync(
            project.Id,
            new InternetPriceDraft
            {
                Id = Guid.NewGuid(),
                LineId = lineId,
                BasketId = manual.Id,
                SourceUrl = "https://loja.exemplo.test/cafe",
                UnitPrice = 105m,
                Description = "Café torrado pacote",
                SupplierName = "Fornecedor Web",
                SupplierTaxId = "11222333000181",
                PriceImage = pricePrint,
                TaxIdImage = taxPrint,
                CapturedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            },
            manual.Id,
            manual.Name);
        await sourceRepository.ConfirmBasketAsync(lineId, completed.Basket.Key);
        var incompleteDraft = await sourceRepository.SaveInternetPriceDraftAsync(
            new InternetPriceDraft
            {
                Id = Guid.NewGuid(),
                LineId = lineId,
                BasketId = manual.Id,
                SourceUrl = "https://rascunho.exemplo.test/cafe",
                PriceImage = pricePrint,
                CapturedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });

        var workspace = new QuotationItemSearchWorkspace
        {
            LineId = lineId,
            Slot = ItemSearchPromptSlot.Intermediate,
            SearchText = "café pacote",
            GeoFilter = SearchGeoFilter.State("SP"),
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 7, 27),
            Sort = SearchSort.Nearest,
            BatchCount = 4,
            Checkpoint = new QuotationItemSearchCheckpoint
            {
                RandomPivot = 42,
                Cursor = new ItemCandidateCursor(0, 1, 2, 3, "contrato-a"),
                ContractsExamined = 50,
                BatchesCompleted = 1
            },
            MatchedItems = 2,
            RevealedPrices = 1,
            StatusMessage = "Checkpoint preservado",
            UpdatedAt = now
        };
        await sourceRepository.SaveProcessedContractAsync(
            workspace,
            [
                new QuotationItemSearchHit
                {
                    LineId = lineId,
                    Slot = workspace.Slot,
                    ContractId = "contrato-a",
                    ItemNumber = 7,
                    MatchedPromptLevel = PromptMatchLevel.Intermediate,
                    MatchedSearchText = workspace.SearchText,
                    DiscoveredOrder = 1
                }
            ]);

        var packagePath = Path.Combine(source.Directory, "cotacao.pncpcotacao");
        var sourcePackages = new QuotationPackageService(
            source.Repository.DatabasePath,
            source.Directory);
        await sourcePackages.ExportAsync(packagePath, project.Id);
        var sourcePreview = await sourcePackages.InspectAsync(packagePath);
        Assert.True(sourcePreview.HasProjectConflict);
        Assert.Equal(1, sourcePreview.ItemCount);
        Assert.Equal(4, sourcePreview.ReferenceCount);
        Assert.Equal(1, sourcePreview.ManualBasketCount);
        Assert.Equal(2, sourcePreview.EvidenceCount);

        var destinationPackages = new QuotationPackageService(
            destination.Repository.DatabasePath,
            destination.Directory);
        var destinationPreview = await destinationPackages.InspectAsync(packagePath);
        Assert.False(destinationPreview.HasProjectConflict);
        var imported = await destinationPackages.ImportAsync(
            packagePath,
            QuotationPackageImportMode.PreserveIdentity);

        Assert.Equal(project.Id, imported.ProjectId);
        Assert.Empty(imported.Warnings);
        var destinationRepository = new SqliteQuotationRepository(
            destination.Repository.DatabasePath);
        var destinationQuotations = new QuotationService(
            destinationRepository,
            new QuotationAnalyzer(new DateOnly(2026, 7, 27)));
        var restored = Assert.Single(
            await destinationQuotations.GetAnalysesAsync(project.Id));
        Assert.Equal(4, restored.References.Count);
        Assert.True(restored.Line.SelectionConfirmed);
        var restoredManual = Assert.Single(
            restored.Baskets,
            basket => basket.IsManual);
        Assert.Equal(completed.Basket.Key, restoredManual.Key);
        Assert.Equal(4, restoredManual.References.Count);
        Assert.Equal(
            QuotationReferenceSource.InternetIncisoIII,
            Assert.Single(
                restored.References,
                reference => reference.Source == QuotationReferenceSource.InternetIncisoIII).Source);

        var restoredDraft = Assert.Single(
            await destinationRepository.GetInternetPriceDraftsAsync(lineId));
        Assert.Equal(incompleteDraft.Id, restoredDraft.Id);
        Assert.NotNull(restoredDraft.PriceImage);
        Assert.Null(restoredDraft.TaxIdImage);
        var restoredEvidence = Assert.Single(
            await destinationRepository.GetInternetPriceEvidenceAsync(lineId));
        var destinationStore = new InternetEvidenceStore(destination.Directory);
        Assert.True(await destinationStore.VerifyAsync(restoredEvidence.Value.PriceImage));
        Assert.True(await destinationStore.VerifyAsync(restoredEvidence.Value.TaxIdImage));

        var restoredWorkspace = await destinationRepository.GetWorkspaceAsync(
            lineId,
            ItemSearchPromptSlot.Intermediate);
        Assert.Equal(workspace, restoredWorkspace);
        var restoredHit = Assert.Single(
            await destinationRepository.GetWorkspaceHitsAsync(
                lineId,
                ItemSearchPromptSlot.Intermediate));
        Assert.Equal("contrato-a", restoredHit.ContractId);
    }

    [Fact]
    public async Task Package_RejectsAlteredData()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var quotation = new QuotationService(repository, new QuotationAnalyzer());
        var project = await quotation.CreateProjectAsync("Integridade");
        var lineId = Guid.NewGuid();
        await repository.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Papel", 1m, "resma", null, null),
            [
                Reference(lineId, "a", 10m, "Papel resma"),
                Reference(lineId, "b", 11m, "Papel resma"),
                Reference(lineId, "c", 12m, "Papel resma")
            ]);
        var path = Path.Combine(database.Directory, "integridade.pncpcotacao");
        var service = new QuotationPackageService(
            database.Repository.DatabasePath,
            database.Directory);
        await service.ExportAsync(path, project.Id);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("quotation.json")!;
            entry.Delete();
            var replacement = archive.CreateEntry("quotation.json");
            await using var output = replacement.Open();
            await output.WriteAsync("{}"u8.ToArray());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectAsync(path));
    }

    [Fact]
    public async Task Package_CopyRemapsRelationshipsAndReplaceKeepsRecoveryAndOtherData()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var sourceRepository = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var sourceQuotation = new QuotationService(
            sourceRepository,
            new QuotationAnalyzer(new DateOnly(2026, 7, 27)));
        var project = await sourceQuotation.CreateProjectAsync("Transferência");
        var lineId = Guid.NewGuid();
        await sourceRepository.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Fita crepe", 12m, "rolo", null, null),
            [
                Reference(lineId, "fita-1", 8m, "Fita crepe rolo"),
                Reference(lineId, "fita-2", 9m, "Fita crepe rolo"),
                Reference(lineId, "fita-3", 10m, "Fita crepe rolo")
            ]);
        var analysis = Assert.Single(await sourceQuotation.GetAnalysesAsync(project.Id));
        var manual = await sourceRepository.SaveManualBasketAsync(
            lineId,
            null,
            "Cesta escolhida",
            analysis.References.Select(reference => reference.Id).ToArray());
        await sourceRepository.ConfirmBasketAsync(lineId, manual.Key);

        var packagePath = Path.Combine(source.Directory, "transferencia.pncpcotacao");
        var sourcePackages = new QuotationPackageService(
            source.Repository.DatabasePath,
            source.Directory);
        await sourcePackages.ExportAsync(packagePath, project.Id);

        await destination.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("nao-relacionado", "Registro preservado", "SP", 1)
        ]);
        var destinationRepository = new SqliteQuotationRepository(
            destination.Repository.DatabasePath);
        var unrelated = await destinationRepository.CreateProjectAsync("Não relacionado");
        var destinationPackages = new QuotationPackageService(
            destination.Repository.DatabasePath,
            destination.Directory);
        await destinationPackages.ImportAsync(
            packagePath,
            QuotationPackageImportMode.PreserveIdentity);
        var copied = await destinationPackages.ImportAsync(
            packagePath,
            QuotationPackageImportMode.Copy);

        Assert.NotEqual(project.Id, copied.ProjectId);
        Assert.Equal("Transferência (cópia importada)", copied.ProjectName);
        var destinationQuotation = new QuotationService(
            destinationRepository,
            new QuotationAnalyzer(new DateOnly(2026, 7, 27)));
        var originalAnalysis = Assert.Single(
            await destinationQuotation.GetAnalysesAsync(project.Id));
        var copyAnalysis = Assert.Single(
            await destinationQuotation.GetAnalysesAsync(copied.ProjectId));
        Assert.NotEqual(originalAnalysis.Line.Id, copyAnalysis.Line.Id);
        Assert.Equal(
            originalAnalysis.References.Select(reference => reference.Id).Order().ToArray(),
            copyAnalysis.References.Select(reference => reference.Id).Order().ToArray());
        var originalManual = Assert.Single(
            originalAnalysis.Baskets,
            basket => basket.IsManual);
        var copyManual = Assert.Single(
            copyAnalysis.Baskets,
            basket => basket.IsManual);
        Assert.NotEqual(originalManual.ManualBasketId, copyManual.ManualBasketId);
        Assert.Equal(copyManual.Key, copyAnalysis.Line.SelectedBasketKey);
        Assert.True(copyAnalysis.Line.SelectionConfirmed);

        await destinationRepository.RenameProjectAsync(project.Id, "Alterada localmente");
        var extraLineId = Guid.NewGuid();
        await destinationRepository.SaveSampleAsync(
            project.Id,
            extraLineId,
            new QuotationLineInput("Linha que será substituída", 1m, "unidade", null, null),
            []);
        var replaced = await destinationPackages.ImportAsync(
            packagePath,
            QuotationPackageImportMode.Replace);

        Assert.NotNull(replaced.RecoveryPackagePath);
        Assert.True(File.Exists(replaced.RecoveryPackagePath));
        var recoveryPreview = await destinationPackages.InspectAsync(
            replaced.RecoveryPackagePath!);
        Assert.Equal("Alterada localmente", recoveryPreview.ProjectName);
        Assert.Equal(2, recoveryPreview.ItemCount);
        Assert.Single(await destinationQuotation.GetAnalysesAsync(project.Id));
        Assert.Contains(
            await destinationRepository.GetProjectsAsync(),
            value => value.Id == unrelated.Id);
        Assert.Contains(
            await destinationRepository.GetProjectsAsync(),
            value => value.Id == copied.ProjectId);
        Assert.NotNull(await destination.Repository.GetContractAsync("nao-relacionado"));
    }

    [Fact]
    public async Task Package_RestoresProcessedContractsAvailableInDestinationAndWarnsForMissingOnes()
    {
        await using var source = await TestDatabase.CreateAsync();
        var sourceRepository = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var sourceQuotation = new QuotationService(
            sourceRepository,
            new QuotationAnalyzer(new DateOnly(2026, 7, 27)));
        var project = await sourceQuotation.CreateProjectAsync("Automação retomável");
        var run = await sourceRepository.CreateTimedAutomationRunAsync(
            project.Id,
            SearchGeoFilter.All,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 7, 27),
            [
                new QuotationImportItem(
                    1,
                    "café pacote",
                    "Café torrado",
                    10m,
                    "pacote",
                    null,
                    null,
                    3,
                    3,
                    IntermediateSearchText: "café torrado",
                    BroadSearchText: "café")
            ],
            AdequacyWeights.Default,
            TimeSpan.FromMinutes(30),
            ["material escolar"]);
        await source.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("processado-1", "Material escolar café", "SP", 1),
            RepositorySearchTests.Contract("processado-2", "Material escolar café", "MG", 2)
        ]);
        await sourceRepository.SaveProcessedContractAsync(
            new ContractSearchCheckpoint
            {
                RunId = run.Id,
                ContractId = "processado-1",
                PromptOrder = 0,
                ProcessedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                MatchedItems = 1,
                RevealedPrices = 1
            },
            new TimedQuotationProgress
            {
                UniqueContractsProcessed = 1,
                MatchedItems = 1,
                RevealedPrices = 1
            });
        await sourceRepository.SaveProcessedContractAsync(
            new ContractSearchCheckpoint
            {
                RunId = run.Id,
                ContractId = "processado-2",
                PromptOrder = 0,
                ProcessedAt = DateTimeOffset.UtcNow,
                MatchedItems = 1,
                RevealedPrices = 0
            },
            new TimedQuotationProgress
            {
                UniqueContractsProcessed = 2,
                MatchedItems = 2,
                RevealedPrices = 1
            });
        var packagePath = Path.Combine(source.Directory, "automacao.pncpcotacao");
        await new QuotationPackageService(
                source.Repository.DatabasePath,
                source.Directory)
            .ExportAsync(packagePath, project.Id);

        await using var none = await TestDatabase.CreateAsync();
        var noneResult = await new QuotationPackageService(
                none.Repository.DatabasePath,
                none.Directory)
            .ImportAsync(packagePath, QuotationPackageImportMode.PreserveIdentity);
        Assert.Contains(
            noneResult.Warnings,
            warning => warning.Contains("2 marcador", StringComparison.Ordinal));
        Assert.Empty(
            await new SqliteQuotationRepository(none.Repository.DatabasePath)
                .GetProcessedContractsAsync(run.Id));

        await using var some = await TestDatabase.CreateAsync();
        await some.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("processado-1", "Material escolar café", "SP", 1)
        ]);
        var someResult = await new QuotationPackageService(
                some.Repository.DatabasePath,
                some.Directory)
            .ImportAsync(packagePath, QuotationPackageImportMode.PreserveIdentity);
        Assert.Contains(
            someResult.Warnings,
            warning => warning.Contains("1 marcador", StringComparison.Ordinal));
        var someRepository = new SqliteQuotationRepository(some.Repository.DatabasePath);
        Assert.Single(await someRepository.GetProcessedContractsAsync(run.Id));
        Assert.NotEmpty(await someRepository.GetContractSearchPromptsAsync(run.Id));

        await using var all = await TestDatabase.CreateAsync();
        await all.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("processado-1", "Material escolar café", "SP", 1),
            RepositorySearchTests.Contract("processado-2", "Material escolar café", "MG", 2)
        ]);
        var allResult = await new QuotationPackageService(
                all.Repository.DatabasePath,
                all.Directory)
            .ImportAsync(packagePath, QuotationPackageImportMode.PreserveIdentity);
        Assert.Empty(allResult.Warnings);
        var allRepository = new SqliteQuotationRepository(all.Repository.DatabasePath);
        Assert.Equal(2, (await allRepository.GetProcessedContractsAsync(run.Id)).Count);
        Assert.NotNull(await allRepository.GetLatestAutomationRunAsync(project.Id));
    }

    [Fact]
    public async Task Package_ExportBlocksMissingPrintAndLeavesNoPartialArchive()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var quotation = new QuotationService(repository, new QuotationAnalyzer());
        var project = await quotation.CreateProjectAsync("Print ausente");
        var lineId = Guid.NewGuid();
        await repository.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Tesoura", 1m, "unidade", null, null),
            [Reference(lineId, "tesoura", 20m, "Tesoura unidade")]);
        var store = new InternetEvidenceStore(database.Directory);
        var image = await store.SavePngAsync(CreatePng(SKColors.Red), 100, 80);
        var now = DateTimeOffset.UtcNow;
        await repository.SaveInternetPriceDraftAsync(new InternetPriceDraft
        {
            Id = Guid.NewGuid(),
            LineId = lineId,
            SourceUrl = "https://exemplo.test/tesoura",
            PriceImage = image,
            CapturedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        File.Delete(Path.Combine(database.Directory, image.RelativePath));
        var path = Path.Combine(database.Directory, "nao-criar.pncpcotacao");
        var service = new QuotationPackageService(
            database.Repository.DatabasePath,
            database.Directory);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ExportAsync(path, project.Id));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task Package_ImportFailureRollsBackDatabaseAndNewPrints()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var sourceRepository = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var quotation = new QuotationService(sourceRepository, new QuotationAnalyzer());
        var project = await quotation.CreateProjectAsync("Falha atômica");
        var lineId = Guid.NewGuid();
        await sourceRepository.SaveSampleAsync(
            project.Id,
            lineId,
            new QuotationLineInput("Caneta", 1m, "unidade", null, null),
            [Reference(lineId, "caneta", 5m, "Caneta unidade")]);
        var store = new InternetEvidenceStore(source.Directory);
        var image = await store.SavePngAsync(CreatePng(SKColors.DarkGreen), 120, 90);
        var now = DateTimeOffset.UtcNow;
        await sourceRepository.SaveInternetPriceDraftAsync(new InternetPriceDraft
        {
            Id = Guid.NewGuid(),
            LineId = lineId,
            SourceUrl = "https://exemplo.test/caneta",
            PriceImage = image,
            CapturedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        var packagePath = Path.Combine(source.Directory, "falha.pncpcotacao");
        await new QuotationPackageService(
                source.Repository.DatabasePath,
                source.Directory)
            .ExportAsync(packagePath, project.Id);

        var destinationPrint = Path.Combine(destination.Directory, image.RelativePath);
        var packages = new QuotationPackageService(
            destination.Repository.DatabasePath,
            destination.Directory,
            () => throw new IOException("Falha simulada antes do commit."));

        await Assert.ThrowsAsync<IOException>(() =>
            packages.ImportAsync(
                packagePath,
                QuotationPackageImportMode.PreserveIdentity));

        Assert.DoesNotContain(
            await new SqliteQuotationRepository(
                destination.Repository.DatabasePath).GetProjectsAsync(),
            candidate => candidate.Id == project.Id);
        Assert.False(File.Exists(destinationPrint));
    }

    private static QuotationReference Reference(
        Guid lineId,
        string id,
        decimal price,
        string description = "Café torrado pacote") => new()
    {
        Id = id,
        LineId = lineId,
        ContractId = $"contrato-{id}",
        ItemNumber = 1,
        ResultSequence = 1,
        SupplierName = $"Fornecedor {id}",
        SupplierTaxId = "11222333000181",
        HomologatedQuantity = 10m,
        UnitPrice = price,
        ResultDate = new DateOnly(2026, 7, 20),
        ItemDescription = description,
        ItemUnit = "pacote",
        ItemRequestedQuantity = 10m,
        Organization = "Órgão",
        Municipality = "Ribeirão Preto",
        Uf = "SP",
        PublicationDate = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
        PortalUrl = $"https://pncp.gov.br/app/editais/contrato-{id}",
        State = QuotationReferenceState.Eligible
    };

    private static byte[] CreatePng(SKColor color)
    {
        using var bitmap = new SKBitmap(32, 24);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
