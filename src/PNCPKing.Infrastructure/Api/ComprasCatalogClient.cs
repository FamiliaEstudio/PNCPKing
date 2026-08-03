using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Api;

public sealed class ComprasCatalogClient(HttpClient httpClient) : IComprasCatalogClient
{
    private const string MaterialEndpoint = "modulo-material/4_consultarItemMaterial";
    private const string ServiceEndpoint = "modulo-servico/6_consultarItemServico";

    public async Task<CatalogPage> GetPageAsync(
        CatalogKind kind,
        int page,
        int pageSize = 500,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        pageSize = Math.Clamp(pageSize, 10, 500);
        var endpoint = kind == CatalogKind.Catmat ? MaterialEndpoint : ServiceEndpoint;
        var activeParameter = kind == CatalogKind.Catmat ? "statusItem" : "statusServico";
        var uri = $"{endpoint}?pagina={page.ToString(CultureInfo.InvariantCulture)}" +
                  $"&tamanhoPagina={pageSize.ToString(CultureInfo.InvariantCulture)}&{activeParameter}=1";
        using var response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<CatalogApiPage>(
                          cancellationToken: cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidDataException("A API do catálogo retornou uma resposta vazia.");
        var entries = (payload.Resultado ?? [])
            .Select(item => Map(kind, item))
            .Where(item => item.Active && item.Code.Length > 0 && item.Description.Length > 0)
            .DistinctBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
        return new CatalogPage(
            kind,
            page,
            Math.Max(0, payload.TotalPaginas),
            Math.Max(0, payload.TotalRegistros),
            entries);
    }

    private static CatalogEntry Map(CatalogKind kind, CatalogApiItem item)
    {
        var code = kind == CatalogKind.Catmat ? item.CodigoItem : item.CodigoServico;
        var description = SearchText.Sanitize(
            kind == CatalogKind.Catmat ? item.DescricaoItem : item.NomeServico).Trim();
        var values = kind == CatalogKind.Catmat
            ? new[]
            {
                item.NomeGrupo, item.NomeClasse, item.NomePdm, description, item.CodigoNcm
            }
            : new[]
            {
                item.NomeSecao, item.NomeDivisao, item.NomeGrupo, item.NomeClasse,
                item.NomeSubclasse, description
            };
        return new CatalogEntry
        {
            Kind = kind,
            Code = code?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Description = description,
            Active = kind == CatalogKind.Catmat ? item.StatusItem : item.StatusServico,
            Level1Code = Number(item.CodigoGrupo, item.CodigoSecao, kind),
            Level1Name = Text(kind == CatalogKind.Catmat ? item.NomeGrupo : item.NomeSecao),
            Level2Code = Number(item.CodigoClasse, item.CodigoDivisao, kind),
            Level2Name = Text(kind == CatalogKind.Catmat ? item.NomeClasse : item.NomeDivisao),
            Level3Code = Number(item.CodigoPdm, item.CodigoGrupo, kind),
            Level3Name = Text(kind == CatalogKind.Catmat ? item.NomePdm : item.NomeGrupo),
            Level4Code = kind == CatalogKind.Catser ? Number(item.CodigoClasse) : string.Empty,
            Level4Name = kind == CatalogKind.Catser ? Text(item.NomeClasse) : string.Empty,
            Level5Code = kind == CatalogKind.Catser ? Number(item.CodigoSubclasse) : string.Empty,
            Level5Name = kind == CatalogKind.Catser ? Text(item.NomeSubclasse) : string.Empty,
            NcmCode = Text(item.CodigoNcm),
            Sustainable = item.ItemSustentavel,
            ExclusiveCentralPurchasing = item.ExclusivoCentralCompras,
            RemoteUpdatedAt = ParseDate(item.DataHoraAtualizacao),
            SearchText = SearchText.Normalize(string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value))))
        };
    }

    private static string Number(long? material, long? service, CatalogKind kind) =>
        Number(kind == CatalogKind.Catmat ? material : service);

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Text(string? value) => SearchText.Sanitize(value).Trim();
    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private sealed class CatalogApiPage
    {
        [JsonPropertyName("resultado")]
        public List<CatalogApiItem>? Resultado { get; set; }
        [JsonPropertyName("totalRegistros")]
        public long TotalRegistros { get; set; }
        [JsonPropertyName("totalPaginas")]
        public int TotalPaginas { get; set; }
    }

    private sealed class CatalogApiItem
    {
        public long? CodigoItem { get; set; }
        public long? CodigoServico { get; set; }
        public string? DescricaoItem { get; set; }
        public string? NomeServico { get; set; }
        public bool StatusItem { get; set; }
        public bool StatusServico { get; set; }
        public long? CodigoSecao { get; set; }
        public string? NomeSecao { get; set; }
        public long? CodigoDivisao { get; set; }
        public string? NomeDivisao { get; set; }
        public long? CodigoGrupo { get; set; }
        public string? NomeGrupo { get; set; }
        public long? CodigoClasse { get; set; }
        public string? NomeClasse { get; set; }
        public long? CodigoSubclasse { get; set; }
        public string? NomeSubclasse { get; set; }
        public long? CodigoPdm { get; set; }
        public string? NomePdm { get; set; }
        [JsonPropertyName("codigo_ncm")]
        public string? CodigoNcm { get; set; }
        public bool ItemSustentavel { get; set; }
        public bool ExclusivoCentralCompras { get; set; }
        public string? DataHoraAtualizacao { get; set; }
    }
}
