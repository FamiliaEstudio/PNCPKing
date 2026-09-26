using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;

namespace PNCPKing.Tests;

public sealed class ItemSearchDisplayRowTests
{
    [Fact]
    public void PinAndBasketSelection_AreIndependentRetentionStates()
    {
        var row = Row(active: true, unitPrice: 12m);

        row.IsSelectedForBasket = true;
        Assert.True(row.IsRetained);
        Assert.False(row.IsPinned);
        Assert.Equal("Cesta", row.RetentionMarker);

        row.IsPinned = true;
        Assert.Equal("Fixado · Cesta", row.RetentionMarker);

        row.IsSelectedForBasket = false;
        Assert.True(row.IsRetained);
        Assert.Equal("Fixado", row.RetentionMarker);

        row.IsPinned = false;
        Assert.False(row.IsRetained);
        Assert.Empty(row.RetentionMarker);
    }

    [Theory]
    [InlineData(true, 12, true)]
    [InlineData(false, 12, false)]
    [InlineData(true, 0, false)]
    public void BasketEligibility_RequiresActivePositiveHomologatedPrice(
        bool active,
        int unitPrice,
        bool expected)
    {
        Assert.Equal(expected, Row(active, unitPrice).IsBasketEligible);
    }

    [Fact]
    public void CollectiveActions_AreIdempotentAndSkipIneligibleBasketPrices()
    {
        var rows = new[] { Row(true, 12), Row(false, 12), Row(true, 0) };
        Assert.All(rows, row => Assert.True(row.TryApplyAction(ItemPriceAction.Pin)));
        Assert.All(rows, row => Assert.True(row.TryApplyAction(ItemPriceAction.Pin)));
        Assert.All(rows, row => Assert.True(row.IsPinned));
        Assert.Equal(1, rows.Count(row => row.TryApplyAction(ItemPriceAction.MarkForBasket)));
        Assert.Equal(1, rows.Count(row => row.TryApplyAction(ItemPriceAction.MarkForBasket)));
        Assert.True(rows[0].IsSelectedForBasket);
        Assert.All(rows.Skip(1), row => Assert.False(row.IsSelectedForBasket));
        Assert.All(rows, row => Assert.True(row.TryApplyAction(ItemPriceAction.Clear)));
        Assert.All(rows, row => Assert.False(row.IsRetained));
    }

    private static ItemSearchDisplayRow Row(bool active, decimal unitPrice)
    {
        var contract = RepositorySearchTests.Contract("marked-price", "Café", "SP", 1);
        var item = new ProcurementItem
        {
            ContractId = contract.PncpId,
            ItemNumber = 1,
            Description = "Café em grãos",
            Unit = "kg",
            HasResult = true,
            HydrationStatus = ItemHydrationStatus.Complete
        };
        var result = new HomologationResult
        {
            ContractId = contract.PncpId,
            ItemNumber = item.ItemNumber,
            ResultSequence = 1,
            HomologatedUnitValueScaled = DecimalScale.ToScaled(unitPrice),
            ResultStatusId = active ? 1 : 2,
            ResultStatusName = active ? "Informado" : "Cancelado"
        };
        return new ItemSearchDisplayRow(new ItemSearchRow(
            contract,
            item,
            result,
            active ? ItemSearchPriceState.Homologated : ItemSearchPriceState.Cancelled,
            result.ResultStatusName,
            IsTemporary: false));
    }
}
