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

public sealed class ItemSearchDisplayRow : ObservableObject
{
    private bool _isPinned;
    private bool _isSelectedForBasket;

    public ItemSearchDisplayRow(ItemSearchRow source)
    {
        Source = source;
        if (source.Contract.DistanceFromRibeiraoKilometers is { } storedDistance)
        {
            DistanceFromRibeiraoKilometers = storedDistance;
        }
        else if (BrazilMunicipalityCatalog.TryResolve(
                source.Contract.MunicipalityIbgeCode,
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
    public DateTimeOffset? PublicationDate => Source.Contract.PublicationDate;
    public string Uf => Source.Contract.Uf;
    public string Description => Source.Item.Description;
    public string Unit => Source.Item.Unit;
    public decimal? HomologatedQuantity => Source.HomologatedQuantity;
    public decimal? HomologatedUnitValue => Source.HomologatedUnitValue;
    public decimal? HomologatedTotalValue => Source.HomologatedTotalValue;
    public string Supplier => Source.Result?.SupplierName ?? string.Empty;
    public string SupplierTaxId => Source.Result?.SupplierTaxId ?? string.Empty;
    public DateOnly? ResultDate => Source.Result?.ResultDate;
    public string DisplayStatus => Source.DisplayStatus;
    public bool IsCancelled => Source.PriceState == ItemSearchPriceState.Cancelled;
    public bool IsBasketEligible =>
        Source.PriceState == ItemSearchPriceState.Homologated &&
        Source.Result is { IsActive: true, HomologatedUnitValue: > 0 };

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(IsRetained));
                OnPropertyChanged(nameof(RetentionMarker));
            }
        }
    }

    public bool IsSelectedForBasket
    {
        get => _isSelectedForBasket;
        set
        {
            if (SetProperty(ref _isSelectedForBasket, value))
            {
                OnPropertyChanged(nameof(IsRetained));
                OnPropertyChanged(nameof(RetentionMarker));
            }
        }
    }

    public bool IsRetained => IsPinned || IsSelectedForBasket;

    public string RetentionMarker => (IsPinned, IsSelectedForBasket) switch
    {
        (true, true) => "Fixado · Cesta",
        (true, false) => "Fixado",
        (false, true) => "Cesta",
        _ => string.Empty
    };

    public string PortalUrl => Source.Contract.PortalUri.AbsoluteUri;
    public double? DistanceFromRibeiraoKilometers { get; }
}
