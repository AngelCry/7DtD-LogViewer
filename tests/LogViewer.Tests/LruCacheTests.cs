using LogViewer.Core;

namespace LogViewer.Tests;

public sealed class LruCacheTests
{
    [Fact]
    public void Set_OverCapacity_EvictsLeastRecentlyUsed()
    {
        var cache = new LruCache<int, string>(2);
        cache.Set(1, "one");
        cache.Set(2, "two");

        cache.Set(3, "three");

        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out string? two));
        Assert.Equal("two", two);
        Assert.True(cache.TryGet(3, out string? three));
        Assert.Equal("three", three);
    }

    [Fact]
    public void TryGet_RefreshesRecency()
    {
        var cache = new LruCache<int, string>(2);
        cache.Set(1, "one");
        cache.Set(2, "two");

        Assert.True(cache.TryGet(1, out _));
        cache.Set(3, "three");

        Assert.True(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void Set_ExistingKey_UpdatesValueAndRecency()
    {
        var cache = new LruCache<int, string>(2);
        cache.Set(1, "one");
        cache.Set(2, "two");

        cache.Set(1, "updated");
        cache.Set(3, "three");

        Assert.True(cache.TryGet(1, out string? value));
        Assert.Equal("updated", value);
        Assert.False(cache.TryGet(2, out _));
    }

    [Fact]
    public void Remove_RemovesOnlyRequestedKey()
    {
        var cache = new LruCache<int, string>(2);
        cache.Set(1, "one");
        cache.Set(2, "two");

        cache.Remove(1);

        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out _));
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var cache = new LruCache<int, string>(3);
        cache.Set(1, "one");
        cache.Set(2, "two");

        cache.Clear();

        Assert.False(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(2, out _));
    }

    [Fact]
    public void ZeroCapacity_DisablesCaching()
    {
        var cache = new LruCache<int, string>(0);

        cache.Set(1, "one");

        Assert.False(cache.TryGet(1, out _));
    }
}
