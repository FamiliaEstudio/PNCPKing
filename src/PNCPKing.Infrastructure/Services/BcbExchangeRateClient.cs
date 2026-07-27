using System.Globalization;
using System.Text.Json;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class BcbExchangeRateClient : IExchangeRateClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly HttpClient _httpClient;
    private readonly string _cachePath;

    public BcbExchangeRateClient(HttpClient httpClient, string dataFolder)
    {
        _httpClient = httpClient;
        _cachePath = Path.Combine(dataFolder, "ai-automation-cache", "ptax-usd.json");
    }

    public async Task<ExchangeRateQuote> GetUsdSellRateAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var start = today.AddDays(-14);
            var uri =
                "https://olinda.bcb.gov.br/olinda/servico/PTAX/versao/v1/odata/" +
                "CotacaoDolarPeriodo(dataInicial=@dataInicial,dataFinalCotacao=@dataFinalCotacao)" +
                $"?%40dataInicial='{start:MM-dd-yyyy}'&%40dataFinalCotacao='{today:MM-dd-yyyy}'" +
                "&%24format=json&%24orderby=dataHoraCotacao%20desc&%24top=1";
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var value = document.RootElement.GetProperty("value");
            if (value.GetArrayLength() == 0)
            {
                throw new InvalidDataException("A consulta PTAX não retornou cotações.");
            }

            var quote = value[0];
            var sellRate = quote.GetProperty("cotacaoVenda").GetDecimal();
            var dateText = quote.GetProperty("dataHoraCotacao").GetString();
            var date = DateOnly.FromDateTime(DateTime.Parse(
                dateText!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal));
            var result = new ExchangeRateQuote("USD", sellRate, date, false);
            await SaveCacheAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            exception is HttpRequestException or IOException or JsonException or InvalidDataException or FormatException)
        {
            var cached = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached with { FromCache = true };
            }

            throw new InvalidOperationException(
                "Não foi possível consultar a PTAX e ainda não existe uma cotação em cache.",
                exception);
        }
    }

    public async Task SaveManualUsdSellRateAsync(
        decimal sellRate,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        if (sellRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sellRate), "O câmbio manual deve ser positivo.");
        }

        await SaveCacheAsync(
            new ExchangeRateQuote("USD", sellRate, date, true),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveCacheAsync(ExchangeRateQuote quote, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var temporary = _cachePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    quote,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<ExchangeRateQuote?> LoadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_cachePath);
            return await JsonSerializer.DeserializeAsync<ExchangeRateQuote>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
