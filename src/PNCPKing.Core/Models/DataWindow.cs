namespace PNCPKing.Core.Models;

/// <summary>The inclusive publication window shared by searches and local retention.</summary>
public static class DataWindow
{
    public const int Months = 11;

    public static DateOnly Start(DateOnly today, int months = Months) => today.AddMonths(-months);

    public static (DateOnly Start, DateOnly End) Normalize(DateOnly start, DateOnly end, DateOnly today)
    {
        var minimum = Start(today);
        start = start < minimum ? minimum : start;
        end = end > today ? today : end;
        return start > end ? (minimum, today) : (start, end);
    }

    public static void Validate(DateOnly start, DateOnly end, DateOnly today)
    {
        if (start > end)
            throw new ArgumentException("A data inicial deve ser anterior ou igual à data final.");
        if (start < Start(today) || end > today)
            throw new ArgumentException(
                $"O período deve estar entre {Start(today):dd/MM/yyyy} e {today:dd/MM/yyyy} (últimos 11 meses).");
    }
}
