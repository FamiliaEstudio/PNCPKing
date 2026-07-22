namespace PNCPKing.Core.Models;

public enum GeoScopeKind
{
    All,
    Southeast,
    State
}

public sealed record GeoScope(GeoScopeKind Kind, string? Uf = null)
{
    public static GeoScope All { get; } = new(GeoScopeKind.All);
    public static GeoScope Southeast { get; } = new(GeoScopeKind.Southeast);

    public static GeoScope State(string uf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uf);
        var normalized = uf.Trim().ToUpperInvariant();
        if (normalized.Length != 2)
        {
            throw new ArgumentException("A UF deve ter duas letras.", nameof(uf));
        }

        return new GeoScope(GeoScopeKind.State, normalized);
    }

    public IReadOnlyList<string?> ApiUfFilters => Kind switch
    {
        GeoScopeKind.All => [null],
        GeoScopeKind.Southeast => ["ES", "MG", "RJ", "SP"],
        GeoScopeKind.State => [Uf],
        _ => throw new ArgumentOutOfRangeException()
    };

    public override string ToString() => Kind switch
    {
        GeoScopeKind.All => "Todos",
        GeoScopeKind.Southeast => "Sudeste",
        GeoScopeKind.State => Uf ?? "UF",
        _ => "Todos"
    };
}
