# Workflow: вьювер логов 7 Days to Die (C#, WinForms, .NET 8)

Документ для автономного агента. Пользователь пишет: «выполни шаг N из workflow». Всё, что нужно для шага, находится в разделах 0–5 и в самом шаге. Вопросов задавать не нужно.

---

## 0. Протокол выполнения (читать перед каждым шагом)

1. Выполняй только шаг N. Ничего из шагов > N не реализуй и не «подготавливай заранее».
   Не изменяй `samples/Player.log`, не удаляй и не «улучшай» `PlayerLogTests.cs` — это smoke test (см. `docs/DECISIONS.md`, раздел «Smoke test и общие соглашения»).
   Если smoke test падает — сообщи в отчёте, но не правь.
2. Перед стартом шага N > 1 выполни `dotnet build -c Release` и `dotnet test -c Release`. Если красное — почини минимально и запиши в `docs/DECISIONS.md`.
3. Checkpoint перед шагом: выполни `git status --porcelain`.
   - Пусто — коммит не нужен, переходи к шагу.
   - Есть изменения — `git add -A && git commit -m "Checkpoint before step N"`.
   - Если git недоступен или коммит упал — сообщи в отчёте, но шаг выполняй.
4. Вопросов не задавай. Если требование неоднозначно, выбери простой вариант, не нарушающий инварианты (раздел 5), и запиши в `docs/DECISIONS.md`.
5. Публичный API из шагов не менять без необходимости. Если меняешь — обнови и запиши в `docs/DECISIONS.md`.
6. Критерий завершения: `dotnet build -c Release` зелёный, все тесты шага зелёные, пункты «Приёмка» выполнены.
7. Итоговый отчёт: что сделано, список файлов, результат сборки и тестов, новые записи из `docs/DECISIONS.md`.
8. После успешного завершения шага (build зелёный, все тесты шага зелёные) 
   сделай коммит: `git add -A && git commit -m "Step N: <краткое описание>"`. 
   Не коммить, если build или тесты красные. В отчёте укажи хэш коммита.

---

## 1. Цель и контекст

Просмотрщик больших логов 7 Days to Die: от сотен мегабайт до единиц гигабайт, миллионы записей. Игра пишет файл во время работы. Нужно:

- открывать файл быстро, не читая целиком в память;
- плавно прокручивать и фильтровать;
- следить за ростом файла (tail).

---

## 2. Принятые решения (не пересматривать)

- Чтение файла: `FileStream` с `FileOptions.RandomAccess` и `FileShare.ReadWrite | FileShare.Delete`, чтение через `RandomAccess.Read`. `MemoryMappedFile` не использовать: размер mapping фиксируется при создании, а файл растёт.
- Индекс хранит логические записи (раздел 3), а не физические строки. Хранение: параллельные чанки `long[]` (смещения) и `byte[]` (уровни).
- Индексация и фильтрация работают на байтах (`Span<byte>`), без декодирования строк, кроме мест, где прямо указано.
- UI: `ListView` с `VirtualMode = true`. Интерфейс в коде, без `.Designer.cs`.
- Бизнес-логика в `LogViewer.Core` (без зависимости от WinForms). `MainForm` только связывает UI с ядром.
- Кодировка: UTF-8, BOM допустим.
- Сторонние NuGet-пакеты запрещены, кроме тестовых (xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk).

---

## 3. Формат лога, термины, правила индексации

### Формат

```
2024-10-23T14:23:45 123.456 INF [Component] message
2024-10-23T14:23:45 123.456 WRN [Component] message
2024-10-23T14:23:45 123.456 ERR [Component] message
NullReferenceException: ...          <- без timestamp
  at Namespace.Class.Method () ...   <- без timestamp
```

- `[Component]` может отсутствовать.
- На месте уровня возможен токен `EXC`.

### Термины

- **Строка**: байты до `\n` (включительно). `\r` перед `\n` не считается содержимым.
- **Строка с timestamp**: первые 19 байт соответствуют шаблону `dddd-dd-ddTdd:dd:dd` (цифры и разделители на фиксированных позициях).
- **Запись (entry)**: строка с timestamp плюс все следующие за ней строки без timestamp, до следующей строки с timestamp. Это единица индекса, списка, фильтра и копирования.
- **Offset(i)**: смещение первого байта записи i. **End(i)**: `Offset(i+1)`, для последней — `IndexedLength`.
- **IndexedLength**: смещение сразу после последнего `\n` проиндексированной части файла. Незавершённая последняя строка (без `\n`) в индекс не попадает.

### Правила индексации (нормативно)

1. Если индекс пуст и файл начинается с BOM `EF BB BF` — первые 3 байта пропускаются.
2. Строка с timestamp открывает новую запись. Уровень: сразу после 19 байт timestamp идёт пробел, число (цифры и `.`), пробел, три ASCII-буквы, после которых пробел или конец строки. `INF` = Info, `WRN` = Warning, `ERR` = Error, `EXC` = Exception, любое другое = Other. Искать `" INF "` по всей строке нельзя.
3. Строка без timestamp продолжает текущую запись и в индекс отдельным элементом не попадает. Если она (после пропуска пробелов) начинается с `Exception` или первый токен оканчивается на `Exception` — уровень записи повышается до Exception. Уровень не понижается. Выдели в метод `IsExceptionContinuation(ReadOnlySpan<byte>)`.
4. Если первая же строка файла без timestamp — создаётся запись Other.
5. Строка, разрезанная границей блока чтения, не теряется. Касается и `\r` / `\n`.
6. Последняя строка без `\n` не индексируется, пока `\n` не появится.

---

## 4. Структура репозитория

```
LogViewer.sln
Directory.Build.props          # Nullable=enable, ImplicitUsings=enable, LangVersion=12
.gitignore
docs/DECISIONS.md
src/LogViewer.Core/            # net8.0, без WinForms
src/LogViewer.App/             # net8.0-windows, WinForms
tests/LogViewer.Tests/         # net8.0, xunit, ссылка на Core
tools/LogGen/                  # net8.0, консоль, ссылка на Core
```

---

## 5. Инварианты (проверять в каждом шаге)

- **Потоки.** У `LogIndex` один писатель (индексатор). Читатели видят только опубликованный снимок. Порядок: сначала данные чанков и таблица чанков, затем `Volatile.Write` снимка. Массивы чанков под читателями не реаллоцируются.
- **Память.** Никаких структур, пропорциональных размеру файла, кроме индекса (9 байт/запись) и результата фильтра (4 байта/запись).
- **UI-поток.** Только чтение одной записи за раз (ограниченного размера). Тяжёлое — через `Task.Run`.
- **Отмена.** Все длительные операции принимают `CancellationToken` и проверяют его не реже раза на блок.
- **Ошибки I/O** не роняют приложение: перехватываются и показываются в статус-баре / MessageBox.
- **Освобождение.** `LogSession` реализует `IDisposable`, отменяет фоновые задачи и дожидается их.

---

## Шаг 1. Каркас решения, `LogFile`, `LogLevel`

**Цель.** Решение собирается. `LogFile` открывает файл, отдаёт длину, читает байты по offset.

**Что сделать.**

1. Создай структуру из раздела 4: `.sln`, `Directory.Build.props`, `.gitignore`, `docs/DECISIONS.md`, проекты `LogViewer.Core` и `LogViewer.Tests`.
2. В `LogViewer.Core`:

```csharp
public enum LogLevel : byte { Info = 0, Warning = 1, Error = 2, Exception = 3, Other = 4 }

public sealed class LogFile : IDisposable
{
    public static LogFile Open(string path);              // FileShare.ReadWrite | Delete, FileOptions.RandomAccess
    public string Path { get; }
    public long Length { get; }                           // RandomAccess.GetLength
    public int Read(long offset, Span<byte> destination); // до заполнения или EOF
    public void Dispose();
}
```

3. Тесты (`LogViewer.Tests`, временная папка, удаляется после каждого теста):
   - открытие существующего файла, `Length` совпадает;
   - открытие несуществующего файла — `FileNotFoundException`;
   - чтение с offset 0 в буфер больше файла — возвращает реальную длину;
   - чтение за `Length` — возвращает 0;
   - повторное открытие того же файла другим `LogFile` при уже открытом — успешно (проверяет `FileShare`);
   - `Dispose` не падает при повторном вызове.

**Приёмка.** Сборка зелёная, тесты зелёные, API совпадает.

**Не делать.** `LogIndex`, `LogIndexer`, UI, генератор.

---

## Шаг 2. `LogIndex` — чанковое хранилище

**Цель.** `LogIndex` хранит смещения и уровни в чанках, публикует снимок для читателей.

**Что сделать.**

```csharp
public sealed class IndexSnapshot
{
    public int Count { get; }
    public long IndexedLength { get; }
}

public sealed class LogIndex
{
    public const int ChunkSize = 65536;
    public IndexSnapshot Snapshot { get; }                // Volatile.Read
    public int Count => Snapshot.Count;
    public long IndexedLength => Snapshot.IndexedLength;

    public long Offset(int i);
    public LogLevel Level(int i);
    public long End(int i);                               // Offset(i+1) или IndexedLength для последней

    // Только для писателя:
    public void Append(long offset, LogLevel level);
    public void RaiseLastLevel(LogLevel level);           // никогда не понижает
    public void Publish(long indexedLength);
}
```

Требования:
- хранение чанками по `ChunkSize` (`long[]` и `byte[]`), таблица чанков растёт копированием ссылок;
- публикация и чтение по правилам раздела 5 (один писатель, читатели видят snapshot);
- `End(i)` для последней записи = `IndexedLength`.

Тесты:
- `Append` + `Publish` → `Count`, `Offset`, `Level` корректны;
- `End(i)` = `Offset(i+1)` для не-последних; = `IndexedLength` для последней;
- `RaiseLastLevel` повышает, но не понижает; на пустом индексе — no-op;
- переход через границу чанка (добавить `ChunkSize + 5` записей);
- `Offset`/`Level` вне диапазона бросают `ArgumentOutOfRangeException`.

**Приёмка.** Сборка и тесты зелёные.

**Не делать.** Чтение файла, парсинг, `LogIndexer`.

---

## Шаг 3. `LogIndexer` — парсинг и инкрементальная индексация

**Цель.** `LogIndexer.IndexRange` читает байты `[start, end)`, парсит записи, публикует в `LogIndex`.

**Что сделать.**

```csharp
public sealed class LogIndexer
{
    public const int DefaultBlockSize = 1 << 20;
    public LogIndexer(int blockSize = DefaultBlockSize);

    // start должен быть равен index.IndexedLength (или 0 при пустом индексе).
    // После каждого блока вызывает index.Publish. При отмене публикует достигнутое и бросает OperationCanceledException.
    public void IndexRange(LogFile file, LogIndex index, long start, long end,
                           IProgress<int>? progress, CancellationToken ct);
}
```

Требования:
- читает блоками (размер из конструктора); буфер из `ArrayPool<byte>`; если в заполненном буфере нет `\n` — буфер растёт;
- границы строк через `Span<byte>.IndexOf((byte)'\n')`;
- парсинг строго по правилам раздела 3, без декодирования в строки;
- состояние между вызовами: если индекс не пуст, продолжения привязываются к последней записи и могут повышать её уровень через `RaiseLastLevel`;
- прогресс: процент обработанных байт диапазона, только при изменении целого;
- отмена на каждом блоке.

Тесты (с малым размером блока 16 байт и с дефолтным):
- пустой файл → `Count == 0`, `IndexedLength == 0`;
- файл только с BOM → `Count == 0`;
- LF и CRLF;
- `\r` в конце блока, `\n` в начале следующего;
- кириллица в сообщениях;
- стектрейс (несколько строк без timestamp) — одна запись;
- продолжение с `Exception` повышает уровень записи до Exception; уровень не понижается;
- токен `EXC` на месте уровня → Exception;
- `" INF "` внутри сообщения не меняет уровень записи с `ERR`;
- строка с timestamp без распознаваемого уровня → Other;
- файл, начинающийся с продолжения → первая запись Other;
- последняя строка без `\n` не индексируется; после дописывания и `IndexRange(IndexedLength, newLength)` индексируется;
- отмена: `OperationCanceledException`, индекс согласован (`End(i) > Offset(i)`).

**Приёмка.** Сборка и тесты зелёные.

**Не делать.** UI, `LogSession`, генератор, фильтры, tail.

---

## Шаг 4. `LogGen` — генератор тестовых логов

**Цель.** Консольный инструмент для создания реалистичных логов и замера скорости индексации.

**Что сделать.** Проект `tools/LogGen` (ссылка на Core) с командами:

1. `LogGen generate <path> [--count N] [--seed S] [--crlf] [--bom] [--no-final-newline]`
   - по умолчанию `--count 1000000`, `--seed 12345`;
   - формат: `yyyy-MM-ddTHH:mm:ss <секунды с 3 знаками> <INF|WRN|ERR> [Component] message`; timestamp растёт монотонно; 10–20 компонентов, в 10% записей компонента нет;
   - уровни: ~85% INF, ~10% WRN, ~5% ERR;
   - ~1% записей — исключения: строка ERR (иногда `EXC`), затем 5–30 строк без timestamp (`NullReferenceException: ...`, затем `  at Namespace.Class.Method () ...`);
   - в 5% сообщений кириллица;
   - буферизованный `StreamWriter`, без накопления в памяти.
2. `LogGen bench <path>`: индексирует через `LogIndexer`, печатает число записей, размер, время в мс, МБ/с.

Тесты (в `LogViewer.Tests`, вынеси генерацию в вызываемый из тестов метод):
- `generate --count 10000` создаёт файл, `bench` проходит;
- одинаковые параметры → идентичные файлы;
- число записей индекса = число сгенерированных.

**Приёмка.** Сборка и тесты зелёные. `LogGen generate --count 1000000` + `LogGen bench` проходят.

**Не делать.** `--follow` (это в шаге 10), UI.

---

## Шаг 5. `LogSession` и базовый UI с фоновой индексацией

**Цель.** Окно открывает файл, показывает прогресс, показывает список записей-заглушек.

**Что сделать.**

1. В `LogViewer.Core`:

```csharp
public sealed class LogSession : IDisposable
{
    public static LogSession Open(string path);
    public LogFile File { get; }
    public LogIndex Index { get; }
    public TimeSpan IndexTime { get; }
    public Task IndexAsync(IProgress<int>? progress, CancellationToken ct);
    public void Dispose();
}
```

Тесты: открытие, индексация, отмена, `Dispose` во время индексации.

2. Проект `LogViewer.App` (WinForms, `net8.0-windows`, ссылка на Core). Файлы: `Program.cs`, `MainForm.cs`, `BufferedListView.cs`.
   - `BufferedListView`: `ListView` с `DoubleBuffered = true`.
   - `MainForm`: панель сверху с кнопками «Open…» и «Cancel»; `ProgressBar`; `StatusStrip`; `BufferedListView`.
   - ListView: `View = Details`, одна колонка, `FullRowSelect = true`, `VirtualMode = true`. `RetrieveVirtualItem` возвращает заглушку `"entry N [level]"`, level из `Index.Level(N)`.
3. Логика открытия: `Dispose` предыдущей сессии, `LogSession.Open`, запуск `IndexAsync` с `IProgress<int>`. Пока идёт индексация — «Open…» отключена, «Cancel» включена.
4. `System.Windows.Forms.Timer` 200 мс во время индексации: `VirtualListSize = Index.Count`.
5. Статус после: `Entries: N, Index built in X.X s`. После отмены: `Cancelled, N entries`.
6. Ошибки открытия — в `MessageBox`, приложение не падает.
7. Закрытие формы — `Dispose` сессии.

**Приёмка.** Сборка и тесты зелёные. Приложение запускается без исключений. `MainForm` не содержит логики индексации — только вызовы `LogSession`.

**Не делать.** Чтение текста, кэш, подсветка, фильтры, tail, drag-and-drop.

---

## Шаг 6. `LogReader` и `LruCache` — чтение записей

**Цель.** `LogReader` читает одну запись по offset. `LruCache` кэширует превью.

**Что сделать.**

```csharp
public readonly record struct EntryPreview(string FirstLine, int LineCount, bool Truncated);

public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    public LruCache(int capacity);
    public bool TryGet(TKey key, out TValue value);
    public void Set(TKey key, TValue value);
    public void Remove(TKey key);
    public void Clear();
}   // Dictionary + LinkedList, потокобезопасный (lock)

public sealed class LogReader
{
    public const int PreviewMaxBytes = 32 * 1024;
    public const int FullTextMaxBytes = 1024 * 1024;
    public LogReader(LogFile file, LogIndex index, int cacheCapacity = 2000);
    public EntryPreview GetPreview(int i);
    public string GetFullText(int i);
    public void Invalidate(int i);
    public void ClearCache();
}
```

Правила чтения:
- читается только `[Offset(i), End(i))`, не больше лимита;
- `Encoding.UTF8.GetString` (без stateful Decoder: запись читается целиком);
- при обрезке отступить до границы символа UTF-8, `Truncated = true`, добавить `… [truncated]`;
- хвостовые `\r\n` обрезать;
- `FirstLine`: до первого `\n`, без `\r`, управляющие символы (< 0x20) → пробел; `LineCount`: число строк в прочитанной части;
- ошибка I/O не пробрасывается: `GetPreview` возвращает `<read error: ...>`, `GetFullText` — то же.

Подключи к `LogSession` как свойство `Reader`.

Тесты:
- `GetPreview` на записи из 1 строки / из нескольких / с `\r\n` / с кириллицей;
- `Truncated` на записи больше лимита;
- `LineCount` считается верно;
- `GetFullText` возвращает полный текст до лимита;
- ошибка чтения (удалить файл после индексации) → `<read error: ...>`;
- `LruCache`: вытеснение по LRU, обновление свежести при `TryGet`, `Clear`;
- повторный `GetPreview` не читает файл повторно (проверь счётчиком через обёртку или диагностический метод).

**Приёмка.** Сборка и тесты зелёные.

**Не делать.** Фильтры, tail, OwnerDraw, UI-изменения.

---

## Шаг 7. UI: реальные записи, подсветка, панель деталей

**Цель.** Список показывает настоящие записи с подсветкой, снизу — полный текст выбранной.

**Что сделать.** Изменения только в `LogViewer.App`.

1. `RetrieveVirtualItem` через `Reader.GetPreview`. `CacheVirtualItems` прогревает кэш для `StartIndex..EndIndex`.
2. Текст элемента: `FirstLine`, для multi-line — ` [+N lines]` (если `Truncated`, то ` [+N lines…]`).
3. Подсветка: Warning — `#FFF59D`, Error — `#FFB74D`, Exception — `#D32F2F` с белым текстом. Info/Other — без подсветки. Цвета в одной таблице.
4. `SplitContainer`: сверху список, снизу `TextBox` (`ReadOnly`, `Multiline`, моноширинный, прокрутка, без переноса) с `GetFullText` выбранной записи; пустой, если ничего не выбрано.
5. Ctrl+C в списке копирует записи в буфер обмена:
   - MultiSelect = true (обязательно для многострочного копирования).
   - Если выделение образует непрерывный диапазон в файле (без активного фильтра,
     или при фильтре, где selectedIndex маппится в последовательные номера LogIndex),
     читать [Offset(firstEntry), End(lastEntry)) одним RandomAccess.Read
     и копировать байты как есть, без вставки разделителей.
   - Иначе (non-contiguous) — читать каждую запись через Reader.GetFullText
     и объединять через "\n" (ровно один перевод строки между записями).
   - При активном фильтре: маппить selectedIndex -> entryIndex через
     filterResult.Visible?[selectedIndex] ?? selectedIndex, использовать entryIndex
     для Offset/End/GetFullText.
   - Если выбрано > 10000 записей — MessageBox "Скопировать N записей?" с Cancel;
     при подтверждении — Task.Run с индикацией в статус-баре.
   - Если выбрано > 1000 записей (non-contiguous) — Task.Run, чтобы не блокировать UI.

**Приёмка.** Сборка и тесты зелёные. Открытие файла через `LogGen generate --count 100000` и прокрутка не подвисают.

**Не делать.** Фильтры, tail.

---

## Шаг 8. `FilterEngine` — фильтрация по уровню и тексту

**Цель.** `FilterEngine.RunAsync` фильтрует записи в фоне.

**Что сделать.**

```csharp
[Flags] public enum LogLevelMask { None = 0, Info = 1, Warning = 2, Error = 4, Exception = 8, Other = 16, All = 31 }

public sealed record LogFilter(LogLevelMask Levels, string Text, bool IsRegex)
{
    public bool IsEmpty => Levels == LogLevelMask.All && string.IsNullOrEmpty(Text);
}

public sealed class VisibleList
{
    public int Count { get; }              // Volatile
    public int this[int n] { get; }
    public int IndexOfOrNearest(int entryIndex);   // бинарный поиск
    public void Append(int entryIndex);            // только писатель
    public void RemoveLast();                      // только писатель
}

public sealed class FilterResult
{
    public LogFilter Filter { get; }
    public VisibleList? Visible { get; }   // null = показать все
    public int ProcessedCount { get; }
}

public static class FilterEngine
{
    public static bool TryValidate(LogFilter filter, out string? error);
    public static Task<FilterResult> RunAsync(LogFile file, LogIndex index, LogFilter filter,
                                              IProgress<int>? progress, CancellationToken ct);
}
```

Правила:
- работает со снимком `index.Count` на момент запуска;
- `filter.IsEmpty` → `Visible = null` без прохода;
- фильтр по уровню — только по индексу;
- текстовый фильтр — чтение файла окнами (4 МБ); записи, целиком лежащие в окне, проверяются прямо из окна; запись, выходящая за окно, читается отдельно; без `LogReader.GetFullText` и LRU; текст проверяется по всей записи (≤ 256 КБ);
- non-regex: `IndexOf` с `StringComparison.OrdinalIgnoreCase`;
- regex: `IgnoreCase | CultureInvariant | NonBacktracking`; при `NotSupportedException` — `IgnoreCase | CultureInvariant` с `matchTimeout = 1 с`; `RegexMatchTimeoutException` — запись не подходит; `ArgumentException` → `TryValidate` возвращает `false` и текст ошибки;
- отмена на каждом окне; результат отменённого запуска не используется;
- `progress` — процент прохода.

Тесты:
- пустой фильтр → `null`;
- фильтр по каждому уровню и комбинации;
- текст без учёта регистра;
- regex;
- совпадение только в стектрейсе (запись находится);
- невалидный regex → `TryValidate = false`;
- отмена посреди работы;
- соответствие ручному перебору на сгенерированном логе (фиксированный seed, 3–5 случайных фильтров);
- `VisibleList.IndexOfOrNearest`, `RemoveLast`, рост через границу чанка.

**Приёмка.** Сборка и тесты зелёные. Фильтр по тексту не читает файл на каждую запись (проверь счётчиком чтений: порядок = размер файла / размер окна).

**Не делать.** Tail.

---

## Шаг 9. UI: панель фильтров

**Цель.** Над списком — чекбоксы уровней и поле текста; фильтр применяется с debounce и отменой.

**Что сделать.** Только в `LogViewer.App`.

1. Панель над списком: чекбоксы `INF`, `WRN`, `ERR`, `EXC`, `Other` (все включены), `TextBox` для текста, чекбокс `Regex`.
2. Любое изменение — `System.Windows.Forms.Timer` на 300 мс (debounce); по срабатыванию — отменить предыдущий запуск (`CancellationTokenSource`) и запустить новый; результат применить в UI-потоке только если запуск не отменён и актуален.
3. Невалидный regex: фон `TextBox` светло-красный, tooltip с текстом ошибки, фильтр не применяется.
4. `RetrieveVirtualItem`/`CacheVirtualItems`: `i = result.Visible?[n] ?? n`. `VirtualListSize = result.Visible?.Count ?? Index.Count`.
5. Статус: `Shown: M of N` (во время фильтрации — с процентом).
6. Выбор сохраняется: до применения запомнить выбранную запись, после — найти через `IndexOfOrNearest`, `EnsureVisible`.

**Приёмка.** Сборка и тесты зелёные. На логе `--count 1000000` смена фильтра не подвисает дольше 2 с (кроме полного прохода текста).

**Не делать.** Tail.

---

## Шаг 10. Tail — слежение за ростом файла

**Цель.** Показ новых записей по мере роста. Устойчивость к усечению. Корректная работа с фильтрами.

**Что сделать.**

1. В `LogSession`:

```csharp
public enum PollKind { NoChange, Grown, Reset }
public readonly record struct PollResult(PollKind Kind, int OldCount, int NewCount);

public int Generation { get; }                      // растёт при Reset; Index и Reader заменяются
public Task<PollResult> PollAsync(CancellationToken ct);
```

Поведение:
- сравнить `File.Length` с `Index.IndexedLength`;
- больше: `IndexRange(IndexedLength, Length)` в фоне; `Grown`;
- меньше либо изменились первые 256 байт: новые `LogIndex` и `LogReader`, `Generation++`, полная переиндексация; `Reset`;
- равно: `NoChange`;
- не запускать две индексации параллельно;
- дописанные строки без timestamp к последней записи — только двигают `End(last)`, могут повысить уровень через `RaiseLastLevel`, для этой записи — `Reader.Invalidate(last)`; `PollResult.OldCount` = число записей до опроса.

2. UI:
   - `System.Windows.Forms.Timer` 300 мс;
   - чекбокс `Tail` (включён);
   - чекбокс `Follow` (автопрокрутка к последней записи) включён; выключается при прокрутке вверх / `Up`/`PageUp`/`Home` / выборе не последней; включается вручную или `End`;
   - `VirtualListSize` обновлять пачкой не чаще раза в 250 мс;
   - `Reset`: сбросить выбор, кэш, фильтр, пересчитать, статус `Log was truncated or rotated, reindexed`.

3. `FilterEngine.ExtendAsync(LogFile, LogIndex, FilterResult previous, IProgress<int>?, CancellationToken)`:
   - обрабатывает `[previous.ProcessedCount - 1, index.Count)`;
   - если последняя ранее обработанная запись была в `Visible` и больше не подходит — `RemoveLast`; если не была, но теперь подходит — добавить;
   - возвращает `FilterResult` с тем же `VisibleList` и новым `ProcessedCount`;
   - если `previous.Visible == null` — вернуть с новым `ProcessedCount`;
   - пока идёт полная фильтрация, `Extend` откладывается; смена фильтра отменяет всё.

4. `LogGen generate <path> --follow [--interval-ms 100] [--truncate-every-sec N]`:
   - открывает `FileShare.ReadWrite | Delete`, дописывает по одной записи каждые `interval-ms` мс, `Flush` после каждой;
   - каждая 50-я — ERR со стектрейсом несколькими отдельными записями с паузой;
   - каждая 20-я — в два приёма: часть строки без `\n`, через ≥ 150 мс остаток;
   - `--truncate-every-sec` обнуляет файл (`SetLength(0)`) и продолжает.

Тесты:
- `PollAsync`: `NoChange`, `Grown`, `Reset` (усечение и замена файла с другим началом);
- неполная последняя строка не индексируется, подхватывается после `\n`;
- дописанный стектрейс через несколько опросов повышает уровень последней записи и расширяет `End`;
- инкрементальный результат фильтра идентичен полному `RunAsync` на итоговом файле (10–20 сценариев, фиксированный seed), включая смену подходимости последней записи;
- `Dispose` во время опроса и во время фильтрации.

**Приёмка.** Сборка и тесты зелёные. Ручной сценарий в отчёте: `LogGen --follow` + приложение, ожидаемое поведение.

**Не делать.** Функции вне перечисленного (drag-and-drop, Ctrl+F, экспорт, OwnerDraw).

---

## Приложение: `docs/DECISIONS.md`

Формат: `Шаг N: <решение> — <причина>`. Записывать: отклонения от документа, выбор при неоднозначностях, изменения публичного API, найденные узкие места.