using System.Text;
using System.Text.RegularExpressions;
using LogGen;
using LogViewer.Core;

namespace LogViewer.Tests;

public sealed class FilterEngineTests : IDisposable
{
    private const string FiveLevels =
        "2024-10-23T14:23:45 1.000 INF [A] alpha hello\n" +
        "2024-10-23T14:23:45 1.001 WRN [A] beta hello\n" +
        "2024-10-23T14:23:45 1.002 ERR [A] gamma hello\n" +
        "2024-10-23T14:23:45 1.003 EXC [A] delta hello\n" +
        "2024-10-23T14:23:45 1.004 XYZ [A] epsilon hello\n";

    private readonly string _tempDir;
    private int _fileCounter;

    public FilterEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LogViewerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task EmptyFilter_ReturnsNullVisibleWithoutReadingFile()
    {
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);
        long readsBefore = file.ReadCallCount;

        FilterResult result = await FilterEngine.RunAsync(
            file,
            index,
            new LogFilter(LogLevelMask.All, string.Empty, IsRegex: false),
            null,
            CancellationToken.None);

        Assert.Null(result.Visible);
        Assert.Equal(index.Count, result.ProcessedCount);
        Assert.Equal(readsBefore, file.ReadCallCount);
        Assert.True(new LogFilter(LogLevelMask.All, string.Empty, false).IsEmpty);
        Assert.False(new LogFilter(LogLevelMask.Info, string.Empty, false).IsEmpty);
        Assert.False(new LogFilter(LogLevelMask.All, "x", false).IsEmpty);
    }

    [Theory]
    [InlineData(1, new[] { 0 })]
    [InlineData(2, new[] { 1 })]
    [InlineData(4, new[] { 2 })]
    [InlineData(8, new[] { 3 })]
    [InlineData(16, new[] { 4 })]
    [InlineData(3, new[] { 0, 1 })]
    [InlineData(6, new[] { 1, 2 })]
    [InlineData(17, new[] { 0, 4 })]
    [InlineData(31, new[] { 0, 1, 2, 3, 4 })]
    [InlineData(0, new int[0])]
    public async Task LevelFilter_SelectsRequestedLevels(int mask, int[] expected)
    {
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);

        FilterResult result = await FilterEngine.RunAsync(
            file,
            index,
            new LogFilter((LogLevelMask)mask, string.Empty, IsRegex: false),
            null,
            CancellationToken.None);

        if (mask == (int)LogLevelMask.All)
        {
            // Пустой фильтр не строит список: null означает «показать все».
            Assert.Null(result.Visible);
        }
        else
        {
            Assert.Equal(expected, ToArray(result.Visible));
        }

        Assert.Equal(index.Count, result.ProcessedCount);
    }

    [Fact]
    public async Task TextFilter_IsCaseInsensitive()
    {
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);

        FilterResult result = await FilterEngine.RunAsync(
            file,
            index,
            new LogFilter(LogLevelMask.All, "GAMMA", IsRegex: false),
            null,
            CancellationToken.None);

        Assert.Equal([2], ToArray(result.Visible));
    }

    [Fact]
    public async Task TextMatchOnlyInStackTrace_FindsEntry()
    {
        const string content =
            "2024-10-23T14:23:45 1.000 INF [A] before\n" +
            "2024-10-23T14:23:45 1.001 ERR [A] boom\n" +
            "NullReferenceException: Object reference not set\n" +
            "  at Game.World.Update ()\n";
        using LogFile file = OpenFile(content);
        LogIndex index = Index(file);
        Assert.Equal(LogLevel.Exception, index.Level(1));

        FilterResult result = await FilterEngine.RunAsync(
            file,
            index,
            new LogFilter(LogLevelMask.All, "Game.World.Update", IsRegex: false),
            null,
            CancellationToken.None);

        Assert.Equal([1], ToArray(result.Visible));
    }

    [Fact]
    public async Task RegexFilter_Matches()
    {
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);

        FilterResult result = await FilterEngine.RunAsync(
            file,
            index,
            new LogFilter(LogLevelMask.All, @"ERR \[A\] \w+", IsRegex: true),
            null,
            CancellationToken.None);

        Assert.Equal([2], ToArray(result.Visible));
    }

    [Fact]
    public async Task RegexFilter_UnsupportedByNonBacktracking_FallsBackToTimeoutEngine()
    {
        // Lookahead не поддерживается NonBacktracking, поэтому должен сработать
        // обычный движок с matchTimeout.
        var filter = new LogFilter(LogLevelMask.All, "^(?=.*gamma).*$", IsRegex: true);
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);

        Assert.True(FilterEngine.TryValidate(filter, out string? error));
        Assert.Null(error);

        FilterResult result = await FilterEngine.RunAsync(file, index, filter, null, CancellationToken.None);

        Assert.Equal([2], ToArray(result.Visible));
    }

    [Fact]
    public async Task InvalidRegex_TryValidateReturnsFalse_AndRunAsyncThrows()
    {
        var filter = new LogFilter(LogLevelMask.All, "(", IsRegex: true);
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);

        Assert.False(FilterEngine.TryValidate(filter, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));

        await Assert.ThrowsAsync<ArgumentException>(
            () => FilterEngine.RunAsync(file, index, filter, null, CancellationToken.None));
    }

    [Fact]
    public void NonRegexFilter_IsAlwaysValid()
    {
        Assert.True(FilterEngine.TryValidate(
            new LogFilter(LogLevelMask.Info, "(((", IsRegex: false),
            out string? error));
        Assert.Null(error);
    }

    [Fact]
    public async Task Cancellation_MidRun_Throws()
    {
        string path = Path.Combine(_tempDir, "cancel.log");
        LogGenerator.Generate(path, new LogGenerationOptions(Count: 50_000, Seed: 555));

        using LogFile file = LogFile.Open(path);
        LogIndex index = Index(file);
        var cts = new CancellationTokenSource();
        var progress = new CallbackProgress(percent =>
        {
            if (percent >= 5)
            {
                cts.Cancel();
            }
        });

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => FilterEngine.RunAsync(
                file,
                index,
                new LogFilter(LogLevelMask.All, "Chunk", IsRegex: false),
                progress,
                cts.Token));
        }
        finally
        {
            cts.Dispose();
        }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(FilterEngine.DefaultWindowSize)]
    public async Task MatchesManualIteration_OnGeneratedLog(int windowSize)
    {
        string path = Path.Combine(_tempDir, $"manual{windowSize}.log");
        LogGenerator.Generate(path, new LogGenerationOptions(Count: 3000, Seed: 4242));

        using LogFile file = LogFile.Open(path);
        LogIndex index = Index(file);
        var reader = new LogReader(file, index);

        LogFilter[] filters =
        [
            new(LogLevelMask.Info, string.Empty, false),
            new(LogLevelMask.Warning | LogLevelMask.Error | LogLevelMask.Exception, string.Empty, false),
            new(LogLevelMask.All, "chunk", false),
            new(LogLevelMask.All, "МИР", false),
            new(LogLevelMask.All, @"\bERR\b", true),
            new(LogLevelMask.All, @"^2024-\d{2}-\d{2}", true),
        ];

        foreach (LogFilter filter in filters)
        {
            int[] expected = ManualMatches(index, reader, filter);

            FilterResult result = await FilterEngine.RunAsync(
                file,
                index,
                filter,
                null,
                CancellationToken.None,
                windowSize);

            Assert.Equal(index.Count, result.ProcessedCount);
            Assert.Equal(expected, ToArray(result.Visible));
        }
    }

    [Fact]
    public async Task TextFilter_ReadsFileInWindows_NotPerEntry()
    {
        string path = Path.Combine(_tempDir, "windows.log");
        LogGenerator.Generate(path, new LogGenerationOptions(Count: 5000, Seed: 77));

        using LogFile file = LogFile.Open(path);
        LogIndex index = Index(file);
        var filter = new LogFilter(LogLevelMask.All, "Chunk", IsRegex: false);

        long readsBefore = file.ReadCallCount;
        _ = await FilterEngine.RunAsync(file, index, filter, null, CancellationToken.None);
        long defaultWindowReads = file.ReadCallCount - readsBefore;

        Assert.InRange(defaultWindowReads, 1, 3);
        Assert.True(defaultWindowReads < index.Count / 10);

        const int windowSize = 4096;
        readsBefore = file.ReadCallCount;
        _ = await FilterEngine.RunAsync(file, index, filter, null, CancellationToken.None, windowSize);
        long smallWindowReads = file.ReadCallCount - readsBefore;

        long windows = (file.Length + windowSize - 1) / windowSize;
        Assert.True(smallWindowReads <= 2 * windows + 4, $"reads={smallWindowReads}, windows={windows}");
        Assert.True(smallWindowReads < index.Count);
    }

    [Fact]
    public async Task FilterResult_IsConsistentWhenNothingMatches()
    {
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);

        FilterResult result = await FilterEngine.RunAsync(
            file,
            index,
            new LogFilter(LogLevelMask.All, "no-such-text", false),
            null,
            CancellationToken.None);

        Assert.NotNull(result.Visible);
        Assert.Equal(0, result.Visible!.Count);
        Assert.Equal(index.Count, result.ProcessedCount);
    }

    [Fact]
    public void IsEmpty_AccountsForExcludes()
    {
        Assert.True(new LogFilter(LogLevelMask.All, string.Empty, false, []).IsEmpty);
        Assert.False(new LogFilter(LogLevelMask.All, string.Empty, false, ["foo"]).IsEmpty);
    }

    [Fact]
    public async Task ExcludesFilter_RemovesMatchingEntries()
    {
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);

        FilterResult result = await FilterEngine.RunAsync(
            file,
            index,
            new LogFilter(LogLevelMask.All, string.Empty, IsRegex: false, Excludes: ["gamma"]),
            null,
            CancellationToken.None);

        Assert.NotNull(result.Visible);
        int[] visible = ToArray(result.Visible);
        Assert.DoesNotContain(2, visible); // gamma
        Assert.Equal(new[] { 0, 1, 3, 4 }, visible);
    }

    [Fact]
    public async Task ExcludesFilter_IsCaseInsensitive()
    {
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);

        FilterResult result = await FilterEngine.RunAsync(
            file,
            index,
            new LogFilter(LogLevelMask.All, string.Empty, IsRegex: false, Excludes: ["GAMMA"]),
            null,
            CancellationToken.None);

        Assert.NotNull(result.Visible);
        int[] visible = ToArray(result.Visible);
        Assert.DoesNotContain(2, visible); // gamma
        Assert.Equal(new[] { 0, 1, 3, 4 }, visible);
    }

    [Fact]
    public async Task ExcludesOnly_WithEmptyTextAndAllLevels_StillExcludes()
    {
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);

        // Levels = All, Text пустой, но Excludes не пустой — записи с "gamma" исключаются.
        FilterResult result = await FilterEngine.RunAsync(
            file,
            index,
            new LogFilter(LogLevelMask.All, string.Empty, IsRegex: false, Excludes: ["gamma"]),
            null,
            CancellationToken.None);

        Assert.False(result.Filter.IsEmpty);
        Assert.NotNull(result.Visible);
        int[] visible = ToArray(result.Visible);
        Assert.DoesNotContain(2, visible);
        Assert.Equal(new[] { 0, 1, 3, 4 }, visible);
    }

    [Fact]
    public async Task ExcludesFilter_CombinedWithText()
    {
        using LogFile file = OpenFile(FiveLevels);
        LogIndex index = Index(file);

        // Текст "hello" подходит всем, но запись с gamma исключается.
        FilterResult result = await FilterEngine.RunAsync(
            file,
            index,
            new LogFilter(LogLevelMask.All, "hello", IsRegex: false, Excludes: ["gamma"]),
            null,
            CancellationToken.None);

        Assert.NotNull(result.Visible);
        Assert.Equal(new[] { 0, 1, 3, 4 }, ToArray(result.Visible));
    }

    [Fact]
    public async Task ExtendAsync_AppliesExcludesToNewEntriesAndRechecksLast()
    {
        string path = Path.Combine(_tempDir, $"extend-excludes{++_fileCounter}.log");
        const string initial =
            "2024-10-23T14:23:45 1.000 INF [A] clean start\n" +
            "2024-10-23T14:23:45 1.001 INF [A] spam tail\n";
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(initial));

        using LogFile file = LogFile.Open(path);
        var index = new LogIndex();
        new LogIndexer().IndexRange(file, index, 0, file.Length, null, CancellationToken.None);

        var filter = new LogFilter(LogLevelMask.All, string.Empty, IsRegex: false, Excludes: ["spam"]);
        FilterResult previous = await FilterEngine.RunAsync(file, index, filter, null, CancellationToken.None);
        Assert.Equal(new[] { 0 }, ToArray(previous.Visible));

        // Дописываем новые записи; последняя ранее обработанная запись становится непроходной
        // (в неё дописывается стектрейс со словом spam), плюс добавляются новые проходные/непроходные.
        const string appended =
            "  at Game.Spam.Update ()\n" +
            "2024-10-23T14:23:45 1.002 INF [A] another clean\n" +
            "2024-10-23T14:23:45 1.003 WRN [A] full of spam here\n";
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(Encoding.UTF8.GetBytes(appended));
        }

        new LogIndexer().IndexRange(file, index, index.IndexedLength, file.Length, null, CancellationToken.None);

        FilterResult extended = await FilterEngine.ExtendAsync(file, index, previous, null, CancellationToken.None);
        FilterResult fresh = await FilterEngine.RunAsync(file, index, filter, null, CancellationToken.None);

        Assert.Equal(fresh.Visible?.Count, extended.Visible?.Count);
        Assert.Equal(ToArray(fresh.Visible), ToArray(extended.Visible));
        Assert.Equal(index.Count, extended.ProcessedCount);
    }

    private static int[] ManualMatches(LogIndex index, LogReader reader, LogFilter filter)
    {
        Regex? regex = filter.IsRegex
            ? new Regex(filter.Text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : null;

        var matches = new List<int>();
        for (int i = 0; i < index.Count; i++)
        {
            if (!MatchesLevel(filter.Levels, index.Level(i)))
            {
                continue;
            }

            string text = reader.GetFullText(i);
            bool textMatches = filter.IsRegex
                ? regex!.IsMatch(text)
                : text.Contains(filter.Text, StringComparison.OrdinalIgnoreCase);

            if (textMatches)
            {
                matches.Add(i);
            }
        }

        return [.. matches];
    }

    private static bool MatchesLevel(LogLevelMask mask, LogLevel level)
    {
        LogLevelMask bit = level switch
        {
            LogLevel.Info => LogLevelMask.Info,
            LogLevel.Warning => LogLevelMask.Warning,
            LogLevel.Error => LogLevelMask.Error,
            LogLevel.Exception => LogLevelMask.Exception,
            _ => LogLevelMask.Other,
        };

        return (mask & bit) != 0;
    }

    private static int[] ToArray(VisibleList? visible)
    {
        if (visible is null)
        {
            return [];
        }

        var result = new int[visible.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = visible[i];
        }

        return result;
    }

    private LogFile OpenFile(string content)
    {
        string path = Path.Combine(_tempDir, $"filter{++_fileCounter}.log");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return LogFile.Open(path);
    }

    private static LogIndex Index(LogFile file)
    {
        var index = new LogIndex();
        new LogIndexer().IndexRange(file, index, 0, file.Length, null, CancellationToken.None);
        return index;
    }

    private sealed class CallbackProgress(Action<int> callback) : IProgress<int>
    {
        public void Report(int value) => callback(value);
    }
}
