using PNCPKing.Core.Geography;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationService(
    IQuotationRepository repository,
    QuotationAnalyzer analyzer)
{
    public Task<IReadOnlyList<QuotationProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        repository.GetProjectsAsync(cancellationToken);

    public Task<QuotationProject> CreateProjectAsync(string name, CancellationToken cancellationToken = default) =>
        repository.CreateProjectAsync(name, cancellationToken);

    public Task RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default) =>
        repository.RenameProjectAsync(projectId, name, cancellationToken);

    public Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        repository.DeleteProjectAsync(projectId, cancellationToken);

    public Task DeleteLineAsync(Guid lineId, CancellationToken cancellationToken = default) =>
        repository.DeleteLineAsync(lineId, cancellationToken);

    public Task<QuotationAutomationRun> CreateAutomationRunAsync(
        Guid projectId,
        string outputPath,
        SearchGeoFilter geoFilter,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<QuotationImportItem> items,
        AdequacyWeights weights,
        CancellationToken cancellationToken = default) =>
        repository.CreateAutomationRunAsync(
            projectId,
            outputPath,
            geoFilter,
            startDate,
            endDate,
            items,
            weights,
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
            .Where(reference => IsWithinQuotationPriceRange(reference, input))
            .ToArray();
        var union = existingReferences
            .Where(reference => IsWithinQuotationPriceRange(reference, input))
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
            SampleVersion = (existingLine?.SampleVersion ?? 0) + 1,
            SampledAt = DateTimeOffset.UtcNow,
            SelectedBasketKey = existingLine?.SelectedBasketKey,
            SelectionConfirmed = false
        };
        var analysis = analyzer.Analyze(transientLine, union);
        var savedLine = await repository.SaveSampleAsync(
                projectId,
                id,
                input,
                analysis.References,
                cancellationToken)
            .ConfigureAwait(false);
        return analyzer.Analyze(savedLine, analysis.References);
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
            analyses.Add(analyzer.Analyze(line, references));
        }

        return analyses;
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
            PortalUrl = row.Contract.PortalUri.AbsoluteUri
        };
    }

    private static bool IsWithinQuotationPriceRange(
        QuotationReference reference,
        QuotationLineInput input) =>
        (input.MinimumUnitPrice is null || reference.UnitPrice >= input.MinimumUnitPrice.Value) &&
        (input.MaximumUnitPrice is null || reference.UnitPrice <= input.MaximumUnitPrice.Value);
}
