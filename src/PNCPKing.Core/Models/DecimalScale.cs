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
    public static decimal TruncateToCents(decimal value) =>
        decimal.Truncate(value * 100m) / 100m;

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
