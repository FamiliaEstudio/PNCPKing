using System.Text;
using System.Text.Json;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

public sealed class AiDraftCache : IAiDraftCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _root;

    public AiDraftCache(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        _root = Path.Combine(dataFolder, "ai-automation-cache");
    }

    public string GetDraftFolder(string pdfSha256) =>
        Path.Combine(_root, ValidateHash(pdfSha256));

    public async Task<AiQuotationDraft?> LoadAsync(
        string pdfSha256,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(GetDraftFolder(pdfSha256), "draft.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var draft = await JsonSerializer.DeserializeAsync<AiQuotationDraft>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (draft?.AnalyzerVersion == AiQuotationDraft.CurrentAnalyzerVersion)
            {
                return draft;
            }

            return draft?.AnalyzerVersion == 1 ? UpgradeVersionOne(draft) : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        AiQuotationDraft draft,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var folder = GetDraftFolder(draft.PdfSha256);
        Directory.CreateDirectory(folder);
        await WriteAtomicAsync(
            Path.Combine(folder, "document.md"),
            Encoding.UTF8.GetBytes(markdown),
            cancellationToken).ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(
            buffer,
            draft,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(
            Path.Combine(folder, "draft.json"),
            buffer.ToArray(),
            cancellationToken).ConfigureAwait(false);
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
    }

    public Task DeleteAsync(string pdfSha256, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = GetDraftFolder(pdfSha256);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        return Task.CompletedTask;
    }

    public Task<long> ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = 0L;
        if (!Directory.Exists(_root))
        {
            return Task.FromResult(bytes);
        }

        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(directory);
            if (name.Length != 64 || name.Any(character => !Uri.IsHexDigit(character)))
            {
                continue;
            }

            bytes += Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    try
                    {
                        return new FileInfo(path).Length;
                    }
                    catch (IOException)
                    {
                        return 0L;
                    }
                })
                .Sum();
            Directory.Delete(directory, recursive: true);
        }

        return Task.FromResult(bytes);
    }

    public async Task<AiQuotationDraft?> FindCompatibleAsync(
        IReadOnlyList<QuotationLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!Directory.Exists(_root) || lines.Count == 0)
        {
            return null;
        }

        var orderedLines = lines.OrderBy(value => value.DisplayOrder).ToArray();
        var matches = new List<AiQuotationDraft>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = Path.GetFileName(directory);
            if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            {
                continue;
            }

            var draft = await LoadAsync(hash, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                continue;
            }

            var items = draft.Items
                .Where(value => value.IsSelected)
                .OrderBy(value => value.SourceOrder)
                .ToArray();
            if (items.Length != orderedLines.Length)
            {
                continue;
            }

            var compatible = true;
            for (var index = 0; index < items.Length; index++)
            {
                if (SearchText.Normalize(items[index].Description) !=
                        SearchText.Normalize(orderedLines[index].Description) ||
                    SearchText.Normalize(items[index].Unit) !=
                        SearchText.Normalize(orderedLines[index].RequestedUnit) ||
                    SearchText.Normalize(items[index].SearchText) !=
                        SearchText.Normalize(orderedLines[index].SearchText))
                {
                    compatible = false;
                    break;
                }
            }

            if (compatible)
            {
                matches.Add(draft);
                if (matches.Count > 1)
                {
                    return null;
                }
            }
        }

        return matches.SingleOrDefault();
    }

    private static string ValidateHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Hash SHA-256 inválido.", nameof(value));
        }

        return normalized;
    }

    private static AiQuotationDraft UpgradeVersionOne(AiQuotationDraft draft)
    {
        var items = draft.Items.Select(item =>
        {
            var broad = string.Join(
                " ",
                SearchText.Normalize(item.Description)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(value => value.Length >= 4)
                    .Distinct(StringComparer.Ordinal)
                    .Take(2));
            return item with
            {
                IntermediateSearchText = string.IsNullOrWhiteSpace(item.IntermediateSearchText)
                    ? item.SearchText
                    : item.IntermediateSearchText,
                BroadSearchText = string.IsNullOrWhiteSpace(item.BroadSearchText)
                    ? broad
                    : item.BroadSearchText
            };
        }).ToArray();
        var contractPrompts = draft.ContractSearchPrompts.Count > 0
            ? draft.ContractSearchPrompts
            : items
                .Select(value => value.BroadSearchText)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .ToArray();
        return draft with
        {
            Items = items,
            ContractSearchPrompts = contractPrompts,
            AnalyzerVersion = AiQuotationDraft.CurrentAnalyzerVersion,
            Warnings = draft.Warnings
                .Append("Rascunho anterior atualizado localmente; use Retrabalhar prompts com IA para gerar os níveis novos.")
                .Distinct()
                .ToArray()
        };
    }

    private static async Task WriteAtomicAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
