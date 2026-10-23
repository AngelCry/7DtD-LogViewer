using LogViewer.Core;

namespace LogViewer.Tests;

public sealed class LogIndexTests
{
    [Fact]
    public void AppendAndPublish_CountOffsetsLevelsAreCorrect()
    {
        var index = new LogIndex();

        index.Append(0, LogLevel.Info);
        index.Append(100, LogLevel.Warning);
        index.Append(140, LogLevel.Exception);
        index.Publish(200);

        Assert.Equal(3, index.Count);
        Assert.Equal(3, index.Snapshot.Count);
        Assert.Equal(200, index.IndexedLength);
        Assert.Equal(0, index.Offset(0));
        Assert.Equal(100, index.Offset(1));
        Assert.Equal(140, index.Offset(2));
        Assert.Equal(LogLevel.Info, index.Level(0));
        Assert.Equal(LogLevel.Warning, index.Level(1));
        Assert.Equal(LogLevel.Exception, index.Level(2));
    }

    [Fact]
    public void End_NonLastIsNextOffset_LastIsIndexedLength()
    {
        var index = new LogIndex();
        index.Append(0, LogLevel.Info);
        index.Append(120, LogLevel.Error);
        index.Append(300, LogLevel.Other);
        index.Publish(450);

        Assert.Equal(120, index.End(0));
        Assert.Equal(300, index.End(1));
        Assert.Equal(450, index.End(2));
    }

    [Fact]
    public void RaiseLastLevel_RaisesAndNeverLowers()
    {
        var index = new LogIndex();
        index.Append(0, LogLevel.Info);
        index.Publish(10);

        index.RaiseLastLevel(LogLevel.Warning);
        index.Publish(10);
        Assert.Equal(LogLevel.Warning, index.Level(0));

        index.RaiseLastLevel(LogLevel.Error);
        index.Publish(10);
        Assert.Equal(LogLevel.Error, index.Level(0));

        index.RaiseLastLevel(LogLevel.Info);
        index.Publish(10);
        Assert.Equal(LogLevel.Error, index.Level(0));
    }

    [Fact]
    public void RaiseLastLevel_OnEmptyIndex_IsNoOp()
    {
        var index = new LogIndex();

        index.RaiseLastLevel(LogLevel.Exception);

        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void Append_AcrossChunkBoundary_Works()
    {
        const int count = LogIndex.ChunkSize + 5;
        var index = new LogIndex();

        for (int i = 0; i < count; i++)
        {
            index.Append(i * 10L, (LogLevel)(i % 5));
        }

        index.Publish(count * 10L);

        Assert.Equal(count, index.Count);

        int[] probe = [0, LogIndex.ChunkSize - 1, LogIndex.ChunkSize, LogIndex.ChunkSize + 4, count - 1];
        foreach (int i in probe)
        {
            Assert.Equal(i * 10L, index.Offset(i));
            Assert.Equal((LogLevel)(i % 5), index.Level(i));
        }

        Assert.Equal(LogIndex.ChunkSize * 10L, index.End(LogIndex.ChunkSize - 1));
        Assert.Equal(count * 10L, index.End(count - 1));
    }

    [Fact]
    public void OffsetAndLevel_OutOfRange_Throw()
    {
        var index = new LogIndex();

        Assert.Throws<ArgumentOutOfRangeException>(() => index.Offset(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Level(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Offset(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Level(-1));

        index.Append(0, LogLevel.Info);
        index.Publish(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => index.Offset(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Level(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.End(1));
    }

    [Fact]
    public void PreviouslyPublishedSnapshot_IsNotChangedByLaterAppends()
    {
        var index = new LogIndex();
        index.Append(0, LogLevel.Info);
        index.Publish(10);

        IndexSnapshot oldSnapshot = index.Snapshot;

        for (int i = 1; i < LogIndex.ChunkSize + 3; i++)
        {
            index.Append(i * 10L, LogLevel.Warning);
        }

        index.Publish((LogIndex.ChunkSize + 3) * 10L);

        Assert.Equal(1, oldSnapshot.Count);
        Assert.Equal(10, oldSnapshot.IndexedLength);
        Assert.Equal(0, index.Offset(0));
        Assert.Equal(LogIndex.ChunkSize + 3, index.Snapshot.Count);
        Assert.NotSame(oldSnapshot, index.Snapshot);
    }

    [Fact]
    public void Snapshot_IsImmutableViewOfPublishedState()
    {
        var index = new LogIndex();
        index.Append(0, LogLevel.Info);
        index.Publish(42);

        IndexSnapshot snapshot = index.Snapshot;

        Assert.Same(snapshot, index.Snapshot);
        Assert.Equal(1, snapshot.Count);
        Assert.Equal(42, snapshot.IndexedLength);
    }
}
