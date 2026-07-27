using System.Security.Cryptography;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class InternetEvidenceStore : IInternetEvidenceStore
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly string _dataFolder;
    private readonly string _evidenceFolder;

    public InternetEvidenceStore(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        _dataFolder = Path.GetFullPath(dataFolder);
        _evidenceFolder = Path.Combine(_dataFolder, "internet-evidence");
    }

    public string RootPath => _evidenceFolder;

    public async Task<EvidenceImageDescriptor> SavePngAsync(
        ReadOnlyMemory<byte> pngBytes,
        int pixelWidth,
        int pixelHeight,
        CancellationToken cancellationToken = default)
    {
        if (pngBytes.Length < PngSignature.Length ||
            !pngBytes.Span[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("A captura não contém uma imagem PNG válida.");
        }

        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pixelWidth),
                "A captura precisa possuir dimensões positivas.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(pngBytes.Span)).ToLowerInvariant();
        Directory.CreateDirectory(_evidenceFolder);
        var relativePath = $"internet-evidence/{hash}.png";
        var destination = ResolvePath(relativePath);
        var needsWrite = true;
        if (File.Exists(destination))
        {
            await using var existing = File.OpenRead(destination);
            var existingHash = Convert.ToHexString(
                await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false));
            needsWrite = !string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase);
        }

        if (needsWrite)
        {
            var temporary = destination + $".{Guid.NewGuid():N}.partial";
            try
            {
                await File.WriteAllBytesAsync(temporary, pngBytes.ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        return new EvidenceImageDescriptor
        {
            Sha256 = hash,
            RelativePath = relativePath,
            MimeType = "image/png",
            ByteLength = pngBytes.Length,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<byte[]> ReadVerifiedAsync(
        EvidenceImageDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var path = ResolvePath(descriptor.RelativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"A evidência {descriptor.Sha256} não existe mais. Recapture o print.",
                path);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(descriptor.Sha256)))
        {
            throw new InvalidDataException(
                $"A evidência {descriptor.Sha256} foi alterada. Recapture o print.");
        }

        return bytes;
    }

    public async Task<bool> VerifyAsync(
        EvidenceImageDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await ReadVerifiedAsync(descriptor, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    public Task DeleteOrphansAsync(
        IReadOnlySet<string> referencedHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(referencedHashes);
        if (!Directory.Exists(_evidenceFolder))
        {
            return Task.CompletedTask;
        }

        foreach (var path in Directory.EnumerateFiles(_evidenceFolder, "*.png", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = Path.GetFileNameWithoutExtension(path);
            if (!referencedHashes.Contains(hash))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    private string ResolvePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("O caminho da evidência não pode ser absoluto.");
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_dataFolder, normalized));
        var rootWithSeparator = _evidenceFolder.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("O caminho da evidência saiu da pasta permitida.");
        }

        return fullPath;
    }
}
