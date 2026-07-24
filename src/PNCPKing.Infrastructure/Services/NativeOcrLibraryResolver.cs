namespace PNCPKing.Infrastructure.Services;

internal static class NativeOcrLibraryResolver
{
    public static string? FindTesseractRoot(
        string? nativeSearchDirectories,
        string? applicationBaseDirectory,
        bool is64BitProcess)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(nativeSearchDirectories))
        {
            candidates.AddRange(nativeSearchDirectories.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
        }

        if (!string.IsNullOrWhiteSpace(applicationBaseDirectory))
        {
            candidates.Add(applicationBaseDirectory);
        }

        var platformFolder = is64BitProcess ? "x64" : "x86";
        foreach (var candidate in candidates)
        {
            if (!TryNormalizeDirectory(candidate, out var normalized))
            {
                continue;
            }

            if (File.Exists(Path.Combine(normalized, platformFolder, "tesseract50.dll")) &&
                File.Exists(Path.Combine(normalized, platformFolder, "leptonica-1.82.0.dll")))
            {
                return normalized;
            }
        }

        return null;
    }

    private static bool TryNormalizeDirectory(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path);
            return Directory.Exists(normalized);
        }
        catch (Exception exception) when (exception is
                   ArgumentException or
                   NotSupportedException or
                   PathTooLongException)
        {
            return false;
        }
    }
}
