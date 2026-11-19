using System.Globalization;
using PNCPKing.Core.Models;

namespace PNCPKing.Core.Quotations;

public static class QuotationEvidenceSelection
{
    public static IReadOnlySet<Guid> Parse(string selection, QuotationProjectReport report)
    {
        var items = report.Project.Organization is { } organization
            ? organization.Positions.ToDictionary(position => position.Number, position => position.LineId)
            : report.Lines.Select((line, index) => (Number: index + 1, line.Line.Id))
                .ToDictionary(item => item.Number, item => item.Id);
        var lines = new HashSet<Guid>();
        foreach (var part in selection.Split(';', StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length > 2 || !TryParseNumber(range[0], out var first))
                throw InvalidSelection();

            var last = first;
            if (range.Length == 2 && (!TryParseNumber(range[1], out last) || last < first))
                throw InvalidSelection();

            foreach (var number in Enumerable.Range(first, last - first + 1))
            {
                if (!items.TryGetValue(number, out var lineId))
                    throw new FormatException($"O item {number} não existe na numeração da planilha.");
                lines.Add(lineId);
            }
        }

        return lines;
    }

    private static bool TryParseNumber(string text, out int number) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    private static FormatException InvalidSelection() =>
        new("Separe os itens e intervalos com ponto e vírgula, como em 1-30;57;60-76. Escreva cada intervalo em ordem crescente.");
}
