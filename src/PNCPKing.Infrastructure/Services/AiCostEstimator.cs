using System.Text;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class AiCostEstimator(IExchangeRateClient exchangeRateClient) : IAiCostEstimator
{
    private const int RequestOverheadBytes = 24_000;

    public async Task<AiCostEstimate> EstimateAsync(
        string markdown,
        AiProviderConfiguration provider,
        int probableItemCount,
        decimal safetyMarginPercent = 10m,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(provider);
        safetyMarginPercent = Math.Clamp(safetyMarginPercent, 0m, 100m);
        probableItemCount = Math.Max(1, probableItemCount);

        var utf8Bytes = Encoding.UTF8.GetByteCount(markdown) + RequestOverheadBytes;
        var expectedInput = Math.Max(1L, (long)Math.Ceiling(utf8Bytes / 3.6d));
        var maximumInput = Math.Max(expectedInput, utf8Bytes);
        var expectedOutput = Math.Max(2_000L, probableItemCount * 220L);
        var desiredMaximumOutput = Math.Max(expectedOutput, probableItemCount * 520L + 4_000L);
        var maximumOutput = Math.Min(provider.MaximumOutputTokens, desiredMaximumOutput);
        var contextParts = Math.Max(
            1,
            (int)Math.Ceiling(
                (maximumInput + maximumOutput) /
                (double)Math.Max(1, provider.ContextWindow)));
        var outputParts = Math.Max(
            1,
            (int)Math.Ceiling(expectedOutput / (double)Math.Max(1, provider.MaximumOutputTokens)));
        var suggestedParts = Math.Max(contextParts, outputParts);
        var fits = suggestedParts == 1 &&
                   expectedOutput <= provider.MaximumOutputTokens &&
                   maximumInput + expectedOutput <= provider.ContextWindow;

        decimal inputBrl;
        decimal outputBrl;
        decimal rate = 1m;
        DateOnly? rateDate = null;
        var warnings = new List<string>();
        if (provider.IsFree)
        {
            inputBrl = 0m;
            outputBrl = 0m;
        }
        else if (provider.IsOpenAi)
        {
            var profile = AiModelCatalog.FindOpenAi(provider.Model)
                          ?? throw new InvalidOperationException(
                              $"O modelo OpenAI '{provider.Model}' não possui preço no catálogo " +
                              $"{AiModelCatalog.CatalogVersion}.");
            var quote = await exchangeRateClient.GetUsdSellRateAsync(cancellationToken).ConfigureAwait(false);
            rate = quote.SellRate;
            rateDate = quote.Date;
            if (quote.FromCache)
            {
                warnings.Add($"Foi usada a PTAX em cache de {quote.Date:dd/MM/yyyy}.");
            }

            inputBrl = profile.InputUsdPerMillion * rate;
            outputBrl = profile.OutputUsdPerMillion * rate;
        }
        else
        {
            inputBrl = provider.InputCostBrlPerMillion;
            outputBrl = provider.OutputCostBrlPerMillion;
        }

        var marginMultiplier = 1m + safetyMarginPercent / 100m;
        decimal Cost(long input, long output) =>
            ((input / 1_000_000m) * inputBrl + (output / 1_000_000m) * outputBrl) *
            marginMultiplier;

        if (!fits)
        {
            warnings.Add(
                $"O documento requer aproximadamente {suggestedParts:N0} parte(s) para os limites informados.");
        }

        return new AiCostEstimate
        {
            ExpectedInputTokens = expectedInput,
            MaximumInputTokens = maximumInput,
            ExpectedOutputTokens = expectedOutput,
            MaximumOutputTokens = maximumOutput,
            ExpectedCostBrl = decimal.Round(Cost(expectedInput, expectedOutput), 2),
            MaximumCostBrl = decimal.Round(Cost(maximumInput, maximumOutput), 2),
            ExchangeRate = rate,
            ExchangeRateDate = rateDate,
            SafetyMarginPercent = safetyMarginPercent,
            FitsContext = fits,
            SuggestedPartCount = suggestedParts,
            Warnings = warnings
        };
    }

    public Task SaveManualUsdSellRateAsync(
        decimal sellRate,
        DateOnly date,
        CancellationToken cancellationToken = default) =>
        exchangeRateClient.SaveManualUsdSellRateAsync(sellRate, date, cancellationToken);
}
