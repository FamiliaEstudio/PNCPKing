using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationOrganizationTests
{
    [Theory]
    [InlineData("Item 1 Água", "Água")]
    [InlineData("item 1 Água", "Água")]
    [InlineData("item 01 Água", "Água")]
    [InlineData("Item 01 - Água", "Água")]
    [InlineData("Item 1 - Água", "Água")]
    [InlineData("Item 3-Água", "Água")]
    [InlineData("  ITEM 003 – Álcool 70% 500 mL", "Álcool 70% 500 mL")]
    [InlineData("Item 4— Água", "Água")]
    [InlineData("Água", "Água")]
    [InlineData("Água - Item 1", "Água - Item 1")]
    [InlineData("Item para limpeza", "Item para limpeza")]
    [InlineData("Item 1", "Item 1")]
    [InlineData("Item 04 -", "Item 04 -")]
    [InlineData("Item 04 -  ", "Item 04 -  ")]
    [InlineData("Item 1A - Água", "Item 1A - Água")]
    [InlineData("Item 1,5 L de água", "Item 1,5 L de água")]
    [InlineData("Item 1.5 L de água", "Item 1.5 L de água")]
    public void RemovesOnlyTheInitialManualItemNumberWhenANameRemains(string name, string expected) =>
        Assert.Equal(expected, QuotationOrganization.WithoutItemNumberPrefix(name));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlphabeticalOptionsWorkWithoutQuantityUnitOrBasketsAndPersistInPackages(bool removePrefixes)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer());
        var project = await repository.CreateProjectAsync("Ordenar sem preenchimento");
        var zebra = await repository.CreateLineAsync(project.Id, new("Item 1 - Zebra", 0, "", null, null));
        var water = await repository.CreateLineAsync(project.Id, new("item 02 - Água", 0, "", null, null));
        var banana = await repository.CreateLineAsync(project.Id, new("Descrição técnica da banana", 0, "", null, null));
        await repository.RenameLineDisplayNameAsync(banana.Id, "ITEM 03 - Banana");
        var emptyName = await repository.CreateLineAsync(project.Id, new("Item 04 -", 0, "", null, null));
        var otherProject = await repository.CreateProjectAsync("Outro projeto");
        var other = await repository.CreateLineAsync(otherProject.Id, new("Item 1 - Intocado", 0, "", null, null));
        var before = await repository.GetLinesAsync(project.Id);

        await service.OrganizeProjectAsync(project.Id, new QuotationOrganizationOptions(true, true, removePrefixes));
        service = new(new SqliteQuotationRepository(database.Repository.DatabasePath), new QuotationAnalyzer());
        var report = await service.GetReportAsync(project.Id);
        Assert.True(report.Project.SortItemsAlphabetically);
        Assert.True(report.Project.IgnoreItemNumberPrefix);
        Assert.Null(report.Project.Organization);
        Assert.False(report.OrganizationIsStale);
        Assert.Equal([water.Id, banana.Id, emptyName.Id, zebra.Id], report.Lines.Select(value => value.Line.Id));
        Assert.All(report.Lines, value =>
        {
            Assert.Equal(0, value.Line.RequestedQuantity);
            Assert.Equal("", value.Line.RequestedUnit);
            Assert.Empty(value.Baskets);
            Assert.Equal(before.Single(line => line.Id == value.Line.Id).Description, value.Line.Description);
        });
        Assert.Equal(removePrefixes ? "Banana" : "ITEM 03 - Banana",
            report.Lines.Single(value => value.Line.Id == banana.Id).Line.EffectiveDisplayName);
        Assert.Equal(before.Select(line => (line.Id, line.DisplayOrder)),
            (await repository.GetLinesAsync(project.Id)).Select(line => (line.Id, line.DisplayOrder)));
        Assert.Equal(other, await repository.GetLineAsync(otherProject.Id, other.Id));

        var xlsx = Path.Combine(database.Directory, "ordem-sem-quantidades.xlsx");
        await new QuotationWorkbookService().ExportAsync(xlsx, report, "Responsável");
        using (var workbook = new XLWorkbook(xlsx))
        {
            var titles = workbook.Worksheet(1).Column(2).CellsUsed().Select(cell => cell.GetString())
                .Where(text => text.StartsWith("Item ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(report.Lines.Select((value, index) => $"Item {index + 1} - {value.Line.EffectiveDisplayName}"), titles);
        }

        var package = Path.Combine(database.Directory, "ordem.pncpcotacao");
        await new QuotationPackageService(database.Repository.DatabasePath, database.Directory).ExportAsync(package, project.Id);
        var copied = await new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory)
            .ImportAsync(package, QuotationPackageImportMode.Copy);
        var restoredService = new QuotationService(new SqliteQuotationRepository(destination.Repository.DatabasePath), new QuotationAnalyzer());
        var restored = await restoredService.GetReportAsync(copied.ProjectId);
        Assert.True(restored.Project.IgnoreItemNumberPrefix);
        Assert.True(restored.Project.SortItemsAlphabetically);
        Assert.Null(restored.Project.Organization);
        Assert.Equal(report.Lines.Select(value => value.Line.EffectiveDisplayName), restored.Lines.Select(value => value.Line.EffectiveDisplayName));
        var added = await restoredService.CreateLineAsync(copied.ProjectId, new("Item 99 - Abacate", 0, "", null, null));
        Assert.Equal(added.Line.Id, (await restoredService.GetReportAsync(copied.ProjectId)).Lines[0].Line.Id);

        // Turning the ignore option off restores ordering by the complete visible names.
        await service.OrganizeProjectAsync(project.Id, new QuotationOrganizationOptions(true));
        report = await service.GetReportAsync(project.Id);
        Assert.False(report.Project.IgnoreItemNumberPrefix);
        Assert.Equal(QuotationOrganization.Alphabetical(report.Lines).Select(value => value.Line.Id),
            report.Lines.Select(value => value.Line.Id));
    }

    [Fact]
    public void IgnoringNumbersOrdersGroupMembersAndGroupsByTheActualName()
    {
        var project = Project() with { IgnoreItemNumberPrefix = true };
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var water = Item(project, "Item 20 - Água", 8, 6000, first);
        var zebra = Item(project, "Item 1 - Zebra", 8, 6000, first);
        var banana = Item(project, "Item 02 - Banana", 8, 12000, second);
        var looseZebra = Item(project, "Item 03 - Zebra", 1, 1);
        var looseWater = Item(project, "Item 04 - Água", 1, 1);
        var report = Apply(project, [zebra, banana, water, looseZebra, looseWater],
            new(first, project.Id, "Primeiro", [zebra.Line.Id, water.Line.Id]),
            new(second, project.Id, "Segundo", [banana.Line.Id]));
        Assert.Equal([first, first, second, second], report.Project.Organization!.Groups.Select(group => group.SourceGroupId));
        Assert.Equal([water.Line.Id, zebra.Line.Id, water.Line.Id, zebra.Line.Id, banana.Line.Id, banana.Line.Id,
            looseWater.Line.Id, looseZebra.Line.Id], report.Project.Organization.Positions.Select(position => position.LineId));
        Assert.Equal("Item 20 - Água", report.Lines[0].Line.EffectiveDisplayName);
    }

    [Fact]
    public async Task BatchRemovalIsAtomicKeepsBasketsAndAllowsReturningFromQuotasToSimpleOrder()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer());
        var project = await repository.CreateProjectAsync("Limpar projeto");
        var zebra = await AddConfirmedAsync(repository, service, project, "Item 1 - Zebra", 8, 12000);
        var water = await AddConfirmedAsync(repository, service, project, "item 02 - Água", 8, 12000);
        await repository.SaveGroupsAsync(project.Id, [new(Guid.NewGuid(), project.Id, "Grupo", [zebra.Id, water.Id])]);
        var before = await service.GetReportAsync(project.Id);
        await repository.UpdateLineRequestedDetailsAsync(water.Id, 0, "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OrganizeProjectAsync(project.Id, new QuotationOrganizationOptions(false, true, true)));
        Assert.Equal("Item 1 - Zebra", (await repository.GetLineAsync(project.Id, zebra.Id))!.EffectiveDisplayName);
        Assert.Equal("item 02 - Água", (await repository.GetLineAsync(project.Id, water.Id))!.EffectiveDisplayName);
        Assert.Null((await service.GetReportAsync(project.Id)).Project.Organization);

        await repository.UpdateLineRequestedDetailsAsync(water.Id, 8, "unidade");
        var current = await service.GetAnalysesAsync(project.Id);
        var waterAnalysis = current.Single(value => value.Line.Id == water.Id);
        await repository.ConfirmBasketAsync(water.Id, waterAnalysis.Baskets[0].Key);
        await service.OrganizeProjectAsync(project.Id, new QuotationOrganizationOptions(false, true, true));
        var report = await service.GetReportAsync(project.Id);
        Assert.False(report.OrganizationIsStale);
        Assert.Equal(["Água", "Zebra"], report.Lines.Select(value => value.Line.EffectiveDisplayName));
        QuotationOrganization.ValidateSnapshot(report);
        foreach (var value in report.Lines)
        {
            var original = before.Lines.Single(old => old.Line.Id == value.Line.Id);
            Assert.Equal(original.Line.Description, value.Line.Description);
            Assert.Equal(original.SelectedBasket!.AdoptedPrice, value.SelectedBasket!.AdoptedPrice);
            Assert.Equal(original.SelectedBasket.References.Select(reference => reference.Id),
                value.SelectedBasket.References.Select(reference => reference.Id));
        }
        var package = Path.Combine(database.Directory, "cotas.pncpcotacao");
        await new QuotationPackageService(database.Repository.DatabasePath, database.Directory).ExportAsync(package, project.Id);
        var copied = await new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory)
            .ImportAsync(package, QuotationPackageImportMode.Copy);
        var restored = await new QuotationService(new SqliteQuotationRepository(destination.Repository.DatabasePath), new QuotationAnalyzer())
            .GetReportAsync(copied.ProjectId);
        Assert.True(restored.Project.IgnoreItemNumberPrefix);
        Assert.False(restored.OrganizationIsStale);
        QuotationOrganization.ValidateSnapshot(restored);

        await service.OrganizeProjectAsync(project.Id, new QuotationOrganizationOptions(true, true));
        report = await service.GetReportAsync(project.Id);
        Assert.Null(report.Project.Organization);
        Assert.Single(report.Groups);
        report.ValidateExport();
    }

    private static QuotationProject Project(bool medication = false) =>
        new(Guid.NewGuid(), "Organização", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { IsMedication = medication };

    private static QuotationLineAnalysis Item(QuotationProject project, string name, decimal quantity, decimal price,
        Guid? group = null, int order = 0) => new(new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Description = name, RequestedQuantity = quantity,
            RequestedUnit = "unidade", GroupId = group, DisplayOrder = order,
            SelectedBasketKey = "selected", SelectionConfirmed = true
        }, [], [new QuotationBasket
        {
            Key = "selected", References = [], AveragePrice = price, AdoptedPrice = price,
            MinimumPrice = price, MaximumPrice = price, MaximumDeviationPercent = 0, Score = 100
        }], 0, 0, 0, 0, 0) { PriceDecimalPlaces = project.PriceDecimalPlaces };

    private static QuotationProjectReport Apply(QuotationProject project, IReadOnlyList<QuotationLineAnalysis> lines,
        params QuotationGroup[] groups)
    {
        project = project with { Organization = QuotationOrganization.Calculate(project, lines, groups) };
        var report = new QuotationProjectReport(project, QuotationOrganization.Order(project, lines)) { Groups = groups };
        QuotationOrganization.ValidateSnapshot(report);
        return report;
    }

    [Fact]
    public void OrdersFourCategoriesAndGivesPrincipalAndReservedDistinctGroups()
    {
        var project = Project(); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var third = Guid.NewGuid();
        var agua = Item(project, "Água", 8, 6000, first);
        var zebra = Item(project, "Zebra", 8, 6000, first);
        var banana = Item(project, "Banana", 4, 21000, second);
        var abacate = Item(project, "Abacate", 4, 1, third);
        var individual = Item(project, "Açúcar", 11, 8000);
        var last = Item(project, "Arroz", 4, 1);
        var report = Apply(project, [last, zebra, banana, individual, abacate, agua],
            new(first, project.Id, "Z — nome não ordena", [zebra.Line.Id, agua.Line.Id]),
            new(second, project.Id, "A — nome não ordena", [banana.Line.Id]),
            new(third, project.Id, "Sem cota", [abacate.Line.Id]));
        var snapshot = report.Project.Organization!;
        Assert.Equal([1, 2, 3, 4, 5], snapshot.Groups.Select(group => group.Number));
        Assert.Equal([first, first, second, second, third], snapshot.Groups.Select(group => group.SourceGroupId));
        Assert.Equal(5, snapshot.Groups.Select(group => group.Id).Distinct().Count());
        Assert.Equal(snapshot.Groups[1].Id, snapshot.Groups[0].PairedGroupId);
        Assert.Equal(snapshot.Groups[0].Id, snapshot.Groups[1].PairedGroupId);
        Assert.Equal([agua.Line.Id, zebra.Line.Id, agua.Line.Id, zebra.Line.Id, banana.Line.Id,
            banana.Line.Id, abacate.Line.Id, individual.Line.Id, individual.Line.Id, last.Line.Id],
            snapshot.Positions.Select(position => position.LineId));
        Assert.Equal("1 e 3", report.ItemNumbers(0));
        Assert.Equal("2 e 4", report.ItemNumbers(1));
        Assert.Equal("8 e 9", snapshot.ItemNumbers(individual.Line.Id));
        Assert.All(snapshot.Positions.Where(position => position.LineId == individual.Line.Id),
            position => Assert.Null(position.ResultGroupId));
        Assert.Equal([9m, 2m], snapshot.Positions.Where(position => position.LineId == individual.Line.Id).Select(position => position.Quantity));
        Assert.False(report.OrganizationIsStale);
    }

    [Theory]
    [InlineData(false, 19999.99, false)]
    [InlineData(false, 20000, false)]
    [InlineData(false, 20000.001, false)]
    [InlineData(false, 20000.01, true)]
    [InlineData(true, 20000.0001, true)]
    public void ThresholdUsesEffectivePrecision(bool medication, decimal price, bool quota)
    {
        var project = Project(medication);
        var report = Apply(project, [Item(project, "Item", 4, price)]);
        Assert.Equal(quota ? 2 : 1, report.Project.Organization!.Positions.Count);
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(2, 0)] [InlineData(3, 0)] [InlineData(4, 1)]
    [InlineData(5, 1)] [InlineData(7, 1)] [InlineData(8, 2)] [InlineData(11, 2)] [InlineData(100, 25)]
    public void IntegerReservationNeverExceedsQuarterAndPreservesTotals(int quantity, int reserved)
    {
        var project = Project(); var item = Item(project, "Item", quantity, 90000);
        var positions = Apply(project, [item]).Project.Organization!.Positions;
        Assert.Equal((decimal)quantity, positions.Sum(position => position.Quantity));
        Assert.Equal(quantity * 90000m, positions.Sum(position => position.TotalPrice));
        Assert.Equal((decimal)reserved, positions.Where(position => position.Kind == QuotationQuotaKind.Reserved).Sum(position => position.Quantity));
        Assert.Equal(reserved == 0 ? 1 : 2, positions.Count);
        Assert.All(positions.Where(position => position.Kind == QuotationQuotaKind.Reserved), position =>
        {
            Assert.True(position.Quantity <= quantity * 0.25m);
            Assert.True(position.TotalPrice <= quantity * 90000m * 0.25m);
        });
        Assert.DoesNotContain(positions, position => position.Quantity == 0);
    }

    [Theory]
    [InlineData(false, 90000.123499, 90000.12)]
    [InlineData(true, 90000.123499, 90000.1234)]
    public void MixedGroupReservesPerItemWithoutDuplicatingSmallQuantities(
        bool medication, decimal price, decimal effectivePrice)
    {
        var project = Project(medication); var groupId = Guid.NewGuid();
        int[] quantities = [1, 2, 3, 4, 5, 7, 8, 100];
        decimal[] principal = [1, 2, 3, 3, 4, 6, 6, 75];
        decimal[] reserved = [0, 0, 0, 1, 1, 1, 2, 25];
        string[] numbers = ["11", "12", "13", "1 e 6", "2 e 7", "3 e 8", "4 e 9", "5 e 10"];
        var lines = quantities.Select(quantity => Item(project, $"Item {quantity:D3}", quantity, price, groupId)).ToArray();
        var report = Apply(project, Enumerable.Reverse(lines).ToArray(),
            new QuotationGroup(groupId, project.Id, "Misto", lines.Select(value => value.Line.Id).ToArray()));
        var snapshot = report.Project.Organization!;
        var mainGroup = Assert.Single(snapshot.Groups, group => group.Kind == QuotationQuotaKind.Principal);
        var reserveGroup = Assert.Single(snapshot.Groups, group => group.Kind == QuotationQuotaKind.Reserved);
        var smallGroup = Assert.Single(snapshot.Groups, group => group.Kind == QuotationQuotaKind.None);
        Assert.Equal(3, snapshot.Groups.Count);
        Assert.Equal([1, 2, 3], snapshot.Groups.Select(group => group.Number));
        Assert.Equal(groupId, smallGroup.SourceGroupId);
        Assert.Null(smallGroup.PairedGroupId);
        Assert.Equal(reserveGroup.Id, mainGroup.PairedGroupId);
        Assert.Equal(mainGroup.Id, reserveGroup.PairedGroupId);
        Assert.Equal(Enumerable.Range(1, 13), snapshot.Positions.Select(position => position.Number));
        Assert.Equal(lines.Skip(3).Concat(lines.Skip(3)).Concat(lines.Take(3)).Select(value => value.Line.Id),
            snapshot.Positions.Select(position => position.LineId));
        Assert.All(snapshot.Positions, position =>
        {
            Assert.True(position.Quantity > 0);
            Assert.Equal(effectivePrice, position.UnitPrice);
            Assert.Equal(position.Quantity * effectivePrice, position.TotalPrice);
        });
        for (var index = 0; index < lines.Length; index++)
        {
            var positions = snapshot.Positions.Where(position => position.LineId == lines[index].Line.Id).ToArray();
            var main = Assert.Single(positions, position => position.Kind != QuotationQuotaKind.Reserved);
            var reserve = positions.Where(position => position.Kind == QuotationQuotaKind.Reserved).ToArray();
            Assert.Equal(principal[index], main.Quantity);
            Assert.Equal(quantities[index] < 4 ? smallGroup.Id : mainGroup.Id, main.ResultGroupId);
            Assert.Equal(reserved[index], reserve.Sum(position => position.Quantity));
            Assert.Equal((decimal)quantities[index], positions.Sum(position => position.Quantity));
            Assert.Equal(quantities[index] * effectivePrice, positions.Sum(position => position.TotalPrice));
            Assert.True(reserve.Sum(position => position.Quantity) <= quantities[index] * 0.25m);
            Assert.True(reserve.Sum(position => position.TotalPrice) <= quantities[index] * effectivePrice * 0.25m);
            Assert.Equal(numbers[index], snapshot.ItemNumbers(lines[index].Line.Id));
            if (quantities[index] < 4)
            {
                Assert.Single(positions);
                Assert.Empty(reserve);
                Assert.Equal(QuotationQuotaKind.None, main.Kind);
                Assert.Equal("Grupo 3 — Sem cota", snapshot.GroupNumbers(lines[index].Line.Id));
            }
            else
                Assert.Equal(reserveGroup.Id, Assert.Single(reserve).ResultGroupId);
        }
    }

    [Theory]
    [InlineData(false, 90000.12)]
    [InlineData(true, 90000.1234)]
    public void GroupOfOnlySmallExpensiveItemsHasNoReservedGroup(bool medication, decimal price)
    {
        var project = Project(medication); var groupId = Guid.NewGuid();
        var lines = Enumerable.Range(1, 3)
            .Select(quantity => Item(project, $"Item {quantity}", quantity, price, groupId)).ToArray();
        var report = Apply(project, lines,
            new QuotationGroup(groupId, project.Id, "Pequenas quantidades", lines.Select(value => value.Line.Id).ToArray()));
        var snapshot = report.Project.Organization!;
        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(QuotationQuotaKind.None, group.Kind);
        Assert.Null(group.PairedGroupId);
        Assert.Equal([1, 2, 3], snapshot.Positions.Select(position => position.Number));
        foreach (var item in lines)
        {
            var position = Assert.Single(snapshot.Positions, position => position.LineId == item.Line.Id);
            Assert.Equal(group.Id, position.ResultGroupId);
            Assert.Equal(QuotationQuotaKind.None, position.Kind);
            Assert.Equal(item.Line.RequestedQuantity, position.Quantity);
            Assert.Equal(price, position.UnitPrice);
            Assert.Equal(item.Line.RequestedQuantity * price, position.TotalPrice);
        }
    }

    [Fact]
    public void RemovingOneSmallItemReevaluatesTheRemainingGroupThreshold()
    {
        var project = Project(); var id = Guid.NewGuid();
        var a = Item(project, "A", 2, 25000, id); var b = Item(project, "B", 4, 10000, id);
        var report = Apply(project, [b, a], new QuotationGroup(id, project.Id, "Grupo", [a.Line.Id, b.Line.Id]));
        var snapshot = report.Project.Organization!;
        Assert.Equal(QuotationQuotaKind.None, Assert.Single(snapshot.Groups).Kind);
        Assert.Equal([b.Line.Id, a.Line.Id], snapshot.Positions.Select(position => position.LineId));
        Assert.Equal([4m, 2m], snapshot.Positions.Select(position => position.Quantity));
        Assert.Null(snapshot.Positions[1].ResultGroupId);
        Assert.Equal("1", report.ItemNumbers(0)); Assert.Equal("2", report.ItemNumbers(1));
        var onlySmall = Apply(project, [a], new QuotationGroup(id, project.Id, "Grupo", [a.Line.Id]));
        Assert.Equal(QuotationQuotaKind.None, Assert.Single(onlySmall.Project.Organization!.Groups).Kind);
        var expensiveSmall = a with { Line = a.Line with { RequestedQuantity = 3 } };
        expensiveSmall = expensiveSmall with { Baskets = [expensiveSmall.Baskets[0] with { AdoptedPrice = 90000 }] };
        Assert.Equal(QuotationQuotaKind.None, Assert.Single(Apply(project, [expensiveSmall],
            new QuotationGroup(id, project.Id, "Grupo", [a.Line.Id])).Project.Organization!.Groups).Kind);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void OneRemovedItemJoinsIndividualsWithoutChangingSourceGroups(int quantity, bool ignorePrefixes)
    {
        var project = Project() with { IgnoreItemNumberPrefix = ignorePrefixes };
        var id = Guid.NewGuid();
        var small = Item(project, "Item 2 - Banana", quantity, 90000, id);
        var large = Item(project, "Item 4 - Zebra", 8, 11000, id);
        var individualQuota = Item(project, "Item 3 - Café", 4, 25000);
        var individual = Item(project, "Item 1 - Água", 1, 1);
        QuotationLineAnalysis[] lines = [small, large, individual, individualQuota];
        var group = new QuotationGroup(id, project.Id, "Original", [small.Line.Id, large.Line.Id]);
        var snapshot = Apply(project, lines, group).Project.Organization!;
        Assert.Equal([large.Line.Id, large.Line.Id, individualQuota.Line.Id, individualQuota.Line.Id,
            individual.Line.Id, small.Line.Id], snapshot.Positions.Select(position => position.LineId));
        Assert.Equal([6m, 2m, 3m, 1m, 1m, (decimal)quantity], snapshot.Positions.Select(position => position.Quantity));
        var removed = snapshot.Positions[^1];
        Assert.Null(removed.ResultGroupId);
        Assert.Equal(QuotationQuotaKind.None, removed.Kind);
        Assert.Equal(2, snapshot.Groups.Count);
        Assert.Equal([small.Line.Id, large.Line.Id], group.LineIds);
        Assert.Equal(id, small.Line.GroupId);
        var repeated = Apply(project, lines, group).Project.Organization!;
        Assert.Equal(snapshot.Positions.Select(position => (position.Number, position.LineId, position.Kind, position.Quantity)),
            repeated.Positions.Select(position => (position.Number, position.LineId, position.Kind, position.Quantity)));
    }

    [Theory]
    [InlineData(false, 20000, false)]
    [InlineData(false, 20000.001, false)]
    [InlineData(false, 20000.01, true)]
    [InlineData(true, 20000.0001, true)]
    public void RemainingGroupUsesTheSameThresholdAndPricePrecision(bool medication, decimal price, bool quota)
    {
        var project = Project(medication); var id = Guid.NewGuid();
        var large = Item(project, "B", 4, price, id);
        var small = Item(project, "A", 1, 90000, id);
        var snapshot = Apply(project, [small, large], new QuotationGroup(id, project.Id, "Misto", [small.Line.Id, large.Line.Id]))
            .Project.Organization!;
        Assert.Equal(quota ? 2 : 1, snapshot.Groups.Count);
        Assert.Equal(quota ? QuotationQuotaKind.Principal : QuotationQuotaKind.None, snapshot.Groups[0].Kind);
        Assert.Null(snapshot.Positions[^1].ResultGroupId);
        Assert.Equal(small.Line.Id, snapshot.Positions[^1].LineId);
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(16000)]
    public void MixedGroupAtOrBelowThresholdIsNotSplit(int price)
    {
        var project = Project(); var id = Guid.NewGuid();
        var small = Item(project, "A", 1, price, id);
        var large = Item(project, "B", 4, price, id);
        var snapshot = Apply(project, [small, large], new QuotationGroup(id, project.Id, "Misto", [small.Line.Id, large.Line.Id]))
            .Project.Organization!;
        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(QuotationQuotaKind.None, group.Kind);
        Assert.All(snapshot.Positions, position => Assert.Equal(group.Id, position.ResultGroupId));
        Assert.Equal([1m, 4m], snapshot.Positions.Select(position => position.Quantity));
    }

    [Fact]
    public void RemovedGroupsStaySeparateAndSortWithOtherGroupsByTheirFirstItem()
    {
        var project = Project(); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var third = Guid.NewGuid();
        var a = Item(project, "Água", 1, 90000, first);
        var d = Item(project, "Damasco", 3, 90000, first);
        var z = Item(project, "Zinco", 4, 25000, first);
        var c = Item(project, "Café", 2, 90000, second);
        var e = Item(project, "Erva", 3, 90000, second);
        var y = Item(project, "Xarope", 8, 12000, second);
        var b = Item(project, "Banana", 4, 1, third);
        QuotationLineAnalysis[] lines = [z, y, e, d, c, b, a];
        var snapshot = Apply(project, lines,
            new(first, project.Id, "Primeiro", [z.Line.Id, d.Line.Id, a.Line.Id]),
            new(second, project.Id, "Segundo", [y.Line.Id, e.Line.Id, c.Line.Id]),
            new(third, project.Id, "Terceiro", [b.Line.Id])).Project.Organization!;
        Assert.Equal([second, second, first, first, first, third, second],
            snapshot.Groups.Select(group => group.SourceGroupId));
        Assert.Equal([y.Line.Id, y.Line.Id, z.Line.Id, z.Line.Id, a.Line.Id, d.Line.Id, b.Line.Id, c.Line.Id, e.Line.Id],
            snapshot.Positions.Select(position => position.LineId));
        Assert.Equal(Enumerable.Range(1, 9), snapshot.Positions.Select(position => position.Number));
        Assert.All(snapshot.Groups.Skip(4), group => Assert.Equal(QuotationQuotaKind.None, group.Kind));
        Assert.Equal(snapshot.Positions[4].ResultGroupId, snapshot.Positions[5].ResultGroupId);
        Assert.Equal(snapshot.Positions[7].ResultGroupId, snapshot.Positions[8].ResultGroupId);
        Assert.NotEqual(snapshot.Positions[4].ResultGroupId, snapshot.Positions[7].ResultGroupId);
        Assert.Equal(lines.Sum(value => value.Line.RequestedQuantity), snapshot.Positions.Sum(position => position.Quantity));
        Assert.Equal(lines.Sum(value => value.Line.RequestedQuantity * value.SelectedBasket!.AdoptedPrice),
            snapshot.Positions.Sum(position => position.TotalPrice));
    }

    [Theory]
    [InlineData(false, false, "456F209E51FF9086E1CF650E37536108CBD06E4B17596A80F3826668EEC0D4FC")]
    [InlineData(false, true, "BD812805DA53B75AF792076DB509653426CAF81C9BA683D7043D53965001BA3E")]
    [InlineData(true, false, "0855072156DB7DCAB46815E61768653DCFCE1E5E935996B8610AE5C6E30540B5")]
    [InlineData(true, true, "D4F6E06D72BE16D93A7B12B8FD3AE7D70B0E519F05396EF3E5A5064124F906AA")]
    public void OnlyAffectedLegacySignaturesRequireOrganizingAgain(bool hasSmallItem, bool ignorePrefixes, string legacySignature)
    {
        var project = Project() with { IgnoreItemNumberPrefix = ignorePrefixes };
        var groupId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var small = Item(project, "Small", hasSmallItem ? 1 : 4, 30000, groupId);
        small = small with { Line = small.Line with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") } };
        var large = Item(project, "Large", 4, 30000, groupId);
        large = large with { Line = large.Line with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") } };
        var report = Apply(project, [small, large], new QuotationGroup(groupId, project.Id, "Legacy", [small.Line.Id, large.Line.Id]));
        var legacy = report with { Project = report.Project with
            { Organization = report.Project.Organization! with { Signature = legacySignature } } };
        if (hasSmallItem)
        {
            // The old organization kept the small item entirely in the principal group.
            var snapshot = legacy.Project.Organization!;
            legacy = legacy with { Project = legacy.Project with { Organization = snapshot with
            {
                Positions = [
                    new(1, large.Line.Id, snapshot.Groups[0].Id, QuotationQuotaKind.Principal, 3, 30000, 90000),
                    new(2, small.Line.Id, snapshot.Groups[0].Id, QuotationQuotaKind.Principal, 1, 30000, 30000),
                    new(3, large.Line.Id, snapshot.Groups[1].Id, QuotationQuotaKind.Reserved, 1, 30000, 30000)]
            } } };
        }
        Assert.Equal(hasSmallItem, legacy.OrganizationIsStale);
        if (hasSmallItem) Assert.Throws<InvalidOperationException>(legacy.ValidateExport);
        else legacy.ValidateExport();
        Assert.False(report.OrganizationIsStale);
        QuotationOrganization.ValidateSnapshot(legacy);
    }

    [Fact]
    public void EqualVisibleNamesUseOriginalOrderAndRenamesInvalidateSnapshotWithoutReordering()
    {
        var project = Project(); var second = Item(project, "Água", 4, 1, order: 2);
        var first = Item(project, "Água", 4, 1, order: 1);
        var report = Apply(project, [second, first]);
        Assert.Equal(first.Line.Id, report.Lines[0].Line.Id);
        var changed = report.Lines.Select(value => value.Line.Id == second.Line.Id
            ? value with { Line = value.Line with { DisplayName = "Abacate" } } : value).ToArray();
        var stale = report with { Lines = QuotationOrganization.Order(report.Project, changed) };
        Assert.True(stale.OrganizationIsStale);
        Assert.Equal(first.Line.Id, stale.Lines[0].Line.Id);
        Assert.Throws<InvalidOperationException>(stale.ValidateExport);
    }

    [Fact]
    public void PendingBasketOrMissingOrFractionalQuantityCannotBeOrganized()
    {
        var project = Project(); var item = Item(project, "Item", 4, 10);
        foreach (var line in new[] { item.Line with { SelectionConfirmed = false },
                     item.Line with { SelectedBasketKey = null }, item.Line with { RequestedQuantity = 0 },
                     item.Line with { RequestedQuantity = 4.5m } })
            Assert.Throws<InvalidOperationException>(() => QuotationOrganization.Calculate(project, [item with { Line = line }], []));
    }

    [Fact]
    public async Task ReopenEditDeleteAndReapplyPreserveIdentityAndDoNotChangeRepositoryOrder()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer());
        var project = await repository.CreateProjectAsync("Teste");
        var z = await AddConfirmedAsync(repository, service, project, "Zebra", 8, 6000);
        var a = await AddConfirmedAsync(repository, service, project, "Água", 8, 6000);
        var id = Guid.NewGuid();
        await service.SaveGroupsAsync(project.Id, [new QuotationGroup(id, project.Id, "Grupo", [z.Id, a.Id])]);
        await service.OrganizeProjectAsync(project.Id);
        service = new(new SqliteQuotationRepository(database.Repository.DatabasePath), new QuotationAnalyzer());
        var report = await service.GetReportAsync(project.Id);
        Assert.False(report.OrganizationIsStale);
        Assert.Equal("1 e 3", report.Project.Organization!.ItemNumbers(a.Id));
        Assert.Equal([z.Id, a.Id], (await repository.GetLinesAsync(project.Id)).Select(line => line.Id));
        var previous = report.Project.Organization.ToJson();
        await repository.RenameLineDisplayNameAsync(z.Id, "Abacate");
        report = await service.GetReportAsync(project.Id);
        Assert.True(report.OrganizationIsStale); Assert.Equal(previous, report.Project.Organization!.ToJson());
        await service.OrganizeProjectAsync(project.Id);
        Assert.Equal("1 e 3", (await service.GetReportAsync(project.Id)).Project.Organization!.ItemNumbers(z.Id));
        await repository.DeleteLineAsync(a.Id);
        Assert.Single(await repository.GetGroupsAsync(project.Id));
        Assert.True((await service.GetReportAsync(project.Id)).OrganizationIsStale);
        await repository.DeleteLineAsync(z.Id);
        Assert.Empty(await repository.GetGroupsAsync(project.Id));
    }

    [Theory]
    [InlineData(false, 90000.12)]
    [InlineData(true, 90000.1234)]
    public async Task MixedGroupKeepsSingleNumbersForSmallItemsAfterReopenAndExport(bool medication, decimal price)
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer());
        var project = await repository.CreateProjectAsync("Grupo misto");
        await repository.SetProjectMedicationAsync(project.Id, medication);
        int[] quantities = [1, 2, 3, 4, 5, 7, 8, 100];
        string[] numbers = ["1 e 6", "2 e 7", "3 e 8", "4 e 9", "5 e 10", "11", "12", "13"];
        var lines = new List<QuotationLine>();
        foreach (var quantity in quantities)
            lines.Add(await AddConfirmedAsync(repository, service, project, $"Item {quantity:D3}", quantity, price));
        await service.SaveGroupsAsync(project.Id,
            [new QuotationGroup(Guid.NewGuid(), project.Id, "Misto", lines.Select(line => line.Id).ToArray())]);
        await service.OrganizeProjectAsync(project.Id);
        var before = (await service.GetReportAsync(project.Id)).Project.Organization!.ToJson();

        var reopened = new QuotationService(new SqliteQuotationRepository(database.Repository.DatabasePath), new QuotationAnalyzer());
        var report = await reopened.GetReportAsync(project.Id);
        Assert.False(report.OrganizationIsStale);
        Assert.Equal(medication, report.Project.IsMedication);
        Assert.Equal(before, report.Project.Organization!.ToJson());
        QuotationOrganization.ValidateSnapshot(report);
        var orderedLines = lines.Skip(3).Concat(lines.Take(3)).ToArray();
        Assert.Equal(orderedLines.Select(line => line.Id), report.Lines.Select(value => value.Line.Id));
        Assert.Equal(numbers, Enumerable.Range(0, lines.Count).Select(report.ItemNumbers));
        Assert.All(report.Project.Organization.Positions, position => Assert.Equal(price, position.UnitPrice));
        foreach (var line in lines.Take(3))
        {
            var position = Assert.Single(report.Project.Organization.Positions, position => position.LineId == line.Id);
            Assert.Equal(QuotationQuotaKind.None, position.Kind);
            Assert.Equal(line.RequestedQuantity, position.Quantity);
        }

        var path = Path.Combine(database.Directory, "grupo-misto.xlsx");
        await new QuotationWorkbookService().ExportAsync(path, report, "Responsável");
        using var workbook = new XLWorkbook(path);
        var titles = workbook.Worksheet(1).Column(2).CellsUsed().Select(cell => cell.GetString())
            .Where(text => text.StartsWith("Item ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(orderedLines.Select((line, index) => $"Item {numbers[index]} - {line.EffectiveDisplayName}"), titles);
    }

    [Theory]
    [InlineData(false, QuotationPackageImportMode.Copy)]
    [InlineData(true, QuotationPackageImportMode.Copy)]
    [InlineData(false, QuotationPackageImportMode.PreserveIdentity)]
    [InlineData(false, QuotationPackageImportMode.Replace)]
    [InlineData(false, QuotationPackageImportMode.PreserveIdentity, true)]
    [InlineData(true, QuotationPackageImportMode.PreserveIdentity, true)]
    [InlineData(false, QuotationPackageImportMode.Replace, true)]
    [InlineData(false, QuotationPackageImportMode.Copy, false, 1)]
    [InlineData(false, QuotationPackageImportMode.Copy, false, 2)]
    [InlineData(false, QuotationPackageImportMode.PreserveIdentity, false, 1)]
    [InlineData(false, QuotationPackageImportMode.PreserveIdentity, false, 2)]
    [InlineData(false, QuotationPackageImportMode.Replace, false, 1)]
    [InlineData(false, QuotationPackageImportMode.Replace, false, 2)]
    public async Task PackagePreservesGroupsNumbersAndStaleness(bool stale, QuotationPackageImportMode mode,
        bool conflict = false, int smallCount = 0)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Grupos portáteis");
        var line = await AddConfirmedAsync(repo, service, project, "Item", 4, 25000);
        var other = await AddConfirmedAsync(repo, service, project, "Item", 8, 25000);
        var members = new List<Guid> { line.Id, other.Id };
        for (var quantity = 1; quantity <= smallCount; quantity++)
            members.Add((await AddConfirmedAsync(repo, service, project, $"Small {quantity}", quantity, 25000)).Id);
        await using (var connection = new SqliteConnection($"Data Source={source.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE quotation_lines SET display_order = 0;";
            await command.ExecuteNonQueryAsync();
        }
        await repo.SaveGroupsAsync(project.Id, [new(Guid.NewGuid(), project.Id, "Origem", members)]);
        await service.OrganizeProjectAsync(project.Id);
        if (stale) await repo.RenameLineDisplayNameAsync(line.Id, "Alterado");
        var before = await service.GetReportAsync(project.Id);
        var path = Path.Combine(source.Directory, "grupos.pncpcotacao");
        await new QuotationPackageService(source.Repository.DatabasePath, source.Directory).ExportAsync(path, project.Id);
        var importer = new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory);
        if (mode == QuotationPackageImportMode.Replace)
            await importer.ImportAsync(path, QuotationPackageImportMode.PreserveIdentity);
        if (conflict)
        {
            var destinationRepo = new SqliteQuotationRepository(destination.Repository.DatabasePath);
            var existing = await destinationRepo.CreateProjectAsync("Existente");
            if (mode == QuotationPackageImportMode.Replace)
            {
                await using var connection = new SqliteConnection($"Data Source={destination.Repository.DatabasePath}");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE quotation_lines SET project_id = $project, group_id = NULL WHERE id = $line;";
                command.Parameters.AddWithValue("$project", existing.Id.ToString("N"));
                command.Parameters.AddWithValue("$line", line.Id.ToString("N"));
                await command.ExecuteNonQueryAsync();
            }
            else
                await destinationRepo.SaveSampleAsync(existing.Id, line.Id, new("Conflito legado", 1, "un", null, null), []);
        }
        var result = await importer.ImportAsync(path, mode);
        var restored = await new QuotationService(new SqliteQuotationRepository(destination.Repository.DatabasePath), new QuotationAnalyzer())
            .GetReportAsync(result.ProjectId);
        Assert.Equal(stale, restored.OrganizationIsStale);
        Assert.Single(restored.Groups);
        Assert.Equal(smallCount > 1 ? 3 : 2, restored.Project.Organization!.Groups.Count);
        Assert.Equal("1 e 3", restored.ItemNumbers(0));
        Assert.Equal(before.Lines.Select(value => value.Line.RequestedQuantity), restored.Lines.Select(value => value.Line.RequestedQuantity));
        QuotationOrganization.ValidateSnapshot(restored);
        if (mode == QuotationPackageImportMode.Copy)
        {
            Assert.NotEqual(project.Id, restored.Project.Id); Assert.NotEqual(line.Id, restored.Lines[0].Line.Id);
            Assert.NotEqual(before.Groups[0].Id, restored.Groups[0].Id);
            Assert.DoesNotContain(restored.Project.Organization.Groups, group => before.Project.Organization!.Groups.Any(old => old.Id == group.Id));
        }
    }

    [Theory]
    [InlineData(BackupProfile.Full)] [InlineData(BackupProfile.Compact)]
    public async Task BackupPreservesAppliedOrganization(BackupProfile profile)
    {
        await using var database = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Backup dos grupos");
        var line = await AddConfirmedAsync(repo, service, project, "Item", 4, 25000);
        await repo.SaveGroupsAsync(project.Id, [new(Guid.NewGuid(), project.Id, "Origem", [line.Id])]);
        await service.OrganizeProjectAsync(project.Id);
        var json = (await service.GetReportAsync(project.Id)).Project.Organization!.ToJson();
        var path = Path.Combine(database.Directory, "grupos.pncpking");
        var backup = new BackupService(database.Repository);
        await backup.ExportAsync(path, profile);
        await repo.DeleteProjectAsync(project.Id);
        await backup.ImportAsync(path);
        var report = await service.GetReportAsync(project.Id);
        Assert.Equal(json, report.Project.Organization!.ToJson()); Assert.Single(report.Groups);
        Assert.False(report.OrganizationIsStale);
    }

    [Fact]
    public async Task ExportsUseSharedNumbersAndRejectStaleOrFractionalWithoutOverwriting()
    {
        await using var database = await TestDatabase.CreateAsync();
        var project = Project(); var groupId = Guid.NewGuid(); var precedingId = Guid.NewGuid();
        var preceding = Enumerable.Range(1, 35).Select(index => Item(project, $"A {index:D2}", index == 1 ? 1 : 4, 1000, precedingId)).ToArray();
        var lines = Enumerable.Range(1, 34).Select(index => Item(project, $"B {index:D2}", 4, 1000, groupId)).ToArray();
        var report = Apply(project, preceding.Concat(lines).ToArray(),
            new(precedingId, project.Id, "Anterior", preceding.Select(value => value.Line.Id).ToArray()),
            new(groupId, project.Id, "Grande", lines.Select(value => value.Line.Id).ToArray()));
        var xlsx = Path.Combine(database.Directory, "grande.xlsx");
        await new QuotationWorkbookService().ExportAsync(xlsx, report, "Responsável");
        using (var workbook = new XLWorkbook(xlsx))
        {
            var titles = workbook.Worksheet(1).Column(2).CellsUsed().Select(cell => cell.GetString())
                .Where(text => text.StartsWith("Item ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(69, titles.Length); Assert.Equal("Item 69 e 103 - B 01", titles[34]);
            Assert.Equal("Item 137 - A 01", titles[^1]);
        }
        var pdfPath = Path.Combine(database.Directory, "evidencias.pdf");
        var smallReport = Apply(project, [Item(project, "Teste", 4, 25000)]);
        await new QuotationEvidenceExportService(null!, null!, null!).ExportAsync(pdfPath, smallReport);
        using var actual = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
        var expectedPath = Path.Combine(database.Directory, "esperado.pdf");
        using (var writer = new EvidencePdfWriter())
        {
            writer.AddTextPage("Item 1 e 2 — Teste", ["Nenhum preço foi exportado; não há documentos para pesquisar."]);
            writer.Save(expectedPath);
        }
        using var expected = UglyToad.PdfPig.PdfDocument.Open(expectedPath);
        Assert.Equal(Assert.Single(expected.GetPage(1).GetImages()).RawBytes.ToArray(),
            Assert.Single(actual.GetPage(2).GetImages()).RawBytes.ToArray());
        var bytes = await File.ReadAllBytesAsync(xlsx);
        var changed = report with { Lines = report.Lines.Select(value => value with { Line = value.Line with { RequestedQuantity = 5 } }).ToArray() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new QuotationWorkbookService().ExportAsync(xlsx, changed, "Responsável"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(xlsx));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new QuotationWorkbookService().ExportAsync(xlsx,
            new(project, [Item(project, "Fração", 2.5m, 1)]), "Responsável"));
    }

    [Fact]
    public async Task FractionalInputIsRejectedAndLegacyFractionSurvivesMigrationForCorrection()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await repo.CreateProjectAsync("Inteiros");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.CreateLineAsync(project.Id, new("Fração", 1.5m, "unidade", null, null)));
        var line = await repo.CreateLineAsync(project.Id, new("Legado", 1, "unidade", null, null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.UpdateLineRequestedDetailsAsync(line.Id, 1.5m, "unidade"));
        await using (var connection = new SqliteConnection($"Data Source={database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER quotation_groups_remove_empty;
                DROP INDEX idx_quotation_lines_group;
                ALTER TABLE quotation_lines DROP COLUMN group_id;
                DROP TABLE quotation_groups;
                ALTER TABLE quotation_projects DROP COLUMN organization_json;
                UPDATE schema_info SET version = 31;
                UPDATE quotation_lines SET requested_quantity_scaled = 15000;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var migration = await database.Repository.InitializeAsync();
        Assert.Equal([32, 33], migration.AppliedMigrations);
        var report = await new QuotationService(repo, new QuotationAnalyzer()).GetReportAsync(project.Id);
        Assert.Null(report.Project.Organization); Assert.Equal(1.5m, Assert.Single(report.Lines).Line.RequestedQuantity);
        Assert.Throws<InvalidOperationException>(report.ValidateExport);
        Assert.Empty((await database.Repository.InitializeAsync()).AppliedMigrations);
    }

    private static async Task<QuotationLine> AddConfirmedAsync(SqliteQuotationRepository repository,
        QuotationService service, QuotationProject project, string name, decimal quantity, decimal price)
    {
        var id = Guid.NewGuid(); var today = DateOnly.FromDateTime(DateTime.Today);
        var refs = Enumerable.Range(1, 3).Select(index => new QuotationReference
        {
            Id = $"{id:N}-{index}", LineId = id, ContractId = $"{id:N}-{index}", ItemNumber = 1, ResultSequence = 1,
            SupplierTaxId = index.ToString("D14"), SupplierName = $"Fornecedor {index}", UnitPrice = price,
            ItemDescription = name, ItemUnit = "unidade", HomologatedQuantity = quantity,
            ItemRequestedQuantity = quantity, State = QuotationReferenceState.Eligible,
            ResultDate = today, PublicationDate = DateTimeOffset.Now, DistanceFromRibeiraoKilometers = 0
        }).ToArray();
        var line = await repository.SaveSampleAsync(project.Id, id, new(name, quantity, "unidade", null, null), refs);
        var analysis = (await service.GetAnalysisAsync(project.Id, id))!;
        var basket = analysis.Baskets.First(value => value.IsRecommended);
        await repository.ConfirmBasketAsync(id, basket.Key);
        return line;
    }

    [Fact]
    public async Task InvalidGroupsAndFailedOrganizationLeavePreviousDataUntouched()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Atomicidade");
        var line = await AddConfirmedAsync(repo, service, project, "Item", 4, 25000);
        var group = new QuotationGroup(Guid.NewGuid(), project.Id, "Origem", [line.Id]);
        await repo.SaveGroupsAsync(project.Id, [group]); await service.OrganizeProjectAsync(project.Id);
        var snapshot = (await service.GetReportAsync(project.Id)).Project.Organization!.ToJson();
        await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveGroupsAsync(project.Id, [group,
            new(Guid.NewGuid(), project.Id, "Duplicado", [line.Id])]));
        var foreign = await repo.CreateProjectAsync("Outra cotação");
        var foreignLine = await repo.CreateLineAsync(foreign.Id, new("Outro", 1, "unidade", null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveGroupsAsync(project.Id,
            [group with { LineIds = [foreignLine.Id] }]));
        Assert.Equal(group.Id, Assert.Single(await repo.GetGroupsAsync(project.Id)).Id);
        await repo.UpdateLineRequestedDetailsAsync(line.Id, 5, "unidade");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OrganizeProjectAsync(project.Id));
        Assert.Equal(snapshot, (await service.GetReportAsync(project.Id)).Project.Organization!.ToJson());
    }

    [Fact]
    public async Task SelectedMedianAndConversionDriveQuotaAndReferencesRemainShared()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Cesta adotada");
        var line = await AddConfirmedAsync(repo, service, project, "Item", 4, 50000);
        var refs = await repo.GetReferencesAsync(line.Id);
        var manual = await repo.SaveManualBasketAsync(line.Id, null, "Mediana convertida", refs.Select(value => value.Id).ToArray());
        await repo.SetManualBasketAggregationMethodAsync(manual.Id, QuotationAggregationMethod.Median);
        foreach (var reference in refs) await repo.SetManualBasketConversionFactorAsync(manual.Id, reference.Id, 0.5m);
        await repo.ConfirmBasketAsync(line.Id, manual.Key);
        await service.OrganizeProjectAsync(project.Id);
        var report = await service.GetReportAsync(project.Id);
        Assert.All(report.Project.Organization!.Positions, position => Assert.Equal(25000m, position.UnitPrice));
        Assert.Equal([3m, 1m], report.Project.Organization.Positions.Select(position => position.Quantity));
        var path = Path.Combine(database.Directory, "referencias.xlsx");
        await new QuotationWorkbookService().ExportAsync(path, report, "Responsável");
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Referências");
        Assert.Equal(4, sheet.LastRowUsed()!.RowNumber());
        Assert.All(Enumerable.Range(2, 3), row => Assert.Equal("1 e 2", sheet.Cell(row, 1).GetString()));
        await repo.SetProjectMedicationAsync(project.Id, true);
        Assert.True((await service.GetReportAsync(project.Id)).OrganizationIsStale);
    }

    [Fact]
    public async Task WorkbookImportRejectsFractionWithCellLocation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var path = Path.Combine(database.Directory, "fracao.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Itens");
            sheet.Cell(1, 1).Value = "Café"; sheet.Cell(1, 2).Value = "Café";
            sheet.Cell(1, 3).Value = 1.5m; sheet.Cell(1, 4).Value = "Unidade"; sheet.Cell(1, 7).Value = 1;
            workbook.SaveAs(path);
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new QuotationWorkbookImportService().ReadAsync(path));
        Assert.Contains("Itens!C1", error.Message); Assert.Contains("inteiro", error.Message);
    }
}
