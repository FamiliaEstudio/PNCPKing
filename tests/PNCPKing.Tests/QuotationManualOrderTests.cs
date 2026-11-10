using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed partial class QuotationOrganizationTests
{
    [Fact]
    public async Task ManualOrderNeedsNoBasketsPersistsAndWritesOnlyChangedPositions()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Sequência manual");
        var z = await repo.CreateLineAsync(project.Id, new("Zebra", 0, "", null, null));
        var a = await repo.CreateLineAsync(project.Id, new("Água", 0, "", null, null));
        var b = await repo.CreateLineAsync(project.Id, new("Banana", 0, "", null, null));
        var before = await repo.GetLinesAsync(project.Id);
        var other = await repo.CreateProjectAsync("Intocado");
        var foreign = await repo.CreateLineAsync(other.Id, new("Outro", 0, "", null, null));
        await using var connection = new SqliteConnection($"Data Source={source.Repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE order_writes(line_id TEXT);
            CREATE TRIGGER log_order_write AFTER UPDATE OF display_order ON quotation_lines
            BEGIN INSERT INTO order_writes VALUES(NEW.id); END;
            """;
        await command.ExecuteNonQueryAsync();
        var options = new QuotationOrganizationOptions(ManualOnly: true, ItemOrder: [b.Id, z.Id, a.Id]);
        await service.OrganizeProjectAsync(project.Id, options);
        command.CommandText = "SELECT COUNT(*) FROM order_writes;";
        Assert.Equal(3L, await command.ExecuteScalarAsync());
        await service.OrganizeProjectAsync(project.Id, options);
        Assert.Equal(3L, await command.ExecuteScalarAsync()); // No rewrites when the order is unchanged.
        service = new(new SqliteQuotationRepository(source.Repository.DatabasePath), new QuotationAnalyzer());
        var report = await service.GetReportAsync(project.Id);
        Assert.Equal([b.Id, z.Id, a.Id], report.Lines.Select(value => value.Line.Id));
        Assert.True(report.Project.ManualItemOrder);
        Assert.False(report.Project.SortItemsAlphabetically);
        Assert.Null(report.Project.Organization);
        Assert.All(report.Lines, value => Assert.Equal(before.Single(line => line.Id == value.Line.Id),
            value.Line with { DisplayOrder = before.Single(line => line.Id == value.Line.Id).DisplayOrder }));
        Assert.Equal(foreign, await repo.GetLineAsync(other.Id, foreign.Id));

        var xlsx = Path.Combine(source.Directory, "manual.xlsx");
        await new QuotationWorkbookService().ExportAsync(xlsx, report, "Responsável");
        using (var workbook = new XLWorkbook(xlsx))
            Assert.Equal(["Item 1 - Banana", "Item 2 - Zebra", "Item 3 - Água"],
                workbook.Worksheet(1).Column(2).CellsUsed().Select(cell => cell.GetString())
                    .Where(text => text.StartsWith("Item ", StringComparison.Ordinal)));
        var docx = Path.Combine(source.Directory, "manual.docx");
        await new QuotationWordService().ExportAsync(docx, report, new());
        using (var document = WordprocessingDocument.Open(docx, false))
        {
            var text = document.MainDocumentPart!.Document.InnerText;
            Assert.True(text.IndexOf("Banana", StringComparison.Ordinal) < text.IndexOf("Zebra", StringComparison.Ordinal));
            Assert.True(text.IndexOf("Zebra", StringComparison.Ordinal) < text.IndexOf("Água", StringComparison.Ordinal));
        }
        var package = Path.Combine(source.Directory, "manual.pncpcotacao");
        await new QuotationPackageService(source.Repository.DatabasePath, source.Directory).ExportAsync(package, project.Id);
        var copied = await new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory)
            .ImportAsync(package, QuotationPackageImportMode.Copy);
        var restoredService = new QuotationService(new SqliteQuotationRepository(destination.Repository.DatabasePath), new QuotationAnalyzer());
        var restored = await restoredService.GetReportAsync(copied.ProjectId);
        Assert.True(restored.Project.ManualItemOrder);
        Assert.Equal(["Banana", "Zebra", "Água"], restored.Lines.Select(value => value.Line.EffectiveDisplayName));
        var added = await restoredService.CreateLineAsync(copied.ProjectId, new("Abacate", 0, "", null, null));
        Assert.Equal(added.Line.Id, (await restoredService.GetReportAsync(copied.ProjectId)).Lines[^1].Line.Id);
        await service.OrganizeProjectAsync(project.Id, new QuotationOrganizationOptions(AlphabeticalOnly: true));
        report = await service.GetReportAsync(project.Id);
        Assert.False(report.Project.ManualItemOrder);
        Assert.Equal([a.Id, b.Id, z.Id], report.Lines.Select(value => value.Line.Id));
    }

    [Fact]
    public async Task InvalidOrOutdatedOrderAndFailedQuotasDoNotPartiallySave()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Atomicidade manual");
        var a = await repo.CreateLineAsync(project.Id, new("Item 1 - Água", 0, "", null, null));
        var b = await repo.CreateLineAsync(project.Id, new("Item 2 - Banana", 0, "", null, null));
        var other = await repo.CreateProjectAsync("Outro");
        var foreign = await repo.CreateLineAsync(other.Id, new("Outro", 0, "", null, null));
        var before = await repo.GetLinesAsync(project.Id);
        var beforeProject = (await repo.GetProjectsAsync()).Single(value => value.Id == project.Id);
        foreach (Guid[] order in new Guid[][] { [a.Id], [a.Id, a.Id], [foreign.Id, b.Id], [a.Id, b.Id, Guid.NewGuid()] })
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.OrganizeProjectAsync(project.Id,
                new QuotationOrganizationOptions(RemoveItemNumberPrefixes: true, ManualOnly: true, ItemOrder: order)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OrganizeProjectAsync(project.Id,
            new QuotationOrganizationOptions(RemoveItemNumberPrefixes: true, ItemOrder: [b.Id, a.Id])));
        Assert.Equal(before, await repo.GetLinesAsync(project.Id));
        Assert.Equal(beforeProject, (await repo.GetProjectsAsync()).Single(value => value.Id == project.Id));
        Assert.Equal(foreign, await repo.GetLineAsync(other.Id, foreign.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(true, true)]
    public async Task QuotasFollowChosenSequenceAndKeepItAcrossReapplyAndPackage(bool manual, bool sameApply = false)
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var repo = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var service = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Ordem antes das cotas");
        var z = await AddConfirmedAsync(repo, service, project, "Zebra", 8, 6000);
        var a = await AddConfirmedAsync(repo, service, project, "Água", 8, 6000);
        await service.SaveGroupsAsync(project.Id, [new(Guid.NewGuid(), project.Id, "Grupo", [a.Id, z.Id])]);
        await service.OrganizeProjectAsync(project.Id, new QuotationOrganizationOptions(AlphabeticalOnly: true));
        if (manual && !sameApply) await service.OrganizeProjectAsync(project.Id, new QuotationOrganizationOptions(ManualOnly: true, ItemOrder: [z.Id, a.Id]));
        var expected = manual ? new[] { z.Id, a.Id } : [a.Id, z.Id];
        var before = await service.GetReportAsync(project.Id);
        await service.OrganizeProjectAsync(project.Id, new QuotationOrganizationOptions(ItemOrder: sameApply ? expected : null));
        var report = await service.GetReportAsync(project.Id);
        Assert.Equal(expected, report.Project.Organization!.InputOrder);
        Assert.Equal(expected.Concat(expected), report.Project.Organization.Positions.Select(position => position.LineId));
        Assert.False(report.OrganizationIsStale);
        QuotationOrganization.ValidateSnapshot(report);
        foreach (var line in report.Lines)
        {
            var old = before.Lines.Single(value => value.Line.Id == line.Line.Id);
            Assert.Equal(old.Line with { DisplayOrder = line.Line.DisplayOrder }, line.Line);
            Assert.Equal(old.SelectedBasket!.AdoptedPrice, line.SelectedBasket!.AdoptedPrice);
            Assert.Equal(old.SelectedBasket.References.Select(value => (value.Id, value.UnitPrice)),
                line.SelectedBasket.References.Select(value => (value.Id, value.UnitPrice)));
        }
        await service.OrganizeProjectAsync(project.Id);
        Assert.Equal(expected, (await service.GetReportAsync(project.Id)).Lines.Select(value => value.Line.Id));
        var package = Path.Combine(source.Directory, "cotas-ordenadas.pncpcotacao");
        await new QuotationPackageService(source.Repository.DatabasePath, source.Directory).ExportAsync(package, project.Id);
        var copied = await new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory)
            .ImportAsync(package, QuotationPackageImportMode.Copy);
        var restored = await new QuotationService(new SqliteQuotationRepository(destination.Repository.DatabasePath), new QuotationAnalyzer())
            .GetReportAsync(copied.ProjectId);
        Assert.False(restored.OrganizationIsStale);
        QuotationOrganization.ValidateSnapshot(restored);
        Assert.Equal(report.Lines.Select(value => value.Line.EffectiveDisplayName), restored.Lines.Select(value => value.Line.EffectiveDisplayName));
        Assert.DoesNotContain(restored.Project.Organization!.InputOrder!, id => expected.Contains(id));
    }

    [Fact]
    public void CurrentSequenceOrdersGroupsMembersAndIndividualsWithoutChangingQuotaPriorities()
    {
        var project = Project(); var g1 = Guid.NewGuid(); var g2 = Guid.NewGuid(); var g3 = Guid.NewGuid();
        var z = Item(project, "Zebra", 8, 6000, g1);
        var y = Item(project, "Y", 8, 6000, g1);
        var a = Item(project, "Água", 8, 12000, g2);
        var cheap = Item(project, "A barato", 4, 1, g3);
        var looseZ = Item(project, "Z solto", 4, 1);
        var looseA = Item(project, "A solto", 4, 1);
        var quota = Item(project, "Cota individual", 8, 12000);
        QuotationLineAnalysis[] lines = [looseZ, cheap, z, looseA, a, y, quota];
        QuotationGroup[] groups = [new(g2, project.Id, "Segundo", [a.Line.Id]),
            new(g3, project.Id, "Sem cota", [cheap.Line.Id]), new(g1, project.Id, "Primeiro", [y.Line.Id, z.Line.Id])];
        var snapshot = QuotationOrganization.Calculate(project, lines, groups, preserveCurrentOrder: true);
        Assert.Equal([g1, g1, g2, g2, g3], snapshot.Groups.Select(group => group.SourceGroupId));
        Assert.Equal([z.Line.Id, y.Line.Id, z.Line.Id, y.Line.Id, a.Line.Id, a.Line.Id,
            cheap.Line.Id, quota.Line.Id, quota.Line.Id, looseZ.Line.Id, looseA.Line.Id], snapshot.Positions.Select(position => position.LineId));
        var report = new QuotationProjectReport(project with { Organization = snapshot }, lines) { Groups = groups };
        QuotationOrganization.ValidateSnapshot(report);
        Assert.Throws<InvalidDataException>(() => QuotationOrganization.ValidateSnapshot(report with
            { Project = report.Project with { Organization = snapshot with { InputOrder = [z.Line.Id, z.Line.Id] } } }));
    }
}
