using System.Globalization;
using System.Text.RegularExpressions;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Core.Quotations;

public sealed partial class QuotationAnalyzer
{
    public const int BasketAlgorithmVersion = 1;
    public const string RulesVersion = "2.0";
    public const int MaximumBasketPoolSize = 60;
    public const int MaximumCuratedBaskets = 100;
    public const decimal MinimumDescriptionScore = 20m;
    public const decimal MinimumTotalScore = 60m;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "ao", "aos", "as", "com", "da", "das", "de", "do", "dos", "e", "em", "o", "os", "para", "por"
    };

    private static readonly IReadOnlyDictionary<string, string> TokenAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pct"] = "pacote", ["pcte"] = "pacote", ["pac"] = "pacote",
        ["cx"] = "caixa", ["un"] = "unidade", ["und"] = "unidade", ["unid"] = "unidade",
        ["quilograma"] = "kg", ["quilogramas"] = "kg", ["quilo"] = "kg", ["quilos"] = "kg",
        ["gr"] = "g", ["grama"] = "g", ["gramas"] = "g", ["litro"] = "l", ["litros"] = "l",
        ["mililitro"] = "ml", ["mililitros"] = "ml", ["garrafas"] = "garrafa",
        ["pacotes"] = "pacote", ["caixas"] = "caixa", ["unidades"] = "unidade"
    };

    private static readonly HashSet<string> PackagingUnits = new(StringComparer.Ordinal)
    {
        "pacote", "caixa", "unidade", "garrafa", "lata", "saco", "sacola", "kit", "par", "rolo", "resma", "fardo"
    };

    private readonly DateOnly _today;

    public QuotationAnalyzer(DateOnly? today = null)
    {
        _today = today ?? DateOnly.FromDateTime(DateTime.Today);
    }

    public QuotationLineAnalysis Analyze(
        QuotationLine line,
        IReadOnlyList<QuotationReference> collectedReferences,
        IReadOnlyList<QuotationManualBasket>? manualBaskets = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(collectedReferences);
        line.Weights.Validate();
        if (line.RequestedBasketSize is < 3 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(line),
                "O número de preços da cesta automática deve estar entre 3 e 10.");
        }

        var exactlyUnique = collectedReferences
            .GroupBy(reference => reference.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var scored = exactlyUnique.Select(reference => ScoreReference(line, reference)).ToArray();
        var eligible = scored.Where(reference => reference.State == QuotationReferenceState.Eligible).ToArray();
        var pool = BuildBasketPool(
            eligible.Where(reference =>
                reference.Source == QuotationReferenceSource.PncpIncisoII).ToArray());
        var automaticBaskets = BuildAutomaticBaskets(pool, line.RequestedBasketSize);
        var persistedManualBaskets = BuildManualBaskets(scored, manualBaskets ?? []);
        var baskets = persistedManualBaskets.Concat(automaticBaskets).ToArray();

        return new QuotationLineAnalysis(
            line,
            scored,
            baskets,
            exactlyUnique.Length,
            eligible.Length,
            0,
            scored.Count(reference => reference.State == QuotationReferenceState.Rejected),
            pool.Count);
    }

    public QuotationReference ScoreReference(QuotationLine line, QuotationReference reference)
    {
        if (reference.Source == QuotationReferenceSource.InternetIncisoIII)
        {
            return ScoreInternetReference(line, reference);
        }

        var requestedDescriptor = line.Description;
        var foundDescriptor = string.Join(' ', new[]
        {
            reference.ItemDescription,
            reference.ItemAdditionalInformation,
            reference.ItemUnit,
            reference.ItemCategory,
            reference.NcmNbsCode,
            reference.NcmNbsDescription,
            reference.CatalogCode,
            reference.CatalogName,
            reference.CatalogCategory
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var (descriptionQuality, descriptionExplanation) = CalculateDescriptionScore(requestedDescriptor, foundDescriptor);
        var (unitQuality, unitExplanation, unitConflict) = CalculateUnitScore(
            line.RequestedUnit,
            line.Description,
            reference.ItemUnit,
            $"{reference.ItemDescription} {reference.ItemAdditionalInformation}");
        var (measureConflict, measureExplanation) = HasMeasureConflict(line.Description, foundDescriptor);
        var quantity = reference.HomologatedQuantity ?? reference.ItemRequestedQuantity;
        var quantityQuality = CalculateQuantityScore(line.RequestedQuantity, quantity);
        var proximityQuality = CalculateProximityScore(reference);
        var recencyQuality = CalculateRecencyScore(reference.ResultDate, reference.PublicationDate);
        var breakdown = new AdequacyBreakdown(
            ApplyWeight(descriptionQuality, 50m, line.Weights.Description),
            ApplyWeight(unitQuality, 20m, line.Weights.Unit),
            ApplyWeight(quantityQuality, 10m, line.Weights.Quantity),
            ApplyWeight(proximityQuality, 15m, line.Weights.Proximity),
            ApplyWeight(recencyQuality, 5m, line.Weights.Recency),
            BuildExplanation(
                line.Weights,
                descriptionQuality,
                descriptionExplanation,
                unitQuality,
                unitExplanation,
                quantity,
                quantityQuality,
                proximityQuality,
                recencyQuality,
                measureExplanation));

        var normalizedTaxId = NormalizeTaxId(reference.SupplierTaxId);
        var rejectionReason = GetRejectionReason(
            line,
            reference,
            descriptionQuality);
        var observations = new List<string>();
        if (!IsValidCnpj(normalizedTaxId))
        {
            observations.Add("CNPJ/NI não validado");
        }

        if (unitConflict || measureConflict)
        {
            observations.Add("unidade, embalagem ou medida divergente");
        }

        if (breakdown.Total < MinimumTotalScore)
        {
            observations.Add($"índice abaixo de {MinimumTotalScore:N0}/100");
        }

        var eligibleReason = observations.Count == 0
            ? "Referência elegível; o índice é informativo."
            : $"Referência elegível; o índice é informativo. Atenção: {string.Join("; ", observations)}.";
        return reference with
        {
            SupplierTaxId = normalizedTaxId,
            Adequacy = breakdown,
            State = rejectionReason is null ? QuotationReferenceState.Eligible : QuotationReferenceState.Rejected,
            StateReason = rejectionReason ?? eligibleReason,
            DuplicateOfReferenceId = null
        };
    }

    private static QuotationReference ScoreInternetReference(
        QuotationLine line,
        QuotationReference reference)
    {
        var normalizedTaxId = NormalizeTaxId(reference.SupplierTaxId);
        var problems = new List<string>();
        if (reference.UnitPrice <= 0)
        {
            problems.Add("preço unitário não positivo");
        }

        if (line.MinimumUnitPrice is not null && reference.UnitPrice < line.MinimumUnitPrice)
        {
            problems.Add("preço abaixo do mínimo definido");
        }

        if (line.MaximumUnitPrice is not null && reference.UnitPrice > line.MaximumUnitPrice)
        {
            problems.Add("preço acima do máximo definido");
        }

        if (!Uri.TryCreate(reference.PortalUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            problems.Add("URL da fonte inválida");
        }

        if (string.IsNullOrWhiteSpace(reference.ItemDescription))
        {
            problems.Add("descrição ausente");
        }

        if (string.IsNullOrWhiteSpace(reference.SupplierName))
        {
            problems.Add("empresa ausente");
        }

        if (!IsValidCnpj(normalizedTaxId))
        {
            problems.Add("CNPJ inválido");
        }

        return reference with
        {
            SupplierTaxId = normalizedTaxId,
            Adequacy = new AdequacyBreakdown(
                0m,
                0m,
                0m,
                0m,
                0m,
                "Referência do Inciso III incluída manualmente com evidências de preço e CNPJ."),
            State = problems.Count == 0
                ? QuotationReferenceState.Eligible
                : QuotationReferenceState.Rejected,
            StateReason = problems.Count == 0
                ? "Referência manual do Inciso III com cadastro e evidências completos."
                : $"Referência do Inciso III inválida: {string.Join("; ", problems)}.",
            DuplicateOfReferenceId = null,
            MatchedPromptLevel = null,
            MatchedSearchText = string.Empty
        };
    }

    public static bool IsValidCnpj(string? value)
    {
        var normalized = NormalizeTaxId(value);
        if (normalized.Length != 14 || normalized[..12].Any(character => !char.IsAsciiLetterOrDigit(character)) ||
            normalized[^2..].Any(character => !char.IsAsciiDigit(character)))
        {
            return false;
        }

        var upper = normalized.ToUpperInvariant();
        if (upper[..12].Distinct().Count() == 1)
        {
            return false;
        }

        var first = CalculateCnpjDigit(upper[..12], [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        var second = CalculateCnpjDigit(upper[..12] + first.ToString(CultureInfo.InvariantCulture), [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        return upper[12] - '0' == first && upper[13] - '0' == second;
    }

    private QuotationReference[] SuppressProbableDuplicates(IReadOnlyList<QuotationReference> references)
    {
        var result = references.ToDictionary(reference => reference.Id, StringComparer.Ordinal);
        var accepted = new List<QuotationReference>();
        foreach (var candidate in references
                     .Where(reference => reference.State == QuotationReferenceState.Eligible)
                     .OrderByDescending(reference => reference.Adequacy.Total)
                     .ThenBy(reference => reference.DistanceFromRibeiraoKilometers ?? double.MaxValue)
                     .ThenByDescending(reference => reference.ResultDate)
                     .ThenBy(PromptRank)
                     .ThenBy(reference => reference.Id, StringComparer.Ordinal))
        {
            var duplicateOf = accepted.FirstOrDefault(existing => IsProbableDuplicate(existing, candidate));
            if (duplicateOf is null)
            {
                accepted.Add(candidate);
                continue;
            }

            result[candidate.Id] = candidate with
            {
                State = QuotationReferenceState.Duplicate,
                StateReason = $"Provável repetição da referência {duplicateOf.Id}.",
                DuplicateOfReferenceId = duplicateOf.Id
            };
        }

        return result.Values.OrderBy(reference => reference.Id, StringComparer.Ordinal).ToArray();
    }

    private static bool IsProbableDuplicate(QuotationReference first, QuotationReference second)
    {
        if (!string.Equals(first.SupplierTaxId, second.SupplierTaxId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SearchText.Normalize(first.Organization), SearchText.Normalize(second.Organization), StringComparison.Ordinal) ||
            !string.Equals(ItemSignature(first), ItemSignature(second), StringComparison.Ordinal))
        {
            return false;
        }

        var higherPrice = Math.Max(first.UnitPrice, second.UnitPrice);
        if (higherPrice <= 0 || Math.Abs(first.UnitPrice - second.UnitPrice) / higherPrice > 0.01m)
        {
            return false;
        }

        return first.ResultDate is not null && second.ResultDate is not null &&
               Math.Abs(first.ResultDate.Value.DayNumber - second.ResultDate.Value.DayNumber) <= 30;
    }

    private static string ItemSignature(QuotationReference reference)
    {
        var canonicalUnit = ResolveUnit(reference.ItemUnit, $"{reference.ItemDescription} {reference.ItemAdditionalInformation}").Unit;
        var measure = ExtractMeasure($"{reference.ItemDescription} {reference.ItemAdditionalInformation}");
        var normalizedDescription = string.Join(' ', Tokenize(reference.ItemDescription));
        return $"{normalizedDescription}|{canonicalUnit}|{measure?.Dimension}:{measure?.BaseValue}";
    }

    private static IReadOnlyList<QuotationReference> BuildBasketPool(IReadOnlyList<QuotationReference> eligible)
    {
        var selected = new Dictionary<string, QuotationReference>(StringComparer.Ordinal);
        Add(selected, eligible.OrderByDescending(reference => reference.Adequacy.Total).ThenBy(PromptRank).ThenBy(reference => reference.Id), 40);
        Add(selected, eligible.OrderBy(reference => reference.UnitPrice).ThenByDescending(reference => reference.Adequacy.Total).ThenBy(PromptRank), 10);
        Add(selected, eligible.OrderByDescending(reference => reference.UnitPrice).ThenByDescending(reference => reference.Adequacy.Total).ThenBy(PromptRank), 10);
        Fill(selected, eligible.OrderByDescending(reference => reference.Adequacy.Total).ThenBy(PromptRank).ThenBy(reference => reference.Id), MaximumBasketPoolSize);
        return selected.Values.Take(MaximumBasketPoolSize).ToArray();
    }

    private static void Add(
        IDictionary<string, QuotationReference> selected,
        IEnumerable<QuotationReference> candidates,
        int count)
    {
        foreach (var candidate in candidates.Take(count))
        {
            selected.TryAdd(candidate.Id, candidate);
        }
    }

    private static void Fill(
        IDictionary<string, QuotationReference> selected,
        IEnumerable<QuotationReference> candidates,
        int maximumCount)
    {
        foreach (var candidate in candidates)
        {
            selected.TryAdd(candidate.Id, candidate);
            if (selected.Count >= maximumCount)
            {
                break;
            }
        }
    }

    private static IReadOnlyList<QuotationBasket> BuildAutomaticBaskets(
        IReadOnlyList<QuotationReference> pool,
        int requestedSize)
    {
        if (pool.Count < 2)
        {
            return [];
        }

        var basketSize = Math.Min(requestedSize, pool.Count);
        var candidates = new Dictionary<string, QuotationBasket>(StringComparer.Ordinal);
        var rankings = new[]
        {
            pool.OrderByDescending(reference => reference.Adequacy.Total)
                .ThenBy(PromptRank)
                .ThenBy(reference => reference.Id, StringComparer.Ordinal).ToArray(),
            pool.OrderBy(reference => reference.UnitPrice)
                .ThenByDescending(reference => reference.Adequacy.Total)
                .ThenBy(PromptRank)
                .ThenBy(reference => reference.Id, StringComparer.Ordinal).ToArray(),
            pool.OrderByDescending(reference => reference.UnitPrice)
                .ThenByDescending(reference => reference.Adequacy.Total)
                .ThenBy(PromptRank)
                .ThenBy(reference => reference.Id, StringComparer.Ordinal).ToArray()
        };

        foreach (var ranking in rankings)
        {
            for (var start = 0; start < ranking.Length; start++)
            {
                AddAutomaticCandidate(
                    candidates,
                    Enumerable.Range(0, basketSize)
                        .Select(offset => ranking[(start + offset) % ranking.Length]),
                    requestedSize);
            }
        }

        foreach (var seed in pool.OrderBy(reference => reference.Id, StringComparer.Ordinal))
        {
            var selected = new List<QuotationReference> { seed };
            while (selected.Count < basketSize)
            {
                var next = pool
                    .Where(candidate => selected.All(existing => existing.Id != candidate.Id))
                    .Select(candidate => new
                    {
                        Reference = candidate,
                        Basket = CreateBasket(
                            selected.Append(candidate).ToArray(),
                            QuotationBasketKind.Automatic,
                            string.Empty,
                            null,
                            requestedSize)
                    })
                    .OrderByDescending(candidate => candidate.Basket.Score)
                    .ThenBy(candidate => BasketPromptRank(candidate.Basket))
                    .ThenBy(candidate => PromptRank(candidate.Reference))
                    .ThenBy(candidate => candidate.Reference.Id, StringComparer.Ordinal)
                    .First()
                    .Reference;
                selected.Add(next);
            }

            AddAutomaticCandidate(candidates, selected, requestedSize);
        }

        var baskets = candidates.Values.ToArray();
        var recommended = baskets
            .OrderByDescending(basket => basket.Score)
            .ThenBy(BasketPromptRank)
            .ThenBy(basket => basket.Key, StringComparer.Ordinal)
            .First();
        var cheapest = baskets
            .OrderBy(basket => basket.AveragePrice)
            .ThenByDescending(basket => basket.Score)
            .ThenBy(BasketPromptRank)
            .ThenBy(basket => basket.Key, StringComparer.Ordinal)
            .First();
        var mostExpensive = baskets
            .OrderByDescending(basket => basket.AveragePrice)
            .ThenByDescending(basket => basket.Score)
            .ThenBy(BasketPromptRank)
            .ThenBy(basket => basket.Key, StringComparer.Ordinal)
            .First();
        var marked = baskets
            .Select(basket => basket with
            {
                IsRecommended = basket.Key == recommended.Key,
                IsCheapest = basket.Key == cheapest.Key,
                IsMostExpensive = basket.Key == mostExpensive.Key
            })
            .OrderByDescending(basket => basket.Score)
            .ThenBy(BasketPromptRank)
            .ThenBy(basket => basket.Key, StringComparer.Ordinal)
            .ToList();
        var selectedBaskets = marked.Take(MaximumCuratedBaskets).ToDictionary(basket => basket.Key, StringComparer.Ordinal);
        foreach (var mandatory in marked.Where(basket => basket.IsRecommended || basket.IsCheapest || basket.IsMostExpensive))
        {
            if (selectedBaskets.ContainsKey(mandatory.Key))
            {
                continue;
            }

            var removable = selectedBaskets.Values
                .Where(basket => !basket.IsRecommended && !basket.IsCheapest && !basket.IsMostExpensive)
                .OrderBy(basket => basket.Score)
                .ThenByDescending(basket => basket.Key, StringComparer.Ordinal)
                .First();
            selectedBaskets.Remove(removable.Key);
            selectedBaskets.Add(mandatory.Key, mandatory);
        }

        return selectedBaskets.Values
            .OrderByDescending(basket => basket.IsRecommended)
            .ThenByDescending(basket => basket.Score)
            .ThenBy(BasketPromptRank)
            .ThenBy(basket => basket.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddAutomaticCandidate(
        IDictionary<string, QuotationBasket> candidates,
        IEnumerable<QuotationReference> references,
        int requestedSize)
    {
        var basket = CreateBasket(
            references.ToArray(),
            QuotationBasketKind.Automatic,
            string.Empty,
            null,
            requestedSize);
        candidates.TryAdd(basket.Key, basket);
    }

    private static IReadOnlyList<QuotationBasket> BuildManualBaskets(
        IReadOnlyList<QuotationReference> scoredReferences,
        IReadOnlyList<QuotationManualBasket> manualBaskets)
    {
        var referencesById = scoredReferences.ToDictionary(reference => reference.Id, StringComparer.Ordinal);
        var result = new List<QuotationBasket>();
        foreach (var manual in manualBaskets.OrderBy(basket => basket.DisplayOrder).ThenBy(basket => basket.Name))
        {
            var references = manual.ReferenceIds
                .Distinct(StringComparer.Ordinal)
                .Where(referencesById.ContainsKey)
                .Select(id => referencesById[id])
                .ToArray();
            if (references.Length == 0)
            {
                continue;
            }

            result.Add(CreateBasket(
                references,
                QuotationBasketKind.Manual,
                manual.Name,
                manual.Id,
                requestedSize: 3));
        }

        return result;
    }

    private static QuotationBasket CreateBasket(
        IReadOnlyList<QuotationReference> references,
        QuotationBasketKind kind,
        string name,
        Guid? manualBasketId,
        int requestedSize)
    {
        var ordered = references
            .OrderBy(reference => reference.UnitPrice)
            .ThenBy(reference => reference.Id, StringComparer.Ordinal)
            .ToArray();
        var average = ordered.Average(reference => reference.UnitPrice);
        var maximumDeviation = average <= 0
            ? 0
            : ordered.Max(reference => Math.Abs(reference.UnitPrice - average) / average * 100m);
        var averageAdequacy = ordered.Average(reference => reference.Adequacy.Total);
        var minimumAdequacy = ordered.Min(reference => reference.Adequacy.Total);
        var cohesion = Math.Clamp(100m * (1m - maximumDeviation / 25m), 0m, 100m);
        var visualState = kind == QuotationBasketKind.Automatic
            ? maximumDeviation <= 25m
                ? QuotationBasketVisualState.AutomaticRegular
                : QuotationBasketVisualState.AutomaticHighDispersion
            : ordered.Length < 3
                ? QuotationBasketVisualState.ManualIncomplete
                : ordered.All(reference => reference.State == QuotationReferenceState.Eligible) &&
                  maximumDeviation <= 25m
                    ? QuotationBasketVisualState.ManualRegular
                    : QuotationBasketVisualState.ManualInvalid;
        var validationMessage = visualState switch
        {
            QuotationBasketVisualState.AutomaticRegular when ordered.Length < requestedSize =>
                $"Cesta automática reduzida: {ordered.Length:N0} de {requestedSize:N0} preços.",
            QuotationBasketVisualState.AutomaticHighDispersion when ordered.Length < requestedSize =>
                $"Cesta automática reduzida para {ordered.Length:N0} preço(s) e com desvio acima de 25%.",
            QuotationBasketVisualState.AutomaticHighDispersion => "Desvio máximo acima de 25%.",
            QuotationBasketVisualState.ManualIncomplete =>
                $"Cesta manual incompleta: {ordered.Length:N0} de pelo menos 3 preços.",
            QuotationBasketVisualState.ManualInvalid =>
                BuildManualValidationMessage(ordered, maximumDeviation),
            _ => "Cesta regular."
        };
        var ids = ordered.Select(reference => reference.Id).Order(StringComparer.Ordinal).ToArray();
        return new QuotationBasket
        {
            Key = kind == QuotationBasketKind.Manual
                ? $"manual:{manualBasketId!.Value:N}"
                : string.Join("||", ids),
            References = ordered,
            AveragePrice = average,
            MinimumPrice = ordered.Min(reference => reference.UnitPrice),
            MaximumPrice = ordered.Max(reference => reference.UnitPrice),
            MaximumDeviationPercent = maximumDeviation,
            Score = 0.70m * averageAdequacy + 0.20m * minimumAdequacy + 0.10m * cohesion,
            Kind = kind,
            Name = kind == QuotationBasketKind.Manual ? name : string.Empty,
            ManualBasketId = manualBasketId,
            RequestedSize = requestedSize,
            VisualState = visualState,
            ValidationMessage = validationMessage
        };
    }

    private static int PromptRank(QuotationReference reference)
    {
        return reference.MatchedPromptLevel is null
            ? 3
            : (int)reference.MatchedPromptLevel.Value;
    }

    private static decimal BasketPromptRank(QuotationBasket basket)
    {
        return basket.References.Count == 0
            ? 3m
            : basket.References.Average(reference => (decimal)PromptRank(reference));
    }

    private static string BuildManualValidationMessage(
        IReadOnlyList<QuotationReference> references,
        decimal maximumDeviation)
    {
        var reasons = new List<string>();
        var ineligible = references.Count(reference => reference.State != QuotationReferenceState.Eligible);
        if (ineligible > 0)
        {
            reasons.Add($"{ineligible:N0} referência(s) inelegível(is)");
        }

        if (maximumDeviation > 25m)
        {
            reasons.Add($"desvio máximo de {maximumDeviation:N2}%");
        }

        return $"Cesta manual inválida: {string.Join("; ", reasons)}.";
    }

    private static (decimal Score, string Explanation) CalculateDescriptionScore(string requested, string found)
    {
        var requestedTokens = Tokenize(requested);
        var foundTokens = Tokenize(found).ToHashSet(StringComparer.Ordinal);
        if (requestedTokens.Count == 0 || foundTokens.Count == 0)
        {
            return (0m, "Termos coincidentes: nenhum; ausentes: descritivo insuficiente.");
        }

        var requestedWeights = requestedTokens
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(token => token, TokenWeight, StringComparer.Ordinal);
        var intersectionWeight = requestedWeights.Where(pair => foundTokens.Contains(pair.Key)).Sum(pair => pair.Value);
        var requestedWeight = requestedWeights.Values.Sum();
        var requestedCoverage = requestedWeight == 0 ? 0m : intersectionWeight / requestedWeight;

        var requestedBigrams = requestedTokens.Zip(requestedTokens.Skip(1), (left, right) => $"{left} {right}").ToArray();
        var normalizedFound = string.Join(' ', Tokenize(found));
        var bigramCoverage = requestedBigrams.Length == 0
            ? (foundTokens.Contains(requestedTokens[0]) ? 1m : 0m)
            : requestedBigrams.Count(normalizedFound.Contains) / (decimal)requestedBigrams.Length;
        var categoryAgreement = requestedTokens.Any(foundTokens.Contains) ? 1m : 0m;
        // O texto encontrado costuma ser muito mais detalhado que o pedido. A adequação
        // deve medir quanto do pedido foi comprovado, sem punir atributos adicionais.
        var score = Math.Clamp(
            50m * (0.75m * requestedCoverage + 0.20m * bigramCoverage + 0.05m * categoryAgreement),
            0m,
            50m);
        var distinctRequested = requestedTokens.Distinct(StringComparer.Ordinal).ToArray();
        var coincident = distinctRequested.Where(foundTokens.Contains).ToArray();
        var absent = distinctRequested.Where(token => !foundTokens.Contains(token)).ToArray();
        var explanation = $"Termos coincidentes: {FormatTerms(coincident)}; ausentes: {FormatTerms(absent)}.";
        return (score, explanation);
    }

    private static string FormatTerms(IReadOnlyList<string> terms) =>
        terms.Count == 0 ? "nenhum" : string.Join(", ", terms);

    private static decimal TokenWeight(string token) =>
        token.Any(char.IsDigit) || token is "kg" or "g" or "mg" or "l" or "ml" ? 3m :
        token.Length >= 5 && !PackagingUnits.Contains(token) ? 2m : 1m;

    private static decimal ApplyWeight(decimal qualityScore, decimal qualityMaximum, int weight) =>
        weight == 0 || qualityMaximum <= 0 ? 0m : qualityScore / qualityMaximum * weight;

    private static IReadOnlyList<string> Tokenize(string? text) => WordRegex()
        .Matches(AlphanumericBoundaryRegex().Replace(SearchText.Normalize(text), " "))
        .Select(match => CanonicalToken(match.Value))
        .Where(token => token.Length > 0 && !StopWords.Contains(token))
        .ToArray();

    private static string CanonicalToken(string token) =>
        TokenAliases.TryGetValue(token, out var alias) ? alias : token;

    private static (decimal Score, string Explanation, bool Conflict) CalculateUnitScore(
        string requestedUnit,
        string requestedDescription,
        string foundUnit,
        string foundDescription)
    {
        var requested = ResolveUnit(requestedUnit, requestedDescription);
        var found = ResolveUnit(foundUnit, foundDescription);
        if (requested.Conflict || found.Conflict)
        {
            return (0m, "Há unidades conflitantes no cadastro e no descritivo.", true);
        }

        if (requested.Unit is null || found.Unit is null)
        {
            return (10m, "Unidade genérica ou não identificada; requer revisão.", false);
        }

        if (!string.Equals(requested.Unit, found.Unit, StringComparison.Ordinal))
        {
            return (0m, $"Unidades incompatíveis: {requested.Unit} e {found.Unit}.", true);
        }

        if (requested.Inferred || found.Inferred)
        {
            return (18m, $"Unidade {requested.Unit} compatível, inferida pelo descritivo.", false);
        }

        return (20m, $"Unidade {requested.Unit} compatível.", false);
    }

    private static UnitResolution ResolveUnit(string? structuredUnit, string? description)
    {
        var structured = Tokenize(structuredUnit).FirstOrDefault(PackagingUnits.Contains);
        var described = Tokenize(description).FirstOrDefault(PackagingUnits.Contains);
        if (structured == "unidade" && described is not null and not "unidade")
        {
            return new UnitResolution(described, true, false);
        }

        if (structured is not null && described is not null && structured != "unidade" && described != "unidade" && structured != described)
        {
            return new UnitResolution(structured, false, true);
        }

        return structured is not null
            ? new UnitResolution(structured, false, false)
            : described is not null
                ? new UnitResolution(described, true, false)
                : new UnitResolution(null, false, false);
    }

    private static (bool Conflict, string Explanation) HasMeasureConflict(string requested, string found)
    {
        var requestedMeasure = ExtractMeasure(requested);
        var foundMeasure = ExtractMeasure(found);
        if (requestedMeasure is null || foundMeasure is null)
        {
            return (false, string.Empty);
        }

        if (requestedMeasure.Value.Dimension != foundMeasure.Value.Dimension)
        {
            return (true, "Medidas pertencem a dimensões incompatíveis.");
        }

        var higher = Math.Max(requestedMeasure.Value.BaseValue, foundMeasure.Value.BaseValue);
        var conflict = higher <= 0 || Math.Abs(requestedMeasure.Value.BaseValue - foundMeasure.Value.BaseValue) / higher > 0.005m;
        return conflict
            ? (true, $"Medida solicitada {requestedMeasure.Value.Label} difere da encontrada {foundMeasure.Value.Label}.")
            : (false, $"Medida {requestedMeasure.Value.Label} compatível.");
    }

    private static Measure? ExtractMeasure(string? text)
    {
        var normalized = SearchText.Normalize(text).Replace(',', '.');
        var match = MeasureRegex().Match(normalized);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var unit = CanonicalToken(match.Groups[2].Value);
        return unit switch
        {
            "kg" => new Measure("mass", value * 1000m, $"{value:N4} kg"),
            "g" => new Measure("mass", value, $"{value:N4} g"),
            "mg" => new Measure("mass", value / 1000m, $"{value:N4} mg"),
            "l" => new Measure("volume", value * 1000m, $"{value:N4} l"),
            "ml" => new Measure("volume", value, $"{value:N4} ml"),
            _ => null
        };
    }

    private static decimal CalculateQuantityScore(decimal requested, decimal? found)
    {
        if (found is null || found <= 0 || requested <= 0)
        {
            return 5m;
        }

        var ratio = Math.Max(requested, found.Value) / Math.Min(requested, found.Value);
        return ratio switch
        {
            <= 1.25m => 10m,
            <= 2m => 9m,
            <= 5m => 8m,
            <= 10m => 7m,
            <= 25m => 6m,
            <= 100m => 4m,
            _ => 2m
        };
    }

    private static decimal CalculateProximityScore(QuotationReference reference)
    {
        if (reference.DistanceFromRibeiraoKilometers is { } distance)
        {
            return 15m / (1m + (decimal)Math.Max(0d, distance) / 50m);
        }

        if (SearchText.Normalize(reference.Municipality) == "ribeirao preto" && reference.Uf.Equals("SP", StringComparison.OrdinalIgnoreCase))
        {
            return 15m;
        }

        if (reference.Uf.Equals("SP", StringComparison.OrdinalIgnoreCase))
        {
            return 4m;
        }

        return reference.Uf.ToUpperInvariant() is "ES" or "MG" or "RJ" ? 2m : 0m;
    }

    private decimal CalculateRecencyScore(DateOnly? resultDate, DateTimeOffset? publicationDate)
    {
        var date = resultDate ?? (publicationDate is null ? null : DateOnly.FromDateTime(publicationDate.Value.Date));
        if (date is null)
        {
            return 0m;
        }

        var age = Math.Max(0, _today.DayNumber - date.Value.DayNumber);
        return age <= 90 ? 5m : age <= 180 ? 3m : age <= 365 ? 1m : 0m;
    }

    private static string? GetRejectionReason(
        QuotationLine line,
        QuotationReference reference,
        decimal descriptionQuality)
    {
        if (reference.UnitPrice <= 0)
        {
            return "Preço unitário homologado ausente ou não positivo.";
        }

        if (line.MinimumUnitPrice is not null && reference.UnitPrice < line.MinimumUnitPrice)
        {
            return "Preço abaixo do mínimo definido para a cotação.";
        }

        if (line.MaximumUnitPrice is not null && reference.UnitPrice > line.MaximumUnitPrice)
        {
            return "Preço acima do máximo definido para a cotação.";
        }

        if (descriptionQuality < MinimumDescriptionScore)
        {
            return $"Adequação descritiva abaixo de {MinimumDescriptionScore:N0}/50.";
        }

        return null;
    }

    private static string BuildExplanation(
        AdequacyWeights weights,
        decimal descriptionQuality,
        string descriptionExplanation,
        decimal unitQuality,
        string unitExplanation,
        decimal? quantity,
        decimal quantityQuality,
        decimal proximityQuality,
        decimal recencyQuality,
        string measureExplanation)
    {
        var quantityText = quantity is null ? "quantidade não informada" : $"quantidade comparada: {quantity:N4}";
        return $"Descrição {ApplyWeight(descriptionQuality, 50m, weights.Description):N1}/{weights.Description} " +
               $"(compatibilidade {descriptionQuality:N1}/50). {descriptionExplanation} " +
               $"Unidade {ApplyWeight(unitQuality, 20m, weights.Unit):N1}/{weights.Unit}. {unitExplanation} {measureExplanation} " +
               $"Quantidade {ApplyWeight(quantityQuality, 10m, weights.Quantity):N1}/{weights.Quantity}; {quantityText}. " +
               $"Proximidade {ApplyWeight(proximityQuality, 15m, weights.Proximity):N1}/{weights.Proximity}; " +
               $"atualidade {ApplyWeight(recencyQuality, 5m, weights.Recency):N1}/{weights.Recency}.";
    }

    private static string NormalizeTaxId(string? value) => new((value ?? string.Empty)
        .Where(char.IsAsciiLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

    private static int CalculateCnpjDigit(string value, IReadOnlyList<int> weights)
    {
        var sum = value.Select((character, index) => (character - '0') * weights[index]).Sum();
        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }

    private readonly record struct UnitResolution(string? Unit, bool Inferred, bool Conflict);
    private readonly record struct Measure(string Dimension, decimal BaseValue, string Label);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"(?<=\d)(?=\p{L})|(?<=\p{L})(?=\d)")]
    private static partial Regex AlphanumericBoundaryRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(\d+(?:\.\d+)?)\s*(kg|gr|g|mg|l|ml)(?![\p{L}])", RegexOptions.IgnoreCase)]
    private static partial Regex MeasureRegex();
}
