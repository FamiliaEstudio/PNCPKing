using PNCPKing.Core.Models;

namespace PNCPKing.Core.Interfaces;

public interface IInternetEvidenceStore
{
    string RootPath { get; }

    Task<EvidenceImageDescriptor> SavePngAsync(
        ReadOnlyMemory<byte> pngBytes,
        int pixelWidth,
        int pixelHeight,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadVerifiedAsync(
        EvidenceImageDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(
        EvidenceImageDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task DeleteOrphansAsync(
        IReadOnlySet<string> referencedHashes,
        CancellationToken cancellationToken = default);
}

public interface IInternetPriceService
{
    Task<IReadOnlyList<InternetPriceDraft>> GetDraftsAsync(
        Guid lineId,
        CancellationToken cancellationToken = default);

    Task<InternetPriceDraft> SaveDraftAsync(
        InternetPriceDraft draft,
        CancellationToken cancellationToken = default);

    Task DeleteDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default);

    Task<(QuotationLineAnalysis Analysis, QuotationManualBasket Basket, QuotationReference Reference)>
        CompleteDraftAsync(
            Guid projectId,
            InternetPriceDraft draft,
            Guid? basketId,
            string basketName,
            CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, InternetPriceEvidence>> GetEvidenceAsync(
        Guid lineId,
        CancellationToken cancellationToken = default);

    Task ValidateReportEvidenceAsync(
        QuotationProjectReport report,
        CancellationToken cancellationToken = default);

    Task DeleteInternetReferenceAsync(
        Guid lineId,
        string referenceId,
        CancellationToken cancellationToken = default);
}

public interface IWindowCaptureService
{
    Task<WindowCaptureResult> CaptureForegroundWindowAsync(
        nint excludedWindowHandle,
        CancellationToken cancellationToken = default);
}

public sealed record WindowCaptureResult(
    byte[] PngBytes,
    int PixelWidth,
    int PixelHeight,
    string WindowTitle);
