using System.Runtime.CompilerServices;
using System.IO.Compression;
using System.Security.Cryptography;
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
        Assert.Equal(3, (await fixture.Repository.GetReferencesAsync(fixture.Line.Id)).Count);
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
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
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath, null,
            allowPriceRangeExpansion: true, allowTechnicalReview: true);
        Assert.Contains("alterado", preview.Items.Single().Message);
        Assert.Equal(0, preview.ImportableCount);
        await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath,
            allowPriceRangeExpansion: true, allowTechnicalReview: true);
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
    public async Task DatabaseFailureAfterCandidateImport_PreservesCandidatesWithoutPartialBasket()
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
        Assert.Equal(3, (await fixture.Repository.GetReferencesAsync(fixture.Line.Id)).Count);
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        Assert.Equal(1, (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!.SampleVersion);
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

    [Fact]
    public async Task AllRetainedCandidates_AreImported_AndCanFeedAlternativeBaskets()
    {
        await using var fixture = await Fixture.CreateAsync(referenceCount: 8);
        fixture.CompleteGuide();
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(8, preview.Items.Single().CandidateCount);
        Assert.Equal(1, preview.ImportableCount);
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        var applied = await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(8, applied.ImportedCandidateCount);
        var references = await fixture.Repository.GetReferencesAsync(fixture.Line.Id);
        var manual = Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        Assert.Equal(3, manual.ReferenceIds.Count);
        Assert.Equal(fixture.References.Select(value => value.Id).Order(), references.Select(value => value.Id).Order());
        var current = (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!;
        var analysis = new PNCPKing.Core.Quotations.QuotationAnalyzer().Analyze(current, references, [manual]);
        Assert.Contains(analysis.Baskets, value => value.IsCheapest);
        Assert.Contains(analysis.Baskets, value => value.IsMostExpensive);
        Assert.True(analysis.Baskets.Count > 2);
        var repeated = await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(0, repeated.ImportedCandidateCount);
        Assert.Equal(1, repeated.UnchangedCount);
    }

    [Fact]
    public async Task InsufficientGuide_StillImportsCandidates_WithoutInventingGuidedBasket()
    {
        await using var fixture = await Fixture.CreateAsync(referenceCount: 8);
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
        {
            workbook.Worksheet("Itens").Cell(2, 3).Value = "insuficiente";
            workbook.Worksheet("Itens").Cell(2, 5).Value = "A equivalência técnica exige revisão humana.";
            workbook.Save();
        }
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(0, preview.ReadyCount);
        Assert.Equal(1, preview.ImportableCount);
        var applied = await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(8, applied.ImportedCandidateCount);
        Assert.Equal(1, applied.PendingCount);
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task LegacyGuide_CanExpandOnlyToSelectedPrices_WithoutChangingSourceHashes()
    {
        await using var fixture = await Fixture.CreateAsync(minimum: 0.5m, maximum: 1m);
        fixture.CompleteGuide();
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
        { workbook.Worksheet("Faixas").Delete(); workbook.Save(); }
        var sourceBytes = await File.ReadAllBytesAsync(fixture.PackagePath);
        Assert.Equal(0, (await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).ReadyCount);
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath, null,
            allowPriceRangeExpansion: true);
        Assert.Equal(1, preview.ReadyCount);
        var adjustment = Assert.IsType<QuotationAiPriceRangeAdjustment>(preview.Items.Single().PriceRangeAdjustment);
        Assert.Equal(0.5m, adjustment.MinimumUnitPrice);
        Assert.Equal(1.03m, adjustment.MaximumUnitPrice);
        Assert.Contains("Faixa original", preview.Items.Single().Message);
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath,
            allowPriceRangeExpansion: true)).AppliedCount);
        var current = (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!;
        Assert.Equal(1.03m, current.MaximumUnitPrice);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(fixture.PackagePath));
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
            Assert.Equal(fixture.Manifest.Items.Single().SpecificationHash, workbook.Worksheet("Itens").Cell(2, 2).GetString());
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath,
            allowPriceRangeExpansion: true)).UnchangedCount);
    }

    [Fact]
    public async Task ExplicitRangeProposal_IsAppliedAtomicallyWithValidBasket_AndKeepsBlankOriginalBound()
    {
        await using var fixture = await Fixture.CreateAsync(minimum: 0.5m, maximum: 1m);
        fixture.CompleteGuide();
        AddRange(fixture, null, 1.1m);
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(1, preview.ReadyCount);
        Assert.Equal(0.5m, preview.Items.Single().PriceRangeAdjustment!.MinimumUnitPrice);
        Assert.Equal(1m, (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!.MaximumUnitPrice);
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).AppliedCount);
        Assert.Equal(1.1m, (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!.MaximumUnitPrice);
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).UnchangedCount);
    }

    [Fact]
    public async Task ExplicitRangeCannotNarrowTheOriginal_AndDoesNotChangeBoundsWithoutValidBasket()
    {
        await using var fixture = await Fixture.CreateAsync(minimum: 0.5m, maximum: 1m);
        fixture.CompleteGuide();
        AddRange(fixture, 0.7m, 1.1m);
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(0, preview.ReadyCount);
        Assert.Contains("somente ampliar", preview.Items.Single().Message);
        var applied = await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(0, applied.AppliedCount);
        Assert.Equal(3, applied.ImportedCandidateCount);
        var current = (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!;
        Assert.Equal(0.5m, current.MinimumUnitPrice);
        Assert.Equal(1m, current.MaximumUnitPrice);
    }

    [Fact]
    public async Task RangeExpansion_DoesNotBypassDispersionRule()
    {
        await using var fixture = await Fixture.CreateAsync(minimum: 0.5m, maximum: 1m, outlier: true);
        fixture.CompleteGuide();
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath, null,
            allowPriceRangeExpansion: true);
        Assert.Equal(0, preview.ReadyCount);
        Assert.Contains("25%", preview.Items.Single().Message);
        var applied = await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath,
            allowPriceRangeExpansion: true);
        Assert.Equal(0, applied.AppliedCount);
        Assert.Equal(1m, (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!.MaximumUnitPrice);
    }

    [Fact]
    public async Task ConcurrentManualRangeChange_IsNotOverwrittenByAutomaticExpansion()
    {
        await using var fixture = await Fixture.CreateAsync(minimum: 0.5m, maximum: 1m);
        fixture.CompleteGuide();
        await fixture.Repository.SaveSampleAsync(fixture.Line.ProjectId, fixture.Line.Id,
            new(fixture.Line.Description, fixture.Line.RequestedQuantity, fixture.Line.RequestedUnit, 0.4m, 1.2m), []);
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath, null,
            allowPriceRangeExpansion: true);
        Assert.Equal(0, preview.ImportableCount);
        Assert.Equal(0, preview.ReadyCount);
        await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath, allowPriceRangeExpansion: true);
        var current = (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!;
        Assert.Equal(0.4m, current.MinimumUnitPrice);
        Assert.Equal(1.2m, current.MaximumUnitPrice);
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task ConvertedGuide_UsesEffectivePriceRange_AndPreservesSourcePrices()
    {
        await using var fixture = await Fixture.CreateAsync(packagingUnits: 12, minimum: 9m, maximum: 11m);
        fixture.CompleteGuide();
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
        {
            var selections = workbook.Worksheet("Selecoes");
            for (var index = 0; index < 3; index++)
            {
                selections.Cell(index + 2, 6).Value = "Pacote com 12 unidades; divisão exata pela quantidade.";
                selections.Cell(index + 2, 7).Value = fixture.References[index].ItemAdditionalInformation;
                selections.Cell(index + 2, 10).Value = 12;
            }
            workbook.Save();
        }
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(1, preview.ReadyCount);
        Assert.Null(preview.Items.Single().PriceRangeAdjustment);
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).AppliedCount);
        var references = await fixture.Repository.GetReferencesAsync(fixture.Line.Id);
        Assert.All(references, value => Assert.True(value.UnitPrice >= 120m));
        Assert.Equal(11m, (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!.MaximumUnitPrice);
    }

    [Fact]
    public async Task CorruptCandidatePart_DoesNotImportEvenForPendingItem()
    {
        await using var fixture = await Fixture.CreateAsync();
        using (var archive = ZipFile.Open(fixture.PackagePath, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(fixture.Manifest.Parts[0].Path)!;
            using var input = entry.Open();
            input.Position = input.Length - 2;
            input.WriteByte((byte)'x');
        }
        var templates = await fixture.Service.CreateTemplatesAsync(fixture.PackagePath, fixture.Database.Directory);
        using (var workbook = new XLWorkbook(templates.GuidePath))
        { workbook.Worksheet("Itens").Cell(2, 5).Value = "Análise pendente."; workbook.Save(); }
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, templates.GuidePath);
        Assert.Equal(0, preview.ImportableCount);
        await fixture.Service.ApplyGuideAsync(fixture.PackagePath, templates.GuidePath);
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task RangeWithUnsupportedPrecision_IsRejectedBeforeImport()
    {
        await using var fixture = await Fixture.CreateAsync(minimum: 0.5m, maximum: 1m);
        fixture.CompleteGuide();
        AddRange(fixture, null, 1.10001m);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath));
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task ShortTechnicalDescription_RequiresExplicitReview_AndKeepsAutomaticFilter()
    {
        await using var fixture = await Fixture.CreateAsync(shortDescription: true);
        fixture.CompleteGuide();
        Assert.Equal(0, (await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath)).ReadyCount);
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath, null,
            allowTechnicalReview: true);
        Assert.Equal(1, preview.ReadyCount);
        Assert.Equal(3, preview.Items.Single().TechnicalReviewCount);
        Assert.Contains("baixo índice lexical", preview.Items.Single().Message);
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath,
            allowTechnicalReview: true)).AppliedCount);
        var references = await fixture.Repository.GetReferencesAsync(fixture.Line.Id);
        Assert.All(references, reference => Assert.NotNull(reference.TechnicalReview));
        var current = (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!;
        var basket = Assert.Single(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        var analysis = new PNCPKing.Core.Quotations.QuotationAnalyzer().Analyze(current, references, [basket]);
        Assert.True(analysis.Baskets.Single(value => value.ManualBasketId == basket.Id).IsValid);
        Assert.All(analysis.References, reference => Assert.Equal(QuotationReferenceState.Rejected, reference.State));
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath,
            allowTechnicalReview: true)).UnchangedCount);
    }

    [Fact]
    public async Task TechnicalReview_DoesNotOverrideClearlyDifferentMeasure()
    {
        await using var fixture = await Fixture.CreateAsync(shortDescription: true, conflictingMeasure: true);
        fixture.CompleteGuide();
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath, null,
            allowTechnicalReview: true);
        Assert.Equal(0, preview.ReadyCount);
        Assert.Equal(0, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath,
            allowTechnicalReview: true)).AppliedCount);
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task TechnicalReview_StillRequiresValidPriceRangeAndLiteralEvidence()
    {
        await using var fixture = await Fixture.CreateAsync(shortDescription: true, minimum: 0.5m, maximum: 1m);
        fixture.CompleteGuide();
        Assert.Equal(0, (await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath, null,
            allowTechnicalReview: true)).ReadyCount);
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
        { workbook.Worksheet("Selecoes").Cell(2, 7).Value = "Texto inventado pela IA"; workbook.Save(); }
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath, null,
            allowPriceRangeExpansion: true, allowTechnicalReview: true);
        Assert.Equal(0, preview.ReadyCount);
        Assert.Contains("trecho", preview.Items.Single().Message);
    }

    [Theory]
    [InlineData("Faixas")]
    [InlineData("Selecoes")]
    public async Task GuideEditedAfterPreview_CannotApplyUnshownSelectionsOrRange(string editedSheet)
    {
        await using var fixture = await Fixture.CreateAsync(referenceCount: 8, minimum: 0.5m, maximum: 1m);
        fixture.CompleteGuide();
        AddRange(fixture, null, 1.1m);
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.Equal(1, preview.ReadyCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.Templates.GuidePath))),
            preview.GuideSha256);
        using (var workbook = new XLWorkbook(fixture.Templates.GuidePath))
        {
            if (editedSheet == "Faixas") workbook.Worksheet("Faixas").Cell(2, 3).Value = 2m;
            else workbook.Worksheet("Selecoes").Cell(2, 2).Value = fixture.References[3].Id;
            workbook.Save();
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ApplyGuideAsync(
            fixture.PackagePath, fixture.Templates.GuidePath, expectedGuideSha256: preview.GuideSha256));
        Assert.Contains("depois da prévia", error.Message);
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
        Assert.Equal(1m, (await fixture.Repository.GetLineAsync(fixture.Line.ProjectId, fixture.Line.Id))!.MaximumUnitPrice);
        var refreshed = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        Assert.NotEqual(preview.GuideSha256, refreshed.GuideSha256);
        Assert.Equal(1, refreshed.ReadyCount);
        Assert.Equal(1, (await fixture.Service.ApplyGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath,
            expectedGuideSha256: refreshed.GuideSha256)).AppliedCount);
    }

    [Fact]
    public async Task GuideBeingWritten_CannotBeArchivedOrApplied()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        await using (var writer = new FileStream(fixture.Templates.GuidePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.Service.ApplyGuideAsync(fixture.PackagePath,
                fixture.Templates.GuidePath, expectedGuideSha256: preview.GuideSha256));
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
    }

    [Fact]
    public async Task ArchivedGuideName_DoesNotSubstituteForItsContentHash()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CompleteGuide();
        var preview = await fixture.Service.PreviewGuideAsync(fixture.PackagePath, fixture.Templates.GuidePath);
        var directory = fixture.PackagePath + ".guias";
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, preview.GuideSha256 + ".xlsx"), "conteúdo alterado");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ApplyGuideAsync(fixture.PackagePath,
            fixture.Templates.GuidePath, expectedGuideSha256: preview.GuideSha256));
        Assert.Empty(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Empty(await fixture.Repository.GetManualBasketsAsync(fixture.Line.Id));
    }

    private static void AddRange(Fixture fixture, decimal? minimum, decimal? maximum)
    {
        using var workbook = new XLWorkbook(fixture.Templates.GuidePath);
        var ranges = workbook.Worksheet("Faixas");
        ranges.Cell(2, 1).Value = fixture.Line.Id.ToString("N");
        if (minimum is { } min) ranges.Cell(2, 2).Value = min;
        if (maximum is { } max) ranges.Cell(2, 3).Value = max;
        ranges.Cell(2, 4).Value = "Os preços mais próximos tecnicamente ultrapassam a estimativa inicial.";
        workbook.Save();
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

        public static async Task<Fixture> CreateAsync(bool sameContract = false, bool missingTaxId = false,
            int? packagingUnits = null, int referenceCount = 3, decimal? minimum = null, decimal? maximum = null,
            bool outlier = false, bool shortDescription = false, bool conflictingMeasure = false)
        {
            var database = await TestDatabase.CreateAsync();
            var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
            var project = await repository.CreateProjectAsync("IA externa");
            var line = await repository.CreateLineAsync(project.Id,
                new(shortDescription
                    ? "Copo descartável de polipropileno 180 ml, transparente, atóxico, resistente, reforçado, liso, íntegro, uniforme, limpo, inodoro, apropriado para bebidas e acondicionado em embalagem protegida"
                    : "Copo descartável de polipropileno 180 ml", 100, "unidade", minimum, maximum));
            var references = Enumerable.Range(1, referenceCount).Select(index => new QuotationReference
            {
                Id = $"{(sameContract ? "contrato" : "contrato" + index)}|{index}|1", LineId = line.Id,
                ContractId = sameContract ? "contrato" : "contrato" + index, ItemNumber = index, ResultSequence = 1,
                ItemDescription = shortDescription ? (conflictingMeasure ? "Copo PP 50 ml" : "Copo PP 180 ml") : line.Description,
                ItemUnit = packagingUnits is null ? "unidade" : "pacote",
                ItemAdditionalInformation = packagingUnits is null ? string.Empty : $"Pacote com {packagingUnits} unidades.",
                UnitPrice = outlier && index == 3 ? 100m : packagingUnits is null ? 1m + index / 100m : 120m + index - 1,
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
            for (var index = 0; index < Math.Min(3, References.Length); index++)
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
