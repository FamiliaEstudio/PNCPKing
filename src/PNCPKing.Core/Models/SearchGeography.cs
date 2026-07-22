namespace PNCPKing.Core.Models;

public enum SearchGeoFilterKind
{
    All,
    Southeast,
    State,
    NearRibeirao
}

/// <summary>
/// Geographic filter used only by local searches. It is deliberately separate
/// from <see cref="GeoScope"/>, which describes the scope downloaded from PNCP.
/// </summary>
public sealed record SearchGeoFilter
{
    private SearchGeoFilter(SearchGeoFilterKind kind, string? uf = null)
    {
        Kind = kind;
        Uf = uf;
    }

    public SearchGeoFilterKind Kind { get; }

    public string? Uf { get; }

    public static SearchGeoFilter All { get; } = new(SearchGeoFilterKind.All);

    public static SearchGeoFilter Southeast { get; } = new(SearchGeoFilterKind.Southeast);

    public static SearchGeoFilter NearRibeirao { get; } = new(SearchGeoFilterKind.NearRibeirao);

    public static SearchGeoFilter State(string uf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uf);
        var normalized = uf.Trim().ToUpperInvariant();
        if (normalized.Length != 2 || normalized.Any(character => !char.IsLetter(character)))
        {
            throw new ArgumentException("A UF deve ter duas letras.", nameof(uf));
        }

        return new SearchGeoFilter(SearchGeoFilterKind.State, normalized);
    }

    public override string ToString() => Kind switch
    {
        SearchGeoFilterKind.All => "Todos",
        SearchGeoFilterKind.Southeast => "Sudeste",
        SearchGeoFilterKind.State => Uf ?? "UF",
        SearchGeoFilterKind.NearRibeirao => "Cidades Próximas",
        _ => throw new ArgumentOutOfRangeException()
    };
}

public enum SearchSort
{
    Relevance,
    Newest,
    Nearest
}
