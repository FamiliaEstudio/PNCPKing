using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using PNCPKing.Core.Search;

namespace PNCPKing.Core.Geography;

/// <summary>
/// Municipal seats used only for local distance metadata and geographic ordering.
/// Runtime searches never issue one request per municipality.
/// </summary>
public static class BrazilMunicipalityCatalog
{
    public const string SourceOrganization = "Instituto Brasileiro de Geografia e Estatística (IBGE)";
    public const string SourceEdition = "Censo Demográfico 2022 — Localidades do Brasil";
    public const string SourceArchiveSha256 =
        "f537128dbf9bd0170c9133426d91287947d8edbbbed3f4b9d8db91ae97fd5c00";
    public const string CatalogSha256 =
        "f8972c11d84486222b8ff437aee9de540e78573da193b08509c315b5fb1b7a63";
    public const int ExpectedMunicipalityCount = 5571;

    private const string ResourceName =
        "PNCPKing.Core.Geography.BrazilMunicipalities2022.csv.gz.b64";

    private static readonly CatalogData Data = Load();

    public static IReadOnlyList<NearbyMunicipality> Municipalities => Data.ByDistance;

    public static IReadOnlyList<NearbyMunicipality> FirstFifty => Data.FirstFifty;

    public static IReadOnlyList<string> StatesByProximity => Data.StatesByProximity;

    public static bool TryResolve(
        string? ibgeCode,
        string? name,
        string? uf,
        out NearbyMunicipality municipality)
    {
        if (!string.IsNullOrWhiteSpace(ibgeCode) &&
            Data.ByCode.TryGetValue(ibgeCode.Trim(), out var byCode))
        {
            municipality = byCode;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(name) &&
            !string.IsNullOrWhiteSpace(uf) &&
            Data.ByNameAndUf.TryGetValue(NameAndUfKey(name, uf), out var byName))
        {
            municipality = byName;
            return true;
        }

        municipality = null!;
        return false;
    }

    public static int GetDistanceRank(string ibgeCode) =>
        Data.DistanceRanks.TryGetValue(ibgeCode, out var rank) ? rank : int.MaxValue;

    public static int GetStateProximityRank(string? uf)
    {
        if (string.IsNullOrWhiteSpace(uf))
        {
            return int.MaxValue;
        }

        return Data.StateRanks.TryGetValue(uf.Trim().ToUpperInvariant(), out var rank)
            ? rank
            : int.MaxValue;
    }

    public static bool IsFirstFifty(string? ibgeCode, string? name, string? uf) =>
        TryResolve(ibgeCode, name, uf, out var municipality) &&
        GetDistanceRank(municipality.IbgeCode) < 50;

    private static CatalogData Load()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"Recurso geográfico ausente: {ResourceName}.");
        using var encodedReader = new StreamReader(resource);
        var encoded = encodedReader.ReadToEnd();
        var compressed = Convert.FromBase64String(encoded);
        using var compressedStream = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var rawStream = new MemoryStream();
        gzip.CopyTo(rawStream);
        var raw = rawStream.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        if (!string.Equals(hash, CatalogSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("O catálogo municipal embutido não passou na verificação SHA-256.");
        }

        var rawMunicipalities = new List<RawMunicipality>(ExpectedMunicipalityCount);
        using var textReader = new StringReader(System.Text.Encoding.UTF8.GetString(raw));
        while (textReader.ReadLine() is { } line)
        {
            var parts = line.Split('|');
            if (parts.Length != 5 ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            {
                throw new InvalidDataException("Linha inválida no catálogo municipal embutido.");
            }

            rawMunicipalities.Add(new RawMunicipality(parts[0], parts[1], parts[2], latitude, longitude));
        }

        if (rawMunicipalities.Count != ExpectedMunicipalityCount ||
            rawMunicipalities.Select(item => item.IbgeCode).Distinct(StringComparer.Ordinal).Count() != ExpectedMunicipalityCount)
        {
            throw new InvalidDataException("O catálogo municipal embutido está incompleto ou contém códigos duplicados.");
        }

        var origin = rawMunicipalities.Single(item =>
            item.IbgeCode == NearbyRibeiraoCatalog.RibeiraoPretoIbgeCode);
        var byDistance = rawMunicipalities
            .Select(item => new NearbyMunicipality(
                item.IbgeCode,
                item.Name,
                item.Uf,
                item.Latitude,
                item.Longitude,
                NearbyRibeiraoCatalog.CalculateDistanceKilometers(
                    origin.Latitude,
                    origin.Longitude,
                    item.Latitude,
                    item.Longitude)))
            .OrderBy(item => item.DistanceFromRibeiraoKilometers)
            .ThenBy(item => item.IbgeCode, StringComparer.Ordinal)
            .ToArray();
        var states = byDistance
            .GroupBy(item => item.Uf, StringComparer.Ordinal)
            .Select(group => new
            {
                Uf = group.Key,
                MinimumDistance = group.Min(item => item.DistanceFromRibeiraoKilometers)
            })
            .OrderBy(item => item.Uf == "SP" ? 0 : 1)
            .ThenBy(item => item.MinimumDistance)
            .ThenBy(item => item.Uf, StringComparer.Ordinal)
            .Select(item => item.Uf)
            .ToArray();

        return new CatalogData(
            Array.AsReadOnly(byDistance),
            Array.AsReadOnly(byDistance.Take(50).ToArray()),
            byDistance.ToDictionary(item => item.IbgeCode, StringComparer.Ordinal),
            byDistance.ToDictionary(item => NameAndUfKey(item.Name, item.Uf), StringComparer.Ordinal),
            byDistance.Select((item, rank) => (item.IbgeCode, rank))
                .ToDictionary(item => item.IbgeCode, item => item.rank, StringComparer.Ordinal),
            Array.AsReadOnly(states),
            states.Select((uf, rank) => (uf, rank))
                .ToDictionary(item => item.uf, item => item.rank, StringComparer.Ordinal));
    }

    private static string NameAndUfKey(string name, string uf) =>
        $"{SearchText.Normalize(name)}|{uf.Trim().ToUpperInvariant()}";

    private sealed record RawMunicipality(
        string IbgeCode,
        string Name,
        string Uf,
        double Latitude,
        double Longitude);

    private sealed record CatalogData(
        IReadOnlyList<NearbyMunicipality> ByDistance,
        IReadOnlyList<NearbyMunicipality> FirstFifty,
        IReadOnlyDictionary<string, NearbyMunicipality> ByCode,
        IReadOnlyDictionary<string, NearbyMunicipality> ByNameAndUf,
        IReadOnlyDictionary<string, int> DistanceRanks,
        IReadOnlyList<string> StatesByProximity,
        IReadOnlyDictionary<string, int> StateRanks);
}
