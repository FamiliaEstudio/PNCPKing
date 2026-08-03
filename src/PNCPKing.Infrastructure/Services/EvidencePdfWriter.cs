using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using PNCPKing.Core.Models;
using SkiaSharp;

namespace PNCPKing.Infrastructure.Services;

internal sealed class EvidencePdfWriter : IDisposable
{
    private const int CanvasWidth = 1240;
    private const int CanvasHeight = 1754;
    private const int OutputScale = 1;

    private readonly PdfDocument _document = new();
    private int _pageNumber;

    public int PageCount => _pageNumber;

    public void AddTextPage(
        string heading,
        IReadOnlyList<string> lines,
        string? portalUrl = null)
    {
        using var bitmap = CreateCanvas();
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(OutputScale);
        DrawPageHeader(canvas, heading, lines);
        AddBitmapPage(bitmap, portalUrl);
    }

    public void AddOccurrencePage(
        string heading,
        IReadOnlyList<string> lines,
        string portalUrl,
        RenderedPdfPage rendered,
        DocumentPageIndex pageIndex,
        IReadOnlyList<TextOccurrence> occurrences)
    {
        using var bitmap = CreateCanvas();
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(OutputScale);
        var contentTop = DrawPageHeader(canvas, heading, lines);
        using var source = SKBitmap.Decode(rendered.PngBytes)
                           ?? throw new InvalidDataException("A página renderizada não pôde ser lida.");

        var fullPage = new DocumentRectangle(0, 0, pageIndex.Width, pageIndex.Height);
        var sourceRectangle = new SKRect(
            0,
            0,
            source.Width,
            source.Height);
        var available = new SKRect(70, contentTop + 35, CanvasWidth - 70, CanvasHeight - 120);
        var fitted = Fit(sourceRectangle, available);
        canvas.DrawBitmap(source, sourceRectangle, fitted);

        using var highlight = new SKPaint
        {
            Color = new SKColor(
                EvidenceHighlightStyle.FillRed,
                EvidenceHighlightStyle.FillGreen,
                EvidenceHighlightStyle.FillBlue,
                EvidenceHighlightStyle.FillAlpha),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var border = new SKPaint
        {
            Color = new SKColor(
                EvidenceHighlightStyle.BorderRed,
                EvidenceHighlightStyle.BorderGreen,
                EvidenceHighlightStyle.BorderBlue),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = EvidenceHighlightStyle.BorderWidthCanvas,
            IsAntialias = true
        };
        foreach (var wordIndex in occurrences
                     .SelectMany(value => value.WordIndexes)
                     .Distinct())
        {
            var word = pageIndex.Words[wordIndex].Bounds;
            var rectangle = MapToDestination(word, fullPage, fitted);
            canvas.DrawRect(rectangle, highlight);
            canvas.DrawRect(rectangle, border);
        }

        AddBitmapPage(bitmap, portalUrl);
    }

    public void AddImageEvidencePage(
        string heading,
        IReadOnlyList<string> lines,
        string sourceUrl,
        ReadOnlyMemory<byte> pngBytes,
        string caption)
    {
        using var bitmap = CreateCanvas();
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(OutputScale);
        var contentTop = DrawPageHeader(
            canvas,
            heading,
            lines.Append(caption).ToArray());
        using var source = SKBitmap.Decode(pngBytes.Span)
                           ?? throw new InvalidDataException("O print não pôde ser lido.");
        var sourceRectangle = new SKRect(0, 0, source.Width, source.Height);
        var available = new SKRect(55, contentTop + 25, CanvasWidth - 55, CanvasHeight - 110);
        var fitted = Fit(sourceRectangle, available);
        canvas.DrawBitmap(source, sourceRectangle, fitted);
        AddBitmapPage(bitmap, sourceUrl);
    }

    public void Save(string destinationPath)
    {
        if (_document.PageCount == 0)
        {
            AddTextPage("Relatório de evidências", ["Nenhuma evidência foi produzida."]);
        }

        _document.Save(destinationPath);
    }

    public void AppendPdf(string sourcePath)
    {
        using var source = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
        foreach (var page in source.Pages)
        {
            _document.AddPage(page);
            _pageNumber++;
        }
    }

    public void Dispose() => _document.Dispose();

    private float DrawPageHeader(
        SKCanvas canvas,
        string heading,
        IReadOnlyList<string> lines)
    {
        using var headingPaint = CreateTextStyle(30, SKColors.DarkSlateBlue, bold: true);
        using var textPaint = CreateTextStyle(20, SKColors.Black);
        using var linkPaint = CreateTextStyle(18, new SKColor(25, 90, 180));
        var y = 65f;
        y = DrawWrapped(canvas, heading, 55, y, CanvasWidth - 110, headingPaint, 10);
        y += 8;
        foreach (var line in lines)
        {
            var paint = line.StartsWith("PNCP:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Fonte:", StringComparison.OrdinalIgnoreCase)
                ? linkPaint
                : textPaint;
            y = DrawWrapped(canvas, line, 55, y, CanvasWidth - 110, paint, 7);
        }

        using var separator = new SKPaint { Color = new SKColor(190, 198, 210), StrokeWidth = 2 };
        canvas.DrawLine(55, y + 8, CanvasWidth - 55, y + 8, separator);
        return y + 12;
    }

    private void AddBitmapPage(SKBitmap bitmap, string? portalUrl)
    {
        _pageNumber++;
        using var footerPaint = CreateTextStyle(16, new SKColor(90, 90, 90));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Scale(OutputScale);
            canvas.DrawText(
                $"PNCP King · Página {_pageNumber:N0}",
                55,
                CanvasHeight - 45,
                SKTextAlign.Left,
                footerPaint.Font,
                footerPaint.Paint);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 88);
        using var memory = new MemoryStream(data.ToArray());
        using var image = XImage.FromStream(memory);
        var page = _document.AddPage();
        page.Width = XUnit.FromMillimeter(210);
        page.Height = XUnit.FromMillimeter(297);
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }

        if (Uri.TryCreate(portalUrl, UriKind.Absolute, out var uri))
        {
            var rectangle = new PdfRectangle(new XRect(
                20,
                20,
                page.Width.Point - 40,
                85));
            page.Annotations.Add(PdfLinkAnnotation.CreateWebLink(rectangle, uri.AbsoluteUri));
        }
    }

    private static SKBitmap CreateCanvas()
    {
        var bitmap = new SKBitmap(
            CanvasWidth * OutputScale,
            CanvasHeight * OutputScale,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        return bitmap;
    }

    private static TextStyle CreateTextStyle(float size, SKColor color, bool bold = false)
    {
        var typeface = SKTypeface.FromFamilyName(
            "Arial",
            bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        return new TextStyle(
            new SKPaint { Color = color, IsAntialias = true },
            new SKFont(typeface, size),
            typeface);
    }

    private static float DrawWrapped(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        float width,
        TextStyle paint,
        int maximumLines)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new List<string>();
        var lines = 0;
        foreach (var word in words)
        {
            var candidate = line.Count == 0 ? word : string.Join(' ', line) + " " + word;
            if (paint.Font.MeasureText(candidate) <= width)
            {
                line.Add(word);
                continue;
            }

            if (line.Count > 0)
            {
                canvas.DrawText(
                    string.Join(' ', line),
                    x,
                    y,
                    SKTextAlign.Left,
                    paint.Font,
                    paint.Paint);
                y += paint.Font.Size * 1.35f;
                lines++;
                if (lines >= maximumLines)
                {
                    return y;
                }
            }

            line.Clear();
            line.Add(word);
        }

        if (line.Count > 0 && lines < maximumLines)
        {
            canvas.DrawText(
                string.Join(' ', line),
                x,
                y,
                SKTextAlign.Left,
                paint.Font,
                paint.Paint);
            y += paint.Font.Size * 1.35f;
        }

        return y;
    }

    private static SKRect Fit(SKRect source, SKRect available)
    {
        var scale = Math.Min(available.Width / source.Width, available.Height / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        var left = available.Left + (available.Width - width) / 2;
        var top = available.Top + (available.Height - height) / 2;
        return new SKRect(left, top, left + width, top + height);
    }

    private static SKRect MapToDestination(
        DocumentRectangle word,
        DocumentRectangle context,
        SKRect destination)
    {
        var left = destination.Left + (float)((word.X - context.X) / context.Width * destination.Width);
        var top = destination.Top + (float)((word.Y - context.Y) / context.Height * destination.Height);
        var right = destination.Left +
                    (float)((word.X + word.Width - context.X) / context.Width * destination.Width);
        var bottom = destination.Top +
                     (float)((word.Y + word.Height - context.Y) / context.Height * destination.Height);
        return new SKRect(left, top, right, bottom);
    }

    private sealed class TextStyle(
        SKPaint paint,
        SKFont font,
        SKTypeface typeface) : IDisposable
    {
        public SKPaint Paint { get; } = paint;
        public SKFont Font { get; } = font;

        public void Dispose()
        {
            Font.Dispose();
            Paint.Dispose();
            typeface.Dispose();
        }
    }
}
