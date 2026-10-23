using System.Text;
using LogGen;
using LogViewer.Core;

namespace LogViewer.Tests;

public sealed class LogGenTests : IDisposable
{
    private readonly string _tempDir;

    public LogGenTests()
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
    public void Generate10000_ThenBench_Succeeds()
    {
        string path = Path.Combine(_tempDir, "generated.log");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exitCode = LogGen.Program.Run(
            ["generate", path, "--count", "10000", "--seed", "777"],
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.True(File.Exists(path));

        using (LogFile file = LogFile.Open(path))
        {
            var index = new LogIndex();
            new LogIndexer().IndexRange(file, index, 0, file.Length, null, CancellationToken.None);
            Assert.Equal(10000, index.Count);
            Assert.Equal(file.Length, index.IndexedLength);
        }

        var benchOutput = new StringWriter();
        exitCode = LogGen.Program.Run(["bench", path], benchOutput, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("Entries: 10000", benchOutput.ToString());
        Assert.Contains("Speed:", benchOutput.ToString());
    }

    [Fact]
    public void SameParameters_ProduceIdenticalFiles()
    {
        var options = new LogGenerationOptions(
            Count: 5000,
            Seed: 42,
            Crlf: true,
            Bom: true,
            FinalNewline: false);

        string first = Path.Combine(_tempDir, "first.log");
        string second = Path.Combine(_tempDir, "second.log");

        LogGen.LogGenerator.Generate(first, options);
        LogGen.LogGenerator.Generate(second, options);

        byte[] firstBytes = File.ReadAllBytes(first);
        byte[] secondBytes = File.ReadAllBytes(second);

        Assert.Equal(firstBytes, secondBytes);
        Assert.True(firstBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.NotEqual((byte)'\n', firstBytes[^1]);
        Assert.Contains("\r\n", Encoding.UTF8.GetString(firstBytes));
    }

    [Fact]
    public void DefaultOptions_DoNotAddBomOrCrLfAndEndWithNewline()
    {
        string path = Path.Combine(_tempDir, "default.log");
        LogGen.LogGenerator.Generate(path, new LogGenerationOptions(Count: 1000, Seed: 42));

        byte[] bytes = File.ReadAllBytes(path);

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain("\r\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void GeneratedEntries_AreInExpectedFormat()
    {
        string path = Path.Combine(_tempDir, "format.log");
        LogGen.LogGenerator.Generate(path, new LogGenerationOptions(Count: 2000, Seed: 123));

        using LogFile file = LogFile.Open(path);
        var index = new LogIndex();
        new LogIndexer().IndexRange(file, index, 0, file.Length, null, CancellationToken.None);

        Assert.Equal(2000, index.Count);
        for (int i = 0; i < index.Count; i++)
        {
            Assert.True(index.End(i) > index.Offset(i));
        }

        var levels = new int[5];
        for (int i = 0; i < index.Count; i++)
        {
            levels[(int)index.Level(i)]++;
        }

        Assert.Equal(index.Count, levels.Sum());
        Assert.True(levels[(int)LogLevel.Info] > index.Count * 0.75);
        Assert.True(levels[(int)LogLevel.Warning] > 0);
        Assert.True(levels[(int)LogLevel.Error] + levels[(int)LogLevel.Exception] > 0);
    }
}
