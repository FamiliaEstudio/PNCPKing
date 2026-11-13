using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

// Compressed pages only, in RAM: no extra reads or writes on mechanical disks.
internal sealed class EvidenceRenderCache(long maximumBytes = 32L * 1024 * 1024, int maximumEntries = 64)
{
    private readonly Dictionary<(string Hash, int Page), LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recent = [];

    public long Bytes { get; private set; }
    public long PeakBytes { get; private set; }
    public int Count => _entries.Count;
    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public int Evictions { get; private set; }
    public int OversizedPages { get; private set; }
    public long MaximumBytes => maximumBytes;

    public bool TryGetValue((string Hash, int Page) key, out RenderedPdfPage rendered)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            Misses++;
            rendered = null!;
            return false;
        }

        _recent.Remove(node);
        _recent.AddLast(node);
        Hits++;
        rendered = node.Value.Page;
        return true;
    }

    public void Add((string Hash, int Page) key, RenderedPdfPage rendered)
    {
        Remove(key);
        var bytes = rendered.PngBytes.LongLength;
        if (bytes > maximumBytes || maximumEntries <= 0)
        {
            OversizedPages++;
            return;
        }

        while (_recent.First is { } oldest &&
               (Bytes + bytes > maximumBytes || Count >= maximumEntries))
        {
            Remove(oldest.Value.Key);
            Evictions++;
        }

        _entries.Add(key, _recent.AddLast(new Entry(key, rendered)));
        Bytes += bytes;
        PeakBytes = Math.Max(PeakBytes, Bytes);
    }

    public void Remove((string Hash, int Page) key)
    {
        if (!_entries.Remove(key, out var node)) return;
        _recent.Remove(node);
        Bytes -= node.Value.Page.PngBytes.LongLength;
    }

    private sealed record Entry((string Hash, int Page) Key, RenderedPdfPage Page);
}
