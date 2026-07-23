using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PNCPKing.Core.Search;

public static partial class SearchText
{
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder? builder = null;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    var scalar = char.ConvertToUtf32(character, text[index + 1]);
                    if (IsUnicodeNoncharacter(scalar))
                    {
                        builder ??= CreateSanitizedBuilder(text, index);
                        builder.Append('\uFFFD');
                        index++;
                        continue;
                    }

                    if (builder is not null)
                    {
                        builder.Append(character);
                        builder.Append(text[++index]);
                    }
                    else
                    {
                        index++;
                    }

                    continue;
                }

                builder ??= CreateSanitizedBuilder(text, index);
                builder.Append('\uFFFD');
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                builder ??= CreateSanitizedBuilder(text, index);
                builder.Append('\uFFFD');
                continue;
            }

            if (IsUnicodeNoncharacter(character))
            {
                builder ??= CreateSanitizedBuilder(text, index);
                builder.Append('\uFFFD');
                continue;
            }

            builder?.Append(character);
        }

        return builder?.ToString() ?? text;
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = Sanitize(text).Normalize(NormalizationForm.FormD);
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

    private static StringBuilder CreateSanitizedBuilder(string text, int invalidIndex)
    {
        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, invalidIndex);
        return builder;
    }

    private static bool IsUnicodeNoncharacter(int scalar) =>
        scalar is >= 0xFDD0 and <= 0xFDEF || (scalar & 0xFFFE) == 0xFFFE;

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
        return Parse(query).ItemMatchQuery;
    }

    public static SearchExpression Parse(string? query)
    {
        var sanitized = Sanitize(query);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return SearchExpression.Empty;
        }

        var tokens = Tokenize(sanitized);
        var groups = new List<SearchConjunction>();
        var current = new List<SearchTerm>();
        var exclusions = new List<SearchTerm>();
        var acceptedUnits = new List<string>();
        var requiresPositiveTerm = false;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            switch (token.Kind)
            {
                case SearchTokenKind.Term:
                    current.Add(token.Term!);
                    requiresPositiveTerm = false;
                    break;

                case SearchTokenKind.Exclude:
                    if (requiresPositiveTerm || index + 1 >= tokens.Count || tokens[index + 1].Kind != SearchTokenKind.Term)
                    {
                        throw new SearchQueryException("Use '-' imediatamente antes de uma palavra ou frase a excluir.");
                    }

                    exclusions.Add(tokens[++index].Term!);
                    break;

                case SearchTokenKind.Unit:
                    if (requiresPositiveTerm)
                    {
                        throw new SearchQueryException(
                            "O filtro de unidade é global; não use '+' ou OU imediatamente antes de uma unidade.");
                    }

                    acceptedUnits.Add(token.Term!.Words.Single());
                    break;

                case SearchTokenKind.And:
                    if (current.Count == 0 || requiresPositiveTerm)
                    {
                        throw new SearchQueryException("O operador '+' precisa ficar entre dois termos positivos.");
                    }

                    requiresPositiveTerm = true;
                    break;

                case SearchTokenKind.Or:
                    if (current.Count == 0 || requiresPositiveTerm)
                    {
                        throw new SearchQueryException("O operador OU precisa ficar entre duas expressões positivas.");
                    }

                    groups.Add(new SearchConjunction(current.ToArray()));
                    current.Clear();
                    requiresPositiveTerm = true;
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (requiresPositiveTerm)
        {
            throw new SearchQueryException("A expressão não pode terminar com um operador.");
        }

        if (current.Count > 0)
        {
            groups.Add(new SearchConjunction(current.ToArray()));
        }

        if (groups.Count == 0 && acceptedUnits.Count == 0)
        {
            throw new SearchQueryException(
                "Informe ao menos uma palavra, frase ou unidade positiva; não é possível pesquisar somente exclusões.");
        }

        return new SearchExpression(
            sanitized.Trim(),
            groups,
            exclusions,
            acceptedUnits.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static List<SearchToken> Tokenize(string text)
    {
        var tokens = new List<SearchToken>();
        for (var index = 0; index < text.Length;)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            var character = text[index];
            if (character == '+')
            {
                tokens.Add(new SearchToken(SearchTokenKind.And));
                index++;
                continue;
            }

            if (character == '|')
            {
                tokens.Add(new SearchToken(SearchTokenKind.Or));
                index++;
                continue;
            }

            if (character == '-')
            {
                if (index + 1 >= text.Length || char.IsWhiteSpace(text[index + 1]))
                {
                    throw new SearchQueryException("Use '-' colado à palavra ou frase que deseja excluir.");
                }

                tokens.Add(new SearchToken(SearchTokenKind.Exclude));
                index++;
                continue;
            }

            if (character is '"' or '“')
            {
                var closingQuote = character == '"' ? text.IndexOf('"', index + 1) : -1;
                var isClosedPhrase = closingQuote > index + 1 && !char.IsWhiteSpace(text[closingQuote - 1]);
                if (isClosedPhrase)
                {
                    var phrase = CreateTerm(text[(index + 1)..closingQuote], isPhrase: true);
                    tokens.Add(new SearchToken(SearchTokenKind.Term, phrase));
                    index = closingQuote + 1;
                    continue;
                }

                index++;
                var unitStart = index;
                while (index < text.Length &&
                       !char.IsWhiteSpace(text[index]) &&
                       text[index] is not '+' and not '|' and not '-' and not '"' and not '“' and not '”')
                {
                    index++;
                }

                var unitWords = WordRegex().Matches(Normalize(text[unitStart..index]))
                    .Select(match => match.Value)
                    .ToArray();
                if (unitWords.Length != 1)
                {
                    throw new SearchQueryException(
                        "Use uma aspas antes de cada unidade, por exemplo: \"pacote \"unidade.");
                }

                tokens.Add(new SearchToken(
                    SearchTokenKind.Unit,
                    new SearchTerm(unitWords, IsPhrase: false)));
                continue;
            }

            var start = index;
            while (index < text.Length &&
                   !char.IsWhiteSpace(text[index]) &&
                   text[index] is not '+' and not '|' and not '-' and not '"' and not '“' and not '”')
            {
                index++;
            }

            var raw = text[start..index];
            var normalized = Normalize(raw);
            if (normalized is "and" or "not" || raw.IndexOfAny(['&', '!', '(', ')']) >= 0)
            {
                throw new SearchQueryException(
                    "Operador inválido. Use espaço ou '+' para E, e OU, OR ou '|' para OU.");
            }

            if (normalized is "ou" or "or")
            {
                tokens.Add(new SearchToken(SearchTokenKind.Or));
                continue;
            }

            foreach (Match word in WordRegex().Matches(normalized))
            {
                tokens.Add(new SearchToken(
                    SearchTokenKind.Term,
                    new SearchTerm([word.Value], IsPhrase: false)));
            }
        }

        if (tokens.Count == 0)
        {
            throw new SearchQueryException("A pesquisa não contém palavras ou frases válidas.");
        }

        return tokens;
    }

    private static SearchTerm CreateTerm(string text, bool isPhrase)
    {
        var words = WordRegex().Matches(Normalize(text)).Select(match => match.Value).ToArray();
        if (words.Length == 0)
        {
            throw new SearchQueryException("Uma frase entre aspas não pode estar vazia.");
        }

        return new SearchTerm(words, isPhrase);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    private enum SearchTokenKind
    {
        Term,
        And,
        Or,
        Exclude,
        Unit
    }

    private sealed record SearchToken(SearchTokenKind Kind, SearchTerm? Term = null);
}

public sealed class SearchQueryException(string message) : FormatException(message);

public sealed record SearchTerm(IReadOnlyList<string> Words, bool IsPhrase)
{
    internal string ToFtsQuery()
    {
        var phrase = $"\"{string.Join(' ', Words)}\"";
        return phrase + "*";
    }

    internal bool Matches(IReadOnlyList<string> foundWords)
    {
        if (!IsPhrase)
        {
            return foundWords.Any(word => word.StartsWith(Words[0], StringComparison.Ordinal));
        }

        for (var start = 0; start <= foundWords.Count - Words.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < Words.Count; offset++)
            {
                var found = foundWords[start + offset];
                var requested = Words[offset];
                var wordMatches = offset == Words.Count - 1
                    ? found.StartsWith(requested, StringComparison.Ordinal)
                    : string.Equals(found, requested, StringComparison.Ordinal);
                if (!wordMatches)
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record SearchConjunction(IReadOnlyList<SearchTerm> Terms)
{
    internal string ToFtsQuery()
    {
        var value = string.Join(" AND ", Terms.Select(term => term.ToFtsQuery()));
        return Terms.Count > 1 ? $"({value})" : value;
    }

    internal bool Matches(IReadOnlyList<string> foundWords) =>
        Terms.All(term => term.Matches(foundWords));
}

public sealed record SearchExpression(
    string OriginalText,
    IReadOnlyList<SearchConjunction> PositiveGroups,
    IReadOnlyList<SearchTerm> Exclusions,
    IReadOnlyList<string> AcceptedUnits)
{
    public static SearchExpression Empty { get; } = new(string.Empty, [], [], []);

    public bool IsEmpty => PositiveGroups.Count == 0 && Exclusions.Count == 0 && AcceptedUnits.Count == 0;

    public bool HasPositiveDescriptionTerms => PositiveGroups.Count > 0;

    public string PositiveText => string.Join(
        ' ',
        PositiveGroups.SelectMany(group => group.Terms).SelectMany(term => term.Words));

    public string ContractMatchQuery => BuildPositiveQuery();

    public string ItemMatchQuery
    {
        get
        {
            var value = BuildPositiveQuery();
            if (value.Length == 0)
            {
                return string.Empty;
            }

            foreach (var exclusion in Exclusions)
            {
                value = $"({value}) NOT {exclusion.ToFtsQuery()}";
            }

            return value;
        }
    }

    public bool MatchesItem(string? description, string? unit)
    {
        var descriptionWords = Words(description);
        var matchesPositive = PositiveGroups.Count == 0 || PositiveGroups.Any(group => group.Matches(descriptionWords));
        if (!matchesPositive || Exclusions.Any(exclusion => exclusion.Matches(descriptionWords)))
        {
            return false;
        }

        if (AcceptedUnits.Count == 0)
        {
            return true;
        }

        var unitWords = Words(unit);
        return AcceptedUnits.Any(accepted =>
            unitWords.Any(found => found.StartsWith(accepted, StringComparison.Ordinal)));
    }

    public string CandidateMatchQuery
    {
        get
        {
            var terms = PositiveGroups
                .SelectMany(group => group.Terms)
                .SelectMany(term => term.Words)
                .Distinct(StringComparer.Ordinal)
                .Select(word => $"\"{word}\"*");
            return string.Join(" OR ", terms);
        }
    }

    private string BuildPositiveQuery()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var value = string.Join(" OR ", PositiveGroups.Select(group => group.ToFtsQuery()));
        return PositiveGroups.Count > 1 ? $"({value})" : value;
    }

    private static IReadOnlyList<string> Words(string? text) => Regex
        .Matches(SearchText.Normalize(text), @"[\p{L}\p{N}]+")
        .Select(match => match.Value)
        .ToArray();
}
