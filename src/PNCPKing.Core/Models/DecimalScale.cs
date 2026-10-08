namespace PNCPKing.Core.Models;

public static class DecimalScale
{
    public const decimal Scale = 10_000m;

    public static long? ToScaled(decimal? value)
    {
        if (value is null)
        {
            return null;
        }

        return checked((long)decimal.Round(value.Value * Scale, 0, MidpointRounding.AwayFromZero));
    }

    public static decimal? FromScaled(long? value) => value is null ? null : value.Value / Scale;
}

public static class QuotationMoney
{
    // A price group can be highlighted only when at least three members pass the
    // same single-pass checks used by the quotation and Excel calculations.
    public static bool[] EvaluatePriceGroup(IReadOnlyList<decimal> prices, int decimalPlaces)
    {
        var validity = new bool[prices.Count];
        if (prices.Count < 3) return validity;
        var effectivePrices = prices.Select(price => Truncate(price, decimalPlaces)).ToArray();
        var total = effectivePrices.Sum();
        var validCount = 0;
        for (var index = 0; index < effectivePrices.Length; index++)
        {
            validity[index] = EvaluatePrice(effectivePrices[index], total, effectivePrices.Length, decimalPlaces).IsValid;
            if (validity[index]) validCount++;
        }
        if (validCount < 3) Array.Clear(validity);
        return validity;
    }

    public static (decimal DeviationPercent, bool IsValid) EvaluatePrice(
        decimal effectivePrice, decimal totalPrice, int priceCount, int decimalPlaces)
    {
        var otherPricesAverage = priceCount < 2
            ? 0m
            : Truncate((totalPrice - effectivePrice) / (priceCount - 1), decimalPlaces);
        var deviation = otherPricesAverage <= 0m
            ? 0m
            : Math.Abs(effectivePrice / otherPricesAverage - 1m) * 100m;
        return (deviation, effectivePrice > 0m && deviation <= 25m);
    }

    public static decimal TruncateToCents(decimal value) => Truncate(value, 2);

    public static decimal Truncate(decimal value, int decimalPlaces)
    {
        var scale = decimalPlaces switch
        {
            2 => 100m,
            4 => 10_000m,
            _ => throw new ArgumentOutOfRangeException(nameof(decimalPlaces))
        };
        return decimal.Truncate(value * scale) / scale;
    }

    public static void ValidateConversionFactor(decimal value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "O fator de conversão deve ser maior que zero.");
        }

        var scaled = value * 1_000_000m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentException(
                "O fator de conversão deve ter no máximo seis casas decimais.",
                nameof(value));
        }
    }
}
