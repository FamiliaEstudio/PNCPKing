using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.Infrastructure.Services;

public sealed class InternetPriceService(
    IQuotationRepository repository,
    QuotationService quotations,
    IInternetEvidenceStore evidenceStore) : IInternetPriceService
{
    public Task<IReadOnlyList<InternetPriceDraft>> GetDraftsAsync(
        Guid lineId,
        CancellationToken cancellationToken = default) =>
        repository.GetInternetPriceDraftsAsync(lineId, cancellationToken);

    public Task<InternetPriceDraft> SaveDraftAsync(
        InternetPriceDraft draft,
        CancellationToken cancellationToken = default) =>
        repository.SaveInternetPriceDraftAsync(draft, cancellationToken);

    public Task DeleteDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default) =>
        repository.DeleteInternetPriceDraftAsync(draftId, cancellationToken);

    public async Task<(
        QuotationLineAnalysis Analysis,
        QuotationManualBasket Basket,
        QuotationReference Reference)> CompleteDraftAsync(
        Guid projectId,
        InternetPriceDraft draft,
        Guid? basketId,
        string basketName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!draft.IsComplete)
        {
            throw new InvalidOperationException(
                "Preencha URL, preço, descrição, empresa, CNPJ válido e os dois prints.");
        }

        if (!await evidenceStore.VerifyAsync(draft.PriceImage!, cancellationToken).ConfigureAwait(false) ||
            !await evidenceStore.VerifyAsync(draft.TaxIdImage!, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "Um dos prints está ausente ou foi alterado. Recapture as duas evidências.");
        }

        var analyses = await quotations.GetAnalysesAsync(projectId, cancellationToken).ConfigureAwait(false);
        var current = analyses.SingleOrDefault(item => item.Line.Id == draft.LineId)
                      ?? throw new InvalidOperationException("O item não pertence à cotação selecionada.");
        var targetBasketId = basketId ?? draft.BasketId ?? Guid.NewGuid();
        var existingBasket = current.Baskets.FirstOrDefault(item =>
            item.IsManual && item.ManualBasketId == targetBasketId);
        var effectiveName = existingBasket?.Name ??
                            (string.IsNullOrWhiteSpace(basketName)
                                ? NextManualBasketName(current)
                                : basketName.Trim());
        var normalizedTaxId = NormalizeTaxId(draft.SupplierTaxId);
        var referenceId = $"internet:{draft.Id:N}";
        var reference = new QuotationReference
        {
            Id = referenceId,
            LineId = draft.LineId,
            ContractId = string.Empty,
            ItemNumber = 0,
            ResultSequence = 0,
            SupplierName = draft.SupplierName.Trim(),
            SupplierTaxId = normalizedTaxId,
            SupplierType = "Fornecedor da internet",
            UnitPrice = draft.UnitPrice!.Value,
            ResultDate = DateOnly.FromDateTime(draft.CapturedAt.LocalDateTime.Date),
            ItemDescription = draft.Description.Trim(),
            ItemUnit = current.Line.RequestedUnit,
            Organization = draft.SupplierName.Trim(),
            PublicationDate = draft.CapturedAt,
            PortalUrl = draft.SourceUrl.Trim(),
            Source = QuotationReferenceSource.InternetIncisoIII,
            State = QuotationReferenceState.Eligible,
            StateReason = "Referência manual do Inciso III com cadastro e evidências completos.",
            Adequacy = new AdequacyBreakdown(
                0,
                0,
                0,
                0,
                0,
                "Referência do Inciso III incluída manualmente com evidências de preço e CNPJ.")
        };
        var evidence = new InternetPriceEvidence
        {
            LineId = draft.LineId,
            ReferenceId = referenceId,
            SourceUrl = reference.PortalUrl,
            CapturedAt = draft.CapturedAt,
            PriceImage = draft.PriceImage!,
            TaxIdImage = draft.TaxIdImage!
        };
        var savedBasket = await repository.SaveInternetPriceReferenceAsync(
            reference,
            evidence,
            targetBasketId,
            effectiveName,
            cancellationToken).ConfigureAwait(false);
        await repository.DeleteInternetPriceDraftAsync(draft.Id, cancellationToken).ConfigureAwait(false);

        var updated = (await quotations.GetAnalysesAsync(projectId, cancellationToken).ConfigureAwait(false))
            .Single(item => item.Line.Id == draft.LineId);
        var basket = await repository.GetManualBasketsAsync(draft.LineId, cancellationToken)
            .ConfigureAwait(false);
        return (
            updated,
            basket.Single(item => item.Id == savedBasket.Id),
            updated.References.Single(item => item.Id == referenceId));
    }

    public Task<IReadOnlyDictionary<string, InternetPriceEvidence>> GetEvidenceAsync(
        Guid lineId,
        CancellationToken cancellationToken = default) =>
        repository.GetInternetPriceEvidenceAsync(lineId, cancellationToken);

    public async Task ValidateReportEvidenceAsync(
        QuotationProjectReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var failures = new List<string>();
        foreach (var analysis in report.Lines)
        {
            var references = analysis.Line.SelectionConfirmed
                ? analysis.SelectedBasket?.References
                : null;
            var internetReferences = (references ?? [])
                .Where(reference =>
                    reference.Source == QuotationReferenceSource.InternetIncisoIII)
                .ToArray();
            if (internetReferences.Length == 0)
            {
                continue;
            }

            var stored = await repository.GetInternetPriceEvidenceAsync(
                analysis.Line.Id,
                cancellationToken).ConfigureAwait(false);
            foreach (var reference in internetReferences)
            {
                if (!stored.TryGetValue(reference.Id, out var evidence))
                {
                    failures.Add($"{analysis.Line.EffectiveDisplayName} / {reference.SupplierName}: prints ausentes");
                    continue;
                }

                var priceValid = await evidenceStore.VerifyAsync(
                    evidence.PriceImage,
                    cancellationToken).ConfigureAwait(false);
                var taxIdValid = await evidenceStore.VerifyAsync(
                    evidence.TaxIdImage,
                    cancellationToken).ConfigureAwait(false);
                if (!priceValid || !taxIdValid)
                {
                    failures.Add(
                        $"{analysis.Line.EffectiveDisplayName} / {reference.SupplierName}: " +
                        (!priceValid && !taxIdValid
                            ? "prints do preço e do CNPJ ausentes ou alterados"
                            : !priceValid
                                ? "print do preço ausente ou alterado"
                                : "print do CNPJ ausente ou alterado"));
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidDataException(
                "Recapture as evidências obrigatórias do Inciso III antes de exportar:" +
                Environment.NewLine + "- " +
                string.Join(Environment.NewLine + "- ", failures));
        }
    }

    public async Task DeleteInternetReferenceAsync(
        Guid lineId,
        string referenceId,
        CancellationToken cancellationToken = default)
    {
        await repository.DeleteInternetPriceReferenceAsync(lineId, referenceId, cancellationToken)
            .ConfigureAwait(false);
        var hashes = await repository.GetReferencedInternetEvidenceHashesAsync(cancellationToken)
            .ConfigureAwait(false);
        await evidenceStore.DeleteOrphansAsync(hashes, cancellationToken).ConfigureAwait(false);
    }

    private static string NextManualBasketName(QuotationLineAnalysis analysis)
    {
        var existing = analysis.Baskets
            .Where(item => item.IsManual)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; ; number++)
        {
            var candidate = $"Manual {number:N0}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string NormalizeTaxId(string? value) =>
        new((value ?? string.Empty)
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
}
