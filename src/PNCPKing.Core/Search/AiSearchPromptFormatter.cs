using System.Text;
using PNCPKing.Core.Models;

namespace PNCPKing.Core.Search;

public static class AiSearchPromptFormatter
{
    public static string Format(
        IReadOnlyList<AiPositiveGroup> positiveGroups,
        IReadOnlyList<AiSearchTerm> exclusions,
        IReadOnlyList<string> acceptedUnits)
    {
        var builder = new StringBuilder();
        for (var groupIndex = 0; groupIndex < positiveGroups.Count; groupIndex++)
        {
            var terms = positiveGroups[groupIndex].Terms
                .Where(term => !string.IsNullOrWhiteSpace(term.Text))
                .ToArray();
            if (terms.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(" OU ");
            }

            builder.Append(string.Join(' ', terms.Select(FormatTerm)));
        }

        foreach (var exclusion in exclusions.Where(term => !string.IsNullOrWhiteSpace(term.Text)))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append('-').Append(FormatTerm(exclusion));
        }

        foreach (var unit in acceptedUnits
                     .Select(SearchText.Normalize)
                     .Where(unit => unit.Length > 0)
                     .Distinct(StringComparer.Ordinal))
        {
            if (unit.Any(char.IsWhiteSpace))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append('"').Append(unit);
        }

        var value = builder.ToString().Trim();
        _ = SearchText.Parse(value);
        return value;
    }

    private static string FormatTerm(AiSearchTerm term)
    {
        var normalized = SearchText.Normalize(term.Text);
        if (normalized.Length == 0)
        {
            throw new SearchQueryException("A IA produziu um termo de pesquisa vazio.");
        }

        return term.IsPhrase && normalized.Contains(' ')
            ? $"\"{normalized}\""
            : normalized.Replace(' ', '+');
    }
}
