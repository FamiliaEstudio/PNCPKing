using Microsoft.Data.Sqlite;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationDuplicatePriceTests
{
    private const string Org = "11222333000181";
    private const string OtherOrg = "08142655000106";
    private const string Supplier = "08236940000196";
    private const string Control = Org + "-1-000123/2026";

    private static QuotationReference Price(Guid line, string id, decimal value = 12m) => new()
    {
        Id = id, LineId = line, ContractId = Control, ItemNumber = 1, ResultSequence = 1,
        SupplierName = "Empresa homônima", SupplierTaxId = Supplier, SupplierType = "PJ",
        UnitPrice = value, ItemDescription = "Café", ItemUnit = "kg", ResultDate = new(2025, 10, 1)
    };

    private static readonly QuotationLineInput Input = new("Café", 10m, "kg", null, null);

    private static HashSet<(Guid LineId, string BasketKey)> AcceptWarnings(IReadOnlyList<QuotationDuplicatePrice> preview) =>
        preview.Where(row => row.Impact.NeedsAcceptance).Select(row => (row.LineId, row.BasketKey)).ToHashSet();

    [Fact]
    public async Task PreviewUsesReliableIdentityAndOnlyComparesWithinEachBasket()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Análise sintética");
        var line = Guid.NewGuid();
        var first = Price(line, "z-primeira", 24) with { SupplierTaxId = "08.236.940/0001-96" };
        var later = Price(line, "a-segunda", 3) with { ResultDate = new(2026, 11, 1), ItemNumber = 2, ResultSequence = 3 };
        var byUrl = Price(line, "via-portal", 20) with { ContractId = "123/2026", PortalUrl = $"https://pncp.gov.br/app/editais/{Org}/2026/123" };
        var otherProcess = Price(line, "outra-compra", 11) with { ContractId = Org + "-1-000124/2026" };
        var otherSupplier = Price(line, "homônimo", 13) with { SupplierTaxId = "11.222.333/0001-81" };
        var differentOrg = Price(line, "outro-órgão") with { ContractId = OtherOrg + "-1-000123/2026" };
        var differentYear = Price(line, "outro-ano") with { ContractId = Org + "-1-000123/2025" };
        var missingSupplier = Price(line, "sem-cnpj") with { SupplierTaxId = "" };
        var invalidSupplier = Price(line, "cnpj-inválido") with { SupplierTaxId = "08236940000197" };
        var missingProcess = Price(line, "número-solto") with { ContractId = "123/2026" };
        var foreignUrl = missingProcess with { Id = "url-externa", PortalUrl = $"https://example.test/app/editais/{Org}/2026/123" };
        var conflicting = Price(line, "contraditório") with { PortalUrl = $"https://pncp.gov.br/app/editais/{OtherOrg}/2026/123" };
        var deepConflict = Price(line, "oficial-profundo") with { PortalUrl = $"https://pncp.gov.br/app/EDITAIS/{OtherOrg}/2026/123/arquivos" };
        var httpConflict = Price(line, "oficial-http-conflitante") with { PortalUrl = $"http://pncp.gov.br/app/editais/{OtherOrg}/2026/123" };
        var apiConflict = Price(line, "api") with { PortalUrl = $"https://pncp.gov.br/api/pncp/v1/orgaos/{OtherOrg}/compras/2026/123/arquivos/1" };
        var keyConflict = Price(line, $"{OtherOrg}-1-000123/2026|2|1");
        var internet = Price(line, "internet") with { Source = QuotationReferenceSource.InternetIncisoIII };
        QuotationReference[] prices = [first, later, byUrl, otherProcess, otherSupplier, differentOrg, differentYear,
            missingSupplier, missingSupplier with { Id = "sem-cnpj-2" }, invalidSupplier, invalidSupplier with { Id = "inválido-2" },
            missingProcess, missingProcess with { Id = "número-solto-2" }, foreignUrl, conflicting, deepConflict, httpConflict, apiConflict, keyConflict, internet];
        await repo.SaveSampleAsync(project.Id, line, Input, prices);
        var a = await repo.SaveManualBasketAsync(line, null, "A", [first.Id, later.Id, byUrl.Id, otherProcess.Id, otherSupplier.Id]);
        await repo.SetManualBasketPriceDivisorAsync(a.Id, first.Id, 2m);
        await repo.SetManualBasketConversionFactorAsync(a.Id, later.Id, 4m);
        await repo.SetManualBasketAggregationMethodAsync(a.Id, QuotationAggregationMethod.Median);
        var b = await repo.SaveManualBasketAsync(line, null, "B", [later.Id, first.Id]);
        var separateIds = prices.Skip(3).Select(p => p.Id).Append(first.Id).ToArray();
        var separate = await repo.SaveManualBasketAsync(line, null, "Separada", separateIds);
        await repo.ConfirmBasketAsync(line, a.Key);
        var outside = await repo.CreateProjectAsync("Outra cotação");
        var otherLine = Guid.NewGuid();
        await repo.SaveSampleAsync(outside.Id, otherLine, Input, [first with { LineId = otherLine }, later with { LineId = otherLine }]);
        var outsideBasket = await repo.SaveManualBasketAsync(otherLine, null, "A", [first.Id, later.Id]);

        var before = await repo.GetManualBasketsAsync(line);
        var raw = await repo.GetReferencesAsync(line);
        var preview = await service.FindDuplicatePricesAsync(project.Id);
        Assert.Equal(3, preview.Count);
        Assert.Equal(new[] { later.Id, byUrl.Id }, preview.Where(row => row.BasketKey == a.Key).Select(row => row.Removed.ReferenceId));
        Assert.All(preview.Where(row => row.BasketKey == a.Key), row => Assert.Equal(first.Id, row.Kept.ReferenceId));
        var reverse = Assert.Single(preview, row => row.BasketKey == b.Key);
        Assert.Equal((later.Id, first.Id), (reverse.Kept.ReferenceId, reverse.Removed.ReferenceId));
        Assert.All(preview, row => Assert.Equal((Supplier, Control), (row.SupplierCnpj, row.PurchaseId)));
        Assert.Equal(before.Select(basket => basket.UpdatedAt), (await repo.GetManualBasketsAsync(line)).Select(basket => basket.UpdatedAt));
        Assert.True((await repo.GetLineAsync(project.Id, line))!.SelectionConfirmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyDuplicatePriceReviewAsync(outside.Id, preview, AcceptWarnings(preview)));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyDuplicatePriceReviewAsync(project.Id, preview, AcceptWarnings(preview), cancel.Token));
        Assert.Equal(preview, await service.FindDuplicatePricesAsync(project.Id));

        await service.ApplyDuplicatePriceReviewAsync(project.Id, preview, AcceptWarnings(preview));
        var reopened = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var reopenedService = new QuotationService(reopened, new QuotationAnalyzer());
        var baskets = await reopened.GetManualBasketsAsync(line);
        Assert.Equal(3, baskets.Count);
        var cleaned = baskets.Single(x => x.Id == a.Id);
        Assert.Equal(new[] { first.Id, otherProcess.Id, otherSupplier.Id }, cleaned.ReferenceIds);
        Assert.Equal(2m, cleaned.PriceDivisors[first.Id]);
        Assert.Equal(QuotationAggregationMethod.Median, cleaned.AggregationMethod);
        Assert.Equal(new[] { later.Id }, baskets.Single(x => x.Id == b.Id).ReferenceIds);
        Assert.Equal(separateIds, baskets.Single(x => x.Id == separate.Id).ReferenceIds);
        Assert.Equal(separate.UpdatedAt, baskets.Single(x => x.Id == separate.Id).UpdatedAt);
        Assert.Equal(outsideBasket.ReferenceIds, (await reopened.GetManualBasketsAsync(otherLine)).Single().ReferenceIds);
        Assert.Equal(raw, await reopened.GetReferencesAsync(line));
        var analysis = (await reopenedService.GetReportAsync(project.Id)).Lines.Single();
        Assert.Equal(a.Key, analysis.Line.SelectedBasketKey);
        Assert.True(analysis.Line.SelectionConfirmed);
        Assert.Equal(12m, analysis.SelectedBasket!.AdoptedPrice);
        Assert.Equal(24m, analysis.SelectedBasket.PriceEntries.Single(p => p.Reference.Id == first.Id).Reference.UnitPrice);
        Assert.Empty(await reopenedService.FindDuplicatePricesAsync(project.Id));
        Assert.Single(await reopenedService.FindDuplicatePricesAsync(outside.Id));
    }

    [Theory]
    [InlineData(false, "a|2", "z|1")]
    [InlineData(true, "z|1", "a|2")]
    public async Task ConfirmedAutomaticAndManualSurviveReopenWithoutChangingSuggestionsOrAnotherProject(
        bool medication, string keep, string remove)
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Cotação em revisão");
        await repo.SetProjectMedicationAsync(project.Id, medication);
        var outside = await repo.CreateProjectAsync("Cotação independente");

        async Task<QuotationLineAnalysis> Sample(Guid projectId, bool duplicates = true)
        {
            var id = Guid.NewGuid();
            await repo.SaveSampleAsync(projectId, id, Input, [
                Price(id, "z|1", 10.001m),
                Price(id, "a|2", 10.009m) with { ResultDate = new(2026, 11, 1), ContractId = duplicates ? Control : OtherOrg + "-1-000123/2026" },
                Price(id, "sem-processo", 11m) with { ContractId = "123/2026" }]);
            return (await service.GetAnalysisAsync(projectId, id))!;
        }
        static string[] Suggestions(QuotationLineAnalysis item) => item.Baskets.Where(b => !b.IsManual)
            .Select(b => $"{b.Key}/{string.Join(",", b.PriceEntries.Select(p => p.Reference.Id))}/{b.AdoptedPrice}").ToArray();
        async Task<string> Confirm(QuotationLineAnalysis item)
        {
            var key = Assert.Single(item.Baskets, b => !b.IsManual).Key;
            await service.ConfirmBasketAsync(item, key);
            return key;
        }

        var active = await Sample(project.Id);
        var line = active.Line.Id;
        var manual = await repo.SaveManualBasketAsync(line, null, "Manual independente", [remove, keep]);
        var originalKey = await Confirm(active);
        var sameKey = await Sample(project.Id);
        Assert.Equal(originalKey, await Confirm(sameKey));
        var suggestion = await Sample(project.Id);
        var cleanAuto = await Sample(project.Id, duplicates: false);
        var cleanKey = await Confirm(cleanAuto);
        var foreign = await Sample(outside.Id);
        Assert.Equal(originalKey, await Confirm(foreign));
        var raw = await repo.GetReferencesAsync(line);
        var preview = await service.FindDuplicatePricesAsync(project.Id);
        Assert.Equal(3, preview.Count);
        var auto = preview.Where(row => row.IsAutomatic).ToArray();
        Assert.Equal(2, auto.Length);
        Assert.All(auto, row => Assert.Equal((originalKey, keep, remove), (row.BasketKey, row.Kept.ReferenceId, row.Removed.ReferenceId)));
        Assert.Equal(2, auto.Select(row => row.LineId).Distinct().Count());
        var manualRow = Assert.Single(preview, row => !row.IsAutomatic);
        Assert.Equal((manual.Key, remove, keep), (manualRow.BasketKey, manualRow.Kept.ReferenceId, manualRow.Removed.ReferenceId));

        // The Cancel button dismisses this read-only preview. Reopen before applying a fresh preview.
        var firstReopen = new QuotationService(new SqliteQuotationRepository(db.Repository.DatabasePath), new QuotationAnalyzer());
        Assert.Equal(preview, await firstReopen.FindDuplicatePricesAsync(project.Id));
        Assert.Equal(originalKey, (await firstReopen.GetAnalysisAsync(project.Id, line))!.SelectedBasket!.Key);
        Assert.Equal(new[] { remove, keep }, Assert.Single(await repo.GetManualBasketsAsync(line)).ReferenceIds);
        var changed = await service.ApplyDuplicatePriceReviewAsync(project.Id, preview, AcceptWarnings(preview));
        Assert.True(changed.SetEquals([line, sameKey.Line.Id]));
        var keys = (await service.GetCleanedItemsAsync(project.Id, changed)).ToDictionary(p => p.Key, p => p.Value.Line.SelectedBasketKey);
        Assert.NotEqual(keys[line], keys[sameKey.Line.Id]);

        var fresh = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var reopenedService = new QuotationService(fresh, new QuotationAnalyzer());
        var report = await reopenedService.GetReportAsync(project.Id);
        foreach (var original in new[] { active, sameKey })
        {
            var item = report.Lines.Single(i => i.Line.Id == original.Line.Id);
            var saved = item.SelectedBasket!;
            Assert.True(item.Line.SelectionConfirmed);
            Assert.True(saved.IsManual);
            Assert.Equal(keys[item.Line.Id], saved.Key);
            Assert.Equal(new[] { keep, "sem-processo" }, saved.PriceEntries.Select(p => p.Reference.Id));
            Assert.Equal(medication ? 10.001m : 10.009m, saved.PriceEntries[0].Reference.UnitPrice);
            Assert.Equal(medication ? 10.5005m : 10.50m, saved.AdoptedPrice);
            Assert.Equal(QuotationAggregationMethod.Mean, saved.AggregationMethod);
            Assert.Equal(Suggestions(original), Suggestions(item));
        }
        var baskets = await fresh.GetManualBasketsAsync(line);
        Assert.Equal(2, baskets.Count);
        Assert.Equal(new[] { remove }, baskets.Single(b => b.Id == manual.Id).ReferenceIds);
        Assert.Single(await fresh.GetManualBasketsAsync(sameKey.Line.Id));
        Assert.Equal(raw, await fresh.GetReferencesAsync(line));
        var unconfirmed = report.Lines.Single(i => i.Line.Id == suggestion.Line.Id);
        Assert.False(unconfirmed.Line.SelectionConfirmed);
        Assert.Equal(Suggestions(suggestion), Suggestions(unconfirmed));
        Assert.Empty(await fresh.GetManualBasketsAsync(suggestion.Line.Id));
        var untouched = report.Lines.Single(i => i.Line.Id == cleanAuto.Line.Id);
        Assert.True(untouched.Line.SelectionConfirmed);
        Assert.Equal(cleanKey, untouched.SelectedBasket!.Key);
        Assert.Empty(await fresh.GetManualBasketsAsync(cleanAuto.Line.Id));
        var outsideItem = (await reopenedService.GetAnalysisAsync(outside.Id, foreign.Line.Id))!;
        Assert.True(outsideItem.Line.SelectionConfirmed);
        Assert.Equal(originalKey, outsideItem.SelectedBasket!.Key);
        Assert.Equal(Suggestions(foreign), Suggestions(outsideItem));
        Assert.Empty(await fresh.GetManualBasketsAsync(foreign.Line.Id));
        Assert.Single(await reopenedService.FindDuplicatePricesAsync(outside.Id));
        var repeat = await reopenedService.FindDuplicatePricesAsync(project.Id);
        Assert.Empty(repeat);
        Assert.Empty(await reopenedService.ApplyDuplicatePriceReviewAsync(project.Id, repeat, AcceptWarnings(repeat)));
        Assert.Equal(2, (await fresh.GetManualBasketsAsync(line)).Count);

        var file = Path.Combine(db.Directory, "revisada.pncpcotacao");
        await new QuotationPackageService(db.Repository.DatabasePath, db.Directory).ExportAsync(file, project.Id);
        await using var destination = await TestDatabase.CreateAsync();
        var imported = await new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory)
            .ImportAsync(file, QuotationPackageImportMode.PreserveIdentity);
        var importedService = new QuotationService(new SqliteQuotationRepository(destination.Repository.DatabasePath), new QuotationAnalyzer());
        var importedReport = await importedService.GetReportAsync(imported.ProjectId);
        var importedItem = importedReport.Lines.Single(i => i.Line.Id == line);
        Assert.True(importedItem.Line.SelectionConfirmed);
        Assert.Equal(keys[line], importedItem.SelectedBasket!.Key);
        Assert.Equal(new[] { keep, "sem-processo" }, importedItem.SelectedBasket.References.Select(r => r.Id));
        Assert.Equal(Suggestions(active), Suggestions(importedItem));
        Assert.Empty(await importedService.FindDuplicatePricesAsync(imported.ProjectId));
    }

    [Fact]
    public async Task ChangedConfirmationAndFailureSavingAutomaticRollBackTheWholeProject()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Falha sintética");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input, [Price(line, "a"), Price(line, "b"), Price(line, "c")]);
        var manual = await repo.SaveManualBasketAsync(line, null, "Manual", ["b", "a"]);
        var analysis = (await service.GetAnalysisAsync(project.Id, line))!;
        var automatic = Assert.Single(analysis.Baskets, b => !b.IsManual);
        await service.ConfirmBasketAsync(analysis, automatic.Key);
        var before = await service.FindDuplicatePricesAsync(project.Id);
        await service.ConfirmBasketAsync(analysis, manual.Key);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyDuplicatePriceReviewAsync(project.Id, before, AcceptWarnings(before)));
        Assert.Equal(manual.ReferenceIds, Assert.Single(await repo.GetManualBasketsAsync(line)).ReferenceIds);
        await service.ConfirmBasketAsync(analysis, automatic.Key);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db.Repository.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER fail_confirmation BEFORE UPDATE OF selected_basket_key ON quotation_lines
            WHEN NEW.selected_basket_key LIKE 'manual:%' BEGIN SELECT RAISE(ABORT, 'falha depois da cópia'); END;
            """;
        await trigger.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => service.ApplyDuplicatePriceReviewAsync(project.Id, before, AcceptWarnings(before)));
        var reopened = new SqliteQuotationRepository(db.Repository.DatabasePath);
        Assert.Equal(manual.ReferenceIds, Assert.Single(await reopened.GetManualBasketsAsync(line)).ReferenceIds);
        Assert.Equal(automatic.Key, (await reopened.GetLineAsync(project.Id, line))!.SelectedBasketKey);
        Assert.True((await reopened.GetLineAsync(project.Id, line))!.SelectionConfirmed);
        Assert.Equal(before, await reopened.FindDuplicatePricesAsync(project.Id));
    }

    [Fact]
    public async Task UnresolvableConfirmedCompositionRequiresReviewWithoutSavingAPartialBasket()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var project = await repo.CreateProjectAsync("Amostra incompleta");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input, [Price(line, "a"), Price(line, "b")]);
        await repo.ConfirmBasketAsync(line, "a||b||ausente");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.FindDuplicatePricesAsync(project.Id));
        Assert.Contains("Revise a seleção", error.Message);
        Assert.Empty(await repo.GetManualBasketsAsync(line));
        Assert.Equal(2, (await repo.GetReferencesAsync(line)).Count);
    }

    [Fact]
    public async Task ConcurrentReorderAndMidTransactionFailureLeaveAllBasketsIntact()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var project = await repo.CreateProjectAsync("Transação sintética");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input, [Price(line, "a"), Price(line, "b"), Price(line, "c")]);
        var a = await repo.SaveManualBasketAsync(line, null, "A", ["a", "b"]);
        var b = await repo.SaveManualBasketAsync(line, null, "B", ["b", "c"]);
        var stale = await repo.FindDuplicatePricesAsync(project.Id);
        await repo.SaveManualBasketAsync(line, a.Id, "A", ["b", "a"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.RemoveDuplicatePricesAsync(project.Id, stale, AcceptWarnings(stale)));
        var other = await repo.SaveManualBasketAsync(line, null, "Sem duplicatas", ["a"]);
        await repo.ConfirmBasketAsync(line, other.Key);
        var preview = await repo.FindDuplicatePricesAsync(project.Id);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db.Repository.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER fail_second_basket BEFORE DELETE ON quotation_manual_basket_references
            WHEN OLD.reference_id = 'c' BEGIN SELECT RAISE(ABORT, 'falha sintética'); END;
            """;
        await trigger.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => repo.RemoveDuplicatePricesAsync(project.Id, preview, AcceptWarnings(preview)));
        Assert.Equal(preview, await repo.FindDuplicatePricesAsync(project.Id));
        Assert.True((await repo.GetLineAsync(project.Id, line))!.SelectionConfirmed);
        trigger.CommandText = "DROP TRIGGER fail_second_basket;";
        await trigger.ExecuteNonQueryAsync();
        await repo.RemoveDuplicatePricesAsync(project.Id, preview, AcceptWarnings(preview));
        Assert.True((await repo.GetLineAsync(project.Id, line))!.SelectionConfirmed);
        Assert.Equal(new[] { "b" }, (await repo.GetManualBasketsAsync(line)).Single(x => x.Id == b.Id).ReferenceIds);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(true, 5)]
    public async Task ZeroPriceOrUnrepresentableMinimumPreservesTheOriginalEvenWithConsent(bool automatic, int minimum)
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var svc = new QuotationService(repo, new QuotationAnalyzer());
        var project = await svc.CreateProjectAsync("Revisão obrigatória");
        var line = Guid.NewGuid();
        QuotationReference P(string id, int purchase, decimal value) =>
            Price(line, id, value) with { ContractId = $"{Org}-1-{purchase:D6}/2026" };
        QuotationReference[] prices = minimum == 5
            ? [P("a", 1, 10), P("b", 1, 10), P("c", 2, 10), P("d", 2, 10), P("e", 3, 10)]
            : [P("a", 1, 10), P("b", 1, 12), P("c", 3, 13.5m)];
        await repo.SaveSampleAsync(project.Id, line, Input with { RequestedBasketSize = minimum }, prices);
        if (!automatic) await repo.SaveManualBasketAsync(line, null, "Manual", prices.Select(p => p.Id).ToArray());
        var initial = (await svc.GetAnalysisAsync(project.Id, line))!;
        var basket = Assert.Single(initial.Baskets, b => b.IsManual != automatic);
        await svc.ConfirmBasketAsync(initial, basket.Key);
        var before = await repo.GetManualBasketsAsync(line);
        var preview = await svc.FindDuplicatePricesAsync(project.Id);
        Assert.NotEmpty(preview);
        Assert.All(preview, row =>
        {
            Assert.Equal(minimum, row.Impact.Minimum);
            Assert.Equal((minimum, minimum == 5 ? 3 : 2), (row.Impact.Before, row.Impact.After));
            Assert.Equal(minimum == 5 ? 10m : 0m, row.Impact.AdoptedPrice);
            Assert.Contains(minimum == 5 ? "mínimo original de 5" : "Nenhum preço válido", row.Impact.BlockReason!);
        });
        var force = preview.Select(row => (row.LineId, row.BasketKey)).ToHashSet();
        Assert.Empty(await svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, force));
        var fresh = new QuotationService(new SqliteQuotationRepository(db.Repository.DatabasePath), new QuotationAnalyzer());
        var reopened = (await fresh.GetAnalysisAsync(project.Id, line))!;
        Assert.True(reopened.Line.SelectionConfirmed);
        Assert.Equal(basket.Key, reopened.SelectedBasket!.Key);
        Assert.Equal(basket.AdoptedPrice, reopened.SelectedBasket.AdoptedPrice);
        Assert.True(reopened.SelectedBasket.AdoptedPrice > 0m);
        Assert.Equal(minimum, reopened.SelectedBasket.RequestedSize);
        Assert.Equal(prices.Length, reopened.SelectedBasket.References.Count);
        Assert.Equal(before.Select(b => b.UpdatedAt), (await repo.GetManualBasketsAsync(line)).Select(b => b.UpdatedAt));
        Assert.Equal(preview, await fresh.FindDuplicatePricesAsync(project.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OldManualPreviewRejectsChangedSelectedKeyOrConfirmationAlone(bool changeKey)
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var svc = new QuotationService(repo, new QuotationAnalyzer());
        var project = await svc.CreateProjectAsync("Seleção alterada");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input, [Price(line, "a"), Price(line, "b"), Price(line, "c")]);
        var a = await repo.SaveManualBasketAsync(line, null, "A", ["a", "b"]);
        var b = await repo.SaveManualBasketAsync(line, null, "B", ["c"]);
        await repo.ConfirmBasketAsync(line, changeKey ? b.Key : a.Key);
        var preview = await svc.FindDuplicatePricesAsync(project.Id);
        if (changeKey)
            await repo.ConfirmBasketAsync(line, a.Key);
        else
        {
            await using var cn = new SqliteConnection($"Data Source={db.Repository.DatabasePath}");
            await cn.OpenAsync();
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "UPDATE quotation_lines SET selection_confirmed = 0 WHERE id = $line";
            cmd.Parameters.AddWithValue("$line", line.ToString("N"));
            await cmd.ExecuteNonQueryAsync();
        }
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, AcceptWarnings(preview)));
        Assert.Contains("desde a prévia", stale.Message);
        var current = (await repo.GetLineAsync(project.Id, line))!;
        Assert.Equal(a.Key, current.SelectedBasketKey);
        Assert.Equal(changeKey, current.SelectionConfirmed);
        Assert.Equal(a.ReferenceIds, (await repo.GetManualBasketsAsync(line)).Single(m => m.Key == a.Key).ReferenceIds);
    }

    [Fact]
    public async Task ConsentIsSpecificToOneBasketEvenOnTheSameLine()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var svc = new QuotationService(repo, new QuotationAnalyzer());
        var project = await svc.CreateProjectAsync("Ressalvas individuais");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input, [Price(line, "a"), Price(line, "b"), Price(line, "c")]);
        var a = await repo.SaveManualBasketAsync(line, null, "A", ["a", "b"]);
        var b = await repo.SaveManualBasketAsync(line, null, "B", ["b", "c"]);
        await repo.ConfirmBasketAsync(line, a.Key);
        var preview = await svc.FindDuplicatePricesAsync(project.Id);
        var accepted = new HashSet<(Guid, string)> { (line, b.Key) };
        Assert.Equal(2, preview.Count(row => row.Impact.NeedsAcceptance));
        Assert.Single(await svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, accepted));
        var saved = await repo.GetManualBasketsAsync(line);
        var untouched = saved.Single(m => m.Key == a.Key);
        Assert.Equal(a.ReferenceIds, untouched.ReferenceIds);
        Assert.Equal(a.UpdatedAt, untouched.UpdatedAt);
        Assert.Equal(new[] { "b" }, saved.Single(m => m.Key == b.Key).ReferenceIds);
        var item = (await svc.GetAnalysisAsync(project.Id, line))!;
        Assert.True(item.Line.SelectionConfirmed);
        Assert.Equal(a.Key, item.SelectedBasket!.Key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(427)]
    [InlineData(451)]
    public void PickerUsesCurrentOrderAndMarksEveryBasketAmong451Items(int lastMarked)
    {
        var project = new QuotationProject(Guid.NewGuid(), "Seletor", default, default) { SortItemsAlphabetically = true };
        var lines = Enumerable.Range(1, 451).Select(i => new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Description = $"Produto {i:D3}", RequestedQuantity = 1, RequestedUnit = "un"
        }).ToArray();
        var marked = lines.Where((_, i) => i + 1 == lastMarked || (i + 1 < lastMarked && i % 3 == 0))
            .Select(line => line.Id).ToHashSet();
        var picker = ManualBasketLineChoice.ForProject(project, Enumerable.Reverse(lines).ToArray(), marked);
        Assert.Equal(452, picker.Choices.Length);
        Assert.Equal("Novo item", picker.Choices[0].Label);
        Assert.False(picker.Choices[0].HasBasket);
        Assert.Equal(lastMarked, picker.OpeningIndex);
        Assert.Equal(lines, picker.Choices.Skip(1).Select(choice => choice.Line));
        Assert.True(marked.SetEquals(picker.Choices.Where(choice => choice.HasBasket).Select(choice => choice.Line!.Id)));
        if (lastMarked < 451) Assert.False(picker.Choices[lastMarked + 1].HasBasket);
    }

    [Fact]
    public async Task PickerMarksSavedBasketsWithinTheSelectedProject()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(db.Repository.DatabasePath);
        var project = await repo.CreateProjectAsync("Seletor");
        var other = await repo.CreateProjectAsync("Outro projeto");
        var line = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, line, Input, [Price(line, "a")]);
        await repo.CreateLineAsync(project.Id, Input);
        var automatic = Guid.NewGuid();
        await repo.SaveSampleAsync(project.Id, automatic, Input, [Price(automatic, "a"), Price(automatic, "b")]);
        await repo.SaveManualBasketAsync(line, null, "Cesta", ["a"]);
        await repo.ConfirmBasketAsync(automatic, "a||b");
        Assert.True((await repo.GetBasketLineIdsAsync(project.Id)).SetEquals([line, automatic]));
        Assert.Empty(await repo.GetBasketLineIdsAsync(other.Id));
    }
}
