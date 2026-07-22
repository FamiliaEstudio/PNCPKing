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
