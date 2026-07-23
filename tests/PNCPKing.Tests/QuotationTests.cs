using ClosedXML.Excel;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationTests
{
    private static readonly DateOnly Today = new(2026, 7, 21);

    [Fact]
    public void WeightSlider_RebalancesOtherComponentsAndAlwaysTotalsOneHundred()
    {
        var weights = AdequacyWeights.Default.Rebalance(AdequacyWeightComponent.Proximity, 40);

        Assert.Equal(new AdequacyWeights(35, 14, 7, 40, 4), weights);
        Assert.Equal(100, weights.Total);
    }

    [Fact]
    public void HigherProximityWeight_ChangesIndexButNotEligibility()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café", 100m, "pacote") with
        {
            Weights = new AdequacyWeights(30, 10, 10, 45, 5)
        };
        var far = analyzer.ScoreReference(
            line,
            Reference("far", "c1", "11222333000181", 35m) with
            {
                Municipality = "Seberi",
                Uf = "RS",
                DistanceFromRibeiraoKilometers = null
            });
        var local = analyzer.ScoreReference(line, Reference("local", "c2", "60701190000104", 35m));

        Assert.Equal(0m, far.Adequacy.ProximityScore);
        Assert.Equal(55m, far.Adequacy.Total);
        Assert.Equal(QuotationReferenceState.Eligible, far.State);
        Assert.Equal(45m, local.Adequacy.ProximityScore);
        Assert.Equal(100m, local.Adequacy.Total);
        Assert.Equal(QuotationReferenceState.Eligible, local.State);
    }

    [Theory]
    [InlineData("11.222.333/0001-81")]
    [InlineData("12.ABC.345/01DE-35")]
    public void CnpjValidation_AcceptsNumericAndNewAlphanumericFormats(string cnpj)
    {
        Assert.True(QuotationAnalyzer.IsValidCnpj(cnpj));
    }

    [Fact]
    public void GenericUnit_UsesPackageAndMeasureFromDescription()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café torrado e moído pacote de 500 g", 100m, "pacote");
        var reference = Reference(
            "ref",
            "contract",
            "11222333000181",
            40m,
            "Café torrado e moído, embalagem pacote 500 g",
            "UNIDADE",
            100m);

        var scored = analyzer.ScoreReference(line, reference);

        Assert.Equal(QuotationReferenceState.Eligible, scored.State);
        Assert.Equal(18m, scored.Adequacy.UnitScore);
        Assert.Contains("inferida", scored.Adequacy.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Termos coincidentes", scored.Adequacy.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ausentes", scored.Adequacy.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompatiblePackageMeasure_IsInformativeAndDoesNotRejectPrice()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café pacote de 500 g", 10m, "pacote");

        var scored = analyzer.ScoreReference(
            line,
            Reference("ref", "contract", "11222333000181", 40m, "Café pacote de 250 g", "pacote", 10m));

        Assert.Equal(QuotationReferenceState.Eligible, scored.State);
        Assert.Contains("informativo", scored.StateReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("divergente", scored.StateReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClearCoffeeReferences_WithDifferentPurchaseQuantities_FormABasket()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café", 5000m, "pacote");
        var result = analyzer.Analyze(line, [
            Reference(
                "galeti",
                "c1",
                "42386723000110",
                35.49m,
                "CAFÉ TORRADO E MOIDO PCT DE 500G",
                "PACOTE (PAC)",
                5m) with
            {
                Municipality = "Seberi",
                Uf = "RS",
                DistanceFromRibeiraoKilometers = null
            },
            Reference(
                "sete-barras",
                "c2",
                "05244997000149",
                33.59m,
                "CAFÉ 500GR",
                "PACOTE",
                69m) with
            {
                Municipality = "Sete Barras",
                DistanceFromRibeiraoKilometers = null
            },
            Reference(
                "sao-paulo",
                "c3",
                "08824503000193",
                34.37m,
                "Café apresentação: torrado moído, intensidade: média, tipo: tradicional, empacotamento: vácuo, prazo validade mínimo: 12 meses, característica adicional: blend arábica e conilon",
                "Pacote 500 G",
                1500m) with
            {
                Municipality = "São Paulo",
                DistanceFromRibeiraoKilometers = null
            }
        ]);

        Assert.Equal(3, result.EligibleCount);
        Assert.All(result.References, reference => Assert.Equal(50m, reference.Adequacy.DescriptionScore));
        Assert.Equal([2m, 4m, 8m], result.References.Select(reference => reference.Adequacy.QuantityScore).Order().ToArray());
        Assert.Single(result.Baskets);
        Assert.InRange(result.Baskets[0].AveragePrice, 34.48m, 34.49m);
    }

    [Fact]
    public void CompactGramNotation_IsEquivalentToSpacedGramNotation()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var scored = analyzer.ScoreReference(
            Line("Café 500 g", 10m, "pacote"),
            Reference(
                "compact-measure",
                "contract",
                "11222333000181",
                34m,
                "CAFÉ 500GR",
                "PACOTE",
                10m));

        Assert.Equal(QuotationReferenceState.Eligible, scored.State);
        Assert.Equal(50m, scored.Adequacy.DescriptionScore);
    }

    [Fact]
    public void BasketDispersion_IsInformativeEvenAboveTwentyFivePercent()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café torrado", 100m, "pacote");
        var exact = analyzer.Analyze(line, [
            Reference("a", "c1", "11222333000181", 75m, quantity: 100m),
            Reference("b", "c2", "60701190000104", 100m, quantity: 100m),
            Reference("c", "c3", "33000167000101", 125m, quantity: 100m)
        ]);
        var above = analyzer.Analyze(line, [
            Reference("d", "c4", "11222333000181", 74.99m, quantity: 100m),
            Reference("e", "c5", "60701190000104", 100m, quantity: 100m),
            Reference("f", "c6", "33000167000101", 125.01m, quantity: 100m)
        ]);

        Assert.Single(exact.Baskets);
        Assert.Equal(25m, exact.Baskets[0].MaximumDeviationPercent);
        Assert.Single(above.Baskets);
        Assert.True(above.Baskets[0].MaximumDeviationPercent > 25m);
    }

    [Fact]
    public void BasketOrigin_IsInformativeAndDoesNotPreventCombination()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café torrado", 100m, "pacote");
        var sameSupplier = analyzer.Analyze(line, [
            Reference("a", "c1", "11222333000181", 90m),
            Reference("b", "c2", "11222333000181", 100m),
            Reference("c", "c3", "33000167000101", 110m)
        ]);
        var sameContract = analyzer.Analyze(line, [
            Reference("d", "c1", "11222333000181", 90m),
            Reference("e", "c1", "60701190000104", 100m),
            Reference("f", "c3", "33000167000101", 110m)
        ]);

        Assert.Single(sameSupplier.Baskets);
        Assert.Single(sameContract.Baskets);
    }

    [Fact]
    public void ProbableDuplicate_RemainsEligibleForUserReview()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café torrado", 100m, "pacote");
        var result = analyzer.Analyze(line, [
            Reference("best", "c1", "11222333000181", 100m, date: Today.AddDays(-5)),
            Reference("repeat", "c2", "11222333000181", 100.50m, date: Today.AddDays(-10))
        ]);

        Assert.Equal(2, result.EligibleCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.All(result.References, reference => Assert.Equal(QuotationReferenceState.Eligible, reference.State));
    }

    [Fact]
    public void OnlyPriceRangeAndDescriptionBlockQuotationEligibility()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var line = Line("Café torrado", 100m, "pacote") with
        {
            MinimumUnitPrice = 30m,
            MaximumUnitPrice = 50m,
            Weights = new AdequacyWeights(10, 10, 10, 65, 5)
        };
        var informativeProblems = analyzer.ScoreReference(
            line,
            Reference("info", "same", "CNPJ INVÁLIDO", 40m, "Café torrado 250 g", "caixa") with
            {
                Municipality = "Cidade distante",
                Uf = "RR",
                DistanceFromRibeiraoKilometers = null
            });
        var outsideRange = analyzer.ScoreReference(
            line,
            Reference("range", "c2", "", 60m, "Café torrado", "pacote"));
        var wrongDescription = analyzer.ScoreReference(
            line,
            Reference("description", "c3", "", 40m, "Açúcar cristal", "pacote"));

        Assert.Equal(QuotationReferenceState.Eligible, informativeProblems.State);
        Assert.Contains("informativo", informativeProblems.StateReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(QuotationReferenceState.Rejected, outsideRange.State);
        Assert.Contains("máximo", outsideRange.StateReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(QuotationReferenceState.Rejected, wrongDescription.State);
        Assert.Contains("descritiva", wrongDescription.StateReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapturedSample_PersistsAcrossRepositoryInstancesAndRequiresReconfirmationOnUpdate()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Café escolar");
        var input = new QuotationLineInput("Café torrado", 100m, "pacote", null, null);
        var first = await service.CaptureSampleAsync(project.Id, null, input, [
            Row("c1", "11222333000181", 90m),
            Row("c2", "60701190000104", 100m),
            Row("c3", "33000167000101", 110m)
        ]);
        await service.ConfirmBasketAsync(first, first.Baskets.Single().Key);

        var reopened = new QuotationService(
            new SqliteQuotationRepository(database.Repository.DatabasePath),
            new QuotationAnalyzer(Today));
        var restored = (await reopened.GetAnalysesAsync(project.Id)).Single();
        Assert.True(restored.Line.SelectionConfirmed);

        var updated = await reopened.CaptureSampleAsync(project.Id, restored.Line.Id, input, [
            Row("c1", "11222333000181", 95m),
            Row("c4", "00360305000104", 105m)
        ]);
        Assert.Equal(2, updated.Line.SampleVersion);
        Assert.False(updated.Line.SelectionConfirmed);
        Assert.Equal(4, updated.CollectedCount);
        Assert.Equal(restored.Line.SelectedBasketKey, updated.Line.SelectedBasketKey);
        Assert.NotNull(updated.SelectedBasket);
        Assert.Equal(95m, updated.References.Single(reference => reference.ContractId == "c1").UnitPrice);
    }

    [Fact]
    public async Task PriceRange_LimitsTheSnapshotUsedForComparison()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Faixa posterior");

        var analysis = await service.CaptureSampleAsync(
            project.Id,
            null,
            new QuotationLineInput("Café torrado", 100m, "pacote", 95m, 105m),
            [
                Row("c1", "11222333000181", 90m),
                Row("c2", "60701190000104", 100m),
                Row("c3", "33000167000101", 110m)
            ]);
        var persisted = await repository.GetReferencesAsync(analysis.Line.Id);

        Assert.Equal(1, analysis.CollectedCount);
        Assert.Single(persisted);
        Assert.Equal(0, analysis.RejectedCount);
        Assert.Equal(100m, persisted[0].UnitPrice);
    }

    [Fact]
    public async Task UpdatingWeights_RecalculatesLocallyPersistsAndRequiresReconfirmation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new QuotationService(
            new SqliteQuotationRepository(database.Repository.DatabasePath),
            new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Pesos configuráveis");
        var analysis = await service.CaptureSampleAsync(
            project.Id,
            null,
            new QuotationLineInput("Café", 100m, "pacote", null, null),
            [
                Row("c1", "11222333000181", 90m),
                Row("c2", "60701190000104", 100m),
                Row("c3", "33000167000101", 110m)
            ]);
        await service.ConfirmBasketAsync(analysis, analysis.Baskets.Single().Key);
        var customWeights = new AdequacyWeights(35, 14, 7, 40, 4);

        await service.UpdateWeightsAsync(analysis.Line.Id, customWeights);
        var reopened = Assert.Single(await service.GetAnalysesAsync(project.Id));

        Assert.Equal(customWeights, reopened.Line.Weights);
        Assert.False(reopened.Line.SelectionConfirmed);
        Assert.Equal(analysis.Line.SampleVersion, reopened.Line.SampleVersion);
    }

    [Fact]
    public async Task Backup_IncludesQuotationProjectsSnapshotsAndConfirmedChoice()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var quotation = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await quotation.CreateProjectAsync("Projeto preservado");
        var analysis = await quotation.CaptureSampleAsync(
            project.Id,
            null,
            new QuotationLineInput("Café torrado", 100m, "pacote", null, null),
            [
                Row("c1", "11222333000181", 90m),
                Row("c2", "60701190000104", 100m),
                Row("c3", "33000167000101", 110m)
            ]);
        await quotation.ConfirmBasketAsync(analysis, analysis.Baskets.Single().Key);

        var backupPath = Path.Combine(database.Directory, "quotation-backup.pncpking");
        var backup = new BackupService(database.Repository);
        await backup.ExportAsync(backupPath);
        await quotation.CreateProjectAsync("Projeto posterior");
        await backup.ImportAsync(backupPath);

        var restored = new QuotationService(
            new SqliteQuotationRepository(database.Repository.DatabasePath),
            new QuotationAnalyzer(Today));
        var projects = await restored.GetProjectsAsync();
        var restoredAnalysis = Assert.Single(await restored.GetAnalysesAsync(Assert.Single(projects).Id));
        Assert.Equal("Projeto preservado", projects[0].Name);
        Assert.True(restoredAnalysis.Line.SelectionConfirmed);
        Assert.Equal(3, restoredAnalysis.References.Count);
        Assert.NotNull(restoredAnalysis.SelectedBasket);
    }

    [Fact]
    public async Task AutomationRun_PersistsImportedSearchesAndRecoversInterruptedItems()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(Today));
        var project = await service.CreateProjectAsync("Automática");
        var run = await service.CreateAutomationRunAsync(
            project.Id,
            Path.Combine(database.Directory, "saida.xlsx"),
            SearchGeoFilter.Southeast,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 7, 1),
            [
                new QuotationImportItem(1, "cafe -maquina \"pacote", "Café", 100m, "pacote", 30m, 45m, 2),
                new QuotationImportItem(2, "acucar", "Açúcar", 50m, "kg", null, null, 1)
            ],
            AdequacyWeights.Default);

        var lines = await repository.GetLinesAsync(project.Id);
        Assert.Equal(2, lines.Count);
        Assert.Equal("cafe -maquina \"pacote", lines[0].SearchText);
        Assert.Equal(2, lines[0].RequestedBatchCount);
        Assert.Equal(QuotationAutomationItemState.Pending, lines[0].AutomationState);
        Assert.Equal(run.Id, lines[0].AutomationRunId);
        Assert.True(lines[0].DisplayOrder < lines[1].DisplayOrder);

        await service.UpdateAutomationItemStateAsync(
            lines[0].Id,
            QuotationAutomationItemState.Running,
            "executando");
        await service.UpdateAutomationRunStateAsync(
            run.Id,
            QuotationAutomationRunState.Running,
            "executando");
        await service.RecoverInterruptedAutomationAsync();

        lines = await repository.GetLinesAsync(project.Id);
        Assert.Equal(QuotationAutomationItemState.Pending, lines[0].AutomationState);
        var recovered = await service.GetLatestAutomationRunAsync(project.Id);
        Assert.NotNull(recovered);
        Assert.Equal(QuotationAutomationRunState.Pending, recovered.State);
        Assert.Equal(SearchGeoFilterKind.Southeast, recovered.GeoFilter.Kind);
    }

    [Fact]
    public async Task Workbook_ExportsOneSimpleSheetWithIncisoIiAndPendingObservation()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var project = new QuotationProject(Guid.NewGuid(), "Cotação teste", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var resolvedLine = Line("Café torrado", 100m, "pacote");
        var resolved = analyzer.Analyze(resolvedLine, [
            Reference("a", "c1", "11222333000181", 90m),
            Reference("b", "c2", "60701190000104", 100m),
            Reference("c", "c3", "33000167000101", 110m)
        ]);
        resolved = resolved with
        {
            Line = resolved.Line with
            {
                SelectedBasketKey = resolved.Baskets.Single().Key,
                SelectionConfirmed = true
            }
        };
        var pending = analyzer.Analyze(Line("Açúcar", 10m, "pacote"), []);
        var path = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"), "quote.xlsx");
        try
        {
            await new QuotationWorkbookService().ExportAsync(
                path,
                new QuotationProjectReport(project, [resolved, pending]));

            using var workbook = new XLWorkbook(path);
            var sheet = Assert.Single(workbook.Worksheets);
            Assert.Equal("Cotação", sheet.Name);
            Assert.Equal("Café torrado", sheet.Cell(1, 1).GetString());
            Assert.Equal("11.222.333/0001-81", sheet.Cell(2, 2).GetString());
            Assert.Equal(XLDataType.Text, sheet.Cell(2, 2).DataType);
            Assert.Equal("INCISO II", sheet.Cell(2, 3).GetString());
            Assert.Equal(90m, sheet.Cell(2, 4).GetValue<decimal>());
            Assert.Equal("Açúcar", sheet.Cell(6, 1).GetString());
            Assert.Contains("Não foram encontrados três preços válidos", sheet.Cell(7, 1).GetString());
            Assert.DoesNotContain(sheet.CellsUsed(), cell => cell.GetString() is "Empresa" or "CNPJ" or "Valor");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Workbook_FormatsNumericAndAlphanumericCnpjAndPreservesOtherIdentifiersAsText()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var project = new QuotationProject(Guid.NewGuid(), "CNPJ", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var analysis = analyzer.Analyze(Line("Café", 10m, "pacote"), [
            Reference("numeric", "c1", "03370573000103", 40m),
            Reference("alpha", "c2", "12ABC34501DE35", 41m),
            Reference("other", "c3", "NI-123", 42m)
        ]);
        var path = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"), "cnpj.xlsx");
        try
        {
            await new QuotationWorkbookService().ExportAsync(
                path,
                new QuotationProjectReport(project, [analysis]));

            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("Cotação");
            var taxIds = sheet.Range(2, 2, 4, 2).Cells().Select(cell => cell.GetString()).ToArray();
            Assert.Contains("03.370.573/0001-03", taxIds);
            Assert.Contains("12.ABC.345/01DE-35", taxIds);
            Assert.Contains("NI123", taxIds);
            Assert.All(sheet.Range(2, 2, 4, 2).Cells(), cell => Assert.Equal(XLDataType.Text, cell.DataType));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Workbook_ExportsTwoBestEligibleReferencesAndInsufficiencyObservation()
    {
        var analyzer = new QuotationAnalyzer(Today);
        var project = new QuotationProject(Guid.NewGuid(), "Cotação curta", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var analysis = analyzer.Analyze(Line("Café", 10m, "pacote"), [
            Reference("melhor", "c1", "11222333000181", 40m),
            Reference("segunda", "c2", "60701190000104", 42m)
        ]);
        var path = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"), "short.xlsx");
        try
        {
            await new QuotationWorkbookService().ExportAsync(
                path,
                new QuotationProjectReport(project, [analysis]));

            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("Cotação");
            Assert.Equal("Café", sheet.Cell(1, 1).GetString());
            Assert.Equal("INCISO II", sheet.Cell(2, 3).GetString());
            Assert.Equal("INCISO II", sheet.Cell(3, 3).GetString());
            Assert.Contains("foram encontrados 2", sheet.Cell(4, 1).GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static QuotationLine Line(string description, decimal quantity, string unit) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Description = description,
        RequestedQuantity = quantity,
        RequestedUnit = unit,
        SampleVersion = 1,
        SampledAt = DateTimeOffset.UtcNow
    };

    private static QuotationReference Reference(
        string id,
        string contractId,
        string cnpj,
        decimal price,
        string description = "Café torrado pacote",
        string unit = "pacote",
        decimal? quantity = 100m,
        DateOnly? date = null) => new()
    {
        Id = id,
        LineId = Guid.NewGuid(),
        ContractId = contractId,
        ItemNumber = 1,
        ResultSequence = 1,
        SupplierName = $"Fornecedor {id}",
        SupplierTaxId = cnpj,
        HomologatedQuantity = quantity,
        UnitPrice = price,
        ResultDate = date ?? Today.AddDays(-20),
        ItemDescription = description,
        ItemUnit = unit,
        ItemRequestedQuantity = quantity,
        Organization = "Órgão de Teste",
        Municipality = "Ribeirão Preto",
        Uf = "SP",
        DistanceFromRibeiraoKilometers = 0,
        PublicationDate = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        PortalUrl = "https://pncp.gov.br/app/editais/teste/2026/1"
    };

    private static ItemSearchRow Row(string contractId, string cnpj, decimal price)
    {
        var contract = RepositorySearchTests.Contract(contractId, "Aquisição de café torrado", "SP", 1) with
        {
            Cnpj = "11222333000181",
            Municipality = "Ribeirão Preto",
            MunicipalityIbgeCode = "3543402"
        };
        var item = new ProcurementItem
        {
            ContractId = contractId,
            ItemNumber = 1,
            Description = "Café torrado pacote",
            Unit = "pacote",
            RequestedQuantityScaled = DecimalScale.ToScaled(100m),
            HasResult = true,
            HydrationStatus = ItemHydrationStatus.Complete
        };
        var result = new HomologationResult
        {
            ContractId = contractId,
            ItemNumber = 1,
            ResultSequence = 1,
            SupplierName = $"Fornecedor {contractId}",
            SupplierTaxId = cnpj,
            HomologatedQuantityScaled = DecimalScale.ToScaled(100m),
            HomologatedUnitValueScaled = DecimalScale.ToScaled(price),
            ResultDate = Today.AddDays(-20),
            ResultStatusId = 1,
            ResultStatusName = "Informado"
        };
        return new ItemSearchRow(contract, item, result, ItemSearchPriceState.Homologated, "Preço homologado", true);
    }
}
