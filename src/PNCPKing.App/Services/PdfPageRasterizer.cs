using PDFtoImage;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using SkiaSharp;
using UglyToad.PdfPig;

namespace PNCPKing.App.Services;

public sealed class PdfPageRasterizer : IPdfPageRasterizer
{
    public Task<RenderedPdfPage> RenderAsync(
        string pdfPath,
        int pageNumber,
        int dpi = 300,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "A renderização de evidências do PNCP King é publicada para Windows.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            double pdfWidth;
            double pdfHeight;
            using (var pdf = PdfDocument.Open(pdfPath))
            {
                var page = pdf.GetPage(pageNumber);
                pdfWidth = page.Width;
                pdfHeight = page.Height;
            }

            var bytes = File.ReadAllBytes(pdfPath);
            var options = new RenderOptions { Dpi = dpi, WithAnnotations = true, UseTiling = true };
            using var bitmap = Conversion.ToImage(
                bytes,
                new Index(pageNumber - 1),
                password: null,
                options);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return new RenderedPdfPage(
                encoded.ToArray(),
                bitmap.Width,
                bitmap.Height,
                pdfWidth,
                pdfHeight);
        }, cancellationToken);
    }
}
