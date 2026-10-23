using System.Text;
using LogViewer.Core;
using Xunit.Abstractions;

namespace LogViewer.Tests;

public sealed class PlayerLogTests(ITestOutputHelper output)
{
    [Fact]
    public void RealPlayerLog_IndexesAndReportsLevelDistribution()
    {
        string path = FindSample("samples/Player.log");

        using LogFile file = LogFile.Open(path);
        var index = new LogIndex();
        new LogIndexer().IndexRange(file, index, 0, file.Length, null, CancellationToken.None);

        var counts = new int[5];
        for (int i = 0; i < index.Count; i++)
        {
            counts[(int)index.Level(i)]++;
        }

        output.WriteLine($"File: {path}");
        output.WriteLine($"File length: {file.Length} bytes");
        output.WriteLine($"Entries: {index.Count}");
        output.WriteLine($"IndexedLength: {index.IndexedLength}");
        output.WriteLine($"Info: {counts[(int)LogLevel.Info]}");
        output.WriteLine($"Warning: {counts[(int)LogLevel.Warning]}");
        output.WriteLine($"Error: {counts[(int)LogLevel.Error]}");
        output.WriteLine($"Exception: {counts[(int)LogLevel.Exception]}");
        output.WriteLine($"Other: {counts[(int)LogLevel.Other]}");

        PrintEntries(file, index, LogLevel.Other, 5);
        PrintEntries(file, index, LogLevel.Warning, 5);
        PrintEntries(file, index, LogLevel.Error, 5);

        double otherPercent = index.Count == 0 ? 0 : counts[(int)LogLevel.Other] * 100.0 / index.Count;
        if (otherPercent > 2)
        {
            output.WriteLine($"Other share: {otherPercent:F2}% — first 10 Other entries:");
            PrintEntries(file, index, LogLevel.Other, 10);
        }

        Assert.True(index.Count > 0);
        Assert.Equal(file.Length, index.IndexedLength);
        for (int i = 0; i < index.Count; i++)
        {
            Assert.True(index.End(i) > index.Offset(i));
        }

        Assert.Equal(index.IndexedLength, index.End(index.Count - 1));
    }

    private void PrintEntries(LogFile file, LogIndex index, LogLevel level, int limit)
    {
        output.WriteLine($"--- {level}: first {limit} ---");

        int shown = 0;
        for (int i = 0; i < index.Count && shown < limit; i++)
        {
            if (index.Level(i) != level)
            {
                continue;
            }

            long offset = index.Offset(i);
            long end = index.End(i);
            output.WriteLine($"[{i}] offset={offset}, length={end - offset}: {FirstLine(file, offset, end)}");
            shown++;
        }

        if (shown == 0)
        {
            output.WriteLine("(none)");
        }
    }

    private static string FirstLine(LogFile file, long offset, long end)
    {
        int length = (int)Math.Min(1024, end - offset);
        byte[] buffer = new byte[length];
        int read = file.Read(offset, buffer);
        string text = Encoding.UTF8.GetString(buffer, 0, read);
        int newline = text.IndexOf('\n');
        if (newline >= 0)
        {
            text = text[..newline];
        }

        text = text.TrimEnd('\r');
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private static string FindSample(string relativePath)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string fromCurrent = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
        if (File.Exists(fromCurrent))
        {
            return fromCurrent;
        }

        throw new FileNotFoundException($"Sample log not found: {relativePath}");
    }
}
