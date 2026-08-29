using System.Globalization;
using System.Net;
using System.Text.Json;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Guard;

internal sealed class GuardPncpException : Exception
{
    public GuardPncpException(string message, HttpStatusCode? statusCode, DateTimeOffset? retryAt, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        RetryAt = retryAt;
    }

    public HttpStatusCode? StatusCode { get; }
    public DateTimeOffset? RetryAt { get; }
}

internal sealed class GuardPncpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public GuardPncpClient(HttpClient httpClient, Uri? baseUri = null)
    {
        _httpClient = httpClient;
        _baseUri = baseUri ?? new Uri("https://pncp.gov.br/api/pncp/");
    }

    public async Task<IReadOnlyList<GuardItem>> GetItemsAsync(
        GuardPlanContract contract,
        CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var itemsByNumber = new Dictionary<long, GuardItem>();
        var order = new List<long>();
        for (var page = 1; ; page++)
        {
            var path = BuildPath(contract, $"itens?pagina={page}&tamanhoPagina={pageSize}");
            var response = await GetAsync<List<ItemDto>>(path, result404IsEmpty: false, cancellationToken)
                .ConfigureAwait(false) ?? [];
            var newNumbers = 0;
            foreach (var dto in response)
            {
                var item = new GuardItem
                {
                    ItemNumber = dto.NumeroItem,
                    Description = SearchText.Sanitize(dto.Descricao),
                    AdditionalInformation = SearchText.Sanitize(dto.InformacaoComplementar),
                    RequestedQuantityScaled = DecimalScale.ToScaled(dto.Quantidade),
                    Unit = SearchText.Sanitize(dto.UnidadeMedida),
                    HasResult = dto.TemResultado
                };
                if (!itemsByNumber.ContainsKey(item.ItemNumber))
                {
                    order.Add(item.ItemNumber);
                    newNumbers++;
                }

                itemsByNumber[item.ItemNumber] = item;
            }

            if (response.Count < pageSize || newNumbers == 0)
            {
                break;
            }
        }

        return order.Select(number => itemsByNumber[number]).ToArray();
    }

    public async Task<IReadOnlyList<GuardResult>> GetResultsAsync(
        GuardPlanContract contract,
        long itemNumber,
        CancellationToken cancellationToken)
    {
        var path = BuildPath(contract, $"itens/{itemNumber}/resultados");
        var response = await GetAsync<List<ResultDto>>(path, result404IsEmpty: true, cancellationToken)
            .ConfigureAwait(false) ?? [];
        return response.Select(dto => new GuardResult
        {
            ItemNumber = itemNumber,
            ResultSequence = dto.SequencialResultado,
            SupplierTaxId = SearchText.Sanitize(dto.NiFornecedor),
            SupplierName = SearchText.Sanitize(dto.NomeRazaoSocialFornecedor),
            SupplierType = SearchText.Sanitize(dto.TipoPessoa),
            SupplierMunicipality = SearchText.Sanitize(dto.LocalidadeFornecedor?.NomeMunicipio),
            SupplierUf = SearchText.Sanitize(dto.LocalidadeFornecedor?.Uf),
            HomologatedQuantityScaled = DecimalScale.ToScaled(dto.QuantidadeHomologada),
            HomologatedUnitValueScaled = DecimalScale.ToScaled(dto.ValorUnitarioHomologado),
            HomologatedTotalValueScaled = DecimalScale.ToScaled(dto.ValorTotalHomologado),
            ResultDate = ParseDate(dto.DataResultado),
            ResultStatusId = ReadFlexibleInt(dto.SituacaoCompraItemResultadoId),
            ResultStatusName = SearchText.Sanitize(dto.SituacaoCompraItemResultadoNome)
        }).ToArray();
    }

    private async Task<T?> GetAsync<T>(string relativePath, bool result404IsEmpty, CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, relativePath);
        try
        {
            using var response = await _httpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound && result404IsEmpty)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                DateTimeOffset? retryAt = response.StatusCode == HttpStatusCode.TooManyRequests ||
                                          (int)response.StatusCode >= 500
                    ? ResolveRetryAt(response)
                    : null;
                throw new GuardPncpException(
                    $"PNCP respondeu {(int)response.StatusCode} ({response.ReasonPhrase}) em {uri.AbsolutePath}.",
                    response.StatusCode,
                    retryAt);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GuardPncpException("A chamada ao PNCP excedeu o tempo limite.", null, DateTimeOffset.UtcNow.AddMinutes(30));
        }
        catch (HttpRequestException exception)
        {
            throw new GuardPncpException(
                "Falha transitória de conexão com o PNCP: " + exception.Message,
                exception.StatusCode,
                DateTimeOffset.UtcNow.AddMinutes(30),
                exception);
        }
    }

    private static DateTimeOffset ResolveRetryAt(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Date is { } date)
        {
            return date;
        }

        if (retry?.Delta is { } delta)
        {
            return DateTimeOffset.UtcNow.Add(delta);
        }

        return DateTimeOffset.UtcNow.AddMinutes(30);
    }

    private static string BuildPath(GuardPlanContract contract, string suffix) =>
        $"v1/orgaos/{Uri.EscapeDataString(contract.Cnpj)}/compras/" +
        $"{contract.PurchaseYear.ToString(CultureInfo.InvariantCulture)}/" +
        $"{contract.PurchaseSequence.ToString(CultureInfo.InvariantCulture)}/{suffix}";

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : null;

    private static int ReadFlexibleInt(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => 0
    };

    private sealed class ItemDto
    {
        public long NumeroItem { get; set; }
        public string? Descricao { get; set; }
        public decimal? Quantidade { get; set; }
        public string? UnidadeMedida { get; set; }
        public string? InformacaoComplementar { get; set; }
        public bool TemResultado { get; set; }
    }

    private sealed class ResultDto
    {
        public long SequencialResultado { get; set; }
        public string? NiFornecedor { get; set; }
        public string? NomeRazaoSocialFornecedor { get; set; }
        public string? TipoPessoa { get; set; }
        public SupplierLocationDto? LocalidadeFornecedor { get; set; }
        public decimal? QuantidadeHomologada { get; set; }
        public decimal? ValorUnitarioHomologado { get; set; }
        public decimal? ValorTotalHomologado { get; set; }
        public string? DataResultado { get; set; }
        public JsonElement SituacaoCompraItemResultadoId { get; set; }
        public string? SituacaoCompraItemResultadoNome { get; set; }
    }

    private sealed class SupplierLocationDto
    {
        public string? NomeMunicipio { get; set; }
        public string? Uf { get; set; }
    }
}
