using System.Reflection;
using System.Security.Cryptography;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;
using Tesseract;

namespace PNCPKing.App.Services;

public sealed class EmbeddedTesseractOcrService : IOcrService, IDisposable
{
    private const string ResourceName = "PNCPKing.App.Assets.tessdata.por.traineddata";
    private const string ModelSha256 =
        "c4932b937207a9514b7514d518b931a99938c02a28a5a5a553f8599ed58b7deb";

    private static readonly object NativeSearchPathGate = new();
    private static bool _nativeSearchPathConfigured;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _dataFolder;
    private TesseractEngine? _engine;
    private bool _disposed;

    public EmbeddedTesseractOcrService()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _dataFolder = Path.Combine(
            localData,
            "PNCP King",
            "ocr",
            $"tessdata-fast-por-{ModelSha256[..12]}");
    }

    public async Task<IReadOnlyList<DocumentWord>> RecognizeAsync(
        RenderedPdfPage page,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureModelAsync(cancellationToken).ConfigureAwait(false);
            EnsureNativeSearchPath();
            _engine ??= new TesseractEngine(_dataFolder, "por", EngineMode.LstmOnly);

            using var pix = Pix.LoadFromMemory(page.PngBytes);
            using var recognized = _engine.Process(pix, PageSegMode.Auto);
            using var iterator = recognized.GetIterator();
            iterator.Begin();
            var words = new List<DocumentWord>();
            var line = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (iterator.IsAtBeginningOf(PageIteratorLevel.TextLine) && words.Count > 0)
                {
                    line++;
                }

                var text = iterator.GetText(PageIteratorLevel.Word)?.Trim();
                if (string.IsNullOrWhiteSpace(text) ||
                    !iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
                {
                    continue;
                }

                var scaleX = page.PdfWidth / page.PixelWidth;
                var scaleY = page.PdfHeight / page.PixelHeight;
                words.Add(new DocumentWord(
                    text,
                    new DocumentRectangle(
                        bounds.X1 * scaleX,
                        bounds.Y1 * scaleY,
                        Math.Max(0, bounds.X2 - bounds.X1) * scaleX,
                        Math.Max(0, bounds.Y2 - bounds.Y1) * scaleY),
                    line));
            } while (iterator.Next(PageIteratorLevel.Word));

            return words;
        }
        catch
        {
            // Native OCR failures can leave a Tesseract engine unusable. Dispose
            // it so the lower-resolution retry starts from a clean native state.
            try
            {
                _engine?.Dispose();
            }
            catch
            {
                // Preserve the original OCR exception.
            }

            _engine = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            _engine?.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void EnsureNativeSearchPath()
    {
        if (_nativeSearchPathConfigured)
        {
            return;
        }

        lock (NativeSearchPathGate)
        {
            if (_nativeSearchPathConfigured)
            {
                return;
            }

            var searchPath = NativeOcrLibraryResolver.FindTesseractRoot(
                AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string,
                AppContext.BaseDirectory,
                Environment.Is64BitProcess);
            if (searchPath is null)
            {
                throw new InvalidOperationException(
                    "As bibliotecas nativas do OCR não foram encontradas na extração do PNCP King.");
            }

            // Tesseract 5.2 obtains its default base path from Assembly.Location.
            // Bundled managed assemblies have an empty Location in a single-file
            // application, so point the wrapper at the native extraction root.
            TesseractEnviornment.CustomSearchPath = searchPath;
            _nativeSearchPathConfigured = true;
        }
    }

    private async Task EnsureModelAsync(CancellationToken cancellationToken)
    {
        var destination = Path.Combine(_dataFolder, "por.traineddata");
        if (File.Exists(destination) &&
            string.Equals(
                await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false),
                ModelSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(_dataFolder);
        await using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                                 ?? throw new InvalidOperationException(
                                     "O modelo OCR português não foi incorporado ao PNCP King.");
        var temporary = destination + ".tmp";
        try
        {
            await using (var output = File.Create(temporary))
            {
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, overwrite: true);
            var extractedHash = await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(extractedHash, ModelSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destination);
                throw new InvalidDataException("O modelo OCR português incorporado não passou na validação SHA-256.");
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
