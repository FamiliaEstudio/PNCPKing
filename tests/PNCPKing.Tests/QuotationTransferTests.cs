using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationTransferTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferPreservesAllPricesAndItemDataAfterOriginIsDeleted(bool medicationDestination)
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
        await repository.SaveWorkspaceAsync(new() { LineId = lineId, SearchText = "cafe", Checkpoint = new() { ContractsExamined = 42 } });
        await repository.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Cancelled, "Pausada");
        var before = (await repository.GetLineAsync(source.Id, lineId))!;
        var references = await repository.GetReferencesAsync(lineId);
        var baskets = await repository.GetManualBasketsAsync(lineId);

        await service.TransferLineAsync(source.Id, lineId, destination.Id);

        Assert.Empty(await repository.GetLinesAsync(source.Id));
        Assert.Empty(await repository.GetGroupsAsync(source.Id));
        Assert.Equal(2, (await repository.GetLinesAsync(destination.Id)).Count);
        Assert.NotNull(await repository.GetLineAsync(destination.Id, first.Id));
        var moved = (await repository.GetLineAsync(destination.Id, lineId))!;
        Assert.Equal(before with
        {
            ProjectId = destination.Id, GroupId = null, DisplayOrder = first.DisplayOrder + 1,
            AutomationRunId = null, AutomationState = QuotationAutomationItemState.Manual,
            AutomationMessage = moved.AutomationMessage, SelectionConfirmed = !medicationDestination
        }, moved);
        Assert.Equal(references, await repository.GetReferencesAsync(lineId));
        Assert.Equal(baskets.Select(value => value.Id), (await repository.GetManualBasketsAsync(lineId)).Select(value => value.Id));
        Assert.Equal(42, (await repository.GetWorkspaceAsync(lineId, ItemSearchPromptSlot.Restrictive))!.Checkpoint.ContractsExamined);
        await repository.DeleteProjectAsync(source.Id);
        var reopened = new SqliteQuotationRepository(database.Repository.DatabasePath);
        Assert.NotNull(await reopened.GetLineAsync(destination.Id, lineId));
        Assert.Equal(references, await reopened.GetReferencesAsync(lineId));
        var restoredBasket = Assert.Single(await reopened.GetManualBasketsAsync(lineId));
        Assert.Equal(basket.Id, restoredBasket.Id);
        Assert.Equal(2m, restoredBasket.ConversionFactors["preco-1"]);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("missing-destination")]
    [InlineData("wrong-source")]
    [InlineData("running-source")]
    [InlineData("running-destination")]
    [InlineData("running-item")]
    public async Task InvalidOrActiveTransferLeavesItemAndProjectsUntouched(string scenario)
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
        Assert.Equal(beforeProjects, await repository.GetProjectsAsync());
        Assert.Equal(beforeLine, await repository.GetLineAsync(source.Id, line.Id));
        Assert.Null(await repository.GetLineAsync(destination.Id, line.Id));
    }
}
