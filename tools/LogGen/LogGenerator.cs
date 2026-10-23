using System.Globalization;
using System.Text;

namespace LogGen;

public sealed record LogGenerationOptions(
    int Count = 1_000_000,
    int Seed = 12_345,
    bool Crlf = false,
    bool Bom = false,
    bool FinalNewline = true);

public readonly record struct LogGenerationResult(int Entries, long Bytes);

/// <summary>
/// Генератор реалистичных логов 7 Days to Die. Пишет файл потоком через
/// буферизованный <see cref="StreamWriter"/>, не накапливая записи в памяти.
/// </summary>
public static class LogGenerator
{
    private static readonly DateTime StartTimestamp =
        new(2024, 10, 23, 14, 23, 45, DateTimeKind.Unspecified);

    private static readonly string[] Components =
    [
        "AI", "World", "Chunk", "Player", "Network", "XUi", "Platform",
        "Audio", "Physics", "Rendering", "Animator", "Save", "Items",
        "Blocks", "Entities", "Vehicles", "Weather", "Quests", "Traders", "Mods",
    ];

    private static readonly string[] EnglishMessages =
    [
        "World generation completed",
        "Chunk loaded from cache",
        "Entity spawned successfully",
        "Player inventory synchronized",
        "Chunk mesh generated",
        "Saving region data",
        "AI task assigned",
        "Quest objective updated",
        "Audio source initialized",
        "Block damage applied",
        "Weather transition started",
        "Vehicle part repaired",
        "Trader inventory refreshed",
        "Light probe update finished",
        "Prefab loading finished",
    ];

    private static readonly string[] CyrillicMessages =
    [
        "Мир успешно сгенерирован",
        "Загрузка чанка завершена",
        "Игрок подключился к серверу",
        "Сохранение данных региона",
        "Обновление инвентаря торговца",
        "Погода изменилась",
        "Задание выполнено",
        "Транспорт восстановлен",
    ];

    private static readonly string[] StackTypes =
    [
        "World", "Chunk", "Entity", "Player", "Network", "XUi",
        "Items", "Blocks", "Vehicles", "Quests",
    ];

    private static readonly string[] StackMethods =
    [
        "Update", "Tick", "Load", "Save", "Process", "Refresh", "Execute",
    ];

    public static LogGenerationResult Generate(string path, LogGenerationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new LogGenerationOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.Count);

        var random = new DeterministicRandom(options.Seed);
        int componentCount = random.Next(10, 21);
        string newLine = options.Crlf ? "\r\n" : "\n";

        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);

        if (options.Bom)
        {
            stream.Write(Encoding.UTF8.GetPreamble());
        }

        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
        bool wroteAny = false;

        void WriteLine(string text)
        {
            if (wroteAny)
            {
                writer.Write(newLine);
            }

            writer.Write(text);
            wroteAny = true;
        }

        TimeSpan elapsed = TimeSpan.Zero;
        for (int i = 0; i < options.Count; i++)
        {
            string timestamp = StartTimestamp
                .Add(elapsed)
                .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            string seconds = elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

            int levelRoll = random.Next(100);
            string level;
            bool isException = false;
            if (levelRoll < 85)
            {
                level = "INF";
            }
            else if (levelRoll < 95)
            {
                level = "WRN";
            }
            else
            {
                level = "ERR";
                isException = random.Next(100) < 20;
                if (isException && random.Next(100) < 20)
                {
                    level = "EXC";
                }
            }

            string? component = random.Next(100) < 10
                ? null
                : Components[random.Next(componentCount)];

            string message = isException
                ? "Unhandled exception while updating game state"
                : random.Next(100) < 5
                    ? CyrillicMessages[random.Next(CyrillicMessages.Length)]
                    : EnglishMessages[random.Next(EnglishMessages.Length)];

            string componentPart = component is null ? "" : $"[{component}] ";
            WriteLine($"{timestamp} {seconds} {level} {componentPart}{message}");

            if (isException)
            {
                int continuationCount = random.Next(5, 31);
                WriteLine("NullReferenceException: Object reference not set to an instance of an object");
                for (int line = 1; line < continuationCount; line++)
                {
                    string type = StackTypes[random.Next(StackTypes.Length)];
                    string method = StackMethods[random.Next(StackMethods.Length)];
                    WriteLine($"  at Game.{type}.{method} ()");
                }
            }

            elapsed += TimeSpan.FromMilliseconds(random.Next(1, 26));
        }

        if (options.FinalNewline && wroteAny)
        {
            writer.Write(newLine);
        }

        writer.Flush();
        return new LogGenerationResult(options.Count, stream.Length);
    }

    /// <summary>Детерминированный PRNG, не зависящий от реализации System.Random.</summary>
    private sealed class DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(int seed)
        {
            _state = (ulong)(uint)seed;
        }

        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            }

            return (int)(NextUInt64() % (uint)maxExclusive);
        }

        public int Next(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            }

            return minInclusive + Next(maxExclusive - minInclusive);
        }

        private ulong NextUInt64()
        {
            // SplitMix64.
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
