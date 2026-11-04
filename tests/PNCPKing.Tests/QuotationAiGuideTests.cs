using System.Runtime.CompilerServices;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationAiGuideTests
{
    [Fact]
    public async Task SparseTriage_RemovesOnlyExplicitRejections_AndPreservesOriginal()
    {
        await using var fixture = await Fixture.CreateAsync();
        using (var workbook = new XLWorkbook(fixture.Templates.TriagePath))
        {
            var sheet = workbook.Worksheet("Triagem");
            AddTriage(sheet, 2, fixture.Line.Id, fixture.References[0].Id, "rejeitar");
            AddTriage(sheet, 3, fixture.Line.Id, fixture.References[1].Id, "revisar");
            workbook.Save();
        }
        var output = Path.Combine(fixture.Database.Directory, "filtrado.zip");
        var manifest = await fixture.Service.FilterAsync(fixture.PackagePath, fixture.Templates.TriagePath, output);
        var kept = new List<QuotationReference>();
        await foreach (var reference in QuotationAiPackage.ReadItemReferencesAsync(output, manifest, fixture.Line.Id)) kept.Add(reference);
        Assert.Equal(fixture.References.Skip(1).Select(reference => reference.Id), kept.Select(reference => reference.Id));
        Assert.Equal(fixture.Manifest.PackageId, manifest.ParentPackageId);
        Assert.Equal(1, manifest.TriageRejectedCount);
        Assert.Equal(1, manifest.TriageReviewedCount);
        Assert.Equal(3, (await QuotationAiPackage.ReadManifestAsync(fixture.PackagePath)).Items.Single().CandidateCount);
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task UnknownTriageReference_DoesNotPublishFilteredPackage()
    {
        await using var fixture = await Fixture.CreateAsync();
        using (var workbook = new XLWorkbook(fixture.Templates.TriagePath))
        {
            AddTriage(workbook.Worksheet("Triagem"), 2, fixture.Line.Id, "unknown", "rejeitar");
            workbook.Save();
        }
        var output = Path.Combine(fixture.Database.Directory, "filtrado.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.FilterAsync(fixture.PackagePath, fixture.Templates.TriagePath, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Guide_ResolvesSourcePrices_AppliesAtomically_AndIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(1, preview.ReadyCount);
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        var first = await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(1, first.AppliedCount);
        var line = await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id);
        Assert.True(line!.SelectionConfirmed);
        var version = line.SampleVersion;
        var repeated = await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(1, repeated.UnchangedCount);
        Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        Assert.Equal(3, (await fixture.Repository.GetReferencesAsync(fixture.Line.Id)).Count);
        Assert.Equal(version, (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!.SampleVersion);
        Assert.Single(Directory.GetFiles(fixture.PackagePath + ".guias", "*.xlsx"));
        Assert.Equal(2, Directory.GetFiles(fixture.PackagePath + ".guias", "*.resultado.json").Length);
        Assert.Equal(33, SqliteContractRepository.CurrentSchemaVersion);
    }

    [Fact]
    public async Task SameCompanyAndContract_IsReview_EvenWithDifferentReferenceIds()
    {
        await using var fixture = await Fixture.CreateAsync(sameContract: true);
        fixture.CompleteGuide();
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(0, preview.ReadyCount);
        Assert.Contains("mesma empresa", preview.Items.Single().Message);
        Assert.Equal(0, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).AppliedCount);
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task MissingTaxId_IsReview_NotAssumedIndependent()
    {
        await using var fixture = await Fixture.CreateAsync(missingTaxId: true);
        fixture.CompleteGuide();
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(0, preview.ReadyCount);
        Assert.Contains("CNPJ", preview.Items.Single().Message);
    }

    [Fact]
    public async Task UnknownSelection_AndChangedSpecification_DoNotWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
        { workbook.Worksheet("Selecoes").Cell(2, 2).Value = "unknown"; workbook.Save(); }
        Assert.Equal(0, (await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).ReadyCount);
        fixture.CompleteGuide();
        await fixture.Repository.UpdateLineRequestedDetailsAsync(fixture.Line.Id, 101, "unidade");
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Contains("alterado", preview.Items.Single().Message);
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task RoundedConversion_IsReview_WithoutSilentlyChangingOriginalPrices()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
        {
            var sheet = workbook.Worksheet("Selecoes");
            sheet.Cell(2, 4).Value = 0.083333m;
            sheet.Cell(2, 6).Value = "Uma embalagem de doze unidades.";
            sheet.Cell(2, 8).Value = 1;
            sheet.Cell(2, 9).Value = 12;
            workbook.Save();
        }
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(0, preview.ReadyCount);
        Assert.Contains("exatamente", preview.Items.Single().Message);
    }

    [Fact]
    public async Task FormulaOrWrongPackageHash_IsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
        { workbook.Worksheet("Controle").Cell(5, 2).Value = "bad"; workbook.Save(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath));
        var clean = await fixture.Service.CreateTemplatesAsync(fixture.PackagePath, fixture.Database.Directory);
        using (var workbook = new XLWorkbook(clean.GuidePath))
        { workbook.Worksheet("Itens").Cell(2, 5).FormulaA1 = "1+1"; workbook.Save(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.PreviewGuideAsync(fixture.PackagePath, clean.GuidePath));
    }

    [Fact]
    public async Task RegeneratingTemplates_PreservesAlreadyFilledWorkbooks()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        var old = await File.ReadAllBytesAsync(fixture.Templates.GuidePath);
        var newTemplates = await fixture.Service.CreateTemplatesAsync(fixture.PackagePath, fixture.Database.Directory);
        Assert.NotEqual(fixture.Templates.GuidePath, newTemplates.GuidePath);
        Assert.Equal(old, await File.ReadAllBytesAsync(fixture.Templates.GuidePath));
    }

    [Fact]
    public async Task EditedGuidedBasket_IsPreservedInsteadOfOverwritten()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        var basket = Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        await fixture.Repository.RenameManualBasketAsync(basket.Id, "Minha revisão");
        var repeated = await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(1, repeated.PendingCount);
        Assert.Equal("Minha revisão", Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id)).Name);
    }

    [Fact]
    public async Task ExactDivisor_IsPersisted_WithoutChangingSourcePrices_AndReimportsOnce()
    {
        await using var fixture = await Fixture.CreateAsync(packagingUnits: 12);
        fixture.CompleteGuide();
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
        {
            var sheet = workbook.Worksheet("Selecoes");
            for (var index = 0; index < fixture.References.Length; index++)
            {
                sheet.Cell(index + 2, 6).Value = "Pacote com 12 unidades, conforme informação complementar.";
                sheet.Cell(index + 2, 7).Value = fixture.References[index].ItemAdditionalInformation;
                sheet.Cell(index + 2, 10).Value = 12;
            }
            workbook.Save();
        }
        Assert.Equal(1, (await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).ReadyCount);
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).AppliedCount);
        var basket = Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        Assert.Equal(3, basket.PriceDivisors.Count);
        Assert.All(basket.PriceDivisors.Values, value => Assert.Equal(12m, value));
        var references = await fixture.Repository.GetReferencesAsync(fixture.Line.Id);
        foreach (var reference in references)
            Assert.Equal(fixture.References.Single(original => original.Id == reference.Id).UnitPrice, reference.UnitPrice);
        var currentLine = (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!;
        var analysis = new PNCPKing.Core.Quotations.QuotationAnalyzer().Analyze(currentLine, references, [basket]);
        var appliedBasket = analysis.Baskets.Single(value => value.ManualBasketId == basket.Id);
        Assert.True(appliedBasket.IsValid);
        Assert.Equal(10m, appliedBasket.PriceEntries.First().EffectiveUnitPrice);
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).UnchangedCount);
    }

    [Fact]
    public async Task EditedGuidedDivisor_IsDetectedAndPreservedOnReimport()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        var basket = Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        await fixture.Repository.SetManualBasketPriceDivisorAsync(basket.Id, fixture.References[0].Id, 12m);
        var repeated = await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(1, repeated.PendingCount);
        Assert.Equal(0, repeated.UnchangedCount);
        Assert.Equal(12m, Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id))
            .PriceDivisors[fixture.References[0].Id]);
    }

    [Fact]
    public async Task GuideCannotCombineFactorWithDivisor()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
        {
            workbook.Worksheet("Selecoes").Cell(2, 4).Value = 0.5m;
            workbook.Worksheet("Selecoes").Cell(2, 10).Value = 12m;
            workbook.Save();
        }
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(0, preview.ReadyCount);
        Assert.Contains("não combine", preview.Items.Single().Message);
    }

    [Fact]
    public async Task DatabaseFailureAfterReferenceInsertion_RollsBackWholeItem()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        await using var connection = new SqliteConnection($"Data Source={fixture.Database.Repository.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_ai_basket BEFORE INSERT ON quotation_manual_baskets BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath));
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        Assert.Equal(0, (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!.SampleVersion);
        await using (var command = connection.CreateCommand())
        { command.CommandText = "DROP TRIGGER fail_ai_basket;"; await command.ExecuteNonQueryAsync(); }
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).AppliedCount);
    }

    [Fact]
    public async Task ExistingManualSelection_IsPreservedWhenGuidedBasketAddsReferences()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        var previousReferences = fixture.References.Select(reference => reference with { Id = "manual-" + reference.Id }).ToArray();
        await fixture.Repository.SaveSampleAsync(fixture.Line.ProjectId, fixture.Line.Id,
            new(fixture.Line.Description, fixture.Line.RequestedQuantity, fixture.Line.RequestedUnit, null, null), previousReferences);
        var previous = await fixture.Repository.SaveManualBasketAsync(fixture.Line.Id, null, "Minha cesta",
            previousReferences.Select(reference => reference.Id).ToArray());
        await fixture.Repository.ConfirmBasketAsync(fixture.Line.Id, previous.Key);
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).AppliedCount);
        var current = await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id);
        Assert.Equal(previous.Key, current!.SelectedBasketKey);
        Assert.True(current.SelectionConfirmed);
        Assert.Equal(2, (await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id)).Count);
        Assert.Equal(6, (await fixture.Repository.GetReferencesAsync(fixture.Line.Id)).Count);
    }

    private static void AddTriage(IXLWorksheet sheet, int row, Guid item, string reference, string decision)
    {
        sheet.Cell(row, 1).Value = item.ToString("N"); sheet.Cell(row, 2).Value = reference;
        sheet.Cell(row, 3).Value = decision; sheet.Cell(row, 4).Value = "Material divergente ou informação a conferir.";
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required SqliteQuotationRepository Repository { get; init; }
        public required QuotationAiGuideService Service { get; init; }
        public required QuotationLine Line { get; init; }
        public required QuotationReference[] References { get; init; }
        public required QuotationAiPackageManifest Manifest { get; init; }
        public required string PackagePath { get; init; }
        public required QuotationAiTemplatePaths Templates { get; init; }

        public static async Task<Fixture> CreateAsync(bool sameContract = false, bool missingTaxId = false, int? packagingUnits = null)
        {
            var database = await TestDatabase.CreateAsync();
            var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
            var project = await repository.CreateProjectAsync("IA externa");
            var line = await repository.CreateLineAsync(project.Id,
                new("Copo descartável de polipropileno 180 ml", 100, "unidade", null, null));
            var references = Enumerable.Range(1, 3).Select(index => new QuotationReference
            {
                Id = $"{(sameContract ? "contrato" : "contrato" + index)}|{index}|1", LineId = line.Id,
                ContractId = sameContract ? "contrato" : "contrato" + index, ItemNumber = index, ResultSequence = 1,
                ItemDescription = line.Description, ItemUnit = packagingUnits is null ? "unidade" : "pacote",
                ItemAdditionalInformation = packagingUnits is null ? string.Empty : $"Pacote com {packagingUnits} unidades.",
                UnitPrice = packagingUnits is null ? 1m + index / 100m : 120m + index - 1,
                SupplierName = "Fornecedor de teste", SupplierTaxId = missingTaxId && index == 1 ? "" : "11222333000181",
                HomologatedQuantity = 100, ResultDate = DateOnly.FromDateTime(DateTime.Today), Municipality = "Ribeirão Preto", Uf = "SP"
            }).ToArray();
            var manifest = new QuotationAiPackageManifest
            {
                PackageId = Guid.NewGuid(), ProjectId = project.Id, RunId = Guid.NewGuid(),
                Items = [new() { Line = line, SpecificationHash = QuotationAiPackage.SpecificationHash(line),
                    Criteria = ["copo"], Complete = true }]
            };
            async IAsyncEnumerable<QuotationReference> Read(Guid id, [EnumeratorCancellation] CancellationToken token)
            { foreach (var reference in references) { token.ThrowIfCancellationRequested(); yield return reference; } await Task.CompletedTask; }
            var path = Path.Combine(database.Directory, "resultado.zip");
            manifest = await QuotationAiPackage.WriteAsync(path, manifest, Read);
            var service = new QuotationAiGuideService(repository);
            return new() { Database = database, Repository = repository, Service = service, Line = line,
                References = references, Manifest = manifest, PackagePath = path,
                Templates = await service.CreateTemplatesAsync(path, database.Directory) };
        }

        public void CompleteGuide()
        {
            using var workbook = new XLWorkbook(Templates.GuidePath);
            var items = workbook.Worksheet("Itens");
            items.Cell(2, 3).Value = "pronto";
            items.Cell(2, 5).Value = "Os candidatos correspondem ao material, à capacidade e à unidade solicitados.";
            var selections = workbook.Worksheet("Selecoes");
            for (var index = 0; index < References.Length; index++)
            {
                var row = index + 2;
                selections.Cell(row, 1).Value = Line.Id.ToString("N");
                selections.Cell(row, 2).Value = References[index].Id;
                selections.Cell(row, 3).Value = index + 1;
                selections.Cell(row, 4).Value = 1;
                selections.Cell(row, 5).Value = "Mesmo material e capacidade, unidade idêntica.";
                selections.Cell(row, 7).Value = References[index].ItemDescription;
            }
            workbook.Save();
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
