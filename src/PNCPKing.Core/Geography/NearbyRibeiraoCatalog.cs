using PNCPKing.Core.Search;

namespace PNCPKing.Core.Geography;

/// <summary>
/// Ribeirão Preto and the 49 municipal seats nearest to it by great-circle
/// distance. Distances are straight-line distances, not road distances.
/// </summary>
public static class NearbyRibeiraoCatalog
{
    public const string SourceOrganization = "Instituto Brasileiro de Geografia e Estatística (IBGE)";
    public const string SourceEdition = "Censo Demográfico 2022 — Localidades do Brasil";
    public const string SourceArchiveName = "Localidades_UFs_gpkg.zip";
    public const string SourceArchiveSha256 =
        "f537128dbf9bd0170c9133426d91287947d8edbbbed3f4b9d8db91ae97fd5c00";
    public const string SourceUri =
        "https://geoftp.ibge.gov.br/organizacao_do_territorio/estrutura_territorial/localidades/" +
        "Localidades_do_Brasil/2022/Localidades_UFs_gpkg.zip";

    public const string RibeiraoPretoIbgeCode = "3543402";
    public const double EarthMeanRadiusKilometers = 6371.0088;

    // Generated from the Cidade records in the official GeoPackage, using the
    // CD_MUN, NM_MUN, SIGLA_UF, LAT_LOCALIDADE and LONG_LOCALIDADE fields.
    // Coordinates retain the four decimal places supplied by the source.
    private static readonly RawMunicipality[] RawMunicipalities =
    [
        new("3543402", "Ribeirão Preto", "SP", -21.1767, -47.8065),
        new("3525102", "Jardinópolis", "SP", -21.0183, -47.7643),
        new("3514601", "Dumont", "SP", -21.2387, -47.9742),
        new("3513108", "Cravinhos", "SP", -21.3377, -47.7331),
        new("3551702", "Sertãozinho", "SP", -21.1370, -47.9928),
        new("3551504", "Serrana", "SP", -21.2037, -47.6036),
        new("3507803", "Brodowski", "SP", -20.9890, -47.6588),
        new("3551405", "Serra Azul", "SP", -21.3118, -47.5679),
        new("3540200", "Pontal", "SP", -21.0244, -48.0368),
        new("3540903", "Pradópolis", "SP", -21.3574, -48.0667),
        new("3505609", "Barrinha", "SP", -21.1946, -48.1645),
        new("3505906", "Batatais", "SP", -20.8894, -47.5853),
        new("3546256", "Santa Cruz da Esperança", "SP", -21.2923, -47.4304),
        new("3550902", "São Simão", "SP", -21.4774, -47.5547),
        new("3518859", "Guatapará", "SP", -21.4956, -48.0393),
        new("3527603", "Luís Antônio", "SP", -21.5513, -47.7036),
        new("3544905", "Sales Oliveira", "SP", -20.7703, -47.8386),
        new("3539509", "Pitangueiras", "SP", -21.0104, -48.2199),
        new("3501004", "Altinópolis", "SP", -21.0269, -47.3731),
        new("3518602", "Guariba", "SP", -21.3607, -48.2274),
        new("3533601", "Nuporanga", "SP", -20.7327, -47.7514),
        new("3534302", "Orlândia", "SP", -20.7196, -47.8848),
        new("3532058", "Motuca", "SP", -21.5129, -48.1501),
        new("3524303", "Jaboticabal", "SP", -21.2546, -48.3107),
        new("3509403", "Cajuru", "SP", -21.2776, -47.3042),
        new("3543709", "Rincão", "SP", -21.5899, -48.0688),
        new("3531902", "Morro Agudo", "SP", -20.7329, -48.0584),
        new("3547601", "Santa Rosa de Viterbo", "SP", -21.4795, -47.3621),
        new("3556800", "Viradouro", "SP", -20.8728, -48.2960),
        new("3546900", "Santa Lúcia", "SP", -21.6850, -48.0840),
        new("3553658", "Taquaral", "SP", -21.0725, -48.4108),
        new("3549409", "São Joaquim da Barra", "SP", -20.5879, -47.8662),
        new("3549508", "São José da Bela Vista", "SP", -20.5946, -47.6396),
        new("3510906", "Cássia dos Coqueiros", "SP", -21.2824, -47.1700),
        new("3553203", "Taiúva", "SP", -21.1264, -48.4522),
        new("3546504", "Santa Ernestina", "SP", -21.4637, -48.3897),
        new("3547908", "Santo Antônio da Alegria", "SP", -21.0912, -47.1524),
        new("3547502", "Santa Rita do Passa Quatro", "SP", -21.7124, -47.4786),
        new("3501707", "Américo Brasiliense", "SP", -21.7289, -48.1030),
        new("3554409", "Terra Roxa", "SP", -20.7870, -48.3314),
        new("3514007", "Dobrada", "SP", -21.5164, -48.3947),
        new("3542701", "Restinga", "SP", -20.6029, -47.4839),
        new("3531308", "Monte Alto", "SP", -21.2612, -48.4962),
        new("3553104", "Taiaçu", "SP", -21.1456, -48.5139),
        new("3529302", "Matão", "SP", -21.6004, -48.3609),
        new("3506102", "Bebedouro", "SP", -20.9494, -48.4826),
        new("3553708", "Taquaritinga", "SP", -21.4065, -48.5047),
        new("3503208", "Araraquara", "SP", -21.7930, -48.1753),
        new("3132909", "Itamogi", "MG", -21.0778, -47.0424),
        new("3553302", "Tambaú", "SP", -21.7054, -47.2748)
    ];

    private static readonly IReadOnlyList<NearbyMunicipality> OrderedMunicipalities =
        Array.AsReadOnly(CreateMunicipalities());

    private static readonly IReadOnlyDictionary<string, NearbyMunicipality> MunicipalitiesByCode =
        OrderedMunicipalities.ToDictionary(municipality => municipality.IbgeCode, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, NearbyMunicipality> MunicipalitiesByNormalizedNameAndUf =
        OrderedMunicipalities.ToDictionary(
            municipality => NameAndUfKey(municipality.Name, municipality.Uf),
            StringComparer.Ordinal);

    public static IReadOnlyList<NearbyMunicipality> Municipalities => OrderedMunicipalities;

    public static bool TryGetByIbgeCode(string? ibgeCode, out NearbyMunicipality municipality)
    {
        if (!string.IsNullOrWhiteSpace(ibgeCode) &&
            MunicipalitiesByCode.TryGetValue(ibgeCode.Trim(), out var found))
        {
            municipality = found;
            return true;
        }

        municipality = null!;
        return false;
    }

    public static bool TryGetByNameAndUf(string? name, string? uf, out NearbyMunicipality municipality)
    {
        if (!string.IsNullOrWhiteSpace(name) &&
            !string.IsNullOrWhiteSpace(uf) &&
            MunicipalitiesByNormalizedNameAndUf.TryGetValue(NameAndUfKey(name, uf), out var found))
        {
            municipality = found;
            return true;
        }

        municipality = null!;
        return false;
    }

    public static double CalculateDistanceKilometers(
        double latitude1,
        double longitude1,
        double latitude2,
        double longitude2)
    {
        ValidateCoordinates(latitude1, longitude1);
        ValidateCoordinates(latitude2, longitude2);

        var latitudeDelta = DegreesToRadians(latitude2 - latitude1);
        var longitudeDelta = DegreesToRadians(longitude2 - longitude1);
        var latitude1Radians = DegreesToRadians(latitude1);
        var latitude2Radians = DegreesToRadians(latitude2);

        var haversine =
            Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
            (Math.Cos(latitude1Radians) * Math.Cos(latitude2Radians) *
             Math.Pow(Math.Sin(longitudeDelta / 2), 2));

        var centralAngle = 2 * Math.Asin(Math.Min(1, Math.Sqrt(haversine)));
        return EarthMeanRadiusKilometers * centralAngle;
    }

    private static NearbyMunicipality[] CreateMunicipalities()
    {
        var origin = RawMunicipalities.Single(municipality => municipality.IbgeCode == RibeiraoPretoIbgeCode);

        return RawMunicipalities
            .Select(municipality => new NearbyMunicipality(
                municipality.IbgeCode,
                municipality.Name,
                municipality.Uf,
                municipality.Latitude,
                municipality.Longitude,
                CalculateDistanceKilometers(
                    origin.Latitude,
                    origin.Longitude,
                    municipality.Latitude,
                    municipality.Longitude)))
            .OrderBy(municipality => municipality.DistanceFromRibeiraoKilometers)
            .ThenBy(municipality => municipality.IbgeCode, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NameAndUfKey(string name, string uf) =>
        $"{SearchText.Normalize(name)}|{uf.Trim().ToUpperInvariant()}";

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private static void ValidateCoordinates(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude));
        }

        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude));
        }
    }

    private sealed record RawMunicipality(
        string IbgeCode,
        string Name,
        string Uf,
        double Latitude,
        double Longitude);
}
