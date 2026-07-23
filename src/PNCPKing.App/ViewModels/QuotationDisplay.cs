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
    public string Description => Line.Description;
    public string SearchText => Line.SearchText;
    public decimal RequestedQuantity => Line.RequestedQuantity;
    public string RequestedUnit => Line.RequestedUnit;
    public int CollectedCount => Analysis.CollectedCount;
    public int EligibleCount => Analysis.EligibleCount;
    public int DuplicateCount => Analysis.DuplicateCount;
    public int RejectedCount => Analysis.RejectedCount;
    public int BasketCount => Analysis.Baskets.Count;
    public int BasketPoolCount => Analysis.BasketPoolCount;
    public int SampleVersion => Line.SampleVersion;
    public DateTimeOffset SampledAt => Line.SampledAt;
    public string WeightSummary => Line.Weights.ToString();
    public string Status => Line.SelectionConfirmed && Analysis.SelectedBasket is not null
        ? "Resolvido"
        : Analysis.Baskets.Count == 0
            ? "Sem cesta válida"
            : Line.SelectedBasketKey is not null && Analysis.SelectedBasket is not null
                ? "Requer reconfirmação"
                : Line.SelectedBasketKey is not null
                    ? "Escolha anterior inválida"
                : "Aguardando escolha";
    public decimal? SelectedAveragePrice => Analysis.SelectedBasket?.AveragePrice;
    public string AutomationStatus => Line.AutomationState switch
    {
        QuotationAutomationItemState.Manual => "Manual",
        QuotationAutomationItemState.Pending => "Pendente",
        QuotationAutomationItemState.Running => "Executando",
        QuotationAutomationItemState.Completed => "Concluído",
        QuotationAutomationItemState.Insufficient => "Insuficiente",
        QuotationAutomationItemState.Failed => "Falha",
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
    public decimal MinimumPrice => Source.MinimumPrice;
    public decimal MaximumPrice => Source.MaximumPrice;
    public decimal MaximumDeviationPercent => Source.MaximumDeviationPercent;
    public decimal Score => Source.Score;
    public string Tags
    {
        get
        {
            var tags = new List<string>();
            if (Source.IsRecommended) tags.Add("Recomendada");
            if (Source.IsCheapest) tags.Add("Mais barata");
            if (Source.IsMostExpensive) tags.Add("Mais cara");
            if (WasPreviouslySelected) tags.Add("Escolhida anteriormente");
            return string.Join(" · ", tags);
        }
    }
}

public sealed class QuotationReferenceDisplay(QuotationReference source)
{
    public QuotationReference Source { get; } = source;
    public string SupplierName => Source.SupplierName;
    public string SupplierTaxId => Source.SupplierTaxId;
    public decimal UnitPrice => Source.UnitPrice;
    public string ItemDescription => Source.ItemDescription;
    public string ItemUnit => Source.ItemUnit;
    public decimal? HomologatedQuantity => Source.HomologatedQuantity;
    public string Municipality => Source.Municipality;
    public string Uf => Source.Uf;
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
}
