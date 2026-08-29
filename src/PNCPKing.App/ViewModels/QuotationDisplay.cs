using PNCPKing.Core.Models;

namespace PNCPKing.App.ViewModels;

public sealed record QuotationProjectDisplay(QuotationProject Source)
{
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public override string ToString() => Name;
}

public sealed class QuotationLineDisplay(QuotationLineAnalysis analysis)
{
    public QuotationLineAnalysis Analysis { get; } = analysis;
    public QuotationLine Line => Analysis.Line;
    public string Description => Line.EffectiveDisplayName;
    public string TechnicalDescription => Line.Description;
    public string CatalogCode => Line.CatalogSelection?.Label ?? string.Empty;
    public string CatalogStatus => Line.CatalogSelection is { IsActive: false }
        ? "Código inativo — substitua a seleção"
        : Line.CatalogSelection?.Description ?? "Sem código atribuído";
    public string SearchText => Line.SearchText;
    public string RestrictiveSearchText => Line.PromptSet?.RestrictiveText ?? Line.SearchText;
    public string IntermediateSearchText => Line.PromptSet?.IntermediateText ?? string.Empty;
    public string BroadSearchText => Line.PromptSet?.BroadText ?? string.Empty;
    public string ActivePromptLevel => Line.PromptSet?.ActiveLevel switch
    {
        PromptMatchLevel.Intermediate => "Intermediário",
        PromptMatchLevel.Broad => "Amplo",
        _ => "Restritivo"
    };
    public int ContractsAtActiveLevel => Line.PromptSet?.ContractsAtActiveLevel ?? 0;
    public decimal? RequestedQuantity => Line.RequestedQuantity > 0
        ? Line.RequestedQuantity
        : null;
    public string RequestedUnit => Line.RequestedUnit;
    public int CollectedCount => Analysis.CollectedCount;
    public int EligibleCount => Analysis.EligibleCount;
    public int DuplicateCount => Analysis.DuplicateCount;
    public int RejectedCount => Analysis.RejectedCount;
    public int BasketCount => Analysis.Baskets.Count;
    public int BasketPoolCount => Analysis.BasketPoolCount;
    public int RequestedBasketSize => Line.RequestedBasketSize;
    public decimal? EstimatedUnitPrice => Line.EstimatedUnitPrice;
    public decimal? EstimatedTotalPrice => Line.EstimatedTotalPrice;
    public string EstimateStage => Line.EstimateStage switch
    {
        EstimateResolutionStage.Within25Percent => "Estimativa ±25%",
        EstimateResolutionStage.Within50Percent => "Estimativa ±50%",
        EstimateResolutionStage.Unrestricted => "Sem faixa",
        _ => "Não utilizada"
    };
    public int ContractsExamined => Line.SearchCheckpoint.ContractsExamined;
    public int BatchesCompleted => Line.SearchCheckpoint.BatchesCompleted;
    public int SampleVersion => Line.SampleVersion;
    public DateTimeOffset? SampledAt => Line.SampleVersion == 0 ? null : Line.SampledAt;
    public string WeightSummary => Line.Weights.ToString();
    public string Status => Analysis.CollectedCount == 0
        ? "Aguardando preços"
        : Line.SelectionConfirmed && Analysis.SelectedBasket is { } selected
        ? selected.IsIncomplete || !selected.IsValid
            ? "Resolvido com ressalva"
            : "Resolvido"
        : Analysis.Baskets.Count == 0
            ? "Sem cesta válida"
            : Line.SelectedBasketKey is not null && Analysis.SelectedBasket is not null
                ? "Requer reconfirmação"
                : Line.SelectedBasketKey is not null
                    ? "Escolha anterior inválida"
                    : "Aguardando escolha";
    public decimal? SelectedAveragePrice => Analysis.SelectedBasket?.AdoptedPrice;
    public string AutomationStatus => Line.AutomationState switch
    {
        QuotationAutomationItemState.Manual => "Manual",
        QuotationAutomationItemState.Pending => "Pendente",
        QuotationAutomationItemState.Running => "Executando",
        QuotationAutomationItemState.Completed => "Concluído",
        QuotationAutomationItemState.Insufficient => "Insuficiente",
        QuotationAutomationItemState.Failed => "Falha",
        QuotationAutomationItemState.CompletedWithWarning => "Concluído com ressalva",
        QuotationAutomationItemState.TimeExpired => "Prazo encerrado",
        _ => Line.AutomationState.ToString()
    };
    public string AutomationMessage => Line.AutomationMessage;
}

public sealed class QuotationBasketDisplay(QuotationBasket source, bool wasPreviouslySelected = false)
{
    public QuotationBasket Source { get; } = source;
    public bool WasPreviouslySelected { get; } = wasPreviouslySelected;
    public string Key => Source.Key;
    public decimal AveragePrice => Source.AveragePrice;
    public decimal AdoptedPrice => Source.AdoptedPrice;
    public string AggregationMethod => Source.AggregationMethod == QuotationAggregationMethod.Median
        ? "Mediana"
        : "Média";
    public decimal MinimumPrice => Source.MinimumPrice;
    public decimal MaximumPrice => Source.MaximumPrice;
    public decimal MaximumDeviationPercent => Source.MaximumDeviationPercent;
    public decimal Score => Source.Score;
    public string Type => Source.IsManual ? "Manual" : "Automática";
    public string Name => Source.IsManual ? Source.Name : string.Empty;
    public string Status => Source.VisualState switch
    {
        QuotationBasketVisualState.AutomaticRegular when Source.References.Count == 2 => "Resolvida com ressalva",
        QuotationBasketVisualState.AutomaticRegular when Source.IsIncomplete => "Reduzida",
        QuotationBasketVisualState.AutomaticRegular => "Regular",
        QuotationBasketVisualState.AutomaticHighDispersion when Source.References.Count == 2 => "Resolvida com ressalva",
        QuotationBasketVisualState.AutomaticHighDispersion => "Desvio > 25%",
        QuotationBasketVisualState.ManualIncomplete => "Incompleta",
        QuotationBasketVisualState.ManualRegular => "Regular",
        QuotationBasketVisualState.ManualInvalid => "Com ressalva",
        _ => string.Empty
    };
    public string ValidationMessage => Source.ValidationMessage;
    public int ReferenceCount => Source.References.Count;
    public string Background => Source.VisualState switch
    {
        QuotationBasketVisualState.AutomaticRegular => "#EAF7EE",
        QuotationBasketVisualState.AutomaticHighDispersion => "#FDECEC",
        QuotationBasketVisualState.ManualIncomplete or QuotationBasketVisualState.ManualRegular => "#EAF2FF",
        QuotationBasketVisualState.ManualInvalid => "#F3EAFB",
        _ => "Transparent"
    };
    public string Tags
    {
        get
        {
            var tags = new List<string>();
            if (Source.IsRecommended) tags.Add("Recomendada");
            if (Source.IsCheapest) tags.Add("Mais barata");
            if (Source.IsMostExpensive) tags.Add("Mais cara");
            if (Source.IsManual) tags.Add(Source.Name);
            if (WasPreviouslySelected) tags.Add("Escolhida anteriormente");
            return string.Join(" · ", tags);
        }
    }
}

public sealed class QuotationReferenceDisplay(QuotationReference source)
{
    public QuotationReference Source { get; } = source;
    public string SupplierName => Source.SupplierName;
    public string Id => Source.Id;
    public string SupplierTaxId => Source.SupplierTaxId;
    public decimal UnitPrice => Source.UnitPrice;
    public string ItemDescription => Source.ItemDescription;
    public string ItemUnit => Source.ItemUnit;
    public decimal? HomologatedQuantity => Source.HomologatedQuantity;
    public string Municipality => Source.Municipality;
    public string Uf => Source.Uf;
    public DateTimeOffset? PublicationDate => Source.PublicationDate;
    public string PortalUrl => Source.PortalUrl;
    public string MunicipalityRegion => string.IsNullOrWhiteSpace(Source.Uf)
        ? Source.Municipality
        : $"{Source.Municipality}/{Source.Uf}";
    public DateOnly? ResultDate => Source.ResultDate;
    public decimal AdequacyTotal => Source.Adequacy.Total;
    public decimal DescriptionScore => Source.Adequacy.DescriptionScore;
    public decimal UnitScore => Source.Adequacy.UnitScore;
    public decimal QuantityScore => Source.Adequacy.QuantityScore;
    public decimal ProximityScore => Source.Adequacy.ProximityScore;
    public decimal RecencyScore => Source.Adequacy.RecencyScore;
    public string State => Source.State switch
    {
        QuotationReferenceState.Eligible => "Elegível",
        QuotationReferenceState.Duplicate => "Duplicada",
        _ => "Descartada"
    };
    public string Explanation => $"{Source.StateReason} {Source.Adequacy.Explanation}";
    public string PromptLevel => Source.MatchedPromptLevel switch
    {
        PromptMatchLevel.Restrictive => "Restritivo",
        PromptMatchLevel.Intermediate => "Intermediário",
        PromptMatchLevel.Broad => "Amplo",
        _ => string.Empty
    };
    public string SourceLabel => Source.Source == QuotationReferenceSource.InternetIncisoIII
        ? "Inciso III"
        : "Inciso II";
    public string DisplayTitle =>
        $"{SourceLabel} · {SupplierName} · {UnitPrice:C4} · {ItemDescription}";
}

public sealed class QuotationPriceDisplayRow(
    QuotationReference source,
    bool isInSelectedBasket,
    decimal conversionFactor = 1m,
    decimal? effectiveUnitPrice = null) : ObservableObject
{
    private bool _isInSelectedBasket = isInSelectedBasket;

    public QuotationReference Source { get; } = source;
    public QuotationReferenceDisplay ReferenceDisplay => new(Source);
    public string Id => Source.Id;
    public string SourceLabel => Source.Source == QuotationReferenceSource.InternetIncisoIII
        ? "Inciso III"
        : "Inciso II";
    public DateTimeOffset? PublicationDate => Source.PublicationDate;
    public string Uf => Source.Uf;
    public string Description => Source.ItemDescription;
    public string Unit => Source.ItemUnit;
    public decimal? Quantity => Source.HomologatedQuantity ?? Source.ItemRequestedQuantity;
    public string SupplierTaxId => Source.SupplierTaxId;
    public string SupplierName => Source.SupplierName;
    public decimal UnitPrice => Source.UnitPrice;
    public decimal ConversionFactor { get; } = conversionFactor;
    public decimal EffectiveUnitPrice { get; } = effectiveUnitPrice ??
        QuotationMoney.TruncateToCents(source.UnitPrice * conversionFactor);
    public string State => Source.State switch
    {
        QuotationReferenceState.Eligible => "Elegível",
        QuotationReferenceState.Duplicate => "Duplicada",
        _ => "Descartada"
    };
    public QuotationReferenceState StateKind => Source.State;
    public decimal AdequacyTotal => Source.Adequacy.Total;
    public decimal DescriptionScore => Source.Adequacy.DescriptionScore;
    public decimal UnitScore => Source.Adequacy.UnitScore;
    public decimal QuantityScore => Source.Adequacy.QuantityScore;
    public decimal ProximityScore => Source.Adequacy.ProximityScore;
    public decimal RecencyScore => Source.Adequacy.RecencyScore;
    public string PromptLevel => Source.MatchedPromptLevel switch
    {
        PromptMatchLevel.Restrictive => "Restritivo",
        PromptMatchLevel.Intermediate => "Intermediário",
        PromptMatchLevel.Broad => "Amplo",
        _ => "Personalizado"
    };
    public string Explanation => $"{Source.StateReason} {Source.Adequacy.Explanation}".Trim();
    public string Municipality => Source.Municipality;
    public string PortalUrl => Source.PortalUrl;

    public bool IsInSelectedBasket
    {
        get => _isInSelectedBasket;
        set => SetProperty(ref _isInSelectedBasket, value);
    }
}
