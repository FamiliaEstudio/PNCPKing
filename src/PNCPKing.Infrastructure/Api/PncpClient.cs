using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Api;

public sealed class PncpClient : IPncpClient, IPncpDocumentClient
{
    private const int MaximumDocumentBytes = 512 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _consultationBase;
    private readonly Uri _integrationBase;
    private readonly Func<int, TimeSpan> _backoffDelay;

    public PncpClient(
        HttpClient httpClient,
        Uri? consultationBase = null,
        Uri? integrationBase = null,
        Func<int, TimeSpan>? backoffDelay = null)
    {
        _httpClient = httpClient;
        _consultationBase = consultationBase ?? new Uri("https://pncp.gov.br/api/consulta/");
        _integrationBase = integrationBase ?? new Uri("https://pncp.gov.br/api/pncp/");
        _backoffDelay = backoffDelay ?? DefaultBackoffDelay;
    }

    public async Task<IReadOnlyList<Modality>> GetModalitiesAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync<List<ModalityDto>>(
            new Uri(_integrationBase, "v1/modalidades"),
            cancellationToken).ConfigureAwait(false);

        return (result.Value ?? [])
            .Where(item => item.StatusAtivo)
            .OrderBy(item => item.Id)
            .Select(item => new Modality(
                item.Id,
                SearchText.Sanitize(item.Nome ?? $"Modalidade {item.Id}"),
                item.StatusAtivo))
            .ToArray();
    }

    public async Task<ContractPage> GetContractsPageAsync(
        DateOnly startDate,
        DateOnly endDate,
        long modalityId,
        string? uf,
        int page,
        int pageSize,
        SyncMode mode,
        CancellationToken cancellationToken = default)
    {
        if (startDate > endDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startDate),
                $"A data inicial {startDate:dd/MM/yyyy} não pode ser posterior à data final {endDate:dd/MM/yyyy}.");
        }

        var endpoint = mode == SyncMode.Publication
            ? "v1/contratacoes/publicacao"
            : "v1/contratacoes/atualizacao";
        var query = new StringBuilder()
            .Append(endpoint)
            .Append("?dataInicial=").Append(startDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
            .Append("&dataFinal=").Append(endDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
            .Append("&codigoModalidadeContratacao=").Append(modalityId.ToString(CultureInfo.InvariantCulture))
            .Append("&pagina=").Append(page.ToString(CultureInfo.InvariantCulture))
            .Append("&tamanhoPagina=").Append(pageSize.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(uf))
        {
            query.Append("&uf=").Append(Uri.EscapeDataString(uf));
        }

        var result = await GetJsonAsync<ContractPageDto>(new Uri(_consultationBase, query.ToString()), cancellationToken)
            .ConfigureAwait(false);
        var dto = result.Value;
        if (dto is null)
        {
            return new ContractPage([], 0, 0, page, result.PayloadBytes, result.Elapsed);
        }

        var contracts = (dto.Data ?? [])
            .Select(MapContract)
            .Where(item => item is not null)
            .Cast<ContractRecord>()
            .ToArray();

        return new ContractPage(
            contracts,
            dto.TotalRegistros,
            dto.TotalPaginas,
            dto.NumeroPagina == 0 ? page : dto.NumeroPagina,
            result.PayloadBytes,
            result.Elapsed);
    }

    public async Task<int> GetItemCountAsync(ContractRecord contract, CancellationToken cancellationToken = default)
    {
        var uri = BuildContractUri(contract, "itens/quantidade");
        var result = await GetJsonAsync<int?>(uri, cancellationToken).ConfigureAwait(false);
        return result.Value ?? 0;
    }

    public async Task<IReadOnlyList<ProcurementItem>> GetItemsAsync(
        ContractRecord contract,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 500;
        var allItems = new Dictionary<long, ProcurementItem>();
        var itemOrder = new List<long>();
        for (var page = 1; ; page++)
        {
            var uri = BuildContractUri(contract, $"itens?pagina={page}&tamanhoPagina={pageSize}");
            var result = await GetJsonAsync<List<ItemDto>>(uri, cancellationToken).ConfigureAwait(false);
            var items = result.Value ?? [];
            var newItemNumbers = 0;
            foreach (var item in items)
            {
                var mapped = new ProcurementItem
                {
                    ContractId = contract.PncpId,
                    ItemNumber = item.NumeroItem,
                    Description = SearchText.Sanitize(item.Descricao),
                    Unit = SearchText.Sanitize(item.UnidadeMedida),
                    RequestedQuantityScaled = DecimalScale.ToScaled(item.Quantidade),
                    AdditionalInformation = SearchText.Sanitize(item.InformacaoComplementar),
                    Category = SearchText.Sanitize(item.ItemCategoriaNome),
                    NcmNbsCode = SearchText.Sanitize(item.NcmNbsCodigo),
                    NcmNbsDescription = SearchText.Sanitize(item.NcmNbsDescricao),
                    CatalogCode = SearchText.Sanitize(item.CatalogoCodigoItem),
                    CatalogName = SearchText.Sanitize(item.Catalogo?.Nome),
                    CatalogCategory = SearchText.Sanitize(item.CategoriaItemCatalogo?.Nome ?? item.CategoriaItemCatalogo?.Descricao),
                    Status = SearchText.Sanitize(item.SituacaoCompraItemNome),
                    HasResult = item.TemResultado,
                    UpdatedAt = ParseDateTime(item.DataAtualizacao),
                    HydrationStatus = item.TemResultado
                        ? ItemHydrationStatus.NotLoaded
                        : ItemHydrationStatus.Complete
                };

                if (!allItems.ContainsKey(mapped.ItemNumber))
                {
                    itemOrder.Add(mapped.ItemNumber);
                    newItemNumbers++;
                }

                // If PNCP repeats an item across a page boundary, retain the most
                // recent representation without creating duplicate local keys.
                allItems[mapped.ItemNumber] = mapped;
            }

            if (items.Count < pageSize || newItemNumbers == 0)
            {
                break;
            }
        }

        return itemOrder.Select(number => allItems[number]).ToArray();
    }

    public async Task<IReadOnlyList<HomologationResult>> GetItemResultsAsync(
        ContractRecord contract,
        long itemNumber,
        CancellationToken cancellationToken = default)
    {
        var uri = BuildContractUri(contract, $"itens/{itemNumber}/resultados");
        var result = await GetJsonAsync<List<ResultDto>>(uri, cancellationToken).ConfigureAwait(false);
        return (result.Value ?? []).Select(item => new HomologationResult
        {
            ContractId = contract.PncpId,
            ItemNumber = itemNumber,
            ResultSequence = item.SequencialResultado,
            SupplierTaxId = SearchText.Sanitize(item.NiFornecedor),
            SupplierName = SearchText.Sanitize(item.NomeRazaoSocialFornecedor),
            SupplierType = SearchText.Sanitize(item.TipoPessoa),
            SupplierMunicipality = SearchText.Sanitize(item.LocalidadeFornecedor?.NomeMunicipio),
            SupplierUf = SearchText.Sanitize(item.LocalidadeFornecedor?.Uf),
            HomologatedQuantityScaled = DecimalScale.ToScaled(item.QuantidadeHomologada),
            HomologatedUnitValueScaled = DecimalScale.ToScaled(item.ValorUnitarioHomologado),
            HomologatedTotalValueScaled = DecimalScale.ToScaled(item.ValorTotalHomologado),
            ResultDate = ParseDate(item.DataResultado),
            ResultStatusId = ReadFlexibleInt(item.SituacaoCompraItemResultadoId),
            ResultStatusName = SearchText.Sanitize(item.SituacaoCompraItemResultadoNome)
        }).ToArray();
    }

    public async Task<IReadOnlyList<PncpDocumentDescriptor>> ListDocumentsAsync(
        PncpContractKey contract,
        CancellationToken cancellationToken = default)
    {
        using var scope = PncpRequestOptions.BeginScope(
            PncpRequestPriority.UserSelectedItem,
            PncpRequestCategory.Other);
        var result = await GetJsonAsync<List<DocumentDto>>(
            BuildDocumentUri(contract),
            cancellationToken).ConfigureAwait(false);
        return (result.Value ?? [])
            .Where(item => item.SequencialDocumento > 0 && item.StatusAtivo is not false)
            .OrderBy(item => item.SequencialDocumento)
            .Select(item => new PncpDocumentDescriptor
            {
                Sequence = item.SequencialDocumento,
                Title = SearchText.Sanitize(
                    string.IsNullOrWhiteSpace(item.Titulo)
                        ? $"Documento {item.SequencialDocumento}"
                        : item.Titulo),
                DocumentType = SearchText.Sanitize(item.TipoDocumentoNome),
                PublishedAt = ParseDateTime(item.DataPublicacaoPncp),
                DownloadUri = SearchText.Sanitize(
                    string.IsNullOrWhiteSpace(item.Uri) ? item.Url : item.Uri),
                Active = item.StatusAtivo is not false
            })
            .ToArray();
    }

    public async Task<PncpDocumentContent> DownloadDocumentAsync(
        PncpContractKey contract,
        PncpDocumentDescriptor document,
        CancellationToken cancellationToken = default)
    {
        using var scope = PncpRequestOptions.BeginScope(
            PncpRequestPriority.UserSelectedItem,
            PncpRequestCategory.Other);
        var uri = BuildDocumentUri(contract, document.Sequence);
        var maximumAttempts = MaximumAttemptsFor(uri);
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    if (response.Content.Headers.ContentLength is > MaximumDocumentBytes)
                    {
                        throw new InvalidDataException(
                            $"O documento {document.Sequence} excede o limite de 512 MiB.");
                    }

                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    using var output = new MemoryStream();
                    var buffer = new byte[81920];
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        if (output.Length + read > MaximumDocumentBytes)
                        {
                            throw new InvalidDataException(
                                $"O documento {document.Sequence} excede o limite de 512 MiB.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    var fileName = response.Content.Headers.ContentDisposition?.FileNameStar ??
                                   response.Content.Headers.ContentDisposition?.FileName;
                    return new PncpDocumentContent(
                        output.ToArray(),
                        response.Content.Headers.ContentType?.MediaType,
                        fileName?.Trim('"'));
                }

                var isTransient = response.StatusCode == HttpStatusCode.TooManyRequests ||
                                  (int)response.StatusCode >= 500;
                if (!isTransient || attempt == maximumAttempts)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw CreateResponseException(response, body, uri);
                }

                var retryDelay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldRetryTransportFailure(
                                                   exception,
                                                   cancellationToken,
                                                   attempt,
                                                   maximumAttempts))
            {
                await Task.Delay(_backoffDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Falha inesperada ao baixar documento do PNCP.");
    }

    private Uri BuildContractUri(ContractRecord contract, string suffix)
    {
        var cnpj = Uri.EscapeDataString(contract.Cnpj);
        return new Uri(
            _integrationBase,
            $"v1/orgaos/{cnpj}/compras/{contract.PurchaseYear}/{contract.PurchaseSequence}/{suffix}");
    }

    private Uri BuildDocumentUri(PncpContractKey contract, long? sequence = null)
    {
        var cnpj = Uri.EscapeDataString(contract.Cnpj);
        var suffix = sequence is null ? string.Empty : $"/{sequence.Value}";
        return new Uri(
            _integrationBase,
            $"v1/orgaos/{cnpj}/compras/{contract.PurchaseYear}/{contract.PurchaseSequence}/arquivos{suffix}");
    }

    private static ContractRecord? MapContract(ContractDto item)
    {
        var cnpj = SearchText.Sanitize(item.OrgaoEntidade?.Cnpj).Trim();
        var pncpId = SearchText.Sanitize(item.NumeroControlePNCP).Trim();
        if (string.IsNullOrWhiteSpace(pncpId) || string.IsNullOrWhiteSpace(cnpj))
        {
            return null;
        }

        return new ContractRecord
        {
            PncpId = pncpId,
            Cnpj = cnpj,
            PurchaseYear = item.AnoCompra,
            PurchaseSequence = item.SequencialCompra,
            Object = SearchText.Sanitize(item.ObjetoCompra),
            AdditionalInformation = SearchText.Sanitize(item.InformacaoComplementar),
            Process = SearchText.Sanitize(item.Processo),
            Organization = SearchText.Sanitize(item.OrgaoEntidade?.RazaoSocial),
            Unit = SearchText.Sanitize(item.UnidadeOrgao?.NomeUnidade),
            Municipality = SearchText.Sanitize(item.UnidadeOrgao?.MunicipioNome),
            MunicipalityIbgeCode = ReadFlexibleString(item.UnidadeOrgao?.CodigoIbge),
            Uf = SearchText.Sanitize(item.UnidadeOrgao?.UfSigla).ToUpperInvariant(),
            ModalityId = item.ModalidadeId,
            ModalityName = SearchText.Sanitize(item.ModalidadeNome),
            Status = SearchText.Sanitize(item.SituacaoCompraNome),
            PublicationDate = ParseDateTime(item.DataPublicacaoPncp),
            GlobalUpdatedAt = ParseDateTime(item.DataAtualizacaoGlobal),
            TotalHomologatedScaled = DecimalScale.ToScaled(item.ValorTotalHomologado)
        };
    }

    private async Task<JsonPayload<T>> GetJsonAsync<T>(Uri uri, CancellationToken cancellationToken)
    {
        var maximumAttempts = MaximumAttemptsFor(uri);
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await _httpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    stopwatch.Stop();
                    return new JsonPayload<T>(default, 0, stopwatch.Elapsed);
                }

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var countingStream = new PayloadCountingStream(stream);
                    T? value;
                    try
                    {
                        value = await JsonSerializer.DeserializeAsync<T>(
                                countingStream,
                                JsonOptions,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (JsonException) when (countingStream.BytesRead == 0)
                    {
                        value = default;
                    }

                    stopwatch.Stop();
                    return new JsonPayload<T>(value, countingStream.BytesRead, stopwatch.Elapsed);
                }

                if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                {
                    var validationBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var isDateRangeRejection = validationBody.Contains("Data Inicial", StringComparison.OrdinalIgnoreCase) &&
                                               validationBody.Contains("Data Final", StringComparison.OrdinalIgnoreCase);
                    if (isDateRangeRejection && attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw CreateResponseException(response, validationBody, uri);
                }

                var isTransient = response.StatusCode == HttpStatusCode.TooManyRequests ||
                                  (int)response.StatusCode >= 500;
                if (!isTransient || attempt == maximumAttempts)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw CreateResponseException(response, body, uri);
                }

                var retryDelay = GetRetryDelay(response, attempt);
                // Release the shared scheduler slot before waiting for Retry-After.
                // Disposing twice is safe because the response is also scoped by using.
                response.Dispose();
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldRetryTransportFailure(
                                                   exception,
                                                   cancellationToken,
                                                   attempt,
                                                   maximumAttempts))
            {
                stopwatch.Stop();
                await Task.Delay(_backoffDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                throw new TimeoutException(
                    "A API do PNCP excedeu o limite de espera da chamada; o trabalho será retomado.",
                    exception);
            }
        }

        throw new InvalidOperationException("Falha inesperada ao consultar o PNCP.");
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } retryDate)
        {
            var fromHeader = retryDate - DateTimeOffset.UtcNow;
            if (fromHeader > TimeSpan.Zero)
            {
                return fromHeader > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : fromHeader;
            }
        }

        return _backoffDelay(attempt);
    }

    private static bool ShouldRetryTransportFailure(
        Exception exception,
        CancellationToken cancellationToken,
        int attempt,
        int maximumAttempts) =>
        attempt < maximumAttempts &&
        !cancellationToken.IsCancellationRequested &&
        (exception is HttpRequestException { StatusCode: null } or IOException or TaskCanceledException);

    private static TimeSpan DefaultBackoffDelay(int attempt)
    {
        var exponentialSeconds = Math.Min(300, 5 * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(exponentialSeconds * 1000 + Random.Shared.Next(250, 1250));
    }

    private static int MaximumAttemptsFor(Uri uri)
    {
        var priority = PncpRequestOptions.ResolveCurrentPriority(uri);
        if (priority == PncpRequestPriority.BackgroundPriceCache)
        {
            return 1;
        }

        return priority <= PncpRequestPriority.AdditionalBatches ? 3 : 7;
    }

    private static int ReadFlexibleInt(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }

    private static string? ReadFlexibleString(JsonElement? element)
    {
        if (element is not { } value)
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        var sanitized = SearchText.Sanitize(text);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized.Trim();
    }

    private static DateTimeOffset? ParseDateTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static HttpRequestException CreateResponseException(
        HttpResponseMessage response,
        string body,
        Uri uri) => new(
        $"PNCP respondeu {(int)response.StatusCode} ({response.ReasonPhrase}). " +
        $"Intervalo solicitado: {GetQueryValue(uri, "dataInicial") ?? "?"} a " +
        $"{GetQueryValue(uri, "dataFinal") ?? "?"}. {Trim(SearchText.Sanitize(body), 300)}",
        null,
        response.StatusCode);

    private static string? GetQueryValue(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && string.Equals(part[..separator], name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(part[(separator + 1)..]);
            }
        }

        return null;
    }

    private sealed record JsonPayload<T>(T? Value, long PayloadBytes, TimeSpan Elapsed);

    private sealed class PayloadCountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The response owns the underlying stream.
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
