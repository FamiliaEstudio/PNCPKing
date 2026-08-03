using System.Globalization;
using System.Text.RegularExpressions;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

public sealed partial class CatalogSearchService(ICatalogRepository repository) : ICatalogSearchService
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "A", "AS", "O", "OS", "DE", "DA", "DAS", "DO", "DOS", "E", "EM", "COM", "PARA", "POR"
    };

    public async Task<CatalogSearchPage> SearchAsync(
        CatalogSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1 || query.PageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Página e tamanho da página são inválidos.");
        }

        var rules = await repository.GetEquivalenceRulesAsync(cancellationToken).ConfigureAwait(false);
        var vocabulary = new Vocabulary(rules);
        var expandedText = vocabulary.ExpandForSearch(query.Text);
        var candidates = (await repository.FindCandidatesAsync(
                query with { Text = expandedText },
                1000,
                cancellationToken).ConfigureAwait(false))
            .ToList();

        var code = new string(query.Text.Where(char.IsDigit).ToArray());
        if (code.Length > 0 && code.Length == query.Text.Trim().Length)
        {
            foreach (var kind in query.Kind is { } selected ? new[] { selected } : new[] { CatalogKind.Catmat, CatalogKind.Catser })
            {
                var exact = await repository.GetEntryAsync(kind, code, cancellationToken).ConfigureAwait(false);
                if (exact is { Active: true } && candidates.All(item => item.Kind != kind || item.Code != code))
                {
                    candidates.Insert(0, exact);
                }
            }
        }

        var ranked = candidates
            .Select(entry => Score(query.Text, entry, vocabulary))
            .OrderByDescending(result => string.Equals(result.Entry.Code, query.Text.Trim(), StringComparison.Ordinal) ? 1 : 0)
            .ThenByDescending(result => result.Score)
            .ThenBy(result => result.ConflictCount)
            .ThenByDescending(result => result.MatchCount)
            .ThenByDescending(result => result.Entry.RemoteUpdatedAt)
            .ThenBy(result => result.Entry.Code, StringComparer.Ordinal)
            .ToArray();
        var offset = (query.Page - 1) * query.PageSize;
        return new CatalogSearchPage(
            ranked.Skip(offset).Take(query.PageSize).ToArray(),
            query.Page,
            query.PageSize,
            ranked.Length);
    }

    private static CatalogSearchResult Score(string requested, CatalogEntry entry, Vocabulary vocabulary)
    {
        var normalizedRequested = vocabulary.Normalize(requested);
        var normalizedCandidate = vocabulary.Normalize(
            $"{entry.Description} {entry.Hierarchy} {entry.Code}");
        var requestedTokens = Tokens(normalizedRequested);
        var candidateTokens = Tokens(normalizedCandidate).ToHashSet(StringComparer.Ordinal);
        var signals = new List<CatalogMatchSignal>();
        var matchedTokens = 0;
        foreach (var token in requestedTokens)
        {
            var matched = candidateTokens.Contains(token);
            if (matched) matchedTokens++;
            signals.Add(new CatalogMatchSignal(
                token,
                matched ? token : string.Empty,
                matched ? CatalogMatchState.Match : CatalogMatchState.Missing,
                matched ? "Termo equivalente localizado." : "O catálogo não informa termo equivalente."));
        }

        var requestedMeasures = ParseMeasures(requested, vocabulary);
        var candidateMeasures = ParseMeasures(entry.Description, vocabulary);
        var measureMatches = 0;
        var conflicts = 0;
        foreach (var measure in requestedMeasures)
        {
            var sameDimension = candidateMeasures.Where(item => item.Dimension == measure.Dimension).ToArray();
            var equivalent = sameDimension.FirstOrDefault(item => Equivalent(measure.CanonicalValue, item.CanonicalValue));
            if (equivalent is not null)
            {
                measureMatches++;
                signals.Add(new CatalogMatchSignal(
                    measure.Original,
                    equivalent.Original,
                    CatalogMatchState.Match,
                    "Medida equivalente após conversão."));
            }
            else if (sameDimension.Length > 0)
            {
                conflicts++;
                signals.Add(new CatalogMatchSignal(
                    measure.Original,
                    sameDimension[0].Original,
                    CatalogMatchState.Conflict,
                    "A mesma dimensão possui valor diferente."));
            }
            else
            {
                signals.Add(new CatalogMatchSignal(
                    measure.Original,
                    string.Empty,
                    CatalogMatchState.Missing,
                    "O catálogo não fornece medida comparável."));
            }
        }

        var requestedFeatures = ParseFeatures(requested, vocabulary);
        var candidateFeatures = ParseFeatures(entry.Description, vocabulary);
        var featureMatches = 0;
        foreach (var feature in requestedFeatures)
        {
            if (!candidateFeatures.TryGetValue(feature.Key, out var candidateValue))
            {
                signals.Add(new CatalogMatchSignal(
                    $"{feature.Key}: {feature.Value}", string.Empty, CatalogMatchState.Missing,
                    "Característica ausente no candidato."));
                continue;
            }

            var featureMatch = string.Equals(feature.Value, candidateValue, StringComparison.Ordinal) ||
                               candidateValue.Contains(feature.Value, StringComparison.Ordinal) ||
                               feature.Value.Contains(candidateValue, StringComparison.Ordinal);
            if (featureMatch)
            {
                featureMatches++;
                signals.Add(new CatalogMatchSignal(
                    $"{feature.Key}: {feature.Value}", candidateValue, CatalogMatchState.Match,
                    "Característica correspondente."));
            }
            else
            {
                conflicts++;
                signals.Add(new CatalogMatchSignal(
                    $"{feature.Key}: {feature.Value}", candidateValue, CatalogMatchState.Conflict,
                    "A mesma característica possui valor diferente."));
            }
        }

        var tokenCoverage = requestedTokens.Length == 0 ? 0m : matchedTokens * 100m / requestedTokens.Length;
        var phraseSimilarity = (decimal)Dice(normalizedRequested, normalizedCandidate) * 100m;
        decimal score;
        var requestedCharacteristicCount = requestedMeasures.Length + requestedFeatures.Count;
        if (requestedCharacteristicCount == 0)
        {
            score = tokenCoverage * .80m + phraseSimilarity * .20m;
        }
        else
        {
            var characteristicScore =
                (measureMatches + featureMatches) * 100m / requestedCharacteristicCount;
            score = tokenCoverage * .50m + phraseSimilarity * .20m + characteristicScore * .30m;
        }

        score = Math.Clamp(score - Math.Min(50, conflicts * 25), 0m, 100m);
        if (string.Equals(entry.Code, requested.Trim(), StringComparison.Ordinal)) score = 100m;
        var compactSignals = signals
            .DistinctBy(signal => (signal.Requested, signal.Found, signal.State))
            .Take(30)
            .ToArray();
        return new CatalogSearchResult(
            entry,
            decimal.Round(score, 1),
            compactSignals,
            compactSignals.Count(signal => signal.State == CatalogMatchState.Match),
            compactSignals.Count(signal => signal.State == CatalogMatchState.Conflict),
            compactSignals.Count(signal => signal.State == CatalogMatchState.Missing));
    }

    private static string[] Tokens(string value) => value
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant())
        .Where(token => token.Length > 1 && !StopWords.Contains(token))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyDictionary<string, string> ParseFeatures(string value, Vocabulary vocabulary)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in FeatureSeparator().Split(value))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0 || colon == part.Length - 1) continue;
            var key = vocabulary.Normalize(part[..colon]).ToUpperInvariant();
            var featureValue = vocabulary.Normalize(part[(colon + 1)..]).ToUpperInvariant();
            if (key.Length > 0 && featureValue.Length > 0) result[key] = featureValue;
        }

        return result;
    }

    private static Measurement[] ParseMeasures(string value, Vocabulary vocabulary)
    {
        var result = new List<Measurement>();
        foreach (Match match in MeasurePattern().Matches(value.ToUpperInvariant().Replace('”', '"').Replace('″', '"')))
        {
            if (!TryParseNumber(match.Groups["number"].Value, out var number) ||
                !vocabulary.TryGetUnit(match.Groups["unit"].Value, out var unit)) continue;
            result.Add(new Measurement(match.Value.Trim(), unit.Dimension, number * unit.Factor));
        }

        return result.ToArray();
    }

    private static bool TryParseNumber(string value, out decimal result)
    {
        value = value.Trim();
        var mixed = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (mixed.Length == 2 && decimal.TryParse(mixed[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) &&
            TryParseFraction(mixed[1], out var fraction))
        {
            result = whole + fraction;
            return true;
        }

        if (TryParseFraction(value, out result)) return true;
        return decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseFraction(string value, out decimal result)
    {
        var parts = value.Split('/');
        if (parts.Length == 2 && decimal.TryParse(parts[0], out var numerator) &&
            decimal.TryParse(parts[1], out var denominator) && denominator != 0)
        {
            result = numerator / denominator;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool Equivalent(decimal first, decimal second)
    {
        var tolerance = Math.Max(.01m, Math.Max(Math.Abs(first), Math.Abs(second)) * .005m);
        return Math.Abs(first - second) <= tolerance;
    }

    private static double Dice(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0) return 0;
        if (second.Contains(first, StringComparison.Ordinal)) return 1;
        var firstPairs = Bigrams(first);
        var secondPairs = Bigrams(second);
        var intersection = firstPairs.Intersect(secondPairs, StringComparer.Ordinal).Count();
        return firstPairs.Count + secondPairs.Count == 0
            ? 0
            : 2d * intersection / (firstPairs.Count + secondPairs.Count);
    }

    private static HashSet<string> Bigrams(string value)
    {
        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < compact.Length; index++) result.Add(compact.Substring(index, 2));
        return result;
    }

    private sealed record Measurement(string Original, string Dimension, decimal CanonicalValue);
    private sealed record Unit(string Dimension, decimal Factor);

    private sealed class Vocabulary(IReadOnlyList<CatalogEquivalenceRule> rules)
    {
        private readonly Dictionary<string, string> _aliases = rules
            .GroupBy(rule => NormalizeKey(rule.Alias), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => NormalizeKey(group.First().Canonical), StringComparer.Ordinal);
        private readonly Dictionary<string, Unit> _units = rules
            .Where(rule => rule.Kind == CatalogRuleKind.UnitConversion)
            .GroupBy(rule => NormalizeKey(rule.Alias), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new Unit(group.First().Dimension, group.First().Factor),
                StringComparer.Ordinal);

        public string Normalize(string value)
        {
            var normalized = SearchText.Normalize(value.Replace('”', '"').Replace('″', '"')).ToUpperInvariant();
            normalized = normalized.Replace("²", "2", StringComparison.Ordinal);
            foreach (var alias in _aliases.OrderByDescending(pair => pair.Key.Length))
            {
                if (alias.Key == "\"")
                {
                    normalized = normalized.Replace("\"", $" {alias.Value} ", StringComparison.Ordinal);
                    continue;
                }

                normalized = Regex.Replace(
                    normalized,
                    $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(alias.Key)}(?![\p{{L}}\p{{N}}])",
                    alias.Value,
                    RegexOptions.CultureInvariant);
            }

            return Regex.Replace(normalized, @"\s+", " ").Trim();
        }

        public string ExpandForSearch(string value)
        {
            var normalized = Normalize(value);
            return string.Join(' ', new[] { value, normalized }.Where(item => !string.IsNullOrWhiteSpace(item)));
        }

        public bool TryGetUnit(string value, out Unit unit) =>
            _units.TryGetValue(NormalizeKey(value.Replace("²", "2", StringComparison.Ordinal)), out unit!);

        private static string NormalizeKey(string value) =>
            SearchText.Normalize(value).ToUpperInvariant().Replace("²", "2", StringComparison.Ordinal);
    }

    [GeneratedRegex(@",\s*(?=[^,:]{2,}:)", RegexOptions.CultureInvariant)]
    private static partial Regex FeatureSeparator();

    [GeneratedRegex(@"(?<number>\d+\s+\d+/\d+|\d+/\d+|\d+(?:[\.,]\d+)?)\s*(?<unit>""|[A-ZÁ-Úa-zá-ú]+(?:²|2)?)", RegexOptions.CultureInvariant)]
    private static partial Regex MeasurePattern();
}
