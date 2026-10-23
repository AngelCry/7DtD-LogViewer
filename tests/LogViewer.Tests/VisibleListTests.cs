using LogViewer.Core;

namespace LogViewer.Tests;

public sealed class VisibleListTests
{
    [Fact]
    public void EmptyList_IndexOfOrNearestIsMinusOne_IndexerThrows()
    {
        var list = new VisibleList();

        Assert.Equal(0, list.Count);
        Assert.Equal(-1, list.IndexOfOrNearest(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = list[0]);
        Assert.Throws<InvalidOperationException>(() => list.RemoveLast());
    }

    [Fact]
    public void IndexOfOrNearest_FindsExactNearestAndClamps()
    {
        var list = new VisibleList();
        foreach (int value in new[] { 0, 10, 20, 30 })
        {
            list.Append(value);
        }

        Assert.Equal(2, list.IndexOfOrNearest(20));
        Assert.Equal(1, list.IndexOfOrNearest(5));
        Assert.Equal(3, list.IndexOfOrNearest(25));
        Assert.Equal(0, list.IndexOfOrNearest(-5));
        Assert.Equal(3, list.IndexOfOrNearest(1000));
    }

    [Fact]
    public void RemoveLast_RemovesOnlyLastAndAllowsReAppend()
    {
        var list = new VisibleList();
        list.Append(1);
        list.Append(2);
        list.Append(3);

        list.RemoveLast();

        Assert.Equal(2, list.Count);
        Assert.Equal(1, list[0]);
        Assert.Equal(2, list[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = list[2]);

        list.Append(3);
        Assert.Equal(3, list.Count);
        Assert.Equal(3, list[2]);
    }

    [Fact]
    public void Append_GrowthAcrossChunkBoundary()
    {
        const int chunkSize = 65536;
        const int total = chunkSize + 5;
        var list = new VisibleList();

        for (int i = 0; i < total; i++)
        {
            list.Append(i);
        }

        Assert.Equal(total, list.Count);
        Assert.Equal(0, list[0]);
        Assert.Equal(chunkSize - 1, list[chunkSize - 1]);
        Assert.Equal(chunkSize, list[chunkSize]);
        Assert.Equal(total - 1, list[total - 1]);
        Assert.Equal(chunkSize, list.IndexOfOrNearest(chunkSize));

        list.RemoveLast();
        Assert.Equal(total - 1, list.Count);
        Assert.Equal(total - 2, list[total - 2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = list[total - 1]);
    }

    [Fact]
    public void Append_RejectsNonIncreasingAndNegativeValues()
    {
        var list = new VisibleList();
        list.Append(5);

        Assert.Throws<ArgumentException>(() => list.Append(5));
        Assert.Throws<ArgumentException>(() => list.Append(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Append(-1));
        Assert.Equal(1, list.Count);
    }
}
