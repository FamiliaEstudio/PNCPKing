using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Core.Quotations;

/// <summary>Filters the saved candidates supplied by one quotation item; never searches the database.</summary>
public static class QuotationCandidateSearch
{
    public static IReadOnlyList<QuotationReference> Filter(IReadOnlyList<QuotationReference> candidates,
        string? query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();
        // This is exactly the main search's item parser and matcher, including
        // independent '+' criteria, accents, phrases, exclusions and unit filters.
        var criteria = SearchText.ParseMainCriteria(query);
        if (criteria.Any(expression => expression.HasExplicitContractCandidates))
            throw new SearchQueryException("Aqui a busca usa a descrição e a unidade dos candidatos deste item. Remova o bloco C: de contratação.");
        if (criteria.Count == 1 && criteria[0].IsEmpty) return candidates;

        var matches = new List<QuotationReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var expression in criteria)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!expression.MatchesItem(candidate.ItemDescription, candidate.ItemUnit)) continue;
                if (seen.Add(candidate.Id)) matches.Add(candidate);
                break;
            }
        }
        return matches;
    }
}
