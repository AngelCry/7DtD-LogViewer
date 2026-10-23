using System.Globalization;

namespace LogGen;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args, Console.Out, Console.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0)
        {
            PrintUsage(error);
            return 1;
        }

        return args[0] switch
        {
            "generate" => RunGenerate(args[1..], output, error),
            "bench" => RunBench(args[1..], output, error),
            _ => UnknownCommand(args[0], error),
        };
    }

    private static int RunGenerate(string[] args, TextWriter output, TextWriter error)
    {
        string? path = null;
        var options = new LogGenerationOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--count":
                    if (!TryReadInt(args, ref i, "--count", error, out int count) || count < 0)
                    {
                        if (count < 0)
                        {
                            error.WriteLine("error: --count must not be negative.");
                        }

                        return 1;
                    }

                    options = options with { Count = count };
                    break;

                case "--seed":
                    if (!TryReadInt(args, ref i, "--seed", error, out int seed))
                    {
                        return 1;
                    }

                    options = options with { Seed = seed };
                    break;

                case "--crlf":
                    options = options with { Crlf = true };
                    break;

                case "--bom":
                    options = options with { Bom = true };
                    break;

                case "--no-final-newline":
                    options = options with { FinalNewline = false };
                    break;

                default:
                    if (args[i].StartsWith('-') || path is not null)
                    {
                        error.WriteLine($"error: unexpected argument '{args[i]}'.");
                        return 1;
                    }

                    path = args[i];
                    break;
            }
        }

        if (path is null)
        {
            error.WriteLine("error: missing <path> for generate.");
            return 1;
        }

        try
        {
            LogGenerationResult result = LogGenerator.Generate(path, options);
            output.WriteLine($"Generated: {Path.GetFullPath(path)}");
            output.WriteLine($"Entries: {result.Entries}");
            output.WriteLine($"Bytes: {result.Bytes}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunBench(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 1 || args[0].StartsWith('-'))
        {
            error.WriteLine("error: bench requires exactly one <path>.");
            return 1;
        }

        try
        {
            BenchmarkResult result = Benchmark.Run(args[0]);
            output.WriteLine($"File: {Path.GetFullPath(args[0])}");
            Benchmark.Print(result, output);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static bool TryReadInt(string[] args, ref int index, string option, TextWriter error, out int value)
    {
        value = 0;
        if (index + 1 >= args.Length)
        {
            error.WriteLine($"error: {option} requires a value.");
            return false;
        }

        index++;
        if (!int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error.WriteLine($"error: invalid value for {option}: '{args[index]}'.");
            return false;
        }

        return true;
    }

    private static int UnknownCommand(string command, TextWriter error)
    {
        error.WriteLine($"error: unknown command '{command}'.");
        PrintUsage(error);
        return 1;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  LogGen generate <path> [--count N] [--seed S] [--crlf] [--bom] [--no-final-newline]");
        writer.WriteLine("  LogGen bench <path>");
    }
}
