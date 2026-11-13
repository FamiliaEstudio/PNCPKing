using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class EvidenceRenderCacheTests
{
    [Fact]
    public void CacheKeepsRecentPagesWithinTheByteBudgetAndDoesNotEvictForAnOversizedPage()
    {
        var cache = new EvidenceRenderCache(maximumBytes: 12);
        var first = Page(6);
        cache.Add(("first", 1), first);
        cache.Add(("second", 1), Page(6));
        Assert.True(cache.TryGetValue(("first", 1), out var reused));
        Assert.Same(first, reused); // No image copy or re-render on a hit.
        cache.Add(("third", 1), Page(6));
        Assert.False(cache.TryGetValue(("second", 1), out _));
        Assert.True(cache.TryGetValue(("first", 1), out _));
        Assert.True(cache.TryGetValue(("third", 1), out _));

        cache.Add(("oversized", 1), Page(13));
        Assert.Equal(12, cache.Bytes);
        Assert.Equal(12, cache.PeakBytes);
        Assert.Equal(2, cache.Count);
        Assert.Equal(1, cache.Evictions);
        Assert.Equal(1, cache.OversizedPages);
        Assert.False(cache.TryGetValue(("oversized", 1), out _));
    }

    [Fact]
    public void LongExportAndReplacementCannotGrowTheCacheBeyondItsByteOrEntryLimits()
    {
        var cache = new EvidenceRenderCache(maximumBytes: 1024, maximumEntries: 4);
        for (var index = 0; index < 10000; index++)
        {
            cache.Add(("document", index), Page(index % 500 + 1));
            Assert.InRange(cache.Bytes, 0, 1024);
            Assert.InRange(cache.Count, 0, 4);
        }
        cache.Add(("last", 1), Page(100));
        cache.Add(("last", 1), Page(50));
        var bytes = cache.Bytes;
        cache.Remove(("last", 1));
        Assert.Equal(bytes - 50, cache.Bytes);
        Assert.False(cache.TryGetValue(("last", 1), out _));
    }

    private static RenderedPdfPage Page(int bytes) => new(new byte[bytes], 10, 10, 10, 10);
}
