namespace PNCPKing.Core.Models;

public sealed record Modality(long Id, string Name, bool Active = true);

public sealed record ContractRecord
{
    public required string PncpId { get; init; }
    public required string Cnpj { get; init; }
    public required int PurchaseYear { get; init; }
    public required int PurchaseSequence { get; init; }
    public string Object { get; init; } = string.Empty;
    public string AdditionalInformation { get; init; } = string.Empty;
    public string Process { get; init; } = string.Empty;
    public string Organization { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string Municipality { get; init; } = string.Empty;
    public string? MunicipalityIbgeCode { get; init; }
    public string Uf { get; init; } = string.Empty;
    public long ModalityId { get; init; }
    public string ModalityName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset? PublicationDate { get; init; }
    public DateTimeOffset? GlobalUpdatedAt { get; init; }
    public long? TotalHomologatedScaled { get; init; }
    public double? DistanceFromRibeiraoKilometers { get; init; }

    public decimal? TotalHomologated => DecimalScale.FromScaled(TotalHomologatedScaled);

    public Uri PortalUri => new($"https://pncp.gov.br/app/editais/{Uri.EscapeDataString(Cnpj)}/{PurchaseYear}/{PurchaseSequence}");
}

public sealed record ContractPage(
    IReadOnlyList<ContractRecord> Contracts,
    long TotalRecords,
    long TotalPages,
    int Page,
    long PayloadBytes,
    TimeSpan Elapsed);

public sealed record SearchQuery(
    string Text,
    GeoScope Scope,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    int Page = 1,
    int PageSize = 50,
    SearchGeoFilter? GeoFilter = null,
    SearchSort Sort = SearchSort.Relevance)
{
    public SearchQuery(
        string Text,
        SearchGeoFilter GeoFilter,
        DateOnly? StartDate = null,
        DateOnly? EndDate = null,
        SearchSort Sort = SearchSort.Relevance,
        int Page = 1,
        int PageSize = 50)
        : this(Text, GeoScope.All, StartDate, EndDate, Page, PageSize, GeoFilter, Sort)
    {
    }

    public SearchGeoFilter EffectiveGeoFilter => GeoFilter ?? Scope.Kind switch
    {
        GeoScopeKind.All => SearchGeoFilter.All,
        GeoScopeKind.Southeast => SearchGeoFilter.Southeast,
        GeoScopeKind.State => SearchGeoFilter.State(Scope.Uf ?? throw new InvalidOperationException("UF ausente.")),
        _ => SearchGeoFilter.All
    };
}

public sealed record SearchPage(
    IReadOnlyList<ContractRecord> Results,
    long Total,
    int Page,
    int PageSize);

public sealed record SearchPageSlice(
    IReadOnlyList<ContractRecord> Results,
    int Page,
    int PageSize,
    bool MayHaveMore);

public sealed record ItemCandidateCursor(
    int GeographicLayer,
    int GroupRank,
    int RotationBand,
    long RandomOrderKey,
    string PncpId);

public sealed record ItemContractCandidate(
    ContractRecord Contract,
    ItemCandidateCursor Cursor);

public sealed record ItemCandidatePage(
    IReadOnlyList<ItemContractCandidate> Results,
    ItemCandidateCursor? NextCursor,
    bool HasMore);
