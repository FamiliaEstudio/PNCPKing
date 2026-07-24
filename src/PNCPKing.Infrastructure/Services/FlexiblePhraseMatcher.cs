using System.Globalization;
using System.Text;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public static class FlexiblePhraseMatcher
{
    public const string RulesVersion = "1.0";

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "ao", "aos", "as", "com", "da", "das", "de", "do", "dos", "e", "em",
        "na", "nas", "no", "nos", "o", "os", "ou", "para", "por", "sem", "um", "uma",
        "uns", "umas"
    };

    public static IReadOnlyList<TextOccurrence> Find(
        string description,
        DocumentPageIndex page)
    {
        var rawQuery = Tokenize(description).ToArray();
        var query = rawQuery
            .Where(token => !StopWords.Contains(token))
            .ToArray();
        var ignoreStopWords = query.Length > 0;
        if (query.Length == 0)
        {
            query = rawQuery;
        }

        if (query.Length == 0 || page.Words.Count == 0)
        {
            return [];
        }

        var pageTokens = page.Words
            .SelectMany((word, wordIndex) => Tokenize(word.Text)
                .Where(token => !ignoreStopWords || !StopWords.Contains(token))
                .Select(token => new IndexedToken(token, wordIndex)))
            .ToArray();
        var matches = new List<TextOccurrence>();
        for (var start = 0; start + query.Length <= pageTokens.Length; start++)
        {
            var matchesQuery = true;
            for (var offset = 0; offset < query.Length; offset++)
            {
                if (!string.Equals(
                        pageTokens[start + offset].Token,
                        query[offset],
                        StringComparison.Ordinal))
                {
                    matchesQuery = false;
                    break;
                }
            }

            if (!matchesQuery)
            {
                continue;
            }

            var wordIndexes = pageTokens
                .Skip(start)
                .Take(query.Length)
                .Select(token => token.WordIndex)
                .Distinct()
                .ToArray();
            var bounds = Union(wordIndexes.Select(index => page.Words[index].Bounds));
            matches.Add(new TextOccurrence(
                page.PageNumber,
                bounds,
                wordIndexes,
                string.Join(' ', wordIndexes.Select(index => page.Words[index].Text))));
        }

        var distinct = matches
            .GroupBy(match => (
                First: match.WordIndexes.FirstOrDefault(),
                Last: match.WordIndexes.LastOrDefault()))
            .Select(group => group.First())
            .OrderBy(match => match.WordIndexes[0])
            .ThenBy(match => match.WordIndexes[^1])
            .ToArray();
        var deduplicated = new List<TextOccurrence>(distinct.Length);
        var lastWordIndex = -1;
        foreach (var occurrence in distinct)
        {
            if (occurrence.WordIndexes[0] <= lastWordIndex)
            {
                continue;
            }

            deduplicated.Add(occurrence);
            lastWordIndex = occurrence.WordIndexes[^1];
        }

        return deduplicated;
    }

    public static string Normalize(string value) =>
        string.Join(' ', Tokenize(value));

    public static IReadOnlyList<string> PrepareExpressions(IEnumerable<string> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        var prepared = new List<string>();
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expression in expressions)
        {
            var trimmed = expression?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var normalizedExpression = Normalize(trimmed);
            if (normalizedExpression.Length == 0 || !normalized.Add(normalizedExpression))
            {
                continue;
            }

            prepared.Add(trimmed);
        }

        return prepared;
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var token = new StringBuilder();
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
            }
            else if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }

    private static DocumentRectangle Union(IEnumerable<DocumentRectangle> rectangles)
    {
        var values = rectangles.ToArray();
        var left = values.Min(rectangle => rectangle.X);
        var top = values.Min(rectangle => rectangle.Y);
        var right = values.Max(rectangle => rectangle.X + rectangle.Width);
        var bottom = values.Max(rectangle => rectangle.Y + rectangle.Height);
        return new DocumentRectangle(left, top, right - left, bottom - top);
    }

    private sealed record IndexedToken(string Token, int WordIndex);
}
