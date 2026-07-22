using PNCPKing.Core.Models;
using PNCPKing.Core.Geography;

namespace PNCPKing.App.ViewModels;

public sealed record DateRangeOption(string Label, int? Days, bool IsCustom = false)
{
    public override string ToString() => Label;
}

public sealed record SearchSortOption(string Label, SearchSort Value)
{
    public override string ToString() => Label;
}

public sealed class ItemSearchDisplayRow
{
    public ItemSearchDisplayRow(ItemSearchRow source)
    {
        Source = source;
        if (source.Contract.DistanceFromRibeiraoKilometers is { } storedDistance)
        {
            DistanceFromRibeiraoKilometers = storedDistance;
        }
        else if (NearbyRibeiraoCatalog.TryGetByNameAndUf(
                source.Contract.Municipality,
                source.Contract.Uf,
                out var municipality))
        {
            DistanceFromRibeiraoKilometers = municipality.DistanceFromRibeiraoKilometers;
        }
    }

    public ItemSearchRow Source { get; }
    public ContractRecord Contract => Source.Contract;
    public ProcurementItem Item => Source.Item;
    public HomologationResult? Result => Source.Result;
    public decimal? HomologatedQuantity => Source.HomologatedQuantity;
    public decimal? HomologatedUnitValue => Source.HomologatedUnitValue;
    public decimal? HomologatedTotalValue => Source.HomologatedTotalValue;
    public string Supplier => Source.Result?.SupplierName ?? string.Empty;
    public string SupplierTaxId => Source.Result?.SupplierTaxId ?? string.Empty;
    public DateOnly? ResultDate => Source.Result?.ResultDate;
    public string DisplayStatus => Source.DisplayStatus;
    public bool IsCancelled => Source.PriceState == ItemSearchPriceState.Cancelled;
    public double? DistanceFromRibeiraoKilometers { get; }
}
