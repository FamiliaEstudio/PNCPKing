using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationTransferTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TransferOrCopyPreservesAllPricesWithNewIdentities(bool copy, bool medicationDestination)
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var service = new QuotationService(repository, new QuotationAnalyzer(new DateOnly(2026, 7, 21)));
        var source = await service.CreateProjectAsync("Origem");
        var destination = await service.CreateProjectAsync("Destino");
        await repository.SetProjectMedicationAsync(destination.Id, medicationDestination);
        var first = await repository.CreateLineAsync(destination.Id, new("Existente", 1, "un", null, null));
        var run = await repository.CreateAutomationRunAsync(source.Id, "saida.xlsx", "Responsável",
            SearchGeoFilter.All, new(2026, 1, 1), new(2026, 7, 21),
            [new QuotationImportItem(1, "cafe", "Café", 10, "pacote", null, null, 1)], AdequacyWeights.Default);
        var lineId = Assert.Single(await repository.GetLinesAsync(source.Id)).Id;
        await repository.SaveSampleAsync(source.Id, lineId, new("Café", 10, "pacote", null, null),
            Enumerable.Range(1, 5).Select(index => new QuotationReference
            {
                Id = "preco-" + index, LineId = lineId, ContractId = "contrato-" + index,
                ItemNumber = 1, ResultSequence = 1, UnitPrice = 100 + index,
                State = QuotationReferenceState.Eligible
            }).ToArray());
        var basket = await repository.SaveManualBasketAsync(lineId, null, "Escolhidos", ["preco-1", "preco-2", "preco-3"]);
        await repository.SetManualBasketConversionFactorAsync(basket.Id, "preco-1", 2);
        await repository.ConfirmBasketAsync(lineId, "manual:" + basket.Id.ToString("N"));
        await repository.RenameLineDisplayNameAsync(lineId, "Café para transferência");
        await repository.UpdateLineDocumentDetailsAsync(lineId, "123456", 2);
        await repository.SetLineCatalogSelectionAsync(lineId, new() { Kind = CatalogKind.Catmat, Code = "123456", Description = "Café" });
        var groupId = Guid.NewGuid();
        await repository.SaveGroupsAsync(source.Id, [new(groupId, source.Id, "Alimentos", [lineId])]);
        await repository.SaveProcessedContractAsync(
            new() { LineId = lineId, SearchText = "cafe", Checkpoint = new() { ContractsExamined = 42 } },
            [new() { LineId = lineId, ContractId = "encontrado", ItemNumber = 7, DiscoveredOrder = 1 }]);
        await repository.SaveWorkspaceFailureAsync(lineId, ItemSearchPromptSlot.Restrictive, "falhou", "Falha temporária");
        await repository.SaveItemSearchPromptSetAsync(new()
        {
            LineId = lineId, Version = 2, RestrictiveText = "café premium",
            IntermediateText = "café pacote", BroadText = "café"
        });
        await repository.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Cancelled, "Pausada");
        var before = (await repository.GetLineAsync(source.Id, lineId))!;
        var references = await repository.GetReferencesAsync(lineId);
        var baskets = await repository.GetManualBasketsAsync(lineId);
        var prompt = await repository.GetItemSearchPromptSetAsync(lineId);
        var sourceProject = (await repository.GetProjectsAsync()).Single(project => project.Id == source.Id);
        var packagePath = Path.Combine(database.Directory, "antes-transferencia.pncpcotacao");
        var packages = new QuotationPackageService(database.Repository.DatabasePath, database.Directory);
        await packages.ExportAsync(packagePath, source.Id);

        if (copy) await service.CopyLineAsync(source.Id, lineId, destination.Id);
        else await service.TransferLineAsync(source.Id, lineId, destination.Id);

        if (copy)
        {
            Assert.Equal(before, await repository.GetLineAsync(source.Id, lineId));
            Assert.Equal(references, await repository.GetReferencesAsync(lineId));
            Assert.Single(await repository.GetGroupsAsync(source.Id));
            Assert.Equal(sourceProject, (await repository.GetProjectsAsync()).Single(project => project.Id == source.Id));
        }
        else
        {
            Assert.Empty(await repository.GetLinesAsync(source.Id));
            Assert.Empty(await repository.GetGroupsAsync(source.Id));
        }
        Assert.Equal(2, (await repository.GetLinesAsync(destination.Id)).Count);
        Assert.NotNull(await repository.GetLineAsync(destination.Id, first.Id));
        var moved = (await repository.GetLinesAsync(destination.Id)).Single(line => line.Id != first.Id);
        Assert.NotEqual(lineId, moved.Id);
        var movedBasket = Assert.Single(await repository.GetManualBasketsAsync(moved.Id));
        Assert.NotEqual(basket.Id, movedBasket.Id);
        Assert.Equal(before with
        {
            Id = moved.Id, ProjectId = destination.Id, GroupId = null, DisplayOrder = first.DisplayOrder + 1,
            AutomationRunId = null, AutomationState = QuotationAutomationItemState.Manual,
            AutomationMessage = moved.AutomationMessage, SelectionConfirmed = !medicationDestination,
            SelectedBasketKey = movedBasket.Key, PromptSet = prompt with { LineId = moved.Id }
        }, moved);
        Assert.Equal(references.Select(reference => reference with { LineId = moved.Id }), await repository.GetReferencesAsync(moved.Id));
        Assert.Equal(baskets.Single().ReferenceIds, movedBasket.ReferenceIds);
        Assert.Equal(42, (await repository.GetWorkspaceAsync(moved.Id, ItemSearchPromptSlot.Restrictive))!.Checkpoint.ContractsExamined);
        Assert.Equal("encontrado", Assert.Single(await repository.GetWorkspaceHitsAsync(moved.Id, ItemSearchPromptSlot.Restrictive)).ContractId);
        Assert.Equal("falhou", Assert.Single(await repository.GetWorkspaceFailuresAsync(moved.Id, ItemSearchPromptSlot.Restrictive)).ContractId);
        Assert.Equal(prompt with { LineId = moved.Id }, await repository.GetItemSearchPromptSetAsync(moved.Id));
        await repository.DeleteProjectAsync(source.Id);
        var reopened = new SqliteQuotationRepository(database.Repository.DatabasePath);
        Assert.NotNull(await reopened.GetLineAsync(destination.Id, moved.Id));
        Assert.Equal(references.Select(reference => reference with { LineId = moved.Id }), await reopened.GetReferencesAsync(moved.Id));
        var restoredBasket = Assert.Single(await reopened.GetManualBasketsAsync(moved.Id));
        Assert.Equal(movedBasket.Id, restoredBasket.Id);
        Assert.Equal(2m, restoredBasket.ConversionFactors["preco-1"]);
        var imported = await packages.ImportAsync(packagePath, QuotationPackageImportMode.PreserveIdentity);
        Assert.DoesNotContain(imported.Warnings, warning => warning.Contains("Identificadores internos", StringComparison.Ordinal));
        Assert.NotNull(await repository.GetLineAsync(source.Id, lineId));
        Assert.Equal(moved, await repository.GetLineAsync(destination.Id, moved.Id));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("missing-destination")]
    [InlineData("wrong-source")]
    [InlineData("running-source")]
    [InlineData("running-destination")]
    [InlineData("running-item")]
    public async Task InvalidOrActiveTransferAndCopyLeaveItemAndProjectsUntouched(string scenario)
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var source = await repository.CreateProjectAsync("Origem");
        var destination = await repository.CreateProjectAsync("Destino");
        var line = await repository.CreateLineAsync(source.Id, new("Item", 5, "un", null, null));
        if (scenario.StartsWith("running-", StringComparison.Ordinal))
        {
            if (scenario == "running-item")
                await repository.UpdateAutomationItemStateAsync(line.Id, QuotationAutomationItemState.Running, "Ativo");
            else
            {
                var run = await repository.CreateAutomationRunAsync(
                    scenario == "running-source" ? source.Id : destination.Id, "saida.xlsx", "Responsável",
                    SearchGeoFilter.All, new(2026, 1, 1), new(2026, 7, 21),
                    [new QuotationImportItem(1, "outro", "Outro", 1, "un", null, null, 1)], AdequacyWeights.Default);
                await repository.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Running, "Ativa");
            }
        }
        var beforeProjects = await repository.GetProjectsAsync();
        var beforeLine = await repository.GetLineAsync(source.Id, line.Id);
        await Assert.ThrowsAnyAsync<Exception>(() => repository.TransferLineAsync(
            scenario == "wrong-source" ? Guid.NewGuid() : source.Id, line.Id,
            scenario == "same" ? source.Id : scenario == "missing-destination" ? Guid.NewGuid() : destination.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => repository.CopyLineAsync(
            scenario == "wrong-source" ? Guid.NewGuid() : source.Id, line.Id,
            scenario == "same" ? source.Id : scenario == "missing-destination" ? Guid.NewGuid() : destination.Id));
        Assert.Equal(beforeProjects, await repository.GetProjectsAsync());
        Assert.Equal(beforeLine, await repository.GetLineAsync(source.Id, line.Id));
        Assert.Null(await repository.GetLineAsync(destination.Id, line.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureDuringTransferOrCopyRollsBackBothProjects(bool copy)
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var source = await repository.CreateProjectAsync("Origem");
        var destination = await repository.CreateProjectAsync("Destino");
        var line = await repository.CreateLineAsync(source.Id, new("Item", 5, "un", null, null));
        await repository.SaveSampleAsync(source.Id, line.Id, new("Item", 5, "un", null, null),
            [new() { Id = "preco", LineId = line.Id, ContractId = "contrato", ItemNumber = 1, ResultSequence = 1, UnitPrice = 100 }]);
        var beforeProjects = await repository.GetProjectsAsync();
        var beforeLine = await repository.GetLineAsync(source.Id, line.Id);
        var beforeReferences = await repository.GetReferencesAsync(line.Id);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database.Repository.DatabasePath, ForeignKeys = true }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_item_copy BEFORE INSERT ON quotation_references
            BEGIN SELECT RAISE(ABORT, 'Falha simulada ao copiar preços.'); END;
            """;
        await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<SqliteException>(() => copy
            ? repository.CopyLineAsync(source.Id, line.Id, destination.Id)
            : repository.TransferLineAsync(source.Id, line.Id, destination.Id));

        Assert.Equal(beforeProjects, await repository.GetProjectsAsync());
        Assert.Equal(beforeLine, await repository.GetLineAsync(source.Id, line.Id));
        Assert.Equal(beforeReferences, await repository.GetReferencesAsync(line.Id));
        Assert.Empty(await repository.GetLinesAsync(destination.Id));
    }
}
