using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PNCPKing.Core.Models;

/// <summary>
/// An explicitly accepted technical justification for one captured reference.
/// It is evidence for manual baskets, never a replacement for financial validation
/// or the normal automatic ranking/filtering rules.
/// </summary>
public sealed record QuotationAiTechnicalReview(string Fingerprint, string Justification, string SourceExcerpt)
{
    public const int MaximumEvidenceLength = 16 * 1024;

    public static QuotationAiTechnicalReview Create(QuotationLine line, QuotationReference reference,
        string justification, string sourceExcerpt)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.LineId != line.Id || line.Id == Guid.Empty || string.IsNullOrWhiteSpace(line.Description) ||
            string.IsNullOrWhiteSpace(reference.ItemDescription))
            throw new InvalidDataException("A revisão técnica exige item e referência correspondentes, com descritivos presentes.");
        var review = new QuotationAiTechnicalReview(CalculateFingerprint(line, reference), justification, sourceExcerpt);
        if (!review.HasValidEvidence(reference))
            throw new InvalidDataException("A revisão técnica exige justificativa e trecho literal da origem, com até 16 mil caracteres cada.");
        return review;
    }

    public bool Matches(QuotationLine line, QuotationReference reference) =>
        line.Id != Guid.Empty && reference.LineId == line.Id &&
        !string.IsNullOrWhiteSpace(line.Description) && !string.IsNullOrWhiteSpace(reference.ItemDescription) &&
        HasValidEvidence(reference) &&
        string.Equals(Fingerprint, CalculateFingerprint(line, reference), StringComparison.Ordinal);

    private bool HasValidEvidence(QuotationReference reference) =>
        Fingerprint is { Length: 64 } &&
        !string.IsNullOrWhiteSpace(Justification) && Justification.Length <= MaximumEvidenceLength &&
        !string.IsNullOrWhiteSpace(SourceExcerpt) && SourceExcerpt.Length <= MaximumEvidenceLength &&
        (reference.ItemDescription + "\n" + reference.ItemAdditionalInformation + "\n" + reference.ItemUnit)
            .Contains(SourceExcerpt, StringComparison.OrdinalIgnoreCase);

    private static string CalculateFingerprint(QuotationLine line, QuotationReference reference) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            line.Id,
            line.ProjectId,
            line.Description,
            line.RequestedUnit,
            reference = new
            {
                reference.Id,
                reference.LineId,
                reference.ContractId,
                reference.ItemNumber,
                reference.ResultSequence,
                reference.ItemDescription,
                reference.ItemAdditionalInformation,
                reference.ItemUnit,
                reference.ItemCategory,
                reference.NcmNbsCode,
                reference.NcmNbsDescription,
                reference.CatalogCode,
                reference.CatalogName,
                reference.CatalogCategory,
                unitPrice = reference.UnitPrice.ToString("G29", CultureInfo.InvariantCulture),
                supplierTaxId = new string((reference.SupplierTaxId ?? string.Empty)
                    .Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray()),
                reference.ResultDate
            }
        })));
}
