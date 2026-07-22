using PNCPKing.Core.Geography;
using PNCPKing.Core.Models;

namespace PNCPKing.Tests;

public sealed class NearbyRibeiraoCatalogTests
{
    [Fact]
    public void BrazilCatalog_ContainsOfficialNationalSetAndPreservesFirstFifty()
    {
        Assert.Equal(BrazilMunicipalityCatalog.ExpectedMunicipalityCount, BrazilMunicipalityCatalog.Municipalities.Count);
        Assert.Equal(
            NearbyRibeiraoCatalog.Municipalities.Select(item => item.IbgeCode),
            BrazilMunicipalityCatalog.FirstFifty.Select(item => item.IbgeCode));
        Assert.Equal("SP", BrazilMunicipalityCatalog.StatesByProximity[0]);
        Assert.Contains("BA", BrazilMunicipalityCatalog.StatesByProximity);
        Assert.True(
            BrazilMunicipalityCatalog.GetStateProximityRank("MG") <
            BrazilMunicipalityCatalog.GetStateProximityRank("BA"));
    }

    [Fact]
    public void BrazilCatalog_ResolvesAccentedNamesAndNationalDistances()
    {
        Assert.True(BrazilMunicipalityCatalog.TryResolve(null, "Ribeirao Preto", "sp", out var ribeirao));
        Assert.Equal(0d, ribeirao.DistanceFromRibeiraoKilometers);
        Assert.True(BrazilMunicipalityCatalog.TryResolve("2927408", null, null, out var salvador));
        Assert.Equal("Salvador", salvador.Name);
        Assert.True(salvador.DistanceFromRibeiraoKilometers > 1_000d);
    }

    [Fact]
    public void CatalogContainsRibeiraoAndExactlyFortyNineNearestMunicipalSeats()
    {
        var municipalities = NearbyRibeiraoCatalog.Municipalities;

        Assert.Equal(50, municipalities.Count);
        Assert.Equal(NearbyRibeiraoCatalog.RibeiraoPretoIbgeCode, municipalities[0].IbgeCode);
        Assert.Equal("Ribeirão Preto", municipalities[0].Name);
        Assert.Equal(0, municipalities[0].DistanceFromRibeiraoKilometers, precision: 10);
        Assert.Equal("3553302", municipalities[^1].IbgeCode);

        Assert.Equal(50, municipalities.Select(item => item.IbgeCode).Distinct().Count());
        Assert.DoesNotContain(municipalities, item => item.IbgeCode == "3536307");
    }

    [Fact]
    public void CatalogIsInIncreasingHaversineDistanceOrder()
    {
        var municipalities = NearbyRibeiraoCatalog.Municipalities;
        var origin = municipalities[0];

        for (var index = 0; index < municipalities.Count; index++)
        {
            var municipality = municipalities[index];
            var recalculated = NearbyRibeiraoCatalog.CalculateDistanceKilometers(
                origin.Latitude,
                origin.Longitude,
                municipality.Latitude,
                municipality.Longitude);

            Assert.Equal(recalculated, municipality.DistanceFromRibeiraoKilometers, precision: 10);
            if (index > 0)
            {
                Assert.True(
                    municipalities[index - 1].DistanceFromRibeiraoKilometers <=
                    municipality.DistanceFromRibeiraoKilometers);
            }
        }
    }

    [Fact]
    public void CatalogSupportsIbgeAndUnaccentedNameLookups()
    {
        Assert.True(NearbyRibeiraoCatalog.TryGetByIbgeCode("3132909", out var itamogi));
        Assert.Equal("Itamogi", itamogi.Name);
        Assert.Equal("MG", itamogi.Uf);

        Assert.True(NearbyRibeiraoCatalog.TryGetByNameAndUf("Ribeirao Preto", "sp", out var ribeirao));
        Assert.Equal(NearbyRibeiraoCatalog.RibeiraoPretoIbgeCode, ribeirao.IbgeCode);
    }

    [Fact]
    public void SearchGeographyIsIndependentFromDownloadScope()
    {
        Assert.Equal("Cidades Próximas", SearchGeoFilter.NearRibeirao.ToString());
        Assert.Equal(SearchGeoFilterKind.State, SearchGeoFilter.State(" sp ").Kind);
        Assert.Equal("SP", SearchGeoFilter.State(" sp ").Uf);
        Assert.Throws<ArgumentException>(() => SearchGeoFilter.State("S1"));
    }
}
