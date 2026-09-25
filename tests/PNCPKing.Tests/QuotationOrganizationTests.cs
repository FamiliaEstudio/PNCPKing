using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationOrganizationTests
{
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
    [InlineData(1, 0)] [InlineData(2, 0)] [InlineData(3, 0)] [InlineData(4, 1)] [InlineData(11, 2)]
    public void IntegerReservationNeverExceedsQuarterAndPreservesTotals(int quantity, int reserved)
    {
        var project = Project(); var item = Item(project, "Item", quantity, 90000);
        var positions = Apply(project, [item]).Project.Organization!.Positions;
        Assert.Equal((decimal)quantity, positions.Sum(position => position.Quantity));
        Assert.Equal(quantity * 90000m, positions.Sum(position => position.TotalPrice));
        Assert.Equal((decimal)reserved, positions.Where(position => position.Kind == QuotationQuotaKind.Reserved).Sum(position => position.Quantity));
        Assert.DoesNotContain(positions, position => position.Quantity == 0);
    }

    [Fact]
    public void GroupThresholdIncludesSmallMembersButZeroReservationDoesNotOccupyNumber()
    {
        var project = Project(); var id = Guid.NewGuid();
        var a = Item(project, "A", 2, 25000, id); var b = Item(project, "B", 4, 10000, id);
        var report = Apply(project, [b, a], new QuotationGroup(id, project.Id, "Grupo", [a.Line.Id, b.Line.Id]));
        Assert.Equal(2, report.Project.Organization!.Groups.Count);
        Assert.Equal("1", report.ItemNumbers(0)); Assert.Equal("2 e 3", report.ItemNumbers(1));
        var onlySmall = Apply(project, [a], new QuotationGroup(id, project.Id, "Grupo", [a.Line.Id]));
        Assert.Equal(QuotationQuotaKind.None, Assert.Single(onlySmall.Project.Organization!.Groups).Kind);
        var expensiveSmall = a with { Line = a.Line with { RequestedQuantity = 3 } };
        expensiveSmall = expensiveSmall with { Baskets = [expensiveSmall.Baskets[0] with { AdoptedPrice = 90000 }] };
        Assert.Equal(QuotationQuotaKind.None, Assert.Single(Apply(project, [expensiveSmall],
            new QuotationGroup(id, project.Id, "Grupo", [a.Line.Id])).Project.Organization!.Groups).Kind);
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
    [InlineData(false, QuotationPackageImportMode.Copy)]
    [InlineData(true, QuotationPackageImportMode.Copy)]
    [InlineData(false, QuotationPackageImportMode.PreserveIdentity)]
    [InlineData(false, QuotationPackageImportMode.Replace)]
    public async Task PackagePreservesGroupsNumbersAndStaleness(bool stale, QuotationPackageImportMode mode)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Grupos portáteis");
        var line = await AddConfirmedAsync(repo, service, project, "Item", 4, 25000);
        var other = await AddConfirmedAsync(repo, service, project, "Item", 8, 25000);
        await using (var connection = new SqliteConnection($"Data Source={source.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE quotation_lines SET display_order = 0;";
            await command.ExecuteNonQueryAsync();
        }
        await repo.SaveGroupsAsync(project.Id, [new(Guid.NewGuid(), project.Id, "Origem", [line.Id, other.Id])]);
        await service.OrganizeProjectAsync(project.Id);
        if (stale) await repo.RenameLineDisplayNameAsync(line.Id, "Alterado");
        var before = await service.GetReportAsync(project.Id);
        var path = Path.Combine(source.Directory, "grupos.pncpcotacao");
        await new QuotationPackageService(source.Repository.DatabasePath, source.Directory).ExportAsync(path, project.Id);
        var importer = new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory);
        if (mode == QuotationPackageImportMode.Replace)
            await importer.ImportAsync(path, QuotationPackageImportMode.PreserveIdentity);
        var result = await importer.ImportAsync(path, mode);
        var restored = await new QuotationService(new SqliteQuotationRepository(destination.Repository.DatabasePath), new QuotationAnalyzer())
            .GetReportAsync(result.ProjectId);
        Assert.Equal(stale, restored.OrganizationIsStale);
        Assert.Single(restored.Groups); Assert.Equal(2, restored.Project.Organization!.Groups.Count);
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
            Assert.Equal(69, titles.Length); Assert.Equal("Item 70 e 104 - B 01", titles[35]);
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
        Assert.Equal([32], migration.AppliedMigrations);
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
