using System.Text;
using LogViewer.Core;

namespace LogViewer.Tests;

public sealed class LogFileTests : IDisposable
{
    private readonly string _tempDir;

    public LogFileTests()
    {
        _tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "LogViewerTests_" + Guid.NewGuid().ToString("N"));
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
            // Временная папка: не мешаем тесту падать из-за уборки.
        }
    }

    private string CreateFile(string name, byte[] content)
    {
        string path = System.IO.Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Open_ExistingFile_LengthMatches()
    {
        byte[] content = "2024-10-23T14:23:45 123.456 INF [Component] message\n"u8.ToArray();
        string path = CreateFile("existing.log", content);

        using LogFile file = LogFile.Open(path);

        Assert.Equal(content.Length, file.Length);
        Assert.Equal(System.IO.Path.GetFullPath(path), file.Path);
    }

    [Fact]
    public void Open_MissingFile_ThrowsFileNotFound()
    {
        string path = System.IO.Path.Combine(_tempDir, "missing.log");

        Assert.Throws<FileNotFoundException>(() => LogFile.Open(path));
    }

    [Fact]
    public void Read_FromZero_LargerBuffer_ReturnsActualLength()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello\r\nworld");
        string path = CreateFile("read.log", content);

        using LogFile file = LogFile.Open(path);
        byte[] buffer = new byte[content.Length + 100];

        int read = file.Read(0, buffer);

        Assert.Equal(content.Length, read);
        Assert.Equal(content, buffer.AsSpan(0, read).ToArray());
    }

    [Fact]
    public void Read_BeyondLength_ReturnsZero()
    {
        string path = CreateFile("short.log", "abc"u8.ToArray());

        using LogFile file = LogFile.Open(path);
        byte[] buffer = new byte[16];

        Assert.Equal(0, file.Read(file.Length, buffer));
        Assert.Equal(0, file.Read(file.Length + 1000, buffer));
    }

    [Fact]
    public void Read_ExactEnd_ReturnsZero()
    {
        string path = CreateFile("exact.log", "abc"u8.ToArray());

        using LogFile file = LogFile.Open(path);
        byte[] buffer = new byte[3];

        Assert.Equal(3, file.Read(0, buffer));
        Assert.Equal(0, file.Read(3, buffer));
    }

    [Fact]
    public void Open_SameFileTwice_Succeeds()
    {
        string path = CreateFile("shared.log", "0123456789"u8.ToArray());

        using LogFile first = LogFile.Open(path);
        using LogFile second = LogFile.Open(path);

        byte[] buffer = new byte[10];
        Assert.Equal(10, second.Read(0, buffer));
        Assert.Equal("0123456789", Encoding.UTF8.GetString(buffer));
    }

    [Fact]
    public void Open_FileOpenedForWriting_Succeeds()
    {
        string path = CreateFile("growing.log", "abc"u8.ToArray());

        using var writer = new FileStream(
            path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using LogFile file = LogFile.Open(path);

        Assert.Equal(3, file.Length);

        writer.Write("def"u8);
        writer.Flush();

        Assert.Equal(6, file.Length);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        string path = CreateFile("dispose.log", "abc"u8.ToArray());
        LogFile file = LogFile.Open(path);

        file.Dispose();
        file.Dispose();
    }

    [Fact]
    public void Read_AfterDispose_ThrowsObjectDisposed()
    {
        string path = CreateFile("disposed.log", "abc"u8.ToArray());
        LogFile file = LogFile.Open(path);
        file.Dispose();

        Assert.Throws<ObjectDisposedException>(() => file.Read(0, new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => _ = file.Length);
    }
}
