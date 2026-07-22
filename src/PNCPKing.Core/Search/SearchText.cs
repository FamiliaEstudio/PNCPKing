using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PNCPKing.Core.Search;

public static partial class SearchText
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                // The explicit fold keeps Portuguese searches deterministic even
                // on machines running with invariant globalization, where Unicode
                // normalization does not necessarily decompose precomposed letters.
                builder.Append(FoldPortugueseLetter(char.ToLowerInvariant(character)));
            }
        }

        return SpaceRegex().Replace(builder.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
    }

    private static char FoldPortugueseLetter(char character) => character switch
    {
        'á' or 'à' or 'â' or 'ã' or 'ä' or 'å' => 'a',
        'ç' => 'c',
        'é' or 'è' or 'ê' or 'ë' => 'e',
        'í' or 'ì' or 'î' or 'ï' => 'i',
        'ñ' => 'n',
        'ó' or 'ò' or 'ô' or 'õ' or 'ö' => 'o',
        'ú' or 'ù' or 'û' or 'ü' => 'u',
        'ý' or 'ÿ' => 'y',
        _ => character
    };

    public static string BuildMatchQuery(string? query)
    {
        var normalized = Normalize(query);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (Match match in QueryPartRegex().Matches(normalized))
        {
            var phrase = match.Groups[1].Success ? match.Groups[1].Value : null;
            if (phrase is not null)
            {
                var words = WordRegex().Matches(phrase).Select(item => item.Value).ToArray();
                if (words.Length > 0)
                {
                    parts.Add($"\"{string.Join(' ', words)}\"");
                }

                continue;
            }

            var word = match.Groups[2].Value;
            if (word.Length > 0)
            {
                parts.Add($"\"{word}\"*");
            }
        }

        return string.Join(" AND ", parts);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex("\"([^\"]+)\"|([\\p{L}\\p{N}]+)")]
    private static partial Regex QueryPartRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
