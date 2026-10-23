using System.Diagnostics;
using System.Globalization;
using LogViewer.Core;

namespace LogGen;

public readonly record struct BenchmarkResult(
    int Entries,
    long Bytes,
    long ElapsedMilliseconds,
    double MegabytesPerSecond);

public static class Benchmark
{
    public static BenchmarkResult Run(string path)
    {
        using LogFile file = LogFile.Open(path);
        long bytes = file.Length;
        var index = new LogIndex();
        var stopwatch = Stopwatch.StartNew();

        new LogIndexer().IndexRange(file, index, 0, bytes, null, CancellationToken.None);

        stopwatch.Stop();
        double megabytes = bytes / (1024.0 * 1024.0);
        double seconds = stopwatch.Elapsed.TotalSeconds;
        double speed = seconds > 0 ? megabytes / seconds : 0;

        return new BenchmarkResult(index.Count, bytes, stopwatch.ElapsedMilliseconds, speed);
    }

    public static void Print(BenchmarkResult result, TextWriter output)
    {
        double megabytes = result.Bytes / (1024.0 * 1024.0);
        output.WriteLine($"Entries: {result.Entries}");
        output.WriteLine($"Size: {result.Bytes} bytes ({megabytes.ToString("F1", CultureInfo.InvariantCulture)} MB)");
        output.WriteLine($"Time: {result.ElapsedMilliseconds} ms");
        output.WriteLine($"Speed: {result.MegabytesPerSecond.ToString("F1", CultureInfo.InvariantCulture)} MB/s");
    }
}
