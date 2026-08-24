using PNCPKing.Core.Geography;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationService(
    IQuotationRepository repository,
    QuotationAnalyzer analyzer,
    IPerformanceTelemetry? performance = null)
{
    private readonly IPerformanceTelemetry _performance = performance ?? NullPerformanceTelemetry.Instance;
    public Task<IReadOnlyList<QuotationProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        repository.GetProjectsAsync(cancellationToken);

    public Task<QuotationProject> CreateProjectAsync(string name, CancellationToken cancellationToken = default) =>
        repository.CreateProjectAsync(name, cancellationToken);

    public Task RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default) =>
        repository.RenameProjectAsync(projectId, name, cancellationToken);

    public Task RenameLineDisplayNameAsync(
        Guid lineId,
        string displayName,
        CancellationToken cancellationToken = default) =>
        repository.RenameLineDisplayNameAsync(lineId, displayName, cancellationToken);

    public Task SetLineCatalogSelectionAsync(
        Guid lineId,
        QuotationCatalogSelection? selection,
        CancellationToken cancellationToken = default) =>
        repository.SetLineCatalogSelectionAsync(lineId, selection, cancellationToken);

    public Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        repository.DeleteProjectAsync(projectId, cancellationToken);

    public Task DeleteLineAsync(Guid lineId, CancellationToken cancellationToken = default) =>
        repository.DeleteLineAsync(lineId, cancellationToken);

    public Task<QuotationAutomationRun> CreateAutomationRunAsync(
        Guid projectId,
        string outputPath,
        string responsibleName,
        SearchGeoFilter geoFilter,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<QuotationImportItem> items,
        AdequacyWeights weights,
        CancellationToken cancellationToken = default) =>
        repository.CreateAutomationRunAsync(
            projectId,
            outputPath,
            responsibleName,
            geoFilter,
            startDate,
            endDate,
            items,
            weights,
            cancellationToken);

    public Task<QuotationAutomationRun> CreateTimedAutomationRunAsync(
        Guid projectId,
        SearchGeoFilter geoFilter,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<QuotationImportItem> items,
        AdequacyWeights weights,
        TimeSpan timeBudget,
        IReadOnlyList<string>? contractSearchPrompts = null,
        Guid? sourceDraftId = null,
        string? sourcePdfSha256 = null,
        CancellationToken cancellationToken = default) =>
        repository.CreateTimedAutomationRunAsync(
            projectId,
            geoFilter,
            startDate,
            endDate,
            items,
            weights,
            timeBudget,
            contractSearchPrompts,
            sourceDraftId,
            sourcePdfSha256,
            cancellationToken);

    public Task<QuotationAutomationRun?> GetLatestAutomationRunAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        repository.GetLatestAutomationRunAsync(projectId, cancellationToken);

    public Task RecoverInterruptedAutomationAsync(CancellationToken cancellationToken = default) =>
        repository.RecoverInterruptedAutomationAsync(cancellationToken);

    public Task UpdateAutomationItemStateAsync(
        Guid lineId,
        QuotationAutomationItemState state,
        string message,
        CancellationToken cancellationToken = default) =>
        repository.UpdateAutomationItemStateAsync(lineId, state, message, cancellationToken);

    public Task UpdateAutomationRunStateAsync(
        Guid runId,
        QuotationAutomationRunState state,
        string message,
        CancellationToken cancellationToken = default) =>
        repository.UpdateAutomationRunStateAsync(runId, state, message, cancellationToken);

    public Task SaveSearchCheckpointAsync(
        Guid lineId,
        ItemSearchCheckpoint checkpoint,
        CancellationToken cancellationToken = default) =>
        repository.SaveSearchCheckpointAsync(lineId, checkpoint, cancellationToken);

    public Task UpdateAutomationTimingAsync(
        Guid runId,
        TimeSpan activeElapsed,
        TimeSpan? newTimeBudget = null,
        CancellationToken cancellationToken = default) =>
        repository.UpdateAutomationTimingAsync(
            runId,
            activeElapsed,
            newTimeBudget,
            cancellationToken);

    public Task UpdateAutomationOutputPathAsync(
        Guid runId,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        repository.UpdateAutomationOutputPathAsync(runId, outputPath, cancellationToken);

    public Task UpdateAutomationResponsibleNameAsync(
        Guid runId,
        string responsibleName,
        CancellationToken cancellationToken = default) =>
        repository.UpdateAutomationResponsibleNameAsync(runId, responsibleName, cancellationToken);

    public Task UpgradeContractSearchStrategyAsync(
        Guid runId,
        int strategyVersion,
        CancellationToken cancellationToken = default) =>
        repository.UpgradeContractSearchStrategyAsync(
            runId,
            strategyVersion,
            cancellationToken);

    public Task LinkAutomationDraftAsync(
        Guid runId,
        Guid draftId,
        string pdfSha256,
        CancellationToken cancellationToken = default) =>
        repository.LinkAutomationDraftAsync(runId, draftId, pdfSha256, cancellationToken);

    public Task<ItemSearchPromptSet> GetItemSearchPromptSetAsync(
        Guid lineId,
        CancellationToken cancellationToken = default) =>
        repository.GetItemSearchPromptSetAsync(lineId, cancellationToken);

    public Task SaveItemSearchPromptSetAsync(
        ItemSearchPromptSet promptSet,
        CancellationToken cancellationToken = default) =>
        repository.SaveItemSearchPromptSetAsync(promptSet, cancellationToken);

    public Task UpdateItemSearchPromptProgressAsync(
        Guid lineId,
        PromptMatchLevel activeLevel,
        int contractsAtActiveLevel,
        int matchedItems,
        int revealedPrices,
        CancellationToken cancellationToken = default) =>
        repository.UpdateItemSearchPromptProgressAsync(
            lineId,
            activeLevel,
            contractsAtActiveLevel,
            matchedItems,
            revealedPrices,
            cancellationToken);

    public Task<IReadOnlyList<ContractSearchPrompt>> GetContractSearchPromptsAsync(
        Guid runId,
        CancellationToken cancellationToken = default) =>
        repository.GetContractSearchPromptsAsync(runId, cancellationToken);

    public Task SaveContractSearchPromptAsync(
        ContractSearchPrompt prompt,
        CancellationToken cancellationToken = default) =>
        repository.SaveContractSearchPromptAsync(prompt, cancellationToken);

    public Task<IReadOnlyList<ContractSearchCheckpoint>> GetProcessedContractsAsync(
        Guid runId,
        CancellationToken cancellationToken = default) =>
        repository.GetProcessedContractsAsync(runId, cancellationToken);

    public Task SaveProcessedContractAsync(
        ContractSearchCheckpoint checkpoint,
        TimedQuotationProgress progress,
        CancellationToken cancellationToken = default) =>
        repository.SaveProcessedContractAsync(checkpoint, progress, cancellationToken);

    public Task<IReadOnlyList<ItemSearchPromptSet>> GetPendingPromptRevalidationsAsync(
        Guid runId,
        CancellationToken cancellationToken = default) =>
        repository.GetPendingPromptRevalidationsAsync(runId, cancellationToken);

    public Task MarkPromptRevalidatedAsync(
        Guid runId,
        Guid lineId,
        int promptVersion,
        CancellationToken cancellationToken = default) =>
        repository.MarkPromptRevalidatedAsync(runId, lineId, promptVersion, cancellationToken);

    public async Task<QuotationLineAnalysis> CaptureSampleAsync(
        Guid projectId,
        Guid? lineId,
        QuotationLineInput input,
        IReadOnlyList<ItemSearchRow> collectedRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collectedRows);
        var existingLine = lineId is null
            ? null
            : (await repository.GetLinesAsync(projectId, cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(line => line.Id == lineId);
        if (lineId is not null && existingLine is null)
        {
            throw new InvalidOperationException("O item selecionado não pertence ao projeto atual.");
        }

        var id = lineId ?? Guid.NewGuid();
        var existingReferences = existingLine is null
            ? []
            : await repository.GetReferencesAsync(id, cancellationToken).ConfigureAwait(false);
        var currentReferences = collectedRows
            .Where(row => row.PriceState == ItemSearchPriceState.Homologated &&
                          row.Result is { IsActive: true, HomologatedUnitValue: > 0 })
            .Select(row => MapReference(id, row))
            .ToArray();
        var union = existingReferences
            .Concat(currentReferences)
            .GroupBy(reference => reference.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        var transientLine = new QuotationLine
        {
            Id = id,
            ProjectId = projectId,
            Description = input.Description.Trim(),
            RequestedQuantity = input.RequestedQuantity,
            RequestedUnit = input.RequestedUnit.Trim(),
            MinimumUnitPrice = input.MinimumUnitPrice,
            MaximumUnitPrice = input.MaximumUnitPrice,
            Weights = input.Weights,
            RequestedBasketSize = input.RequestedBasketSize,
            SampleVersion = (existingLine?.SampleVersion ?? 0) + 1,
            SampledAt = DateTimeOffset.UtcNow,
            SelectedBasketKey = existingLine?.SelectedBasketKey,
            SelectionConfirmed = false,
            SearchText = existingLine?.SearchText ?? input.Description.Trim(),
            RequestedBatchCount = existingLine?.RequestedBatchCount ?? 1,
            DisplayOrder = existingLine?.DisplayOrder ?? 0,
            AutomationRunId = existingLine?.AutomationRunId,
            AutomationState = existingLine?.AutomationState ?? QuotationAutomationItemState.Manual,
            AutomationMessage = existingLine?.AutomationMessage ?? string.Empty,
            EstimatedUnitPrice = existingLine?.EstimatedUnitPrice,
            EstimatedTotalPrice = existingLine?.EstimatedTotalPrice,
            UseEstimatedPrice = existingLine?.UseEstimatedPrice ?? false,
            EstimateStage = existingLine?.EstimateStage ?? EstimateResolutionStage.NotApplicable,
            SearchCheckpoint = existingLine?.SearchCheckpoint ?? new ItemSearchCheckpoint(),
            PromptSet = existingLine?.PromptSet
        };
        var manualBaskets = existingLine is null
            ? []
            : await repository.GetManualBasketsAsync(id, cancellationToken).ConfigureAwait(false);
        var analysis = analyzer.Analyze(transientLine, union, manualBaskets);
        var savedLine = await repository.SaveSampleAsync(
                projectId,
                id,
                input,
                analysis.References,
                cancellationToken)
            .ConfigureAwait(false);
        return analyzer.Analyze(savedLine, analysis.References, manualBaskets);
    }

    public async Task<IReadOnlyList<QuotationLineAnalysis>> GetAnalysesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var lines = await repository.GetLinesAsync(projectId, cancellationToken).ConfigureAwait(false);
        var analyses = new List<QuotationLineAnalysis>(lines.Count);
        foreach (var line in lines)
        {
            var references = await repository.GetReferencesAsync(line.Id, cancellationToken).ConfigureAwait(false);
            var manualBaskets = await repository.GetManualBasketsAsync(line.Id, cancellationToken).ConfigureAwait(false);
            analyses.Add(analyzer.Analyze(line, references, manualBaskets));
        }

        return analyses;
    }

    public async Task<QuotationLineAnalysis?> GetAnalysisAsync(
        Guid projectId,
        Guid lineId,
        CancellationToken cancellationToken = default)
    {
        using var span = _performance.Begin("quotation-item", "analysis-load");
        var line = await repository.GetLineAsync(projectId, lineId, cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            span.Complete();
            return null;
        }

        var references = await repository.GetReferencesAsync(lineId, cancellationToken).ConfigureAwait(false);
        var manualBaskets = await repository.GetManualBasketsAsync(lineId, cancellationToken).ConfigureAwait(false);
        var analysis = analyzer.Analyze(line, references, manualBaskets);
        span.Complete(references.Count + manualBaskets.Count + 1);
        return analysis;
    }

    public async Task ConfirmBasketAsync(
        QuotationLineAnalysis analysis,
        string basketKey,
        CancellationToken cancellationToken = default)
    {
        if (analysis.Baskets.All(basket => basket.Key != basketKey))
        {
            throw new ArgumentException("A cesta escolhida não pertence à versão atual da amostra.", nameof(basketKey));
        }

        await repository.ConfirmBasketAsync(analysis.Line.Id, basketKey, cancellationToken).ConfigureAwait(false);
    }

    public Task UpdateWeightsAsync(
        Guid lineId,
        AdequacyWeights weights,
        CancellationToken cancellationToken = default) =>
        repository.UpdateWeightsAsync(lineId, weights, cancellationToken);

    public async Task<(QuotationLineAnalysis Analysis, QuotationManualBasket Basket)> SaveManualBasketAsync(
        Guid projectId,
        Guid? lineId,
        QuotationLineInput input,
        Guid? basketId,
        string name,
        IReadOnlyList<ItemSearchRow> selectedRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedRows);
        var rows = selectedRows
            .Where(row => row.PriceState == ItemSearchPriceState.Homologated &&
                          row.Result is { IsActive: true, HomologatedUnitValue: > 0 })
            .GroupBy(
                row => $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (rows.Length == 0)
        {
            throw new ArgumentException(
                "Selecione pelo menos um resultado homologado ativo com preço positivo.",
                nameof(selectedRows));
        }

        var id = lineId ?? Guid.NewGuid();
        var existingReferences = lineId is null
            ? []
            : await repository.GetReferencesAsync(id, cancellationToken).ConfigureAwait(false);
        var selectedReferences = rows.Select(row => MapReference(id, row)).ToArray();
        var union = existingReferences
            .Concat(selectedReferences)
            .GroupBy(reference => reference.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        var existingManualBaskets = lineId is null
            ? []
            : await repository.GetManualBasketsAsync(id, cancellationToken).ConfigureAwait(false);
        var existingBasket = basketId is null
            ? null
            : existingManualBaskets.SingleOrDefault(basket => basket.Id == basketId.Value)
              ?? throw new InvalidOperationException("A cesta manual selecionada não pertence ao item.");
        var memberIds = (existingBasket?.ReferenceIds ?? [])
            .Concat(selectedReferences.Select(reference => reference.Id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var transientLine = new QuotationLine
        {
            Id = id,
            ProjectId = projectId,
            Description = input.Description.Trim(),
            RequestedQuantity = input.RequestedQuantity,
            RequestedUnit = input.RequestedUnit.Trim(),
            MinimumUnitPrice = input.MinimumUnitPrice,
            MaximumUnitPrice = input.MaximumUnitPrice,
            Weights = input.Weights,
            RequestedBasketSize = input.RequestedBasketSize,
            SampleVersion = 1,
            SampledAt = DateTimeOffset.UtcNow
        };
        var scored = analyzer.Analyze(transientLine, union, existingManualBaskets);
        var savedLine = await repository.SaveSampleAsync(
                projectId,
                id,
                input,
                scored.References,
                cancellationToken)
            .ConfigureAwait(false);
        var manualBasket = await repository.SaveManualBasketAsync(
                savedLine.Id,
                basketId,
                name,
                memberIds,
                cancellationToken)
            .ConfigureAwait(false);
        var allManualBaskets = await repository.GetManualBasketsAsync(savedLine.Id, cancellationToken)
            .ConfigureAwait(false);
        var analysis = analyzer.Analyze(savedLine, scored.References, allManualBaskets);
        return (analysis, manualBasket);
    }

    public Task RenameManualBasketAsync(
        Guid basketId,
        string name,
        CancellationToken cancellationToken = default) =>
        repository.RenameManualBasketAsync(basketId, name, cancellationToken);

    public Task RemoveManualBasketReferenceAsync(
        Guid basketId,
        string referenceId,
        CancellationToken cancellationToken = default) =>
        repository.RemoveManualBasketReferenceAsync(basketId, referenceId, cancellationToken);

    public Task SetManualBasketAggregationMethodAsync(
        Guid basketId,
        QuotationAggregationMethod aggregationMethod,
        CancellationToken cancellationToken = default) =>
        repository.SetManualBasketAggregationMethodAsync(
            basketId,
            aggregationMethod,
            cancellationToken);

    public Task SetManualBasketConversionFactorAsync(
        Guid basketId,
        string referenceId,
        decimal conversionFactor,
        CancellationToken cancellationToken = default) =>
        repository.SetManualBasketConversionFactorAsync(
            basketId,
            referenceId,
            conversionFactor,
            cancellationToken);

    public async Task<QuotationManualBasket> AddManualBasketReferenceAsync(
        Guid lineId,
        Guid basketId,
        string referenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        var references = await repository.GetReferencesAsync(lineId, cancellationToken)
            .ConfigureAwait(false);
        if (references.All(reference =>
                !string.Equals(reference.Id, referenceId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A referência não pertence ao item da cotação.");
        }

        var basket = (await repository.GetManualBasketsAsync(lineId, cancellationToken)
                .ConfigureAwait(false))
            .SingleOrDefault(value => value.Id == basketId)
            ?? throw new InvalidOperationException("A cesta manual não pertence ao item.");
        return await repository.SaveManualBasketAsync(
                lineId,
                basket.Id,
                basket.Name,
                basket.ReferenceIds
                    .Append(referenceId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<QuotationManualBasket> CreateManualBasketAsync(
        Guid lineId,
        string name,
        IReadOnlyList<string> referenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(referenceIds);
        var available = (await repository.GetReferencesAsync(lineId, cancellationToken)
                .ConfigureAwait(false))
            .Select(reference => reference.Id)
            .ToHashSet(StringComparer.Ordinal);
        var members = referenceIds
            .Where(available.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (members.Length == 0)
        {
            throw new InvalidOperationException(
                "Selecione ao menos uma referência pertencente ao item.");
        }

        return await repository.SaveManualBasketAsync(
                lineId,
                null,
                name.Trim(),
                members,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task DeleteManualBasketAsync(
        Guid basketId,
        CancellationToken cancellationToken = default) =>
        repository.DeleteManualBasketAsync(basketId, cancellationToken);

    public async Task<QuotationManualBasket> CreateManualCopyAsync(
        QuotationLineAnalysis analysis,
        QuotationBasket source,
        string? name = null,
        string? excludedReferenceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(source);
        if (analysis.Baskets.All(basket => basket.Key != source.Key))
        {
            throw new InvalidOperationException("A cesta não pertence ao item informado.");
        }

        if (source.IsManual && source.ManualBasketId is { } existingId &&
            string.IsNullOrWhiteSpace(excludedReferenceId))
        {
            return (await repository.GetManualBasketsAsync(
                    analysis.Line.Id,
                    cancellationToken).ConfigureAwait(false))
                .Single(basket => basket.Id == existingId);
        }

        var references = source.References
            .Where(reference =>
                !string.Equals(reference.Id, excludedReferenceId, StringComparison.Ordinal))
            .Select(reference => reference.Id)
            .ToArray();
        if (references.Length == 0)
        {
            throw new InvalidOperationException("A cópia manual ficaria sem preços.");
        }

        var manualBaskets = await repository.GetManualBasketsAsync(
            analysis.Line.Id,
            cancellationToken).ConfigureAwait(false);
        var effectiveName = string.IsNullOrWhiteSpace(name)
            ? NextManualBasketName(manualBaskets)
            : name.Trim();
        return await repository.SaveManualBasketAsync(
            analysis.Line.Id,
            null,
            effectiveName,
            references,
            cancellationToken).ConfigureAwait(false);
    }

    private static string NextManualBasketName(IReadOnlyList<QuotationManualBasket> baskets)
    {
        var names = baskets.Select(basket => basket.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; ; number++)
        {
            var candidate = $"Manual {number:N0}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    public async Task<QuotationProjectReport> GetReportAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = (await repository.GetProjectsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == projectId)
            ?? throw new InvalidOperationException("O projeto de cotação não existe mais.");
        var lines = await GetAnalysesAsync(projectId, cancellationToken).ConfigureAwait(false);
        return new QuotationProjectReport(project, lines);
    }

    private static QuotationReference MapReference(Guid lineId, ItemSearchRow row)
    {
        var result = row.Result ?? throw new InvalidOperationException("Uma referência homologada deve possuir resultado.");
        var distance = row.Contract.DistanceFromRibeiraoKilometers;
        if (distance is null && BrazilMunicipalityCatalog.TryResolve(
                row.Contract.MunicipalityIbgeCode,
                row.Contract.Municipality,
                row.Contract.Uf,
                out var municipality))
        {
            distance = municipality.DistanceFromRibeiraoKilometers;
        }

        return new QuotationReference
        {
            Id = $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{result.ResultSequence}",
            LineId = lineId,
            ContractId = row.Contract.PncpId,
            ItemNumber = row.Item.ItemNumber,
            ResultSequence = result.ResultSequence,
            SupplierName = result.SupplierName,
            SupplierTaxId = result.SupplierTaxId,
            SupplierType = result.SupplierType,
            SupplierMunicipality = result.SupplierMunicipality,
            SupplierUf = result.SupplierUf,
            HomologatedQuantity = result.HomologatedQuantity,
            UnitPrice = result.HomologatedUnitValue!.Value,
            ResultDate = result.ResultDate,
            ItemDescription = row.Item.Description,
            ItemAdditionalInformation = row.Item.AdditionalInformation,
            ItemUnit = row.Item.Unit,
            ItemRequestedQuantity = row.Item.RequestedQuantity,
            ItemCategory = row.Item.Category,
            NcmNbsCode = row.Item.NcmNbsCode,
            NcmNbsDescription = row.Item.NcmNbsDescription,
            CatalogCode = row.Item.CatalogCode,
            CatalogName = row.Item.CatalogName,
            CatalogCategory = row.Item.CatalogCategory,
            Organization = row.Contract.Organization,
            Municipality = row.Contract.Municipality,
            Uf = row.Contract.Uf,
            DistanceFromRibeiraoKilometers = distance,
            PublicationDate = row.Contract.PublicationDate,
            PortalUrl = row.Contract.PortalUri.AbsoluteUri,
            MatchedPromptLevel = row.MatchedPromptLevel,
            MatchedSearchText = row.MatchedSearchText
        };
    }

}
