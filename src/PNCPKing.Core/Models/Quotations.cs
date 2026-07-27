using PNCPKing.Core.Quotations;

namespace PNCPKing.Core.Models;

public enum AdequacyWeightComponent
{
    Description,
    Unit,
    Quantity,
    Proximity,
    Recency
}

public sealed record AdequacyWeights(
    int Description,
    int Unit,
    int Quantity,
    int Proximity,
    int Recency)
{
    public static AdequacyWeights Default { get; } = new(50, 20, 10, 15, 5);
    public int Total => Description + Unit + Quantity + Proximity + Recency;

    public void Validate()
    {
        if (Description is < 0 or > 100 || Unit is < 0 or > 100 || Quantity is < 0 or > 100 ||
            Proximity is < 0 or > 100 || Recency is < 0 or > 100 || Total != 100)
        {
            throw new ArgumentException("Os cinco pesos do índice devem estar entre 0% e 100% e somar exatamente 100%.");
        }
    }

    public AdequacyWeights Rebalance(AdequacyWeightComponent changedComponent, int requestedValue)
    {
        Validate();
        var changedIndex = (int)changedComponent;
        var values = new[] { Description, Unit, Quantity, Proximity, Recency };
        values[changedIndex] = Math.Clamp(requestedValue, 0, 100);
        var available = 100 - values[changedIndex];
        var otherIndices = Enumerable.Range(0, values.Length).Where(index => index != changedIndex).ToArray();
        var otherTotal = otherIndices.Sum(index => this[index]);
        var basisTotal = otherTotal == 0 ? otherIndices.Length : otherTotal;
        var allocations = otherIndices
            .Select(index =>
            {
                var basis = otherTotal == 0 ? 1 : this[index];
                var exact = available * basis / (decimal)basisTotal;
                return new { Index = index, Exact = exact, Value = (int)Math.Floor(exact) };
            })
            .ToArray();
        foreach (var allocation in allocations)
        {
            values[allocation.Index] = allocation.Value;
        }

        var remaining = available - allocations.Sum(allocation => allocation.Value);
        foreach (var allocation in allocations
                     .OrderByDescending(allocation => allocation.Exact - allocation.Value)
                     .ThenBy(allocation => allocation.Index)
                     .Take(remaining))
        {
            values[allocation.Index]++;
        }

        return new AdequacyWeights(values[0], values[1], values[2], values[3], values[4]);
    }

    public int this[int index] => index switch
    {
        0 => Description,
        1 => Unit,
        2 => Quantity,
        3 => Proximity,
        4 => Recency,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public override string ToString() =>
        $"Descrição {Description}% · Unidade {Unit}% · Quantidade {Quantity}% · Proximidade {Proximity}% · Atualidade {Recency}%";
}

public sealed record QuotationProject(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record QuotationLine
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Description { get; init; }
    public required decimal RequestedQuantity { get; init; }
    public required string RequestedUnit { get; init; }
    public decimal? MinimumUnitPrice { get; init; }
    public decimal? MaximumUnitPrice { get; init; }
    public AdequacyWeights Weights { get; init; } = AdequacyWeights.Default;
    public int SampleVersion { get; init; }
    public DateTimeOffset SampledAt { get; init; }
    public string? SelectedBasketKey { get; init; }
    public bool SelectionConfirmed { get; init; }
    public string SearchText { get; init; } = string.Empty;
    public int RequestedBatchCount { get; init; } = 1;
    public int RequestedBasketSize { get; init; } = 3;
    public int DisplayOrder { get; init; }
    public Guid? AutomationRunId { get; init; }
    public QuotationAutomationItemState AutomationState { get; init; } = QuotationAutomationItemState.Manual;
    public string AutomationMessage { get; init; } = string.Empty;
    public decimal? EstimatedUnitPrice { get; init; }
    public decimal? EstimatedTotalPrice { get; init; }
    public bool UseEstimatedPrice { get; init; }
    public EstimateResolutionStage EstimateStage { get; init; } = EstimateResolutionStage.NotApplicable;
    public ItemSearchCheckpoint SearchCheckpoint { get; init; } = new();
    public ItemSearchPromptSet? PromptSet { get; init; }
}

public enum QuotationAutomationItemState
{
    Manual,
    Pending,
    Running,
    Completed,
    Insufficient,
    Failed,
    CompletedWithWarning,
    TimeExpired
}

public enum QuotationAutomationRunState
{
    Pending,
    Running,
    Completed,
    Cancelled,
    Failed,
    TimeExpired
}

public sealed record QuotationAutomationRun
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string OutputPath { get; init; }
    public required SearchGeoFilter GeoFilter { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public QuotationAutomationRunState State { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public QuotationAutomationMode Mode { get; init; } = QuotationAutomationMode.FixedBatches;
    public TimeSpan TimeBudget { get; init; }
    public TimeSpan ActiveElapsed { get; init; }
    public Guid? SourceDraftId { get; init; }
    public string SourcePdfSha256 { get; init; } = string.Empty;
    public int StrategyVersion { get; init; }
    public int UniqueContractsProcessed { get; init; }
    public int MatchedItems { get; init; }
    public int RevealedPrices { get; init; }
    public int ItemListCacheHits { get; init; }
    public int ItemListApiCalls { get; init; }
    public int ItemResultApiCalls { get; init; }
    public int FailedCalls { get; init; }
    public int ConsecutiveContractsWithoutResult { get; init; }
}

public sealed record QuotationImportItem(
    int SourceRow,
    string SearchText,
    string OutputDescription,
    decimal Quantity,
    string Unit,
    decimal? MinimumUnitPrice,
    decimal? MaximumUnitPrice,
    int BatchCount,
    int RequestedBasketSize = 3,
    decimal? EstimatedUnitPrice = null,
    decimal? EstimatedTotalPrice = null,
    bool UseEstimatedPrice = false,
    string IntermediateSearchText = "",
    string BroadSearchText = "",
    SearchPromptOrigin PromptOrigin = SearchPromptOrigin.Ai);

public sealed record QuotationImportDocument(
    string SourcePath,
    IReadOnlyList<QuotationImportItem> Items);

public enum QuotationReferenceState
{
    Eligible,
    Duplicate,
    Rejected
}

public enum QuotationReferenceSource
{
    PncpIncisoII,
    InternetIncisoIII
}

public sealed record AdequacyBreakdown(
    decimal DescriptionScore,
    decimal UnitScore,
    decimal QuantityScore,
    decimal ProximityScore,
    decimal RecencyScore,
    string Explanation)
{
    public decimal Total => DescriptionScore + UnitScore + QuantityScore + ProximityScore + RecencyScore;
}

public sealed record QuotationReference
{
    public required string Id { get; init; }
    public required Guid LineId { get; init; }
    public required string ContractId { get; init; }
    public required long ItemNumber { get; init; }
    public required long ResultSequence { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public string SupplierTaxId { get; init; } = string.Empty;
    public string SupplierType { get; init; } = string.Empty;
    public decimal? HomologatedQuantity { get; init; }
    public decimal UnitPrice { get; init; }
    public DateOnly? ResultDate { get; init; }
    public string ItemDescription { get; init; } = string.Empty;
    public string ItemAdditionalInformation { get; init; } = string.Empty;
    public string ItemUnit { get; init; } = string.Empty;
    public decimal? ItemRequestedQuantity { get; init; }
    public string ItemCategory { get; init; } = string.Empty;
    public string NcmNbsCode { get; init; } = string.Empty;
    public string NcmNbsDescription { get; init; } = string.Empty;
    public string CatalogCode { get; init; } = string.Empty;
    public string CatalogName { get; init; } = string.Empty;
    public string CatalogCategory { get; init; } = string.Empty;
    public string Organization { get; init; } = string.Empty;
    public string Municipality { get; init; } = string.Empty;
    public string Uf { get; init; } = string.Empty;
    public double? DistanceFromRibeiraoKilometers { get; init; }
    public DateTimeOffset? PublicationDate { get; init; }
    public string PortalUrl { get; init; } = string.Empty;
    public AdequacyBreakdown Adequacy { get; init; } = new(0, 0, 0, 0, 0, string.Empty);
    public QuotationReferenceState State { get; init; } = QuotationReferenceState.Rejected;
    public string StateReason { get; init; } = string.Empty;
    public string? DuplicateOfReferenceId { get; init; }
    public PromptMatchLevel? MatchedPromptLevel { get; init; }
    public string MatchedSearchText { get; init; } = string.Empty;
    public QuotationReferenceSource Source { get; init; } = QuotationReferenceSource.PncpIncisoII;
}

public sealed record EvidenceImageDescriptor
{
    public required string Sha256 { get; init; }
    public required string RelativePath { get; init; }
    public string MimeType { get; init; } = "image/png";
    public long ByteLength { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record InternetPriceDraft
{
    public required Guid Id { get; init; }
    public required Guid LineId { get; init; }
    public Guid? BasketId { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
    public decimal? UnitPrice { get; init; }
    public string Description { get; init; } = string.Empty;
    public string SupplierName { get; init; } = string.Empty;
    public string SupplierTaxId { get; init; } = string.Empty;
    public EvidenceImageDescriptor? PriceImage { get; init; }
    public EvidenceImageDescriptor? TaxIdImage { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public bool HasPriceImage => PriceImage is not null;
    public bool HasTaxIdImage => TaxIdImage is not null;
    public bool IsComplete =>
        Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" &&
        UnitPrice > 0 &&
        !string.IsNullOrWhiteSpace(Description) &&
        !string.IsNullOrWhiteSpace(SupplierName) &&
        QuotationAnalyzer.IsValidCnpj(SupplierTaxId) &&
        PriceImage is not null &&
        TaxIdImage is not null;
}

public sealed record InternetPriceEvidence
{
    public required Guid LineId { get; init; }
    public required string ReferenceId { get; init; }
    public required string SourceUrl { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required EvidenceImageDescriptor PriceImage { get; init; }
    public required EvidenceImageDescriptor TaxIdImage { get; init; }
}

public sealed record InternetPriceInput(
    string SourceUrl,
    decimal UnitPrice,
    string Description,
    string SupplierName,
    string SupplierTaxId,
    DateTimeOffset CapturedAt);

public sealed record QuotationBasket
{
    public required string Key { get; init; }
    public required IReadOnlyList<QuotationReference> References { get; init; }
    public required decimal AveragePrice { get; init; }
    public required decimal MinimumPrice { get; init; }
    public required decimal MaximumPrice { get; init; }
    public required decimal MaximumDeviationPercent { get; init; }
    public required decimal Score { get; init; }
    public QuotationBasketKind Kind { get; init; } = QuotationBasketKind.Automatic;
    public string Name { get; init; } = string.Empty;
    public Guid? ManualBasketId { get; init; }
    public int RequestedSize { get; init; } = 3;
    public QuotationBasketVisualState VisualState { get; init; } = QuotationBasketVisualState.AutomaticRegular;
    public string ValidationMessage { get; init; } = string.Empty;
    public bool IsRecommended { get; init; }
    public bool IsCheapest { get; init; }
    public bool IsMostExpensive { get; init; }

    public bool IsManual => Kind == QuotationBasketKind.Manual;
    public bool IsIncomplete => IsManual ? References.Count < 3 : References.Count < RequestedSize;
    public bool IsValid => !IsManual ||
        VisualState == QuotationBasketVisualState.ManualRegular;
}

public enum QuotationBasketKind
{
    Automatic,
    Manual
}

public enum QuotationBasketVisualState
{
    AutomaticRegular,
    AutomaticHighDispersion,
    ManualIncomplete,
    ManualRegular,
    ManualInvalid
}

public sealed record QuotationManualBasket
{
    public required Guid Id { get; init; }
    public required Guid LineId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> ReferenceIds { get; init; }
    public int DisplayOrder { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public string Key => $"manual:{Id:N}";
}

public sealed record QuotationLineAnalysis(
    QuotationLine Line,
    IReadOnlyList<QuotationReference> References,
    IReadOnlyList<QuotationBasket> Baskets,
    int CollectedCount,
    int EligibleCount,
    int DuplicateCount,
    int RejectedCount,
    int BasketPoolCount)
{
    public QuotationBasket? SelectedBasket => Line.SelectedBasketKey is null
        ? null
        : Baskets.FirstOrDefault(basket => basket.Key == Line.SelectedBasketKey);
}

public sealed record QuotationLineInput(
    string Description,
    decimal RequestedQuantity,
    string RequestedUnit,
    decimal? MinimumUnitPrice,
    decimal? MaximumUnitPrice)
{
    public AdequacyWeights Weights { get; init; } = AdequacyWeights.Default;
    public int RequestedBasketSize { get; init; } = 3;
}

public sealed record QuotationProjectReport(
    QuotationProject Project,
    IReadOnlyList<QuotationLineAnalysis> Lines);
