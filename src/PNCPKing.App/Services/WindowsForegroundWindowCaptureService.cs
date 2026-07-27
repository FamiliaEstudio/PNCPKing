using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PNCPKing.Core.Interfaces;

namespace PNCPKing.App.Services;

public sealed class WindowsForegroundWindowCaptureService : IWindowCaptureService
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int Srccopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;

    public Task<WindowCaptureResult> CaptureForegroundWindowAsync(
        nint excludedWindowHandle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = GetForegroundWindow();
        if (window == 0)
        {
            throw new InvalidOperationException("Nenhuma janela em primeiro plano pôde ser identificada.");
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (window == excludedWindowHandle || processId == Environment.ProcessId)
        {
            throw new InvalidOperationException(
                "Coloque a página da internet em primeiro plano antes de pressionar o atalho.");
        }

        if (IsIconic(window))
        {
            throw new InvalidOperationException("A janela a capturar está minimizada.");
        }

        if (!TryGetCaptureBounds(window, out var bounds))
        {
            throw new InvalidOperationException("Não foi possível determinar os limites da janela.");
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width < 32 || height < 32)
        {
            throw new InvalidOperationException("A janela selecionada é pequena demais para uma evidência.");
        }

        var desktopDc = GetDC(0);
        if (desktopDc == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível acessar a tela.");
        }

        nint memoryDc = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            memoryDc = CreateCompatibleDC(desktopDc);
            bitmap = CreateCompatibleBitmap(desktopDc, width, height);
            if (memoryDc == 0 || bitmap == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Não foi possível preparar a captura.");
            }

            previous = SelectObject(memoryDc, bitmap);
            if (!BitBlt(
                    memoryDc,
                    0,
                    0,
                    width,
                    height,
                    desktopDc,
                    bounds.Left,
                    bounds.Top,
                    Srccopy | CaptureBlt))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "O Windows não conseguiu capturar a janela.");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                0,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            EnsureCaptureHasVisiblePixels(source);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Task.FromResult(new WindowCaptureResult(
                stream.ToArray(),
                source.PixelWidth,
                source.PixelHeight,
                GetTitle(window)));
        }
        finally
        {
            if (previous != 0 && memoryDc != 0)
            {
                _ = SelectObject(memoryDc, previous);
            }

            if (bitmap != 0)
            {
                _ = DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                _ = DeleteDC(memoryDc);
            }

            _ = ReleaseDC(0, desktopDc);
        }
    }

    private static bool TryGetCaptureBounds(nint window, out Rect bounds)
    {
        if (DwmGetWindowAttribute(
                window,
                DwmwaExtendedFrameBounds,
                out bounds,
                Marshal.SizeOf<Rect>()) == 0 &&
            bounds.Right > bounds.Left &&
            bounds.Bottom > bounds.Top)
        {
            return true;
        }

        return GetWindowRect(window, out bounds) &&
               bounds.Right > bounds.Left &&
               bounds.Bottom > bounds.Top;
    }

    private static string GetTitle(nint window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return "Janela capturada";
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private static void EnsureCaptureHasVisiblePixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(
            source,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var samples = 0;
        var visible = 0;
        var step = Math.Max(4, pixels.Length / 4096);
        step -= step % 4;
        for (var offset = 0; offset + 3 < pixels.Length; offset += step)
        {
            samples++;
            if (pixels[offset + 3] > 0 &&
                (pixels[offset] > 8 || pixels[offset + 1] > 8 || pixels[offset + 2] > 8))
            {
                visible++;
            }
        }

        if (samples == 0 || visible < Math.Max(1, samples / 100))
        {
            throw new InvalidDataException(
                "A captura ficou vazia ou protegida. Deixe a página visível e tente novamente.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleBitmap(nint hDc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hDc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        nint destinationDc,
        int destinationX,
        int destinationY,
        int width,
        int height,
        nint sourceDc,
        int sourceX,
        int sourceY,
        int rasterOperation);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint hWnd,
        int attribute,
        out Rect value,
        int valueSize);
}
