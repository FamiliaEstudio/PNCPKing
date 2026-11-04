using System.Text.Json;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Data;

// Reuses the reference's existing explanation text field. Existing databases and
// ordinary explanations remain unchanged; no schema migration or auxiliary file
// is needed to retain the accepted review when a project is reopened.
internal static class QuotationAiReviewStorage
{
    private const string Prefix = "PNCPKING_AI_TECHNICAL_REVIEW_V1\n";
    private const string LiteralPrefix = "PNCPKING_AI_EXPLANATION_LITERAL_V1\n";
    private const int MaximumStoredLength = 256 * 1024;

    public static string Encode(QuotationReference reference)
    {
        var explanation = reference.Adequacy.Explanation;
        if (reference.TechnicalReview is not { } review)
            // Explanation is plain input unless accompanied by the typed, accepted
            // review. Escape reserved prefixes so a package cannot smuggle a valid
            // envelope through its ordinary adequacy.explanation JSON property.
            return explanation.StartsWith(Prefix, StringComparison.Ordinal) ||
                   explanation.StartsWith(LiteralPrefix, StringComparison.Ordinal)
                ? LiteralPrefix + explanation
                : explanation;
        if (review.Justification is null || review.SourceExcerpt is null ||
            review.Justification.Length > QuotationAiTechnicalReview.MaximumEvidenceLength ||
            review.SourceExcerpt.Length > QuotationAiTechnicalReview.MaximumEvidenceLength)
            throw new InvalidDataException("A revisão técnica excede o limite de armazenamento.");
        var stored = Prefix + JsonSerializer.Serialize(new Envelope(1, explanation, review));
        if (stored.Length > MaximumStoredLength)
            throw new InvalidDataException("A revisão técnica excede o limite de armazenamento.");
        return stored;
    }

    public static (string Explanation, QuotationAiTechnicalReview? Review) Decode(string stored)
    {
        if (stored.StartsWith(LiteralPrefix, StringComparison.Ordinal))
            return (stored[LiteralPrefix.Length..], null);
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal) || stored.Length > MaximumStoredLength)
            return (stored, null);
        try
        {
            var data = JsonSerializer.Deserialize<Envelope>(stored.AsSpan(Prefix.Length));
            if (data is { Version: 1, Explanation: not null, Review: not null } &&
                data.Review.Fingerprint is { Length: 64 } &&
                data.Review.Justification is { Length: > 0 and <= QuotationAiTechnicalReview.MaximumEvidenceLength } &&
                data.Review.SourceExcerpt is { Length: > 0 and <= QuotationAiTechnicalReview.MaximumEvidenceLength })
                return (data.Explanation, data.Review);
        }
        catch (JsonException)
        {
            // An unknown/corrupted envelope cannot grant a technical exception.
        }
        return (stored, null);
    }

    private sealed record Envelope(int Version, string Explanation, QuotationAiTechnicalReview Review);
}
